using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Domain.Games;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Bot Updates end to end against the real SQLite database, the production wiring, the real outbox and a scripted
/// Steam: baseline, one card per update, classification, corrections as silent edits, pause/resume, channel and game
/// changes, bounded catch-up, retention, module gate, failures and backoff across restarts, atomic rounds, ambiguous sends,
/// admin authorization, and a second made-up game and provider added by registration alone. Nothing here touches the network.
/// </summary>
public sealed class UpdatesTests
{
    private static readonly GuildId Guild = UpdatesRig.Guild;
    private static readonly GuildId OtherGuild = UpdatesRig.OtherGuild;
    private static readonly ChannelId Channel = UpdatesRig.Channel;
    private static readonly ChannelId Channel2 = UpdatesRig.Channel2;
    private static readonly DateTimeOffset Start = UpdatesRig.Start;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static SteamPost Update(long gid, DateTimeOffset at, string title = "Counter-Strike 2 Update") => SteamNews.Update(gid, at, title);

    private static SteamPost Old => SteamNews.Announcement(1, Start.AddDays(-3));

    /// <summary>A live rig whose baseline (one old announcement) is already established.</summary>
    private static async Task<UpdatesRig> BaselinedAsync(Dictionary<string, string?>? extra = null, Action<IServiceCollection>? replace = null)
    {
        var rig = await UpdatesRig.CreateAsync(extra: extra, replace: replace);
        rig.Steam.Serve([Old]);
        (await rig.PollAsync()).Should().Be(1);
        (await rig.StateAsync())!.BaselineAt.Should().NotBeNull();
        return rig;
    }

