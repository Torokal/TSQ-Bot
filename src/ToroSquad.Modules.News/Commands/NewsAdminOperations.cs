using System.Globalization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Providers;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.News.Commands;

/// <summary>
/// <c>/tsq-admin modul:news</c> — Manage Server: the shared command is hidden by default_member_permissions AND every operation
/// is re-authorized in the service. Works while the module is disabled so the channel can be set and checked before activation
/// (/modules enable news). Every answer is private. Nothing here requests the feed or posts to a channel; there is deliberately
/// no operation to add or remove a news link by hand.
/// </summary>
public sealed class NewsAdminOperations(NewsConfigService config) : IAdminFormHandler
{
    public static readonly AdminModule Definition = AdminModule.For<NewsAdminOperations>(NewsModule.AdminId, NewsModule.ModuleIdTyped)
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
            await AdminForms.PickChannelAsync(call, "admin.news.configure.pick");
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

        var note = call.T(preview.Synthetic ? "news.preview.synthetic" : "news.preview.real");
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
        var channel = status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : call.T("news.status.no_channel");
        if (status.Paused)
            channel += " · " + call.T("news.status.paused");
        var fields = new List<EmbedField>
        {
            new(call.T("news.status.mode"), call.T(status.Mode switch
            {
                NewsMode.Off => "news.status.mode_off",
                NewsMode.DryRun => "news.status.mode_dry",
                _ => status.DryRun ? "news.status.mode_live_delivery_dry" : "news.status.mode_live",
            }), true),
            new(call.T("news.status.module"), call.T(status.ModuleEnabled ? "modules.state_on" : "modules.state_off"), true),
            new(call.T("news.status.channel"), channel, true),
            new(call.T("news.status.feed"), feed?.LastAttemptAt is { } attempt
                ? call.T("news.status.feed_value", feed.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—", ((FeedOutcome)feed.LastOutcome).ToString(),
                    feed.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—", DiscordText.Timestamp(attempt, 'R'), feed.ConsecutiveFailures,
                    feed.NextPollAt is { } next ? DiscordText.Timestamp(next, 'R') : "—")
                : call.T("news.status.feed_never"), false),
            new(call.T("news.status.items"), feed is null ? "—" : call.T("news.status.items_value", feed.LastItemCount, feed.LastRelevantCount,
                feed.LastDeliveryStagedAt is { } d ? DiscordText.Timestamp(d, 'R') : "—"), false),
            new(call.T("news.status.roster"), status.Roster is { } r
                ? call.T(status.RosterFresh ? "news.status.roster_value" : "news.status.roster_stale", r.Source, r.Players.Count, DiscordText.Timestamp(r.VerifiedAt, 'R'))
                : call.T("news.status.roster_none"), false),
            new(call.T("news.status.coverage"), call.T("news.status.coverage_value"), false),
        };
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("news.status.title"), null, null, fields, null, null, ToroInteractionModule.NeutralColor));
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
        var failed = checks.Count(c => c.State == NewsCheckState.Problem);
        var summary = call.T(failed == 0 ? "news.doctor.summary_ok" : "news.doctor.summary_failed", failed);
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("news.doctor.title"), Clip(summary + "\n\n" + string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, failed == 0 ? ToroInteractionModule.NeutralColor : ToroInteractionModule.WarningColor));
    }

    private static string Icon(NewsCheckState state) => state switch
    {
        NewsCheckState.Ok => "✅",
        NewsCheckState.Warning => "⚠️",
        NewsCheckState.Problem => "❌",
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
