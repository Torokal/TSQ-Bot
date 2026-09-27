using Microsoft.Extensions.Configuration;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Birthday;
using ToroSquad.Modules.Birthday.Application;
using ToroSquad.Modules.Birthday.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>TSQ Birthday: the typed date (day + month, no year), 29 February, matching and the local day boundary.</summary>
public sealed class BirthdayDateTests
{
    private static readonly TimeZoneInfo Istanbul = GuildTime.TryResolve("Europe/Istanbul", out var zone) ? zone : throw new InvalidOperationException("tz");

    [Theory]
    [InlineData("14.03")]
    [InlineData("14/03")]
    [InlineData("14-03")]
    [InlineData(" 14.03 ")]
    [InlineData("14.3")]
    public void Day_and_month_parse_with_dot_slash_or_dash(string input)
    {
        BirthdayDate.TryParse(input, out var date).Should().BeTrue(input);
        date.Day.Should().Be(14);
        date.Month.Should().Be(3);
        date.ToString().Should().Be("14.03", "stored canonically as day + month");
    }

    [Theory]
    [InlineData("31.02")]
    [InlineData("30.02")]
    [InlineData("00.05")]
    [InlineData("32.01")]
    [InlineData("13.13")]
    [InlineData("31.04")]
    [InlineData("15.00")]
    [InlineData("14.03.1990")]
    [InlineData("1990-03-14")]
    [InlineData("14.03/")]
    [InlineData("14.03.")]
    [InlineData("14/03-")]
    [InlineData("1403")]
    [InlineData(".03")]
    [InlineData("14.")]
    [InlineData("ab.cd")]
    [InlineData("-1.03")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("14 Mart")]
    [InlineData("１４.０３")]
    public void Dates_that_do_not_exist_and_anything_with_a_year_are_rejected(string? input) =>
        BirthdayDate.TryParse(input, out _).Should().BeFalse(input);

    [Fact]
    public void February_29_is_a_valid_birthday()
    {
        BirthdayDate.TryParse("29.02", out var date).Should().BeTrue();
        date.Should().Be(BirthdayDate.Create(29, 2));
    }

    [Fact]
    public void A_birthday_matches_its_own_day_in_any_year()
    {
        var date = BirthdayDate.Create(14, 3)!.Value;
        date.IsOn(new DateOnly(2027, 3, 14)).Should().BeTrue();
        date.IsOn(new DateOnly(2031, 3, 14)).Should().BeTrue();
        date.IsOn(new DateOnly(2027, 3, 13)).Should().BeFalse();
        date.IsOn(new DateOnly(2027, 4, 14)).Should().BeFalse();
    }

    [Fact]
    public void February_29_only_fires_on_a_real_February_29_never_moved_to_28_February_or_1_March()
    {
        var leapling = BirthdayDate.Create(29, 2)!.Value;
        leapling.IsOn(new DateOnly(2027, 2, 28)).Should().BeFalse();
        leapling.IsOn(new DateOnly(2027, 3, 1)).Should().BeFalse();
        leapling.IsOn(new DateOnly(2028, 2, 29)).Should().BeTrue();
        var days = Enumerable.Range(0, 365).Select(i => new DateOnly(2027, 1, 1).AddDays(i));
        days.Should().NotContain(d => leapling.IsOn(d), "2027 is not a leap year");
    }

    [Fact]
    public void The_day_is_the_Istanbul_calendar_day_not_the_UTC_date()
    {
        // 23:59 in Istanbul on 13 March = 20:59 UTC; one minute later Istanbul is on the 14th while UTC is still on the 13th.
        var before = new DateTimeOffset(2027, 3, 13, 20, 59, 59, TimeSpan.Zero);
        var after = new DateTimeOffset(2027, 3, 13, 21, 0, 0, TimeSpan.Zero);
        BirthdayCalendar.LocalDate(before, Istanbul).Should().Be(new DateOnly(2027, 3, 13));
        BirthdayCalendar.LocalDate(after, Istanbul).Should().Be(new DateOnly(2027, 3, 14));
        DateOnly.FromDateTime(after.UtcDateTime).Should().Be(new DateOnly(2027, 3, 13), "the UTC date would be a day late");

        BirthdayCalendar.StartOfDay(new DateOnly(2027, 3, 14), Istanbul).Should().Be(after);
        BirthdayCalendar.EndOfDay(new DateOnly(2027, 3, 14), Istanbul).Should().Be(after.AddDays(1));
    }

