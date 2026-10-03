using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>A channel as the bot's cache sees it.</summary>
/// <param name="IsSupported">Holds member messages the bot can read (text, announcement, voice/stage chat, thread).</param>
/// <param name="PermissionChannel">Where access is decided: a thread's parent, otherwise the channel itself.</param>
public sealed record SummaryChannel(ChannelId Id, bool IsSupported, ChannelId PermissionChannel);

public enum SummaryFetchStatus
{
    Ok = 0,
    NoAccess = 1,
    Failed = 2,
}

/// <summary>Server display names of the selected messages' authors (by user id) and names for the markup in them.</summary>
public sealed record SummaryNames(IReadOnlyDictionary<ulong, string> Authors, SummaryMentionNames Mentions)
{
    public static SummaryNames Empty { get; } = new(new Dictionary<ulong, string>(), SummaryMentionNames.Empty);
}

/// <summary>The Discord reads /ozetle needs beyond <see cref="IGuildGateway"/>. Implemented over Discord.Net in Commands; faked in tests.</summary>
public interface ISummaryDiscord
{
    SummaryChannel? GetChannel(GuildId guild, ChannelId channel);

    /// <summary>The invoking member's effective permissions in <paramref name="channel"/>; null when unknown.</summary>
    GuildPermission? InvokerPermissions(ChannelId channel);

    /// <summary>A role's current name from the guild cache; null when the role does not exist (any more).</summary>
    string? RoleName(GuildId guild, ulong role);

    /// <summary>
    /// One page of <paramref name="channel"/>'s history only (a thread without its parent), newest first, older than
    /// <paramref name="before"/> when given. Never throws for Discord problems.
    /// </summary>
    Task<SummaryHistoryPage> ReadHistoryPageAsync(GuildId guild, ChannelId channel, ulong? before, CancellationToken cancellationToken);

    /// <summary>Author and mention names for the messages that will be summarized (a few cache/REST reads, bounded).</summary>
    Task<SummaryNames> ResolveNamesAsync(GuildId guild, IReadOnlyList<SummarySourceMessage> messages, CancellationToken cancellationToken);

    /// <summary>The application's Message Content flag (Developer Portal); null when it could not be read.</summary>
    Task<bool?> HasMessageContentAccessAsync();
}

/// <summary>How /ozetle answers (implemented by the Discord command class; faked in tests). Nothing sent here pings.</summary>
public interface ISummaryResponder
{
    /// <summary>Only the member who ran the command sees it (before or after <see cref="DeferPrivateAsync"/>).</summary>
    Task ReplyPrivateAsync(string text);

    /// <summary>Acknowledges within Discord's 3 seconds; only the member sees the "thinking" state.</summary>
    Task DeferPrivateAsync();

    /// <summary>Posts the summary as normal public message(s) in the channel; false when Discord refused.</summary>
    Task<bool> PostPublicAsync(IReadOnlyList<string> parts);
}

/// <summary>
/// Who asked where, in which language, with which time zone (for &lt;t:…&gt; in messages). <paramref name="MemberRoles"/>: the
/// invoking member's role ids from the interaction payload (no REST read).
/// </summary>
public sealed record SummaryRequest(GuildId Guild, ChannelId Channel, UserId Member, string Language, TimeZoneInfo Zone, IReadOnlyCollection<ulong> MemberRoles);

public enum SummaryOutcome
{
    Posted = 0,
    NotConfigured = 1,
    UnsupportedChannel = 2,
    MemberNoAccess = 3,
    BotNoAccess = 4,
    Throttled = 5,
    FetchFailed = 6,
    NotEnoughMessages = 7,
    ContentUnavailable = 8,
    AiFailed = 9,
    PostFailed = 10,
    RoleMissing = 11,
    NotEnoughNewMessages = 12,
    HistoryUnknown = 13,
}

/// <summary>The last run, for /bot status (no ids, no text).</summary>
public sealed record SummaryLastRun(SummaryOutcome Outcome, SummaryAiFailure AiFailure, DateTimeOffset At);

