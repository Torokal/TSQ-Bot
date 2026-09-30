using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Providers;
using DiscordPermission = Discord.GuildPermission;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.News.Commands;

/// <summary>
/// /news-admin — Manage Server: hidden by default_member_permissions AND re-authorized in the service. Works while the module is
/// disabled so the channel can be set and checked before activation (/modules enable news). Every answer is private. Nothing
/// here requests the feed or posts to a channel; there is deliberately no command to add or remove a news link by hand.
/// </summary>
[ToroModule(NewsModule.ModuleIdValue, AllowWhenDisabled = true)]
[Group("news-admin", "Aurora HLTV news (admins)")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class NewsAdminCommands(InteractionServices services, NewsConfigService config) : ToroInteractionModule(services)
{
    [SlashCommand("configure", "Channel for the Aurora HLTV news cards")]
    public async Task ConfigureAsync([Summary("channel", "News channel"), ChannelTypes(ChannelType.Text, ChannelType.News)] IChannel channel)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await config.SetChannelAsync(Actor, channel.Id, CancellationToken.None));
    }

    [SlashCommand("pause", "Stop posting news (news of the pause is skipped)")]
    public async Task PauseAsync()
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await config.SetPausedAsync(Actor, true, CancellationToken.None));
    }

    [SlashCommand("resume", "Post news again (only news from now on)")]
    public async Task ResumeAsync()
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await config.SetPausedAsync(Actor, false, CancellationToken.None));
    }

    [SlashCommand("preview", "Show a news card privately (nothing is posted)")]
    public async Task PreviewAsync()
    {
        await DeferEphemeralAsync();
        var (auth, preview) = await config.PreviewAsync(Actor, await LangAsync(), CancellationToken.None);
        if (preview is null)
        {
            await ReplyResultAsync(auth);
            return;
        }

        var note = await T(preview.Synthetic ? "news.preview.synthetic" : "news.preview.real");
        var embed = preview.Message.Embed!;
        await ReplyEmbedAsync(embed with { Description = "_" + note + "_\n\n" + embed.Description });
    }

    [SlashCommand("status", "News module settings and the feed's state")]
    public async Task StatusAsync()
    {
        await DeferEphemeralAsync();
        var (auth, status) = await config.StatusAsync(Actor, CancellationToken.None);
        if (status is null)
        {
            await ReplyResultAsync(auth);
            return;
        }

        var lang = await LangAsync();
        var feed = status.Feed;
        var channel = status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : await T("news.status.no_channel");
        if (status.Paused)
            channel += " · " + await T("news.status.paused");
        var fields = new List<EmbedField>
        {
            new(await T("news.status.mode"), await T(status.Mode switch
            {
                NewsMode.Off => "news.status.mode_off",
                NewsMode.DryRun => "news.status.mode_dry",
                _ => status.DryRun ? "news.status.mode_live_delivery_dry" : "news.status.mode_live",
            }), true),
            new(await T("news.status.module"), Localizer.Get(lang, status.ModuleEnabled ? "modules.state_on" : "modules.state_off"), true),
            new(await T("news.status.channel"), channel, true),
            new(await T("news.status.feed"), feed?.LastAttemptAt is { } attempt
                ? await T("news.status.feed_value", feed.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—", ((FeedOutcome)feed.LastOutcome).ToString(),
                    feed.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—", DiscordText.Timestamp(attempt, 'R'), feed.ConsecutiveFailures,
                    feed.NextPollAt is { } next ? DiscordText.Timestamp(next, 'R') : "—")
                : await T("news.status.feed_never"), false),
            new(await T("news.status.items"), feed is null ? "—" : await T("news.status.items_value", feed.LastItemCount, feed.LastRelevantCount,
                feed.LastDeliveryStagedAt is { } d ? DiscordText.Timestamp(d, 'R') : "—"), false),
            new(await T("news.status.roster"), status.Roster is { } r
                ? await T(status.RosterFresh ? "news.status.roster_value" : "news.status.roster_stale", r.Source, r.Players.Count, DiscordText.Timestamp(r.VerifiedAt, 'R'))
                : await T("news.status.roster_none"), false),
            new(await T("news.status.coverage"), await T("news.status.coverage_value"), false),
        };
        await ReplyEmbedAsync(new MessageEmbed(await T("news.status.title"), null, null, fields, null, null, NeutralColor));
    }

    [SlashCommand("doctor", "Check channel, permissions, feed, roster and coverage")]
    public async Task DoctorAsync()
    {
        await DeferEphemeralAsync();
        var (auth, checks) = await config.DoctorAsync(Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await ReplyResultAsync(auth);
            return;
        }

        var lang = await LangAsync();
        var lines = checks.Select(c => $"{Icon(c.State)} **{Localizer.Get(lang, c.LabelKey)}** — {Localizer.Get(lang, c.DetailKey, c.Args.ToArray())}");
        var failed = checks.Count(c => c.State == NewsCheckState.Problem);
        var summary = await T(failed == 0 ? "news.doctor.summary_ok" : "news.doctor.summary_failed", failed);
        await ReplyEmbedAsync(new MessageEmbed(await T("news.doctor.title"), Clip(summary + "\n\n" + string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, failed == 0 ? NeutralColor : WarningColor));
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