    private static string Link(long gid) => "(" + SteamNews.CardUrl(gid.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")";

    // ---------- baseline and identity ----------

    [Fact]
    public async Task The_first_answer_is_a_baseline_and_a_later_update_is_posted_once_across_repolls_and_restarts()
    {
        await using var rig = await UpdatesRig.CreateAsync();
        rig.Steam.Serve([Update(100, Start.AddDays(-1)), SteamNews.Announcement(101, Start.AddDays(-2))]);
        (await rig.PollAsync()).Should().Be(1);

        (await rig.OutboxAsync()).Should().BeEmpty("existing updates are never dumped into the channel");
        (await rig.ItemsAsync()).Should().HaveCount(2).And.OnlyContain(i => i.Baseline);
        (await rig.StateAsync())!.BaselineAt.Should().Be(Start);

        var published = rig.Now.AddMinutes(2);
        rig.Steam.Serve([Update(102, published), SteamNews.Announcement(103, published), Update(100, Start.AddDays(-1)), SteamNews.Announcement(101, Start.AddDays(-2))]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Channel.Should().Be(Channel);
        message.Pinged.Should().BeFalse();
        message.Message.Content.Should().BeNull();
        message.Message.Mentions.PingsAnything.Should().BeFalse();
        var embed = message.Message.Embed!;
        embed.Title.Should().Be("🛠️ CS2 Güncellemesi");
        embed.Description.Should().Be("**Counter\\-Strike 2 Update**\n\nYeni Counter-Strike 2 güncellemesi yayınlandı.\n\n" +
                                      "[Steam'de Güncelleme Notlarını Gör](https://store.steampowered.com/news/externalpost/steam_community_announcements/102)");
        embed.Url.Should().Be("https://store.steampowered.com/news/externalpost/steam_community_announcements/102");
        embed.Footer.Should().Be("Kaynak: Steam");
        embed.Timestamp.Should().Be(published);
        embed.Fields.Should().BeEmpty();
        embed.ThumbnailUrl.Should().BeNull("no image is taken from the post");
        var row = (await rig.OutboxAsync()).Should().ContainSingle().Subject;
        row.SourceKey.Should().Be("steam:730:102");
        row.Kind.Should().Be("update:cs2");
        row.PayloadJson.Should().NotContain("Synthetic change", "the patch text never leaves the classifier");

        await rig.PollAsync();
        await rig.PollAsync();
        var restarted = ActivatorUtilities.CreateInstance<UpdatesPoller>(rig.Host.Services);
        await rig.PollAsync(TimeSpan.FromHours(2), restarted);
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle("re-polls and a restart never post the same update again");
        (await rig.OutboxAsync()).Should().ContainSingle();
        restarted.Dispose();
    }

    [Fact]
    public async Task A_failed_empty_or_foreign_first_answer_establishes_no_baseline()
    {
        await using var rig = await UpdatesRig.CreateAsync();
        rig.Steam.Respond = _ => SteamNews.Status(HttpStatusCode.ServiceUnavailable);
        await rig.PollAsync();
        var state = await rig.StateAsync();
        state!.BaselineAt.Should().BeNull();
        state.ConsecutiveFailures.Should().Be(1);
        UpdatesConfigService.OutcomeName(state).Should().Be("ServerError");

        rig.Steam.Respond = _ => SteamNews.Ok("{\"appnews\":{\"appid\":730,\"newsitems\":[");
        await rig.PollAsync();
        (await rig.StateAsync())!.BaselineAt.Should().BeNull("malformed");

        rig.Steam.Respond = _ => SteamNews.Ok(SteamNews.Json([Update(5, Start)], appId: 570));
        await rig.PollAsync();
        state = await rig.StateAsync();
        state!.BaselineAt.Should().BeNull("an answer for another app");
        UpdatesConfigService.OutcomeName(state).Should().Be("UnexpectedSchema");
        (await rig.ItemsAsync()).Should().BeEmpty();

        rig.Steam.Serve([]);
        await rig.PollAsync();
        state = await rig.StateAsync();
        state!.BaselineAt.Should().BeNull("an empty list proves nothing");
        state.ConsecutiveFailures.Should().Be(0, "an empty list is not a failure of the source");
        UpdatesConfigService.OutcomeName(state).Should().Be("SuccessEmpty");

        // The first real answer is the baseline — its update is NOT posted even though it is brand new.
        rig.Steam.Serve([Update(6, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        (await rig.StateAsync())!.BaselineAt.Should().NotBeNull();
        rig.Host.Transport.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Only_posts_classified_as_updates_are_posted_and_the_decision_is_stored_with_its_reason()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve(
        [
            Update(200, t),
            SteamNews.Announcement(201, t, "The Budapest Major Playoffs"),
            new SteamPost("202", "CS2 Workshop Update", t, SteamNews.Prose),
            new SteamPost("203", "Call for Submissions", t, SteamNews.Prose, SteamNews.PatchTag),
            SteamNews.Announcement(204, t, "An update on the schedule"),
        ]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(200));
        var items = (await rig.ItemsAsync()).ToDictionary(i => i.ExternalId);
        ((UpdateClassification)items["200"].Classification, items["200"].ClassificationReason).Should().Be((UpdateClassification.Update, "strong_title"));
        ((UpdateClassification)items["201"].Classification, items["201"].ClassificationReason).Should().Be((UpdateClassification.NotUpdate, "event_or_marketing_title"));
        ((UpdateClassification)items["202"].Classification, items["202"].ClassificationReason).Should().Be((UpdateClassification.Ambiguous, "conflicting_signals"));
        ((UpdateClassification)items["203"].Classification, items["203"].ClassificationReason).Should().Be((UpdateClassification.Ambiguous, "tagged_without_patch_notes"));
        ((UpdateClassification)items["204"].Classification, items["204"].ClassificationReason).Should().Be((UpdateClassification.NotUpdate, "update_word_only"));
        var state = await rig.StateAsync();
        (state!.LastItemCount, state.LastNewCount, state.LastUpdateCount, state.LastAmbiguousCount).Should().Be((5, 5, 1, 2));
        UpdatesConfigService.OutcomeName(state).Should().Be("SuccessItems");
        state.LastDiscoveredAt.Should().Be(rig.Now);

        await rig.PollAsync();
        UpdatesConfigService.OutcomeName((await rig.StateAsync())!).Should().Be("SuccessNoNewItems");
    }

    [Fact]
    public async Task Two_updates_with_the_same_title_are_two_cards_and_an_ambiguous_send_is_reconciled_to_its_own_message()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(300, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        var first = rig.Host.Transport.Messages.Single();

        rig.Steam.Serve([Update(300, t), Update(301, t.AddMinutes(1))]);
        await rig.PollAsync();
        rig.Host.Transport.ScriptAcceptedButTimedOut();
        await rig.DeliverAsync();
        for (var i = 0; i < 4; i++)
        {
            rig.Host.Clock.Advance(TimeSpan.FromMinutes(1));
            await rig.DeliverAsync();
        }

        var rows = await rig.OutboxAsync();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.Status == OutboxStatus.Sent);
        rows[1].DiscordMessageId.Should().NotBe(first.Id.Value, "the link in the description tells two same-titled cards apart");
        rig.Host.Transport.Messages.Should().HaveCount(2, "the title is not the identity, and there is no duplicate");
    }

    [Fact]
    public async Task A_post_without_a_publication_time_is_never_posted_automatically()
    {
        await using var rig = await BaselinedAsync();
        rig.Steam.Serve([Update(350, rig.Now) with { Published = null }]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0, "without a provider time 'published after the window started' cannot be shown");
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "350").PublishedAt.Should().BeNull("the first-seen time is never stored as the publication time");
    }

    // ---------- corrections ----------

    [Fact]
    public async Task A_corrected_title_edits_the_same_message_silently_and_only_when_the_card_changed()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(400, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1);

        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.EditCalls.Should().Be(0, "an unchanged post is not edited");

        // Only the text behind the card changed: re-evaluated, but the card is identical.
        rig.Steam.Serve([Update(400, t) with { Contents = SteamNews.PatchNotes + "[p]One more synthetic line[/p]" }]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.EditCalls.Should().Be(0);
        (await rig.OutboxAsync()).Single().EditPending.Should().BeFalse();
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "400").ContentChangedAt.Should().NotBeNull();

        rig.Steam.Serve([Update(400, t, "Counter-Strike 2 Pre-Release Update")]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "never a second message");
        rig.Host.Transport.EditCalls.Should().Be(1);
        var message = rig.Host.Transport.Messages.Single();
        message.Pinged.Should().BeFalse();
        var edit = message.Edits.Should().ContainSingle().Subject;
        edit.Embed!.Description.Should().Contain("Pre\\-Release Update").And.Contain(Link(400));
        edit.Mentions.PingsAnything.Should().BeFalse();
    }

    [Fact]
    public async Task A_deleted_card_is_not_recreated_by_a_later_correction()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(410, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.DeleteMessage(rig.Host.Transport.Messages.Single().Id);

        rig.Steam.Serve([Update(410, t, "Counter-Strike 2 Pre-Release Update")]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "a moderator's deletion is respected");
    }

    [Fact]
    public async Task A_post_that_becomes_an_update_later_is_posted_once()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([new SteamPost("420", "Release Notes for 10/1/2026", t, SteamNews.Prose)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0, "an update-like title alone is ambiguous");
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "420").Classification.Should().Be((int)UpdateClassification.Ambiguous);

        rig.Steam.Serve([new SteamPost("420", "Release Notes for 10/1/2026", t, SteamNews.PatchNotes, SteamNews.PatchTag)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(420));
        var item = (await rig.ItemsAsync()).Single(i => i.ExternalId == "420");
        item.Classification.Should().Be((int)UpdateClassification.Update);
        item.ClassificationChangedAt.Should().NotBeNull();

        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_baseline_post_stays_baseline_even_when_it_becomes_an_update()
    {
        await using var rig = await UpdatesRig.CreateAsync();
        rig.Steam.Serve([new SteamPost("430", "Release Notes for 10/1/2026", Start, SteamNews.Prose)]);
        await rig.PollAsync();
        rig.Steam.Serve([new SteamPost("430", "Release Notes for 10/1/2026", Start, SteamNews.PatchNotes, SteamNews.PatchTag)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0);
        (await rig.ItemsAsync()).Single().Should().Match<ToroSquad.Modules.Updates.Persistence.UpdatesItemEntity>(i => i.Baseline && i.Classification == (int)UpdateClassification.Update);
    }

    [Fact]
    public async Task A_posted_update_that_stops_being_an_update_keeps_its_card_and_shows_up_in_doctor()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(440, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.Steam.Serve([new SteamPost("440", "Community Sticker Capsule", t, SteamNews.Prose)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1);
        rig.Host.Transport.EditCalls.Should().Be(0, "the card is not rewritten into something that is not an update");
        rig.Host.Transport.DeleteCalls.Should().Be(0, "and never deleted automatically");
        rig.Host.Transport.Messages.Should().ContainSingle();
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "440").Classification.Should().Be((int)UpdateClassification.NotUpdate);
        var (_, checks) = await rig.ConfigAsync(c => c.DoctorAsync(TestHost.Admin(Guild), Ct));
        checks.Should().Contain(c => c.LabelKey == "updates.doctor.stored" && c.DetailKey == "updates.doctor.stored_withdrawn" && c.State == UpdatesCheckState.Warning);
    }

    [Fact]
    public async Task A_correction_never_creates_a_card_and_leaves_a_card_in_a_former_channel_alone()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(450, t), Update(451, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(2);

        rig.AllowChannel(Guild, Channel2);
        (await rig.ConfigAsync(c => c.SetChannelAsync(TestHost.Admin(Guild), Channel2.Value, Ct))).Succeeded.Should().BeTrue();
        rig.Steam.Serve([Update(450, t, "Counter-Strike 2 Pre-Release Update"), Update(451, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.EditCalls.Should().Be(0, "the card lives in the former channel and is left as it is");
        rig.Host.Transport.SendCalls.Should().Be(2, "and it is not posted again in the new channel");
        (await rig.OutboxAsync()).Should().HaveCount(2).And.OnlyContain(r => !r.EditPending && r.Status == OutboxStatus.Sent);

        // Back in the original channel, with the outbox row of one card gone (as after a purge of the guild's data).
        (await rig.ConfigAsync(c => c.SetChannelAsync(TestHost.Admin(Guild), Channel.Value, Ct))).Succeeded.Should().BeTrue();
        await rig.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.Where(o => o.SourceKey == "steam:730:451").ExecuteDeleteAsync(Ct));
        rig.Steam.Serve([Update(450, t, "Counter-Strike 2 Pre-Release Update"), Update(451, t, "Counter-Strike 2 Pre-Release Update")]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(2, "a correction only ever edits; without the card's outbox row it does nothing");
        (await rig.OutboxAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task The_last_card_in_status_is_a_card_that_reached_discord()
    {
        await using var rig = await BaselinedAsync();
        rig.Steam.Serve([Old, Update(460, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        (await rig.GameStatusAsync(Guild)).LastCardAt.Should().BeNull("planned is not sent");
        await rig.DeliverAsync();
        (await rig.GameStatusAsync(Guild)).LastCardAt.Should().Be(rig.Now);
    }

    // ---------- pause, channel, module, game ----------

    [Fact]
    public async Task Updates_of_a_pause_are_skipped_and_only_updates_after_resume_are_posted()
    {
        await using var rig = await BaselinedAsync();
        (await rig.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), true, Ct))).MessageKey.Should().Be("updates.pause.done");
        (await rig.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), true, Ct))).MessageKey.Should().Be("updates.pause.already");
        var during = rig.Now.AddMinutes(1);
        var before = rig.SteamRequests;
        rig.Steam.Serve([Update(500, during)]);
        (await rig.PollAsync()).Should().Be(0);
        rig.SteamRequests.Should().Be(before, "a paused guild alone causes no request");

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        (await rig.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), false, Ct))).MessageKey.Should().Be("updates.resume.done");
        rig.Steam.Serve([Update(500, during), Update(501, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(501), "the update of the pause is skipped, not caught up");
    }

    [Fact]
    public async Task A_queued_card_is_not_sent_once_the_guild_is_paused_and_a_sent_card_is_never_removed()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(510, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Steam.Serve([Update(510, t), Update(511, t.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), true, Ct));
        await rig.DeliverAsync();

        var rows = await rig.OutboxAsync();
        rows[0].Status.Should().Be(OutboxStatus.Sent);
        (rows[1].Status, rows[1].LastError).Should().Be((OutboxStatus.Cancelled, "paused"));
        rig.Host.Transport.Messages.Should().ContainSingle();
        rig.Host.Transport.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_channel_change_never_posts_again_and_new_updates_go_to_the_new_channel()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(520, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        var since = (await rig.GuildConfigAsync(Guild))!.LiveSince;

        rig.Steam.Serve([Update(520, t), Update(521, t.AddMinutes(1))]);
        await rig.PollAsync(); // 521 is queued for the old channel
        rig.AllowChannel(Guild, Channel2);
        rig.AllowChannel(Guild, Channel);
        (await rig.ConfigAsync(c => c.SetChannelAsync(TestHost.Admin(Guild), Channel2.Value, Ct))).Succeeded.Should().BeTrue();
        (await rig.GuildConfigAsync(Guild))!.LiveSince.Should().Be(since, "changing the channel does not restart the window");
        await rig.DeliverAsync();
        var queued = (await rig.OutboxAsync())[1];
        (queued.Status, queued.LastError).Should().Be((OutboxStatus.Cancelled, "channel_changed"));

        rig.Steam.Serve([Update(520, t), Update(521, t.AddMinutes(1)), Update(522, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().HaveCount(2);
        rig.Host.Transport.Messages[0].Channel.Should().Be(Channel);
        rig.Host.Transport.Messages[1].Channel.Should().Be(Channel2);
        rig.Host.Transport.Messages[1].Message.Embed!.Description.Should().Contain(Link(522));
        rig.Host.Transport.Messages.Should().NotContain(m => m.Channel == Channel2 && m.Message.Embed!.Description!.Contains(Link(520)), "the old update is not posted again");
        rig.Host.Transport.DeleteCalls.Should().Be(0, "old messages are neither moved nor deleted");
    }

    [Fact]
    public async Task The_module_gate_stops_polling_cancels_a_queued_card_and_re_enabling_starts_a_new_window()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        rig.Steam.Serve([Update(530, t)]);
        await rig.PollAsync();
        await rig.Host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "updates", false, Ct)).Succeeded.Should().BeTrue());
        await rig.DeliverAsync();
        var row = (await rig.OutboxAsync()).Single();
        (row.Status, row.LastError).Should().Be((OutboxStatus.Cancelled, "module_disabled"));
        var before = rig.SteamRequests;
        (await rig.PollAsync()).Should().Be(0);
        rig.SteamRequests.Should().Be(before);

        rig.Host.Clock.Advance(TimeSpan.FromHours(1));
        rig.Steam.Serve([Update(530, t), Update(531, rig.Now.AddMinutes(-30))]);
        await rig.Host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "updates", true, Ct)).Succeeded.Should().BeTrue());
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0, "an update published while the module was off is not posted on re-enabling");

        rig.Steam.Serve([Update(530, t), Update(531, rig.Now.AddMinutes(-30)), Update(532, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(532));
    }

    [Fact]
    public async Task Games_are_off_until_enabled_and_disabling_a_game_stops_its_requests_and_its_queued_cards()
    {
        await using var rig = await UpdatesRig.CreateAsync(configure: false);
        rig.AllowChannel(Guild, Channel);
        await rig.Host.InScopeAsync(async sp =>
        {
            (await sp.GetRequiredService<UpdatesConfigService>().SetChannelAsync(TestHost.Admin(Guild), Channel.Value, Ct)).Succeeded.Should().BeTrue();
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "updates", true, Ct)).Succeeded.Should().BeTrue();
        });
        rig.Steam.Serve([Old]);
        (await rig.PollAsync()).Should().Be(0, "no game is followed until an admin enables one");
        rig.SteamRequests.Should().Be(0);

        (await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), "cs2", true, Ct))).MessageKey.Should().Be("updates.game.enabled");
        (await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), "cs2", true, Ct))).MessageKey.Should().Be("updates.game.already_on");
        await rig.PollAsync();
        var t = rig.Now;
        rig.Steam.Serve([Old, Update(540, t)]);
        await rig.PollAsync(); // 540 is queued
        (await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), "cs2", false, Ct))).MessageKey.Should().Be("updates.game.disabled");
        await rig.DeliverAsync();
        var row = (await rig.OutboxAsync()).Single();
        (row.Status, row.LastError).Should().Be((OutboxStatus.Cancelled, "game_disabled"));
        var before = rig.SteamRequests;
        (await rig.PollAsync()).Should().Be(0);
        rig.SteamRequests.Should().Be(before);

        // Enabling again starts the game's own window: what was published before is not posted.
        rig.Host.Clock.Advance(TimeSpan.FromMinutes(30));
        rig.Steam.Serve([Old, Update(540, t), Update(541, rig.Now.AddMinutes(-10))]);
        await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), "cs2", true, Ct));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0);
        rig.Steam.Serve([Old, Update(540, t), Update(541, rig.Now.AddMinutes(-10)), Update(542, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(542));
    }

    [Fact]
    public async Task One_request_serves_every_guild_and_each_guild_gets_exactly_one_card()
    {
        await using var rig = await BaselinedAsync();
        await rig.ConfigureGuildAsync(OtherGuild, UpdatesRig.OtherChannel);
        var before = rig.SteamRequests;
        rig.Steam.Serve([Old, Update(550, rig.Now.AddSeconds(30))]);
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();

        (rig.SteamRequests - before).Should().Be(2, "one request per round for the game, however many guilds follow it");
        rig.Host.Transport.Messages.Should().HaveCount(2);
        rig.Host.Transport.Messages.Select(m => m.Channel).Should().BeEquivalentTo([Channel, UpdatesRig.OtherChannel]);
        (await rig.StatesAsync()).Should().ContainSingle("poll state is per provider and game, never per guild");
    }

    // ---------- catch-up ----------

    [Fact]
    public async Task After_a_three_hour_outage_both_missed_updates_are_posted_in_order()
    {
        await using var first = await BaselinedAsync();
        first.Host.Clock.Advance(TimeSpan.FromHours(3)); // the process was down
        await using var second = await UpdatesRig.CreateAsync(shareWith: first);
        var now = second.Now;
        second.Steam.Serve([Update(601, now.AddHours(-1)), Update(600, now.AddHours(-2)), Old]);
        (await second.Poller.TickAsync(Ct)).Should().Be(1);
        await second.DeliverAsync();
        second.CardTexts.Should().HaveCount(2);
        second.CardTexts[0].Should().Contain(Link(600));
        second.CardTexts[1].Should().Contain(Link(601));

        await second.PollAsync();
        await second.DeliverAsync();
        second.Host.Transport.SendCalls.Should().Be(2, "nothing is posted twice after the catch-up");
    }

    [Fact]
    public async Task Catch_up_is_bounded_in_time_and_per_round()
    {
        await using var rig = await BaselinedAsync();
        rig.Host.Clock.Advance(TimeSpan.FromHours(30));
        var now = rig.Now;
        rig.Steam.Serve(
        [
            Update(610, now.AddHours(-26)),
            Update(611, now.AddHours(-20)), Update(612, now.AddHours(-10)), Update(613, now.AddHours(-5)), Update(614, now.AddHours(-2)), Update(615, now.AddHours(-1)),
        ]);
        await rig.Poller.TickAsync(Ct);
        await rig.DeliverAsync();
        rig.CardTexts.Should().HaveCount(3).And.Satisfy(d => d.Contains(Link(611)), d => d.Contains(Link(612)), d => d.Contains(Link(613)));

        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().HaveCount(5, "the rest follows on the next round");
        rig.CardTexts.Should().NotContain(d => d.Contains(Link(610)), "older than the catch-up window");
    }

    [Fact]
    public async Task A_three_month_outage_does_not_flood_the_channel()
    {
        await using var rig = await BaselinedAsync();
        rig.Host.Clock.Advance(TimeSpan.FromDays(90));
        var now = rig.Now;
        rig.Steam.Serve(Enumerable.Range(0, 20).Select(i => Update(700 + i, now.AddDays(-4 * i).AddHours(-2))));
        for (var i = 0; i < 4; i++)
        {
            await rig.PollAsync();
            await rig.DeliverAsync();
        }

        rig.CardTexts.Should().ContainSingle("only the update of the last 24 hours").Which.Should().Contain(Link(700));
    }

    // ---------- failures ----------

    [Fact]
    public async Task A_rate_limit_backs_off_the_wait_survives_a_restart_and_a_failure_is_never_no_updates()
    {
        await using var rig = await BaselinedAsync();
        var success = (await rig.StateAsync())!.LastSuccessAt;
        rig.Steam.Respond = _ => SteamNews.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(45));
        await rig.PollAsync();
        var state = await rig.StateAsync();
        UpdatesConfigService.OutcomeName(state!).Should().Be("RateLimited");
        state!.LastHttpStatus.Should().Be(429);
        state.ConsecutiveFailures.Should().Be(1);
        state.NextPollAt.Should().Be(rig.Now.AddMinutes(45));
        state.LastSuccessAt.Should().Be(success, "the last success is not overwritten by a failure");
        (await rig.ItemsAsync()).Should().ContainSingle("the stored posts are untouched");

        await using var restarted = await UpdatesRig.CreateAsync(shareWith: rig);
        restarted.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        (await restarted.Poller.TickAsync(Ct)).Should().Be(0);
        restarted.SteamRequests.Should().Be(0, "a restart neither resets the backoff nor causes an extra request");

        restarted.Steam.Respond = _ => SteamNews.Status(HttpStatusCode.InternalServerError);
        await restarted.PollAsync();
        state = await restarted.StateAsync();
        state!.ConsecutiveFailures.Should().Be(2);
        state.NextPollAt.Should().Be(restarted.Now.AddMinutes(10), "exponential backoff: 5 min x 2");

        restarted.Steam.Respond = _ => throw new TaskCanceledException("simulated timeout");
        await restarted.PollAsync();
        UpdatesConfigService.OutcomeName((await restarted.StateAsync())!).Should().Be("Timeout");

        restarted.Steam.Serve([Old, Update(800, restarted.Now.AddMinutes(1))]);
        await restarted.PollAsync();
        await restarted.DeliverAsync();
        (await restarted.StateAsync())!.ConsecutiveFailures.Should().Be(0);
        restarted.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(800));
    }

    [Fact]
    public async Task The_cache_lifetime_declared_by_the_source_stretches_the_interval_within_bounds()
    {
        await using var rig = await UpdatesRig.CreateAsync();
        rig.Steam.Serve([Old], expiresIn: TimeSpan.FromMinutes(59));
        await rig.PollAsync();
        var state = await rig.StateAsync();
        state!.NextPollAt.Should().Be(Start.AddMinutes(59), "asking again earlier would return the same cached answer");
        state.SourceCacheSeconds.Should().Be(59 * 60);

        var before = rig.SteamRequests;
        rig.Host.Clock.Advance(TimeSpan.FromMinutes(30));
        (await rig.Poller.TickAsync(Ct)).Should().Be(0);
        rig.SteamRequests.Should().Be(before);

        rig.Steam.Serve([Old], expiresIn: TimeSpan.FromHours(9));
        await rig.PollAsync();
        (await rig.StateAsync())!.NextPollAt.Should().Be(rig.Now + UpdatesPoller.MaxSourceCacheWait, "an implausibly long lifetime is capped");

        rig.Steam.Serve([Old], expiresIn: TimeSpan.FromMinutes(1));
        await rig.PollAsync();
        (await rig.StateAsync())!.NextPollAt.Should().Be(rig.Now.AddMinutes(5), "never more often than the project interval");

        rig.Steam.Serve([Old]);
        await rig.PollAsync();
        state = await rig.StateAsync();
        state!.NextPollAt.Should().Be(rig.Now.AddMinutes(5));
        state.SourceCacheSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Nothing_of_a_round_is_kept_when_it_cannot_be_stored_and_the_update_is_posted_on_the_next_round()
    {
        var failOnce = new FailOnceOutbox();
        await using var rig = await BaselinedAsync(replace: s => s.AddScoped<INotificationOutbox>(sp => failOnce.Wrap(ActivatorUtilities.CreateInstance<NotificationOutbox>(sp))));
        var stored = await rig.StateAsync();
        var t = rig.Now;
        rig.Steam.Serve([Old, Update(900, t)]);
        failOnce.Armed = true;
        await rig.PollAsync(); // the failure stays inside the module (logged), nothing is thrown at the host

        var state = await rig.StateAsync();
        state!.LastAttemptAt.Should().Be(stored!.LastAttemptAt, "the round was not committed at all");
        state.NextPollAt.Should().Be(stored.NextPollAt);
        (await rig.ItemsAsync()).Should().NotContain(i => i.ExternalId == "900");
        (await rig.OutboxAsync()).Should().BeEmpty();

        var requests = rig.SteamRequests;
        await rig.Poller.TickAsync(Ct);
        rig.SteamRequests.Should().Be(requests, "even without a stored next poll time the source is not asked again within the interval");

        await rig.PollAsync(TimeSpan.FromMinutes(5));
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(900));
    }

    [Fact]
    public async Task A_failing_updates_source_never_touches_another_modules_delivery()
    {
        await using var rig = await BaselinedAsync();
        rig.Steam.Respond = _ => throw new InvalidOperationException("simulated crash inside the HTTP stack");
        await rig.PollAsync();
        UpdatesConfigService.OutcomeName((await rig.StateAsync())!).Should().Be("TransportError");

        await rig.Host.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<INotificationOutbox>().StageAsync(new NotificationRequest(Guild, new ModuleId("core"), "x", Channel, "test",
                new OutgoingMessage("hello", null, MentionPolicy.None), rig.Now.AddHours(1), false), Ct);
            await sp.GetRequiredService<ToroDbContext>().SaveChangesAsync(Ct);
        });
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle().Which.Message.Content.Should().Be("hello");

        var report = await rig.Host.Services.GetServices<IModuleHealthCheck>().Single(h => h.Module == new ModuleId("updates")).CheckAsync(Ct);
        report.Overall.Should().Be(HealthState.Degraded, "the last success is recent but the last request failed");
    }

    [Fact]
    public async Task A_shutdown_during_a_request_is_a_cancellation_not_a_stored_failure()
    {
        await using var rig = await BaselinedAsync();
        using var stopping = new CancellationTokenSource();
        rig.Steam.Respond = _ =>
        {
            stopping.Cancel();
            throw new OperationCanceledException(stopping.Token);
        };
        var due = (await rig.StateAsync())!.NextPollAt!.Value;
        rig.Host.Clock.SetUtcNow(due);
        await rig.Poller.Invoking(p => p.TickAsync(stopping.Token)).Should().ThrowAsync<OperationCanceledException>();
        (await rig.StateAsync())!.ConsecutiveFailures.Should().Be(0);
    }

    // ---------- retention ----------

    [Fact]
    public async Task Retention_prunes_titles_and_ids_and_a_pruned_update_never_comes_back()
    {
        await using var rig = await BaselinedAsync(new() { ["Updates:TextRetentionDays"] = "7", ["Updates:DedupRetentionDays"] = "30" });
        var t = rig.Now;
        rig.Steam.Serve([Update(1000, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle();

        rig.Steam.Serve([SteamNews.Announcement(1002, t)]);
        await rig.PollAsync(TimeSpan.FromDays(8));
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "1000").Title.Should().BeNull("titles are kept for 7 days");

        await rig.PollAsync(TimeSpan.FromDays(25));
        (await rig.ItemsAsync()).Should().NotContain(i => i.ExternalId == "1000");
        (await rig.StateAsync())!.PrunedThroughPublishedAt.Should().Be(t);

        // The old update shows up again: with its date it is baseline; without one it cannot be posted at all.
        rig.Steam.Serve([Update(1000, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "1000").Baseline.Should().BeTrue();
        rig.Steam.Serve([Update(1001, t) with { Published = null }]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle("nothing old is posted again");
    }

    [Fact]
    public async Task A_post_that_is_still_listed_is_never_pruned_however_long_nothing_was_polled()
    {
        await using var rig = await BaselinedAsync(new() { ["Updates:TextRetentionDays"] = "7", ["Updates:DedupRetentionDays"] = "30" });
        var t = rig.Now;
        rig.Steam.Serve([Old, Update(1050, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        await rig.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), true, Ct));
        rig.Host.Clock.Advance(TimeSpan.FromDays(40)); // nothing is requested while paused
        await rig.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), false, Ct));
        (await rig.PollAsync()).Should().Be(1);

        var items = await rig.ItemsAsync();
        items.Select(i => i.ExternalId).Should().BeEquivalentTo(["1", "1050"], "both are in the answer again: neither id is dropped");
        items.Should().OnlyContain(i => i.Title != null && i.LastSeenAt == rig.Now);
        (await rig.StateAsync())!.PrunedThroughPublishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Purging_a_guilds_data_removes_its_channel_games_and_delivery_records_and_keeps_the_posts()
    {
        await using var rig = await BaselinedAsync();
        rig.Steam.Serve([Old, Update(1060, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        await rig.Host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<ToroSquad.Core.Privacy.IUserDataContributor>().Single(c => c.Module == new ModuleId("updates"));
            (await data.ExportAsync(Guild, new UserId(2), Ct))["storesPersonalData"]!.GetValue<bool>().Should().BeFalse();
            (await data.PreviewDeletionAsync(Guild, new UserId(2), Ct)).Should().BeEmpty();
            (await data.DeleteAsync(Guild, new UserId(2), Ct)).RecordsDeleted.Should().Be(0);
            (await data.PurgeGuildAsync(OtherGuild, Ct)).Should().Be(0, "another guild's purge touches nothing here");
            (await data.PurgeGuildAsync(Guild, Ct)).Should().Be(3, "channel row, followed game, delivery record");
        });
        (await rig.GuildConfigAsync(Guild)).Should().BeNull();
        (await rig.GameStatusAsync(Guild)).Enabled.Should().BeFalse();
        (await rig.ItemsAsync()).Should().HaveCount(2, "posts and the source state are not guild data");
        (await rig.PollAsync()).Should().Be(0);
    }

    // ---------- admin ----------

    [Fact]
    public async Task Admin_operations_need_manage_server_stay_in_their_guild_and_only_accept_registered_games()
    {
        await using var rig = await BaselinedAsync();
        var member = TestHost.Member(Guild);
        await rig.Host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<UpdatesConfigService>();
            (await config.SetChannelAsync(member, Channel.Value, Ct)).Error.Should().Be(OperationError.Forbidden);
            (await config.SetPausedAsync(member, true, Ct)).Error.Should().Be(OperationError.Forbidden);
            (await config.SetGameEnabledAsync(member, "cs2", false, Ct)).Error.Should().Be(OperationError.Forbidden);
            (await config.StatusAsync(member, Ct)).Status.Should().BeNull();
            (await config.PreviewAsync(member, "cs2", "tr", Ct)).Preview.Should().BeNull();
            (await config.DoctorAsync(member, Ct)).Checks.Should().BeEmpty();

            var other = await config.StatusAsync(TestHost.Admin(OtherGuild), Ct);
            other.Status!.ChannelId.Should().BeNull("another guild's settings are never visible");
            other.Status.Games.Should().OnlyContain(g => !g.Enabled);
            (await config.SetPausedAsync(TestHost.Admin(OtherGuild), true, Ct)).Error.Should().Be(OperationError.InvalidInput, "no channel there");
            (await config.SetChannelAsync(TestHost.Admin(Guild), 424242, Ct)).Error.Should().Be(OperationError.InvalidInput, "unknown channel");
            foreach (var game in new[] { "dota2", "CS2", "", "cs2; drop", null })
                (await config.SetGameEnabledAsync(TestHost.Admin(Guild), game, true, Ct)).MessageKey.Should().Be("updates.game.unknown");
            (await config.PreviewAsync(TestHost.Admin(Guild), "dota2", "tr", Ct)).Preview.Should().BeNull();
        });
        (await rig.GuildConfigAsync(Guild))!.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task Preview_status_and_doctor_describe_the_state_without_sending_or_requesting_anything()
    {
        await using var rig = await UpdatesRig.CreateAsync();
        var admin = TestHost.Admin(Guild);
        var (_, synthetic) = await rig.ConfigAsync(c => c.PreviewAsync(admin, "cs2", "tr", Ct));
        synthetic!.Synthetic.Should().BeTrue("nothing is stored yet");
        synthetic.Message.Embed!.Url.Should().BeNull("a made-up card links nowhere");
        synthetic.Message.Embed.Description.Should().Contain("Counter\\-Strike 2 Update").And.NotContain("](");

        var (_, early) = await rig.ConfigAsync(c => c.DoctorAsync(admin, Ct));
        early.Should().Contain(c => c.LabelKey == "updates.doctor.mode" && c.DetailKey == "updates.doctor.mode_live");
        early.Should().Contain(c => c.DetailKey == "updates.doctor.module_on");
        early.Should().Contain(c => c.DetailKey == "updates.doctor.channel_ok");
        early.Should().Contain(c => c.DetailKey == "updates.doctor.games_value");
        early.Should().Contain(c => c.DetailKey == "updates.doctor.source_never");

        rig.Steam.Serve([Update(1100, Start.AddDays(-1)), Old]);
        await rig.PollAsync();
        var requests = rig.SteamRequests;
        var (_, real) = await rig.ConfigAsync(c => c.PreviewAsync(admin, "cs2", "en", Ct));
        real!.Synthetic.Should().BeFalse("the latest stored update is shown, even a baseline one — only to the admin");
        real.Message.Embed!.Title.Should().Be("🛠️ CS2 Update");
        real.Message.Embed.Description.Should().Contain("A new Counter-Strike 2 update has been released.").And.Contain("[View the update notes on Steam]" + Link(1100));
        real.Message.Embed.Footer.Should().Be("Source: Steam");

        var (_, status) = await rig.ConfigAsync(c => c.StatusAsync(admin, Ct));
        status!.Mode.Should().Be(UpdatesMode.Live);
        status.EffectiveMode.Should().Be(UpdatesMode.Live);
        status.ModuleEnabled.Should().BeTrue();
        status.ChannelId.Should().Be(Channel.Value);
        var game = status.Games.Single(g => g.Game.Key == "cs2");
        game.Game.Should().BeSameAs(Cs2Game.Definition);
        game.ProviderName.Should().Be("Steam");
        game.Enabled.Should().BeTrue();
        game.Source!.LastItemCount.Should().Be(2);
        game.LastDiscovered.Should().BeNull("baseline posts are not discoveries");
        game.LastCardAt.Should().BeNull();

        var (_, checks) = await rig.ConfigAsync(c => c.DoctorAsync(admin, Ct));
        checks.Should().Contain(c => c.DetailKey == "updates.doctor.source_value" && c.State == UpdatesCheckState.Ok);
        checks.Should().Contain(c => c.DetailKey == "updates.doctor.baseline_value");
        checks.Should().Contain(c => c.DetailKey == "updates.doctor.posts_value");
        checks.Should().Contain(c => c.DetailKey == "updates.doctor.stored_value");
        checks.Should().NotContain(c => c.State == UpdatesCheckState.Problem);

        rig.Host.Guilds.SetChannel(Guild, Channel, new ToroSquad.Core.Roles.BotChannelAccess(true, true, ToroSquad.Core.Security.GuildPermission.ViewChannel));
        (await rig.ConfigAsync(c => c.DoctorAsync(admin, Ct))).Checks.Should().Contain(c => c.DetailKey == "updates.doctor.channel_permissions" && c.State == UpdatesCheckState.Problem);

        rig.SteamRequests.Should().Be(requests, "admin operations never request the provider");
        rig.Host.Transport.SendCalls.Should().Be(0);
        (await rig.OutboxAsync()).Should().BeEmpty();
    }

    // ---------- Discord safety ----------

    [Fact]
    public async Task A_hostile_title_cannot_mention_format_or_link_and_the_card_stays_within_discord_limits()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now;
        var hostile = "@everyone <@&5> <@123> [free skins](https://evil.example) **bold** ||spoiler|| `code` https://evil.example Update";
        rig.Steam.Serve([new SteamPost("1200", hostile, t, SteamNews.PatchNotes, SteamNews.PatchTag), new SteamPost("1201", new string('Ü', 280) + " Update", t, SteamNews.PatchNotes, SteamNews.PatchTag)]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.Host.Transport.Messages.Should().HaveCount(2).And.OnlyContain(m => !m.Pinged && !m.Message.Mentions.PingsAnything);
        var card = rig.Host.Transport.Messages.Single(m => m.Message.Embed!.Description!.Contains(Link(1200))).Message;
        var text = card.Embed!.Description!;
        DiscordText.RawMentionPattern().IsMatch(text).Should().BeFalse("no mention syntax survives");
        text.Should().NotContain("](https://evil.example)").And.NotContain("https://evil.example", "a bare URL would become a link");
        text.Should().NotContain("**bold**").And.NotContain("||spoiler||").And.NotContain("`code`");
        System.Text.RegularExpressions.Regex.Matches(text, @"\]\(https://").Should().ContainSingle("the only link is the Steam one");
        foreach (var message in rig.Host.Transport.Messages)
            DiscordLimits.Validate(message.Message).Should().BeEmpty();
    }

    [Fact]
    public async Task The_renderer_refuses_any_link_the_games_provider_does_not_recognize()
    {
        await using var rig = await UpdatesRig.CreateAsync();
        var renderer = rig.Host.Services.GetRequiredService<UpdateCardRenderer>();
        var catalog = rig.Host.Services.GetRequiredService<GameUpdateCatalog>();
        var steam = catalog.ProviderOf(Cs2Game.Definition);
        foreach (var url in new[] { "https://evil.example/x", SteamNews.CdnUrl("5"), "https://store.steampowered.com/app/730", "javascript:alert(1)", "" })
            renderer.Invoking(r => r.Render(Cs2Game.Definition, steam, url, "Counter-Strike 2 Update", Start, "tr")).Should().Throw<ArgumentException>(url);
        var undated = renderer.Render(Cs2Game.Definition, steam, SteamNews.CardUrl("5"), "Counter-Strike 2 Update", null, "tr");
        undated.Embed!.Timestamp.Should().BeNull("no provider time, no timestamp — the first-seen time is never shown instead");
    }

    // ---------- generic by registration ----------

    [Fact]
    public async Task A_second_game_and_provider_are_added_by_registration_alone()
    {
        await using var rig = await UpdatesRig.CreateAsync(configure: false, fakeGame: true);
        await rig.ConfigureGuildAsync(Guild, Channel, true, "cs2", FakeUpdateProvider.GameKey);
        var fake = rig.Fake!;
        rig.Steam.Serve([Old]);
        fake.Add("a0", "Welcome", Start.AddDays(-5));
        (await rig.PollAsync()).Should().Be(2, "one request per game");
        (rig.SteamRequests, fake.Requests).Should().Be((1, 1));
        (await rig.StatesAsync()).Select(s => (s.Provider, s.GameKey, s.ProviderGameId)).Should().Equal(("steam", "cs2", "730"), ("fakestore", "fakegame", "g-1"));

        var t = rig.Now;
        fake.Add("p1", "Patch 1.1", t);
        fake.Add("n1", "Dev diary", t);
        rig.Steam.Serve([Old, Update(1300, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.Host.Transport.Messages.Should().HaveCount(2);
        var fakeCard = rig.Host.Transport.Messages.Single(m => m.Message.Embed!.Title == "🛠️ FG Güncellemesi").Message.Embed!;
        fakeCard.Description.Should().Contain("**Patch 1.1**").And.Contain("Yeni Fake Game güncellemesi yayınlandı.").And.Contain("(https://fakestore.example/notes/p1)");
        fakeCard.Footer.Should().Be("Kaynak: FakeStore");
        rig.Host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Title == "🛠️ CS2 Güncellemesi" && m.Message.Embed.Footer == "Kaynak: Steam");
        (await rig.OutboxAsync()).Select(o => (o.SourceKey, o.Kind)).Should().BeEquivalentTo([("steam:730:1300", "update:cs2"), ("fakestore:g-1:p1", "update:fakegame")]);

        var (_, status) = await rig.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct));
        status!.Games.Select(g => (g.Game.Key, g.ProviderName, g.Enabled)).Should().Equal(("cs2", "Steam", true), ("fakegame", "FakeStore", true), ("wow-forever", "Blizzard", false));

        // One game's source failing (even by throwing) leaves the other game working.
        fake.Throw = new InvalidOperationException("simulated provider bug");
        rig.Steam.Serve([Old, Update(1300, t), Update(1301, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().HaveCount(3);
        UpdatesConfigService.OutcomeName((await rig.StateAsync(FakeUpdateProvider.GameKey))!).Should().Be("TransportError");
        (await rig.StateAsync())!.ConsecutiveFailures.Should().Be(0);

        // Unfollowing one game stops only that game.
        fake.Throw = null;
        await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), FakeUpdateProvider.GameKey, false, Ct));
        var (steamBefore, fakeBefore) = (rig.SteamRequests, fake.Requests);
        await rig.PollAsync();
        (rig.SteamRequests - steamBefore, fake.Requests - fakeBefore).Should().Be((1, 0));
    }

    [Fact]
    public async Task A_provider_cannot_smuggle_another_games_posts_into_a_round()
    {
        await using var rig = await UpdatesRig.CreateAsync(configure: false, fakeGame: true);
        await rig.ConfigureGuildAsync(Guild, Channel, true, FakeUpdateProvider.GameKey);
        var fake = rig.Fake!;
        fake.Add("a0", "Welcome", Start.AddDays(-5));
        await rig.PollAsync();
        fake.Posts.Add(new GameUpdateCandidate("steam", "cs2", "999", "Counter-Strike 2 Update", SteamNews.CardUrl("999"), rig.Now, ["patchnotes"], SteamNews.PatchNotes));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0);
        (await rig.ItemsAsync()).Should().OnlyContain(i => i.GameKey == FakeUpdateProvider.GameKey);
        (await rig.StateAsync(FakeUpdateProvider.GameKey))!.LastSkippedCount.Should().Be(1);

        // An answer with nothing of the requested game is a failure — recorded and backed off as one.
        fake.Posts.RemoveAll(p => p.GameKey == FakeUpdateProvider.GameKey);
        await rig.PollAsync();
        var state = await rig.StateAsync(FakeUpdateProvider.GameKey);
        UpdatesConfigService.OutcomeName(state!).Should().Be("UnexpectedSchema");
        state!.ConsecutiveFailures.Should().Be(1);
        await rig.PollAsync();
        state = await rig.StateAsync(FakeUpdateProvider.GameKey);
        (state!.ConsecutiveFailures, state.NextPollAt).Should().Be((2, rig.Now.AddMinutes(10)));
    }

    [Fact]
    public void The_catalog_refuses_broken_registrations()
    {
        var fake = new FakeUpdateProvider();
        var catalog = new GameUpdateCatalog([FakeUpdateProvider.Game], [fake]);
        catalog.Games.Should().ContainSingle();
        catalog.Find("fakegame").Should().BeSameAs(FakeUpdateProvider.Game);
        catalog.Find("nope").Should().BeNull();
        catalog.Find(null).Should().BeNull();
        catalog.ProviderOf(FakeUpdateProvider.Game).Should().BeSameAs(fake);

        var broken = new Func<GameUpdateCatalog>[]
        {
            () => new GameUpdateCatalog([FakeUpdateProvider.Game, FakeUpdateProvider.Game], [fake]),
            () => new GameUpdateCatalog([FakeUpdateProvider.Game], []),
            () => new GameUpdateCatalog([FakeUpdateProvider.Game with { Key = "Bad Key" }], [fake]),
            () => new GameUpdateCatalog([FakeUpdateProvider.Game with { Key = "a:b" }], [fake]),
            () => new GameUpdateCatalog([FakeUpdateProvider.Game], [fake, new FakeUpdateProvider()]),
        };
        foreach (var create in broken)
            create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task The_shipped_defaults_are_off_with_no_channel_and_no_followed_game()
    {
        await using var host = await TestHost.CreateAsync();
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<UpdatesOptions>>().Value;
        options.Mode.Should().Be(UpdatesMode.Off);
        (options.PollIntervalMinutes, options.CatchUpHours, options.MaxCardsPerRound, options.ItemsPerRequest).Should().Be((5, 24, 3, 20));
        options.Validate().Should().BeEmpty();
        new ToroSquad.Modules.Updates.UpdatesModule().Descriptor.EnabledByDefault.Should().BeFalse();
        host.Services.GetRequiredService<GameUpdateCatalog>().Games.Select(g => g.Key).Should().Equal("cs2", "wow-forever");

        await host.InScopeAsync(async sp =>
        {
            var (_, status) = await sp.GetRequiredService<UpdatesConfigService>().StatusAsync(TestHost.Admin(Guild), Ct);
            status!.ModuleEnabled.Should().BeFalse();
            status.ChannelId.Should().BeNull();
            status.Games.Should().HaveCount(2).And.OnlyContain(g => !g.Enabled, "the games are registered, but no guild follows one until an admin says so");
        });
        (await host.Services.GetRequiredService<UpdatesPoller>().TickAsync(Ct)).Should().Be(0);

        new UpdatesOptions { PollIntervalMinutes = 1 }.Validate().Should().ContainSingle();
        new UpdatesOptions { ItemsPerRequest = 500 }.Validate().Should().ContainSingle();
        new UpdatesOptions { TextRetentionDays = 90, DedupRetentionDays = 30 }.Validate().Should().ContainSingle();
        new UpdatesOptions { UserAgent = "bot" }.Validate().Should().ContainSingle();
    }

    private sealed class FailOnceOutbox
    {
        public bool Armed { get; set; }

        public INotificationOutbox Wrap(INotificationOutbox inner) => new Wrapper(this, inner);

        private sealed class Wrapper(FailOnceOutbox owner, INotificationOutbox inner) : INotificationOutbox
        {
            public Task<StageOutcome> StageAsync(NotificationRequest request, CancellationToken cancellationToken)
            {
                if (owner.Armed && request.Module.Value == "updates")
                {
                    owner.Armed = false;
                    throw new InvalidOperationException("simulated failure while storing the round");
                }

                return inner.StageAsync(request, cancellationToken);
            }
        }
    }
}
