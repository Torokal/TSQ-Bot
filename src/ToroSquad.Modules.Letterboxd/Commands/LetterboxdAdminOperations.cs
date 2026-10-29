using System.Globalization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Letterboxd.Application;
using ToroSquad.Modules.Letterboxd.Domain;
using ToroSquad.Modules.Letterboxd.Providers;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Letterboxd.Commands;

/// <summary>
/// <c>/tsq-admin modul:letterboxd</c> — Manage Server: the shared command is hidden by default_member_permissions AND every
/// operation is re-authorized in the service. Works while the module is disabled so the channel can be set and checked
/// before activation (/modules enable letterboxd). Every answer is private. Nothing here requests the feed or posts to a
/// channel; there is deliberately no operation to post an entry by hand or to follow another account from Discord.
/// </summary>
public sealed class LetterboxdAdminOperations(LetterboxdConfigService config) : IAdminFormHandler
{
    public static readonly AdminModule Definition = AdminModule.For<LetterboxdAdminOperations>(LetterboxdModule.AdminId, LetterboxdModule.ModuleIdTyped)
        .Op("configure", (h, c) => h.ConfigureAsync(c), AdminFields.Channel)
        .Op("pause", (h, c) => h.PauseAsync(c, true))
        .Op("resume", (h, c) => h.PauseAsync(c, false))
        .Op("preview", (h, c) => h.PreviewAsync(c))
        .Op("status", (h, c) => h.StatusAsync(c))
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Build();

    /// <summary>With <c>kanal</c> at once; without it a channel select (nothing is saved until a channel is picked).</summary>
    public async Task ConfigureAsync(AdminCall call)
    {
        if (call.Args.ChannelId is not { } channel)
        {
            await AdminForms.PickChannelAsync(call, "admin.letterboxd.configure.pick");
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    public async Task OnFormAsync(AdminCall call, string action)
    {
        if (action != AdminForms.ChannelAction || AdminForms.ChosenChannel(call) is not { } channel)
        {
            await call.ReplyTextAsync("admin.error.channel_invalid");
            return;
        }

        if (await AdminForms.ClaimAsync(call))
            await call.FinishAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    public async Task PauseAsync(AdminCall call, bool paused)
    {
        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetPausedAsync(call.Actor, paused, CancellationToken.None));
    }

    public async Task PreviewAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, preview) = await config.PreviewAsync(call.Actor, call.Language, CancellationToken.None);
        if (preview is null)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var note = call.T(preview.Synthetic ? "letterboxd.preview.synthetic" : "letterboxd.preview.real");
        var embed = preview.Message.Embed!;
        await call.ReplyEmbedAsync(embed with { Description = "_" + note + "_\n\n" + embed.Description });
    }

    public async Task StatusAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, status) = await config.StatusAsync(call.Actor, CancellationToken.None);
        if (status is null)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var feed = status.Feed;
        var channel = status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : call.T("letterboxd.status.no_channel");
        if (status.Paused)
            channel += " · " + call.T("letterboxd.status.paused");
        var account = DiscordText.UntrustedPlain(status.Account, 40);
        if (status.GuildAccount is { } guildAccount && guildAccount != status.Account)
            account += " · " + call.T("letterboxd.status.account_changed", DiscordText.UntrustedPlain(guildAccount, 40));
        var capabilities = string.Join("\n", status.Capabilities.Select(cap => call.T("letterboxd.status.capability_line", cap.Key, cap.State.ToString())));
        var fields = new List<EmbedField>
        {
            new(call.T("letterboxd.status.account"), account, true),
            new(call.T("letterboxd.status.mode"), call.T(status.Mode switch
            {
                LetterboxdMode.Off => "letterboxd.status.mode_off",
                LetterboxdMode.DryRun => "letterboxd.status.mode_dry",
                _ => status.EffectiveMode == LetterboxdMode.Live ? "letterboxd.status.mode_live" : "letterboxd.status.mode_live_delivery_dry",
            }), true),
            new(call.T("letterboxd.status.module"), call.T(status.ModuleEnabled ? "modules.state_on" : "modules.state_off"), true),
            new(call.T("letterboxd.status.channel"), channel, true),
            new(call.T("letterboxd.status.feed"), feed?.LastAttemptAt is { } attempt
                ? call.T("letterboxd.status.feed_value", feed.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—", ((FeedOutcome)feed.LastOutcome).ToString(),
                    feed.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—", DiscordText.Timestamp(attempt, 'R'), feed.ConsecutiveFailures,
                    feed.NextPollAt is { } next ? DiscordText.Timestamp(next, 'R') : "—")
                : call.T("letterboxd.status.feed_never"), false),
            new(call.T("letterboxd.status.baseline"), feed?.BaselineAt is { } b
                ? call.T("letterboxd.status.baseline_value", DiscordText.Timestamp(b, 'f'), feed.LastItemCount,
                    feed.ModeSince is { } since ? DiscordText.Timestamp(since, 'f') : "—")
                : call.T("letterboxd.status.baseline_none"), false),
            new(call.T("letterboxd.status.warnings"), Warnings(call, status), false),
            new(call.T("letterboxd.status.ratings_source"), call.T("letterboxd.status.ratings_source_value"), false),
            new(call.T("letterboxd.status.capabilities"), capabilities, false),
            new(call.T("letterboxd.status.coverage"), call.T(LetterboxdCapabilities.FullCoverage ? "letterboxd.status.coverage_full" : "letterboxd.status.coverage_partial"), false),
        };
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("letterboxd.status.title"), null, null, fields, null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task DoctorAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, checks) = await config.DoctorAsync(call.Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var lines = checks.Select(c => $"{Icon(c.State)} **{call.T(c.LabelKey)}** — {call.T(c.DetailKey, c.Args.ToArray())}");
        var failed = checks.Count(c => c.State == LetterboxdCheckState.Problem);
        var summary = call.T(failed == 0 ? "letterboxd.doctor.summary_ok" : "letterboxd.doctor.summary_failed", failed);
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("letterboxd.doctor.title"), Clip(summary + "\n\n" + string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, failed == 0 ? ToroInteractionModule.NeutralColor : ToroInteractionModule.WarningColor));
    }

    /// <summary>Stale source, potential coverage gap and identity conflicts — each its own line, none implies another.</summary>
    private static string Warnings(AdminCall call, LetterboxdStatus status)
    {
        var lines = new List<string>();
        if (status.Stale)
            lines.Add(call.T("letterboxd.status.stale"));
        if (status.Feed?.CoverageGapAt is { } gap)
            lines.Add(call.T("letterboxd.status.gap", DiscordText.Timestamp(gap, 'R')));
        if (status.Feed?.LastConflictCount is > 0 and var conflicts)
            lines.Add(call.T("letterboxd.status.conflicts", conflicts));
        return lines.Count == 0 ? call.T("letterboxd.status.no_warnings") : string.Join("\n", lines);
    }

    private static string Icon(LetterboxdCheckState state) => state switch
    {
        LetterboxdCheckState.Ok => "✅",
        LetterboxdCheckState.Warning => "⚠️",
        LetterboxdCheckState.Problem => "❌",
        _ => "ℹ️",
    };

    private static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, max - 2));
        return (cut > 0 ? text[..cut] : text[..(max - 1)]) + "…";
    }
}