/// <summary>
/// /ozetle from start to end, with every check on the server and at most ONE AI request: the member's roles (any one of
/// <see cref="SummaryOptions.EffectiveAllowedRoleIds"/>), configuration, channel type, the member's and the bot's access (View
/// Channel + Read Message History), admission (channel busy, cooldowns, bot-wide limit) — all answered privately and at once,
/// before anything is read. Then a private acknowledgement and a bounded scan of the channel's history
/// (<see cref="SummaryHistory"/>): after an earlier TSQ summary at least <see cref="SummaryOptions.MaxMessages"/> new member
/// messages are required, a first summary needs <see cref="SummaryOptions.MinMessages"/>, and an inconclusive scan fails
/// closed. Then the AI part, by the generation mode read once at the start (<see cref="SummaryOptions.GenerationMode"/>).
/// Legacy: ONE request — the "Name: text" transcript, the model's Markdown cleaned up. Grounded: at most TWO requests —
/// the generator model drafts from records with reply links (<see cref="SummaryGrounded"/>); only a draft whose sources and
/// quotes check out (<see cref="SummaryGroundedAnswer"/>) is sent, with the same records, to the reviewer model for a factual
/// review (<see cref="SummaryGroundedReviewPrompt"/>), and the reviewed answer must pass the same reader to be published.
/// A failed or refused answer of either stage ends the run: nothing is retried, repaired or sent to a third request, and
/// one mode never falls back to the other. The summary goes out as public message(s) without pings. Only a posted
/// summary starts the full cooldowns. Logs carry
/// ids, counts, token usage, latency and outcome — never message text, names, the prompt or the answer.
/// </summary>
public sealed partial class SummaryService(
    ISummaryAiClient ai,
    SummaryThrottle throttle,
    IGuildGateway guilds,
    ILocalizer localizer,
    IOptions<SummaryOptions> options,
    TimeProvider clock,
    ILogger<SummaryService> logger)
{
    /// <summary>What the member and the bot both need in the channel (a thread: in its parent).</summary>
    public const GuildPermission RequiredToRead = GuildPermission.ViewChannel | GuildPermission.ReadMessageHistory;

    /// <summary>The time allowed for reading the channel's history from Discord (up to <see cref="SummaryHistory.MaxPages"/> pages).</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private SummaryLastRun? _last;

    public SummaryLastRun? LastRun => Volatile.Read(ref _last);

    public bool IsConfigured => ai.IsConfigured;

    public async Task<SummaryOutcome> RunAsync(SummaryRequest request, ISummaryDiscord discord, ISummaryResponder responder)
    {
        var trace = TraceCodes.New();
        string T(string key, params object?[] args) => localizer.Get(request.Language, key, args);

        // The settings are taken once and the mode is decided once, from the interaction's own channel or thread id: one run
        // never mixes two modes, whatever the configuration does afterwards.
        var settings = options.Value;
        var decision = settings.ResolveGenerationMode(request.Channel.Value);
        var mode = decision.Mode;

        // Roles first: from the interaction payload, before anything is read or sent anywhere. Any ONE role is enough.
        var allowedRoles = settings.EffectiveAllowedRoleIds;
        if (!allowedRoles.Any(request.MemberRoles.Contains))
        {
            LogRefused(logger, trace, SummaryOutcome.RoleMissing, request.Guild.Value, request.Channel.Value, request.Member.Value);
            await responder.ReplyPrivateAsync(T("summary.role_required", RoleList(request, discord, allowedRoles, trace)));
            return SummaryOutcome.RoleMissing;
        }

        if (!ai.IsConfigured)
        {
            LogNotConfigured(logger, trace, request.Guild.Value, request.Channel.Value, request.Member.Value);
            await responder.ReplyPrivateAsync(T("summary.not_configured"));
            return Remember(SummaryOutcome.NotConfigured);
        }

        if (discord.GetChannel(request.Guild, request.Channel) is not { IsSupported: true } channel)
            return await RefuseAsync(SummaryOutcome.UnsupportedChannel, "summary.unsupported_channel");

        if (discord.InvokerPermissions(channel.PermissionChannel) is not { } member || !member.Grants(RequiredToRead))
            return await RefuseAsync(SummaryOutcome.MemberNoAccess, "summary.member_no_access");

        var bot = await guilds.GetBotChannelAccessAsync(request.Guild, channel.PermissionChannel, CancellationToken.None);
        if (!bot.Exists || !bot.Permissions.Grants(RequiredToRead))
            return await RefuseAsync(SummaryOutcome.BotNoAccess, "summary.bot_no_access");

        var (admission, ticket, retryAt) = throttle.TryEnter(request.Member.Value, request.Channel.Value);
        if (ticket is null)
        {
            LogThrottled(logger, trace, admission, request.Guild.Value, request.Channel.Value, request.Member.Value);
            await responder.ReplyPrivateAsync(admission switch
            {
                SummaryAdmission.ChannelBusy => T("summary.channel_busy"),
                SummaryAdmission.UserCooldown => T("summary.user_cooldown", Seconds(retryAt)),
                SummaryAdmission.ChannelCooldown => T("summary.channel_cooldown", Duration(request.Language, retryAt)),
                _ => T("summary.at_capacity"),
            });
            return SummaryOutcome.Throttled; // not remembered: says nothing about the service
        }

        using (ticket)
        {
            await responder.DeferPrivateAsync();
            return await SummarizeAsync(request, discord, responder, ticket, trace, settings, mode, decision.Source);
        }

        async Task<SummaryOutcome> RefuseAsync(SummaryOutcome outcome, string key)
        {
            LogRefused(logger, trace, outcome, request.Guild.Value, request.Channel.Value, request.Member.Value);
            await responder.ReplyPrivateAsync(T(key));
            return outcome;
        }
    }

    private async Task<SummaryOutcome> SummarizeAsync(
        SummaryRequest request, ISummaryDiscord discord, ISummaryResponder responder, SummaryThrottle.Ticket ticket, string trace,
        SummaryOptions settings, SummaryGenerationMode mode, SummaryModeSource modeSource)
    {
        string T(string key, params object?[] args) => localizer.Get(request.Language, key, args);
        var ids = (Guild: request.Guild.Value, Channel: request.Channel.Value, Member: request.Member.Value);

        SummaryHistoryScan scan;
        SummaryNames names = SummaryNames.Empty;
        IReadOnlyList<SummarySourceMessage> replyContext = [];
        using (var deadline = new CancellationTokenSource(FetchTimeout, clock))
        {
            scan = await SummaryHistory.ScanAsync(discord, request.Guild, request.Channel, settings.MaxMessages, deadline.Token);
            LogHistory(logger, trace, scan.Outcome, scan.PageCount, scan.EligibleCount, scan.MarkerFound, scan.Exhausted, scan.LimitHit,
                ids.Guild, ids.Channel, ids.Member);
            if (scan.Outcome is SummaryHistoryOutcome.Enough or SummaryHistoryOutcome.WholeHistory)
            {
                // Grounded: older reply targets come from what this scan already read (or what Discord sent with the reply).
                if (mode == SummaryGenerationMode.Grounded)
                    replyContext = SummaryGrounded.ContextCandidates(scan.MemberMessages, scan.Read);
                names = await discord.ResolveNamesAsync(request.Guild, [.. scan.MemberMessages, .. replyContext], deadline.Token);
            }
        }

        switch (scan.Outcome)
        {
            case SummaryHistoryOutcome.NoAccess or SummaryHistoryOutcome.Failed:
                await responder.ReplyPrivateAsync(T(scan.Outcome == SummaryHistoryOutcome.NoAccess ? "summary.bot_no_access" : "summary.fetch_failed") + TraceLine(request, trace));
                return Remember(SummaryOutcome.FetchFailed);
            case SummaryHistoryOutcome.LimitHit:
                // Neither enough new messages, nor an earlier summary, nor the channel's start within the limit: never guess.
                await responder.ReplyPrivateAsync(T("summary.history_unknown"));
                return SummaryOutcome.HistoryUnknown;
            case SummaryHistoryOutcome.AfterEarlierSummary:
                await responder.ReplyPrivateAsync(T("summary.not_enough_new", scan.EligibleCount, settings.MaxMessages - scan.EligibleCount));
                return SummaryOutcome.NotEnoughNewMessages;
        }

        SummarySourceMessage Named(SummarySourceMessage m) => names.Authors.TryGetValue(m.AuthorId, out var name) ? m with { AuthorName = name } : m;
        var selected = scan.MemberMessages.Select(Named).ToList();

        // The first request of this run, built by the mode chosen at the start (the other mode's input is never built). Legacy:
        // the only request. Grounded: the generator's draft, which a second and last request reviews if it passes the reader.
        SummaryPromptMessages prompt;
        SummaryGroundedInput? grounded = null;
        int messageCount, truncatedCount, droppedCount, emptyCount;
        if (mode == SummaryGenerationMode.Grounded)
        {
            grounded = SummaryGrounded.Build(selected, replyContext.Select(Named).ToList(), names.Mentions, request.Zone, settings.MaxMessages);
            prompt = SummaryGroundedPrompt.Build(grounded, request.Zone, settings.GroundedMaxOutputTokens, settings.GroundedGenerator);
            (messageCount, truncatedCount, droppedCount, emptyCount) =
                (grounded.MessageCount, grounded.TruncatedMessageCount, grounded.DroppedForSizeCount, grounded.EmptyMessageCount);
        }
        else
        {
            var transcript = SummaryTranscript.Build(selected, names.Mentions, request.Zone, settings.MaxMessages);
            prompt = SummaryPrompt.Build(transcript.Text);
            (messageCount, truncatedCount, droppedCount, emptyCount) =
                (transcript.MessageCount, transcript.TruncatedMessageCount, transcript.DroppedForSizeCount, transcript.EmptyMessageCount);
        }

        if (messageCount < settings.MinMessages)
        {
            // Normal member messages that came back completely empty: Discord withholds content without Message Content access.
            var withheld = emptyCount > 0 && await discord.HasMessageContentAccessAsync() == false;
            LogTooFew(logger, trace, messageCount, emptyCount, withheld, ids.Guild, ids.Channel, ids.Member);
            await responder.ReplyPrivateAsync(withheld ? T("summary.content_unavailable") : T("summary.not_enough", settings.MinMessages));
            return Remember(withheld ? SummaryOutcome.ContentUnavailable : SummaryOutcome.NotEnoughMessages);
        }

        var result = await ai.SummarizeAsync(prompt, CancellationToken.None);
        string? summary = null;
        SummaryAiResult? review = null;
        var draftAccepted = false;
        if (result.Succeeded && grounded is null)
        {
            summary = SummaryOutput.Normalize(result.Text, cutOff: result.FinishReason == "length");
        }
        else if (result.Succeeded && grounded is not null)
        {
            // Structural check only (sources exist, quotes are intact, hidden quotes stay hidden), on every item the model wrote.
            // A refused draft ends the run: it is not repaired, retried or reviewed.
            var draft = SummaryGroundedAnswer.Read(result.Text, result.FinishReason, grounded);
            LogGrounded(logger, trace, "generator", draft.Failure, grounded.Records.Count, grounded.ReplyCount, grounded.UnavailableReplyCount,
                grounded.ContextCount, draft.EvidenceCount, draft.SpoilerClaimCount, draft.CandidatePoints, draft.CandidatePlans,
                draft.ShownPoints, draft.ShownPlans, ids.Guild, ids.Channel);
            draftAccepted = draft.Succeeded;
            if (draftAccepted)
            {
                // The second and LAST request of the run: a factual review of the accepted draft against the same records. Its
                // answer replaces the draft and goes through the same reader from scratch; whatever happens, there is no third.
                review = await ai.SummarizeAsync(
                    SummaryGroundedReviewPrompt.Build(grounded, result.Text!, request.Zone, settings.GroundedMaxOutputTokens, settings.GroundedReviewer),
                    CancellationToken.None);
                if (review.Succeeded)
                {
                    var answer = SummaryGroundedAnswer.Read(review.Text, review.FinishReason, grounded);
                    summary = answer.Markdown;
                    LogGrounded(logger, trace, "reviewer", answer.Failure, grounded.Records.Count, grounded.ReplyCount, grounded.UnavailableReplyCount,
                        grounded.ContextCount, answer.EvidenceCount, answer.SpoilerClaimCount, answer.CandidatePoints, answer.CandidatePlans,
                        answer.ShownPoints, answer.ShownPlans, ids.Guild, ids.Channel);
                }
            }
        }

        // The stage that decided the outcome: a provider failure of that stage, or an answer the reader refused.
        var decisive = review ?? result;
        var failure = !decisive.Succeeded ? decisive.Failure
            : summary is not null ? SummaryAiFailure.None
            : grounded is null ? SummaryAiFailure.EmptyOutput : SummaryAiFailure.InvalidResponse;
        LogInference(logger, trace, failure == SummaryAiFailure.None ? "ok" : "failed", failure, decisive.HttpStatus, decisive.ProviderError,
            grounded is null ? ai.Model : settings.GroundedGeneratorModel,
            messageCount, truncatedCount, droppedCount, result.Usage.InputTokens,
            result.Usage.OutputTokens, result.Usage.ReasoningTokens, result.FinishReason, (long)result.Latency.TotalMilliseconds,
            ids.Guild, ids.Channel, ids.Member, mode, modeSource);
        if (grounded is not null)
        {
            // Which stage ended the run, if any: a failed or refused generator answer, or a failed or refused review.
            var failedStage = failure == SummaryAiFailure.None ? "none" : review is null ? "generator" : "reviewer";
            LogPipeline(logger, trace, review is null ? 1 : 2, draftAccepted, failedStage,
                settings.GroundedGeneratorModel, Count(result.Usage.InputTokens), Count(result.Usage.OutputTokens), Count(result.Usage.ReasoningTokens), (long)result.Latency.TotalMilliseconds,
                review is null ? "not_called" : settings.GroundedReviewerModel,
                review is null ? "not_called" : Count(review.Usage.InputTokens), review is null ? "not_called" : Count(review.Usage.OutputTokens),
                review is null ? "not_called" : Count(review.Usage.ReasoningTokens), review is null ? 0 : (long)review.Latency.TotalMilliseconds,
                (long)(result.Latency + (review?.Latency ?? TimeSpan.Zero)).TotalMilliseconds, ids.Guild, ids.Channel);
        }

        if (summary is null)
        {
            ticket.End(SummaryRunEnd.InferenceFailed);
            await responder.ReplyPrivateAsync(T(failure switch
            {
                SummaryAiFailure.Timeout => "summary.timeout",
                SummaryAiFailure.RateLimited => "summary.provider_busy",
                SummaryAiFailure.EmptyOutput or SummaryAiFailure.InvalidResponse => "summary.empty_output",
                _ => "summary.provider_unavailable",
            }) + TraceLine(request, trace));
            return Remember(SummaryOutcome.AiFailed, failure);
        }

        var parts = SummaryOutput.Split(summary);
        if (!await responder.PostPublicAsync(parts))
        {
            // Not a successful summary (nothing is in the channel): only the short failure cooldown.
            ticket.End(SummaryRunEnd.InferenceFailed);
            LogPostFailed(logger, trace, ids.Guild, ids.Channel);
            return Remember(SummaryOutcome.PostFailed);
        }

        ticket.End(SummaryRunEnd.Completed); // a summary is in the channel: the full member and channel cooldowns
        LogPosted(logger, trace, parts.Count, ids.Guild, ids.Channel);
        return Remember(SummaryOutcome.Posted);
    }

    private SummaryOutcome Remember(SummaryOutcome outcome, SummaryAiFailure failure = SummaryAiFailure.None)
    {
        Volatile.Write(ref _last, new SummaryLastRun(outcome, failure, clock.GetUtcNow()));
        return outcome;
    }

    /// <summary>A token count as the provider reported it; "unknown" when it did not (never a made-up zero).</summary>
    private static string Count(int? tokens) => tokens?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private string TraceLine(SummaryRequest request, string trace) => "\n" + localizer.Get(request.Language, "error.trace_code", trace);

    private string Seconds(DateTimeOffset? until) => SecondsLeft(until).ToString(CultureInfo.InvariantCulture);

    private int SecondsLeft(DateTimeOffset? until) =>
        until is { } at ? Math.Max(1, (int)Math.Ceiling((at - clock.GetUtcNow()).TotalSeconds)) : 1;

    /// <summary>"1 dk 18 sn", "2 dk", "45 sn" (rounded up to whole seconds, on the injected clock).</summary>
    private string Duration(string language, DateTimeOffset? until)
    {
        var total = SecondsLeft(until);
        var (minutes, seconds) = (total / 60, total % 60);
        return minutes == 0 ? localizer.Get(language, "summary.duration.seconds", seconds)
            : seconds == 0 ? localizer.Get(language, "summary.duration.minutes", minutes)
            : localizer.Get(language, "summary.duration.minutes_seconds", minutes, seconds);
    }

    /// <summary>
    /// The allowed roles by their current names from the guild cache, bold, defused (no mention, no markdown break-out); a
    /// role that no longer exists is shown by id and logged — the others are still listed.
    /// </summary>
    private string RoleList(SummaryRequest request, ISummaryDiscord discord, IReadOnlyList<ulong> roles, string trace)
    {
        var shown = new List<string>(roles.Count);
        foreach (var role in roles)
        {
            if (discord.RoleName(request.Guild, role) is { Length: > 0 } name)
            {
                shown.Add("**" + DiscordText.Untrusted(name, 100) + "**");
                continue;
            }

            LogMissingRole(logger, trace, role, request.Guild.Value);
            shown.Add(localizer.Get(request.Language, "summary.role_fallback", role.ToString(CultureInfo.InvariantCulture)));
        }

        return string.Join(", ", shown);
    }

    // Logs carry ids, counts, usage, latency and outcomes only — never message text, names, the prompt or the answer.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary [{Trace}] not configured: " + SummaryOptions.ApiKeyVariable + " is not set guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogNotConfigured(ILogger logger, string trace, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] refused: {Outcome} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogRefused(ILogger logger, string trace, SummaryOutcome outcome, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] throttled: {Admission} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogThrottled(ILogger logger, string trace, SummaryAdmission admission, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] history {Outcome} history_page_count={Pages} eligible_message_count={Eligible} " +
        "summary_marker_found={Marker} history_exhausted={Exhausted} history_limit_hit={LimitHit} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogHistory(ILogger logger, string trace, SummaryHistoryOutcome outcome, int pages, int eligible, bool marker,
        bool exhausted, bool limitHit, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary [{Trace}] configured allowed role {Role} does not exist in guild {Guild}")]
    private static partial void LogMissingRole(ILogger logger, string trace, ulong role, ulong guild);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] no AI request: message_count={Count} empty_message_count={Empty} " +
        "content_withheld={Withheld} guild={Guild} channel={Channel} invoker={Invoker}")]
    private static partial void LogTooFew(ILogger logger, string trace, int count, int empty, bool withheld, ulong guild, ulong channel, ulong invoker);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] inference {Result} failure={Failure} status={Status} provider_error={ProviderError} " +
        "model={Model} message_count={Count} truncated_message_count={Truncated} dropped_message_count={Dropped} input_tokens={Input} " +
        "output_tokens={Output} reasoning_tokens={Reasoning} finish_reason={Finish} latency_ms={Latency} guild={Guild} channel={Channel} invoker={Invoker} " +
        "generation_mode={Mode} mode_source={ModeSource}")]
    private static partial void LogInference(ILogger logger, string trace, string result, SummaryAiFailure failure, int? status, string? providerError,
        string model, int count, int truncated, int dropped, int? input, int? output, int? reasoning, string? finish, long latency,
        ulong guild, ulong channel, ulong invoker, SummaryGenerationMode mode, SummaryModeSource modeSource);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] grounded stage={Stage} validation={Validation} source_count={Sources} reply_count={Replies} " +
        "reply_unavailable_count={Unavailable} context_count={Context} evidence_count={Evidence} spoiler_claim_count={SpoilerClaims} " +
        "candidate_point_count={CandidatePoints} candidate_plan_count={CandidatePlans} shown_point_count={ShownPoints} shown_plan_count={ShownPlans} " +
        "guild={Guild} channel={Channel}")]
    private static partial void LogGrounded(ILogger logger, string trace, string stage, SummaryGroundedFailure validation, int sources, int replies, int unavailable,
        int context, int evidence, int spoilerClaims, int candidatePoints, int candidatePlans, int shownPoints, int shownPlans, ulong guild, ulong channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] grounded pipeline inference_count={Inferences} draft_accepted={DraftAccepted} failed_stage={FailedStage} " +
        "generator_model={GeneratorModel} generator_input_tokens={GeneratorInput} generator_output_tokens={GeneratorOutput} " +
        "generator_reasoning_tokens={GeneratorReasoning} generator_latency_ms={GeneratorLatency} " +
        "reviewer_model={ReviewerModel} reviewer_input_tokens={ReviewerInput} reviewer_output_tokens={ReviewerOutput} " +
        "reviewer_reasoning_tokens={ReviewerReasoning} reviewer_latency_ms={ReviewerLatency} total_ai_latency_ms={TotalLatency} guild={Guild} channel={Channel}")]
    private static partial void LogPipeline(ILogger logger, string trace, int inferences, bool draftAccepted, string failedStage, string generatorModel, string generatorInput,
        string generatorOutput, string generatorReasoning, long generatorLatency, string reviewerModel, string reviewerInput, string reviewerOutput,
        string reviewerReasoning, long reviewerLatency, long totalLatency, ulong guild, ulong channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Summary [{Trace}] Discord refused the public post guild={Guild} channel={Channel}")]
    private static partial void LogPostFailed(ILogger logger, string trace, ulong guild, ulong channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Summary [{Trace}] posted parts={PartCount} guild={Guild} channel={Channel}")]
    private static partial void LogPosted(ILogger logger, string trace, int partCount, ulong guild, ulong channel);
}
