using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Ai;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Modules.Sor.Domain;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>
/// One /sor question, end to end (docs/sor/TSQ_SOR.md):
/// <list type="number">
/// <item>Refusals that need no acknowledgement (empty or too long question, not configured, channel/role, cooldown, daily
/// limits, busy, provider back-off) are answered privately at once — no AI request.</item>
/// <item>The acknowledgement fixes the visibility (private or channel) for good.</item>
/// <item>Admission is written race-free (unique interaction, quota) — a repeated event never generates twice.</item>
/// <item>The context is selected (ownership and scope applied in the queries) and its versions are recorded.</item>
/// <item>ONE request to the shared OpenCode Go provider: no retry, no second model, no extraction or repair call.</item>
/// <item>The answer is parsed (bad memory proposals are dropped, a cut or empty answer is a failure) and kept pending.</item>
/// <item>Before delivery the versions are checked again (the asker's and the mentioned member's memory, every note used, the
/// conversation, the module and the access settings); a stale answer is not shown.</item>
/// <item>After a confirmed delivery the turn and the allowed note changes are saved idempotently; only then does the answer
/// get its "saved" line — written by the application, never by the model.</item>
/// </list>
/// Logs carry ids, stage, failure class, latency and token counts — never a question, answer, note or name. Scoped (one per
/// interaction); the saving step after delivery uses its own fresh scope so a failed attempt can be repeated cleanly.
/// </summary>
public sealed class SorService(
    IServiceScopeFactory scopes,
    [FromKeyedServices(SorModule.ModuleIdValue)] IAiChatClient ai,
    SorThrottle throttle,
    SorStatus status,
    IModuleGate gate,
    ILocalizer localizer,
    IOptions<SorOptions> options,
    TimeProvider clock,
    ILogger<SorService> logger)
{
    public bool IsConfigured => ai.IsConfigured;

    public async Task AskAsync(SorAsk ask, ISorResponder responder, CancellationToken ct = default)
    {
        var settings = options.Value;
        string L(string key, params object?[] args) => localizer.Get(ask.Language, key, args);
        var question = SorText.ReplaceMentions((ask.Question ?? "").Trim(), ask.About?.UserId, ask.About?.DisplayName);

        if (question.Length == 0)
        {
            await responder.RefuseAsync(L("sor.empty"));
            return;
        }

        if (question.Length > settings.MaxQuestionLength)
        {
            await responder.RefuseAsync(L("sor.too_long", settings.MaxQuestionLength));
            return;
        }

        if (!ai.IsConfigured)
        {
            await responder.RefuseAsync(L("sor.not_configured"));
            return;
        }

        SorGuildSettings guildSettings;
        await using (var scope = scopes.CreateAsyncScope())
        {
            guildSettings = await scope.ServiceProvider.GetRequiredService<SorSettingsReader>().GetAsync(ask.Actor.GuildId, ct);
            if (!guildSettings.Allows(ask.ChannelId, ask.ParentChannelId, ask.Actor.RoleIds.Select(r => r.Value), out var refusalKey))
            {
                await responder.RefuseAsync(L(refusalKey!));
                return;
            }

            var check = await scope.ServiceProvider.GetRequiredService<SorStore>().CheckAsync(ask, guildSettings.Limits, ct);
            if (!check.Admitted)
            {
                await responder.RefuseAsync(RefusalText(check, L));
                return;
            }
        }

        var (admission, ticket, retryAt) = throttle.TryEnter(ask.Key);
        if (ticket is null)
        {
            await responder.RefuseAsync(admission switch
            {
                SorAdmission.ConversationBusy => L("sor.busy_conversation"),
                SorAdmission.ProviderBackoff => L("sor.provider_busy_retry", Wait(retryAt)),
                _ => L("sor.at_capacity"),
            });
            return;
        }

        using (ticket)
        {
            await responder.DeferAsync(ask.IsPrivate);
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<SorStore>();
            SorReservation reservation;
            try
            {
                reservation = await store.ReserveAsync(ask, guildSettings.Limits, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing was admitted or counted: the acknowledgement must not stay open.
                var trace = TraceCodes.New();
                logger.LogError(ex, "Sor [{Trace}] database error during admission (guild {Guild})", trace, ask.Actor.GuildId);
                await Finish(ask, responder, "database", null, trace, L("sor.neutral_failure"), L("sor.internal", trace));
                return;
            }

            if (reservation.Refusal == SorRefusal.Duplicate)
            {
                logger.LogInformation("Sor: interaction already admitted, no second generation (guild {Guild})", ask.Actor.GuildId);
                return;
            }

            if (!reservation.Admitted)
            {
                await responder.FailAsync(L("sor.neutral_failure"), RefusalText(reservation, L));
                return;
            }

            await RunAsync(ask, question, guildSettings.Limits, reservation, store, responder, L, ct);
        }
    }

    private async Task RunAsync(SorAsk ask, string question, SorLimits limits, SorReservation reservation, SorStore store, ISorResponder responder,
        Func<string, object?[], string> L, CancellationToken ct)
    {
        var settings = options.Value;
        var requestId = reservation.RequestId;
        SorContext context;
        SorPromptBuild prompt;
        try
        {
            context = await store.LoadContextAsync(ask, limits, ct);
            prompt = SorPrompt.Build(context, ask, question, settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var trace = TraceCodes.New();
            logger.LogError(ex, "Sor [{Trace}] database error before the AI request (guild {Guild})", trace, ask.Actor.GuildId);
            await TryAsync(() => store.ReleaseAsync(requestId, ct));
            await Finish(ask, responder, "database", null, trace, L("sor.neutral_failure", []), L("sor.internal", [trace]));
            return;
        }

        var snapshot = new SorSnapshot(context.AskerStamp, context.AskerMemory, context.Other?.UserId, context.OtherStamp, context.ConversationId,
            context.ConversationStamp, prompt.UsedNotes.Select(n => new SorNoteVersion(n.Id, n.Stamp)).ToList());

        var result = await ai.CompleteAsync(settings.Request(prompt.Messages), ct);
        if (!result.Succeeded)
        {
            var trace = TraceCodes.New();
            if (result.Failure == AiFailure.RateLimited)
                throttle.BackOff(result.RetryAfter);
            var stage = result.Failure == AiFailure.Timeout ? SorFailureStage.Timeout
                : result.Failure is AiFailure.InvalidResponse or AiFailure.EmptyOutput ? SorFailureStage.Parse
                : SorFailureStage.Provider;
            Log(ask, trace, stage.ToString(), result);
            await TryAsync(() => store.MarkFailedAsync(requestId, stage, result.Failure.ToString(), result.Usage, trace, ct));
            await Finish(ask, responder, stage.ToString(), result, trace, L("sor.neutral_failure", []), ProviderText(result, trace, L));
            return;
        }

        var parsed = SorAnswerFormat.Parse(result.Text!, result.Truncated);
        if (!parsed.IsValid)
        {
            var trace = TraceCodes.New();
            Log(ask, trace, "Parse:" + parsed.Problem, result);
            await TryAsync(() => store.MarkFailedAsync(requestId, SorFailureStage.Parse, parsed.Problem.ToString(), result.Usage, trace, ct));
            await Finish(ask, responder, "parse", result, trace, L("sor.neutral_failure", []),
                L(parsed.Problem == SorAnswerProblem.Truncated ? "sor.truncated" : "sor.unusable", [trace]));
            return;
        }

        try
        {
            await store.SaveGeneratedAsync(requestId, context.ConversationId, parsed.Answer, parsed.Candidates, snapshot, result.Usage, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var trace = TraceCodes.New();
            logger.LogError(ex, "Sor [{Trace}] database error saving the generated result (guild {Guild})", trace, ask.Actor.GuildId);
            await TryAsync(() => store.MarkFailedAsync(requestId, SorFailureStage.Database, null, result.Usage, trace, ct));
            await Finish(ask, responder, "database", result, trace, L("sor.neutral_failure", []), L("sor.internal", [trace]));
            return;
        }

        // Nothing the answer depended on may have changed: memory, notes, conversation, module, access.
        if (!await StillAllowedAsync(ask, store, snapshot, ct))
        {
            var trace = TraceCodes.New();
            Log(ask, trace, "Stale", result);
            await TryAsync(() => store.MarkFailedAsync(requestId, SorFailureStage.Stale, null, result.Usage, trace, ct));
            await Finish(ask, responder, "stale", result, trace, L("sor.neutral_failure", []), L("sor.stale", []));
            return;
        }

        var notice = context.PreviousTopicClosed ? L("sor.previous_closed", []) : null;
        var reply = new SorReply(SorText.Split(parsed.Answer), SorText.Excerpt(question), context.ConversationId, ask.Actor.UserId.Value, ask.IsPrivate, notice);
        var delivery = await responder.DeliverAsync(reply);
        if (delivery != SorDelivery.Delivered)
        {
            // Never generated again: an undelivered answer is not history, and its proposals are dropped.
            var trace = TraceCodes.New();
            Log(ask, trace, "Delivery:" + delivery, result);
            await TryAsync(() => store.MarkFailedAsync(requestId, delivery == SorDelivery.Unknown ? SorFailureStage.Unknown : SorFailureStage.Delivery,
                delivery.ToString(), result.Usage, trace, ct));
            status.Record(new SorLastRun(clock.GetUtcNow(), "delivery", delivery.ToString(), null, trace));
            await TryAsync(() => responder.PrivateNoteAsync(L("sor.delivery_failed", [trace]), memoryButton: false));
            return;
        }

        if (reservation.ShowNotice)
            await TryAsync(() => responder.PrivateNoteAsync(L("sor.first_use", []), memoryButton: true));

        var otherNames = context.Other is { } other ? [other.DisplayName] : Array.Empty<string>();
        var (saved, memory) = await FinalizeWithReconcileAsync(ask, requestId, question, otherNames, limits, reservation.ShowNotice, ct);
        Log(ask, null, saved ? "Completed" : "CompletedNotSaved", result);
        status.Record(new SorLastRun(clock.GetUtcNow(), "completed", null, result.HttpStatus, null));
        if (!saved)
            return;

        if (MemoryLine(memory, L) is { } line)
            await TryAsync(() => responder.AddFooterAsync(reply, line));
        if (memory.ForgetAmbiguous)
            await TryAsync(() => responder.PrivateNoteAsync(L("sor.forget_ambiguous", []), memoryButton: true));
        else if (memory.Full)
            await TryAsync(() => responder.PrivateNoteAsync(L("sor.memory_full", [settings.MaxMemoryItems]), memoryButton: true));
    }

    /// <summary>
    /// The saving step after a delivered answer. A database failure is retried a bounded number of times in a new scope — the
    /// answer is never generated again; the stamps decide again each time. Discord and the database are not one transaction:
    /// when every attempt fails the answer stays shown, but neither the turn nor a note is saved (and nothing claims so).
    /// </summary>
    private async Task<(bool Saved, SorMemoryOutcome Memory)> FinalizeWithReconcileAsync(SorAsk ask, long requestId, string question,
        IReadOnlyCollection<string> otherNames, SorLimits limits, bool noticeShown, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<SorStore>().FinalizeAsync(requestId, question, otherNames, limits, noticeShown, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sor: the saving step after delivery failed (attempt {Attempt}, guild {Guild})", attempt, ask.Actor.GuildId);
            }
        }

        return (false, SorMemoryOutcome.None);
    }

    private async Task<bool> StillAllowedAsync(SorAsk ask, SorStore store, SorSnapshot snapshot, CancellationToken ct)
    {
        if (!await gate.IsEnabledAsync(ask.Actor.GuildId, SorModule.ModuleIdTyped, ct))
            return false;
        await using var scope = scopes.CreateAsyncScope();
        var current = await scope.ServiceProvider.GetRequiredService<SorSettingsReader>().GetAsync(ask.Actor.GuildId, ct);
        if (!current.Allows(ask.ChannelId, ask.ParentChannelId, ask.Actor.RoleIds.Select(r => r.Value), out _))
            return false;
        return await store.IsCurrentAsync(ask.Actor.GuildId.Value, ask.Actor.UserId.Value, snapshot, ct);
    }

    private async Task Finish(SorAsk ask, ISorResponder responder, string outcome, AiChatResult? result, string trace, string neutral, string detail)
    {
        status.Record(new SorLastRun(clock.GetUtcNow(), outcome, result?.Failure.ToString(), result?.HttpStatus, trace));
        await TryAsync(() => responder.FailAsync(neutral, detail));
    }

    private void Log(SorAsk ask, string? trace, string outcome, AiChatResult result) =>
        logger.LogInformation(
            "Sor [{Trace}] {Outcome} guild={Guild} private={Private} failure={Failure} http={Status} provider={ProviderError} finish={Finish} latencyMs={Latency} in={In} out={Out} reasoning={Reasoning} cached={Cached}",
            trace ?? "-", outcome, ask.Actor.GuildId, ask.IsPrivate, result.Failure, result.HttpStatus, result.ProviderError ?? "-", result.FinishReason ?? "-",
            (int)result.Latency.TotalMilliseconds, result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.ReasoningTokens, result.Usage.CachedInputTokens);

    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sor: a follow-up step failed");
        }
    }

    private string RefusalText(SorReservation refusal, Func<string, object?[], string> L) => refusal.Refusal switch
    {
        SorRefusal.Cooldown => L("sor.cooldown", [Wait(refusal.RetryAt)]),
        SorRefusal.UserQuota => L("sor.user_quota", [refusal.Limit]),
        SorRefusal.GuildQuota => L("sor.guild_quota", [refusal.Limit]),
        _ => L("sor.neutral_failure", []),
    };

    private string Wait(DateTimeOffset? until)
    {
        var seconds = until is { } u ? Math.Max(1, (int)Math.Ceiling((u - clock.GetUtcNow()).TotalSeconds)) : 1;
        return seconds.ToString(CultureInfo.InvariantCulture);
    }

    private string ProviderText(AiChatResult result, string trace, Func<string, object?[], string> L) => result.Failure switch
    {
        AiFailure.NotConfigured => L("sor.not_configured", []),
        AiFailure.Timeout => L("sor.timeout", [trace]),
        AiFailure.RateLimited => L("sor.provider_busy_retry", [Wait(clock.GetUtcNow() + (result.RetryAfter ?? SorThrottle.DefaultBackoff))]),
        AiFailure.ServerError or AiFailure.Network => L("sor.provider_unavailable", [trace]),
        AiFailure.InvalidResponse or AiFailure.EmptyOutput => L("sor.unusable", [trace]),
        _ => L("sor.provider_refused", [trace]), // 401, 403, other 4xx: configuration or account — retrying will not help
    };

    private static string? MemoryLine(SorMemoryOutcome memory, Func<string, object?[], string> L)
    {
        var parts = new List<string>();
        if (memory.Added > 0)
            parts.Add(L("sor.memory.added", [memory.Added]));
        if (memory.Updated > 0)
            parts.Add(L("sor.memory.updated", [memory.Updated]));
        if (memory.Forgotten > 0)
            parts.Add(L("sor.memory.forgotten", [memory.Forgotten]));
        return parts.Count == 0 ? null : "🧠 " + string.Join(" · ", parts);
    }
}