    [Fact]
    public void Day_boundaries_follow_a_zone_with_daylight_saving_time()
    {
        GuildTime.TryResolve("Europe/Berlin", out var berlin).Should().BeTrue();
        // 29 March 2026: Berlin switches to summer time at 02:00; the day still starts at 00:00 CET = 23:00 UTC the day before.
        BirthdayCalendar.StartOfDay(new DateOnly(2026, 3, 29), berlin).Should().Be(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero));
        BirthdayCalendar.EndOfDay(new DateOnly(2026, 3, 29), berlin).Should().Be(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero), "a 23-hour day");
    }

    [Fact]
    public void Announcement_is_one_message_that_pings_exactly_the_celebrants_it_names()
    {
        var renderer = new BirthdayAnnouncementRenderer(Localizer());
        var one = renderer.Render([new UserId(11)], "tr");
        one.Content.Should().Be("🎂 Bugün <@11> doğum gününü kutluyor!\nİyi ki doğdun! 🥳");
        one.Mentions.Users.Should().Equal(new UserId(11));
        one.Mentions.Everyone.Should().BeFalse();
        one.Mentions.Roles.Should().BeEmpty();
        one.Embed.Should().BeNull("a plain message, no card");

        var three = renderer.Render([new UserId(11), new UserId(22), new UserId(33)], "tr");
        three.Content.Should().Be("🎂 Bugün <@11>, <@22> ve <@33> doğum günlerini kutluyor!\nİyi ki doğdunuz! 🥳");
        three.Mentions.Users.Should().Equal(new UserId(11), new UserId(22), new UserId(33));
        MentionedIds(three.Content!).Should().Equal(three.Mentions.Users!.Select(u => u.Value), "the pinged ids are exactly the ids in the text");

        var two = renderer.Render([new UserId(11), new UserId(22)], "tr");
        two.Content.Should().StartWith("🎂 Bugün <@11> ve <@22> doğum");

        var crowd = renderer.Render(Enumerable.Range(1, 90).Select(i => new UserId((ulong)(1_000_000_000_000_000_000 + i))).ToList(), "tr");
        crowd.Content!.Length.Should().BeLessThanOrEqualTo(DiscordLimits.ContentMax);
        crowd.Content.Should().Contain("30 kişi daha");
        crowd.Mentions.Users.Should().HaveCount(BirthdayAnnouncementRenderer.MaxNamed, "only the named members ping, never the summarized ones");
        MentionedIds(crowd.Content).Should().Equal(crowd.Mentions.Users!.Select(u => u.Value));
        DiscordLimits.Validate(crowd).Should().BeEmpty();
    }

    [Fact]
    public void Announcement_allowed_mentions_list_only_the_celebrants_and_never_everyone_here_or_roles()
    {
        var message = new BirthdayAnnouncementRenderer(Localizer()).Render([new UserId(11), new UserId(22)], "tr");
        var wire = ToroSquad.Discord.Transport.DiscordConversions.ToAllowedMentions(message.Mentions);
        wire.AllowedTypes.Should().Be(global::Discord.AllowedMentionTypes.None, "nothing is parsed from the text: no @everyone/@here, no users by text");
        wire.RoleIds.Should().BeNullOrEmpty();
        wire.UserIds.Should().Equal(11UL, 22UL);
        wire.MentionRepliedUser.Should().BeFalse();
        message.WithoutPings().Mentions.PingsAnything.Should().BeFalse("previews/edits never ping");
        message.Mentions.WithoutUserPings().PingsAnything.Should().BeFalse("a resend after an uncertain delivery never pings twice");
    }

    private static List<ulong> MentionedIds(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"<@!?(\d+)>").Select(m => ulong.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();

    [Fact]
    public void Options_default_to_the_TSQ_role_and_Istanbul_and_reject_an_unknown_zone()
    {
        var o = new BirthdayOptions();
        o.RoleId.Should().Be(1553890408348520468UL);
        o.TimeZone.Should().Be("Europe/Istanbul");
        o.Validate().Should().BeEmpty();
        new BirthdayOptions { TimeZone = "Mars/Olympus" }.Validate().Should().ContainSingle();
        new BirthdayOptions { RoleId = 0 }.Role.Should().BeNull("0 = announcements only");
        new BirthdayModule().Descriptor.EnabledByDefault.Should().BeFalse();

        // The production file binds the 64-bit role id losslessly (written as a string, never a JSON number).
        var appsettings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json")).Build();
        var bound = appsettings.GetSection(BirthdayOptions.Section).Get<BirthdayOptions>()!;
        bound.RoleId.Should().Be(BirthdayOptions.DefaultRoleId);
        bound.TimeZone.Should().Be("Europe/Istanbul");
        new BirthdayModule().ValidateConfiguration(appsettings).Should().BeEmpty();
    }

    internal static LocalizationCatalog Localizer() => new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(BirthdayModule).Assembly, "ToroSquad.Modules.Birthday.Localization"),
    ]);
}
