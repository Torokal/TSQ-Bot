using Discord;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;
using GuildPermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The listing form's text mapping (start, duration, team size → the existing create input), the edit prefill, the
/// in-memory drafts (only ever the opener's, short-lived, saved once) and the additive button row break.
/// </summary>
public sealed class LfgFormTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly GuildId Guild = new(4242);
    private static readonly ChannelId Channel = new(55);
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static ActorContext User(ulong id, ulong guild = 4242) =>
        new(new GuildId(guild), new UserId(id), GuildPermission.ViewChannel, [], false, 1);

    // ---- start / duration / team size ----

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData("şimdi", null, null)]
    [InlineData("Şimdi", null, null)]
    [InlineData("hemen", null, null)]
    [InlineData("0 dk", null, null)]
    [InlineData("30 dk", 30, null)]
    [InlineData("30dk", 30, null)]
    [InlineData("45 dakika", 45, null)]
    [InlineData("1 saat", 60, null)]
    [InlineData("1 SAAT", 60, null)]
    [InlineData("1.5 saat", 90, null)]
    [InlineData("1,5 saat", 90, null)]
    [InlineData("2 saat", 120, null)]
    [InlineData("2 saat sonra", 120, null)]
    [InlineData("2 h", 120, null)]
    [InlineData("90 min", 90, null)]
    [InlineData("1 gün", 1440, null)]
    [InlineData("1,33 saat", LfgStartText.Unusable, null)]
    [InlineData("05.10.2026 21:30", null, "05.10.2026 21:30")]
    [InlineData("2026-10-05 21:30", null, "2026-10-05 21:30")]
    [InlineData("30", null, "30")]
    [InlineData("yarın", null, "yarın")]
    public void The_start_field_is_now_a_relative_start_or_a_date(string? text, int? minutes, string? at)
    {
        var start = LfgStartText.Parse(text);

        (start.Minutes, start.At).Should().Be((minutes, at));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("2", 120)]
    [InlineData("2 saat", 120)]
    [InlineData("3saat", 180)]
    [InlineData("1,5", 90)]
    [InlineData("90 dk", 90)]
    [InlineData("iki", LfgFormText.Invalid)]
    [InlineData("1,33", LfgFormText.Invalid)]
    [InlineData("-2", LfgFormText.Invalid)]
    public void The_duration_field_is_hours_unless_a_unit_says_otherwise(string? text, int? minutes) =>
        LfgFormText.DurationMinutes(text).Should().Be(minutes);

    [Theory]
    [InlineData("6", 6)]
    [InlineData(" 12 ", 12)]
    [InlineData("", LfgFormText.Invalid)]
    [InlineData("6 kişi", LfgFormText.Invalid)]
    [InlineData("+6", LfgFormText.Invalid)]
    [InlineData("١٢", LfgFormText.Invalid)] // non-ASCII digits are not a team size
    [InlineData("1000", LfgFormText.Invalid)]
    public void The_team_size_field_is_a_plain_number(string text, int players) => LfgFormText.Players(text).Should().Be(players);

    [Fact]
    public void The_form_maps_onto_the_existing_create_input()
    {
        LfgForm.ToCreateInput(new LfgFormValues("Deadlock", "6", "Casual", "2 saat", "3"), true, false, new ChannelId(9))
            .Should().Be(new LfgCreateInput("Deadlock", 6, "Casual", 180, 120, true, false, new ChannelId(9), null));
        LfgForm.ToCreateInput(new LfgFormValues("CS2", "5", null, "05.10.2026 21:30", null), false, false, null)
            .Should().Be(new LfgCreateInput("CS2", 5, null, null, null, false, false, null, "05.10.2026 21:30"));
        LfgForm.ToCreateInput(LfgFormValues.Empty, false, false, null).Should().Be(new LfgCreateInput(null, LfgFormText.Invalid));
    }

    [Fact]
    public void Relative_starts_are_limited_like_custom_dates()
    {
        LfgRules.ResolveStart(LfgRules.MaxStartMinutes, null, null, null).Start!.Delay.Should().Be(TimeSpan.FromDays(365));
        LfgRules.ResolveStart(LfgRules.MaxStartMinutes + 1, null, null, null).Error.Should().Be(LfgDraftError.StartInvalid);
        LfgRules.ResolveStart(-1, null, null, null).Error.Should().Be(LfgDraftError.StartInvalid);
        LfgRules.ResolveStart(0, null, null, null).Start!.IsNow.Should().BeTrue();
        LfgRules.ResolveStart(30, "05.10.2026 21:30", Istanbul, T0).Error.Should().Be(LfgDraftError.StartConflict);
    }

    // ---- prefill ----

    [Fact]
    public void An_existing_listing_prefills_the_form_in_the_guild_time_zone()
    {
        var scheduled = Listing(eventAt: new DateTimeOffset(2026, 10, 5, 18, 30, 0, TimeSpan.Zero), expiresAt: new DateTimeOffset(2026, 10, 5, 20, 30, 0, TimeSpan.Zero));
        LfgForm.Prefill(scheduled, Istanbul).Should().Be(new LfgFormValues("Deadlock", "6", "Casual", "05.10.2026 21:30", "2"));

        var now = Listing(eventAt: null, expiresAt: T0.AddMinutes(90));
        LfgForm.Prefill(now, Istanbul).Should().Be(new LfgFormValues("Deadlock", "6", "Casual", null, "90 dk"),
            "a listing that started now has no start to show; a duration that is not whole hours keeps its minutes");
        LfgFormText.DurationMinutes("90 dk").Should().Be(90, "the prefill reads back as the same duration");
        LfgStartText.Parse("05.10.2026 21:30").At.Should().Be("05.10.2026 21:30");
    }

    [Fact]
    public void Untouched_fields_are_recognized_regardless_of_spacing_and_case()
    {
        LfgForm.SameText("05.10.2026 21:30", " 05.10.2026  21:30 ").Should().BeTrue();
        LfgForm.SameText(null, "").Should().BeTrue();
        LfgForm.SameText("90 DK", "90 dk").Should().BeTrue();
        LfgForm.SameText("2", "3").Should().BeFalse();
    }

    // ---- drafts ----

    [Fact]
    public void A_draft_is_only_ever_the_openers()
    {
        var drafts = new LfgFormDrafts(new FakeTimeProvider(T0));
        var draft = drafts.Open(User(1), Channel, LfgFormKind.Edit, 77, LfgFormValues.Empty);

        draft.Id.Should().MatchRegex("^[A-Za-z0-9_-]{22}$", "128 random bits, custom-id safe");
        drafts.Get(draft.Id, User(1)).Should().NotBeNull();
        drafts.Get(draft.Id, User(2)).Should().BeNull("another user cannot use a copied id");
        drafts.Get(draft.Id, User(1, guild: 999)).Should().BeNull("nor the same user from another guild");
        drafts.Update(draft.Id, User(2), d => d with { NotifyAtStart = true }).Should().BeNull();
        drafts.Take(draft.Id, User(2)).Should().BeNull();
        drafts.Get("not-a-draft", User(1)).Should().BeNull();
    }

    [Fact]
    public void An_update_cannot_retarget_a_draft()
    {
        var drafts = new LfgFormDrafts(new FakeTimeProvider(T0));
        var draft = drafts.Open(User(1), Channel, LfgFormKind.Edit, 77, LfgFormValues.Empty);

        var updated = drafts.Update(draft.Id, User(1), d => d with { ListingId = 1, Kind = LfgFormKind.Create, User = new UserId(2), NotifyAtStart = true })!;

        (updated.ListingId, updated.Kind, updated.User, updated.NotifyAtStart).Should().Be(((long?)77, LfgFormKind.Edit, new UserId(1), true));
    }

    [Fact]
    public void Drafts_expire_and_are_capped()
    {
        var clock = new FakeTimeProvider(T0);
        var drafts = new LfgFormDrafts(clock);
        var first = drafts.Open(User(1), Channel, LfgFormKind.Create, null, LfgFormValues.Empty);
        clock.Advance(LfgFormDrafts.Lifetime - TimeSpan.FromMinutes(1));
        drafts.Update(first.Id, User(1), d => d).Should().NotBeNull("using a draft keeps it alive");
        clock.Advance(LfgFormDrafts.Lifetime + TimeSpan.FromSeconds(1));
        drafts.Get(first.Id, User(1)).Should().BeNull("expired");

        var ids = Enumerable.Range(0, LfgFormDrafts.MaxPerUser + 2).Select(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return drafts.Open(User(1), Channel, LfgFormKind.Create, null, LfgFormValues.Empty).Id;
        }).ToList();
        ids.Count(id => drafts.Get(id, User(1)) is not null).Should().Be(LfgFormDrafts.MaxPerUser);
        drafts.Get(ids[^1], User(1)).Should().NotBeNull("the newest survive");
    }

    [Fact]
    public void A_draft_is_saved_once_and_comes_back_only_when_the_save_is_refused()
    {
        var drafts = new LfgFormDrafts(new FakeTimeProvider(T0));
        var draft = drafts.Open(User(1), Channel, LfgFormKind.Create, null, LfgFormValues.Empty);

        var taken = drafts.Take(draft.Id, User(1));
        taken.Should().NotBeNull();
        drafts.Take(draft.Id, User(1)).Should().BeNull("a double click saves once");

        drafts.Return(taken!);
        drafts.Get(draft.Id, User(1)).Should().NotBeNull();
        drafts.Remove(draft.Id, User(1));
        drafts.Get(draft.Id, User(1)).Should().BeNull();
    }

    // ---- button rows (Core, additive) ----

    [Fact]
    public void A_button_can_start_a_new_row_without_changing_existing_payloads()
    {
        var plain = new OutgoingMessage("x", null, MentionPolicy.None, [new MessageButton("Join", "id", null)]);
        PayloadSerializer.Serialize(plain).Should().NotContain("newRow");
        var split = plain with { Buttons = [new MessageButton("A", "a", null), new MessageButton("B", "b", null, NewRow: true)] };
        PayloadSerializer.Serialize(split).Should().Contain("\"newRow\":true");
        PayloadSerializer.Deserialize(PayloadSerializer.Serialize(split)).Buttons![1].NewRow.Should().BeTrue();
    }

    [Theory]
    [InlineData(new[] { false, false, false, false, true, false }, new[] { 4, 2 })] // the LFG card with voice
    [InlineData(new[] { false, false, false, true, false }, new[] { 3, 2 })] // the LFG card without voice
    [InlineData(new[] { false, false, false, false, false, false, false }, new[] { 5, 2 })] // unchanged: rows of five
    [InlineData(new[] { true, false }, new[] { 2 })] // a break on the first button is no empty row
    [InlineData(new[] { false, false, false, false, false, true, false }, new[] { 5, 2 })]
    public void Buttons_fill_rows_of_five_and_break_where_asked(bool[] newRow, int[] rows)
    {
        var buttons = newRow.Select((b, i) => new MessageButton("B" + i, "id" + i, null, NewRow: b)).ToList();

        var components = DiscordConversions.ToComponents(buttons)!;

        components.Components.Cast<ActionRowComponent>().Select(r => r.Components.Count).Should().Equal(rows);
    }

    private static LfgListingView Listing(DateTimeOffset? eventAt, DateTimeOffset expiresAt) =>
        new(7, Guild, Channel, new MessageId(99), new UserId(1), "Deadlock", "Casual", 6, LfgStatus.Open, T0, expiresAt, null, [new UserId(1)], EventAt: eventAt);
}
