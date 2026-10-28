using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core.Localization;
using ToroSquad.Modules.Pfp;
using ToroSquad.Modules.Pfp.Application;
using ToroSquad.Modules.Pfp.Commands;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// /pfp and /banner end to end without Discord: the six selection rules and the automatic order, no silent fallback for an
/// explicit "Sunucu", a confirmed absence vs a picture Discord did not report vs a failed read, the global banner always from
/// Get User, animated pictures, the
/// default avatar, the card (large image, one link button to the same URL, defused name) and the response lifecycle
/// (private refusals before the acknowledgement, one public acknowledgement completed exactly once). The Discord adapter is
/// faked at <see cref="IPfpProfileSource"/>: this proves the rules, not Discord's real answers (docs/pfp/TSQ_PFP.md, live checklist).
/// </summary>
public sealed class PfpFlowTests
{
    private const ulong Guild = 689812743242514448;
    private const ulong OtherGuild = 618763184815472651;
    private const ulong Invoker = 111111111111111111;
    private const ulong Target = 222222222222222222;
    private const string Global = "0123456789abcdef0123456789abcdef";
    private const string Server = "fedcba9876543210fedcba9876543210";
    private const string GlobalBanner = "00112233445566778899aabbccddeeff";
    private const string ServerBanner = "ffeeddccbbaa99887766554433221100";
    private const string DefaultUrl = "https://cdn.discordapp.com/embed/avatars/3.png";

    private static readonly LocalizationCatalog Catalog = new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(PfpModule).Assembly, "ToroSquad.Modules.Pfp.Localization"),
    ]);

    private sealed class FakeSource : IPfpProfileSource
    {
        public Func<CancellationToken, Task<PfpMemberLookup>> Member { get; set; } = _ => Task.FromResult(PfpMemberLookup.Failed);
        public Func<CancellationToken, Task<PfpBannerLookup>> Banner { get; set; } = _ => Task.FromResult(PfpBannerLookup.Failed);
        public List<(ulong Guild, ulong User)> MemberReads { get; } = [];
        public List<ulong> UserReads { get; } = [];

        public Task<PfpMemberLookup> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken)
        {
            MemberReads.Add((guildId, userId));
            return Member(cancellationToken);
        }

        public Task<PfpBannerLookup> GetUserBannerAsync(ulong userId, CancellationToken cancellationToken)
        {
            UserReads.Add(userId);
            return Banner(cancellationToken);
        }
    }

    /// <summary>Records what the command would have sent to Discord, in order.</summary>
    private sealed class FakeResponder : IPfpResponder
    {
        public List<string> Events { get; } = [];
        public List<string> Private { get; } = [];
        public List<PfpReply> Public { get; } = [];

        public Task ReplyPrivateAsync(string text)
        {
            Events.Add("private");
            Private.Add(text);
            return Task.CompletedTask;
        }

        public Task DeferPublicAsync()
        {
            Events.Add("defer");
            return Task.CompletedTask;
        }

        public Task ReplyPublicAsync(PfpReply reply)
        {
            Events.Add("public");
            Public.Add(reply);
            return Task.CompletedTask;
        }
    }

    private sealed class Kit
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        public FakeSource Source { get; } = new();
        public PfpCommandFlow Flow { get; }

        public Kit() => Flow = new PfpCommandFlow(new PfpResolver(NullLogger<PfpResolver>.Instance), new PfpCooldown(Clock), new PfpCards(Catalog),
            Clock, NullLogger<PfpCommandFlow>.Instance);

        /// <summary>As the Discord adapter maps them: a null avatar is "none" (always-sent field), a null server picture "not reported".</summary>
        public Kit WithMember(string? avatar = Global, string? guildAvatar = null, string? guildBanner = null, string name = "Yusuf") =>
            WithProfile(PfpHash.FromRequiredField(avatar), PfpHash.FromOptionalField(guildAvatar), PfpHash.FromOptionalField(guildBanner), name);

        public Kit WithProfile(PfpHash avatar, PfpHash guildAvatar, PfpHash guildBanner, string name = "Yusuf")
        {
            Source.Member = _ => Task.FromResult(PfpMemberLookup.Found(new PfpMemberProfile(Target, name, avatar, guildAvatar, guildBanner, DefaultUrl)));
            return this;
        }

        public Kit WithUserBanner(string? banner)
        {
            Source.Banner = _ => Task.FromResult(PfpBannerLookup.Found(PfpHash.FromOptionalField(banner)));
            return this;
        }

        public async Task<FakeResponder> RunAsync(PfpKind kind, string? scope, ulong guild = Guild, ulong invoker = Invoker, ulong target = Target)
        {
            var responder = new FakeResponder();
            await Flow.RunAsync(new PfpRequest(guild, invoker, target, kind, scope, "tr"), Source, responder);
            return responder;
        }
    }

    private static PfpCard CardOf(FakeResponder responder)
    {
        responder.Events.Should().Equal("defer", "public");
        var reply = responder.Public.Should().ContainSingle().Subject;
        reply.Text.Should().BeNull();
        return reply.Card!;
    }

    private static string TextOf(FakeResponder responder)
    {
        responder.Events.Should().Equal("defer", "public");
        var reply = responder.Public.Should().ContainSingle().Subject;
        reply.Card.Should().BeNull();
        return reply.Text!;
    }

    private static string MemberAvatarUrl(string hash) => PfpCdn.MemberAvatar(Guild, Target, hash)!;

    // ---- Avatar: the three rules ----

    [Fact]
    public async Task Pfp_auto_prefers_this_servers_avatar()
    {
        var kit = new Kit().WithMember(avatar: Global, guildAvatar: Server);
        var card = CardOf(await kit.RunAsync(PfpKind.Avatar, null));
        card.Title.Should().Be("Yusuf — Sunucu profil fotoğrafı");
        card.ImageUrl.Should().Be(MemberAvatarUrl(Server));
        kit.Source.UserReads.Should().BeEmpty("an avatar never needs Get User");
    }

    [Fact]
    public async Task Pfp_auto_past_a_server_avatar_discord_did_not_report_shows_the_global_one_and_says_so()
    {
        var card = CardOf(await new Kit().WithMember(avatar: Global, guildAvatar: null).RunAsync(PfpKind.Avatar, PfpScopes.Auto));
        card.Title.Should().Be("Yusuf — Genel profil fotoğrafı");
        card.ImageUrl.Should().Be(PfpCdn.UserAvatar(Target, Global));
        card.Description.Should().Be("Discord bu sunucuya özel bir profil fotoğrafı bildirmedi; genel profil fotoğrafı gösteriliyor.");
    }

    [Fact]
    public async Task Pfp_auto_past_a_confirmed_absent_server_avatar_falls_back_without_a_note()
    {
        var kit = new Kit().WithProfile(PfpHash.Of(Global), PfpHash.None, PfpHash.None);
        CardOf(await kit.RunAsync(PfpKind.Avatar, null)).Description.Should().BeNull();
    }

    [Fact]
    public async Task Pfp_auto_ends_with_discords_default_avatar_and_says_so()
    {
        var card = CardOf(await new Kit().WithMember(avatar: null, guildAvatar: null).RunAsync(PfpKind.Avatar, null));
        card.Title.Should().Be("Yusuf — Varsayılan profil fotoğrafı");
        card.ImageUrl.Should().Be(DefaultUrl, "the SDK's default avatar, not upscaled");
        card.Description.Should().Be("Discord bu sunucuya özel bir profil fotoğrafı bildirmedi; genel profil fotoğrafı gösteriliyor.\n" +
            "Bu kullanıcının kendi profil fotoğrafı yok; Discord’un varsayılan profil fotoğrafı gösteriliyor.");
    }

    [Fact]
    public async Task A_global_avatar_field_that_was_not_sent_is_unavailable_never_the_default_avatar()
    {
        var kit = new Kit().WithProfile(PfpHash.NotReported, PfpHash.NotReported, PfpHash.NotReported);
        TextOf(await kit.RunAsync(PfpKind.Avatar, PfpScopes.Global)).Should().Be("Profil bilgisi şu anda alınamadı. Biraz sonra tekrar dene.");
    }

    [Fact]
    public async Task Pfp_genel_shows_the_global_avatar_even_when_a_server_avatar_exists()
    {
        var card = CardOf(await new Kit().WithMember(avatar: Global, guildAvatar: Server).RunAsync(PfpKind.Avatar, PfpScopes.Global));
        card.Title.Should().Be("Yusuf — Genel profil fotoğrafı");
        card.ImageUrl.Should().Be(PfpCdn.UserAvatar(Target, Global)).And.NotContain("/guilds/");
    }

    [Fact]
    public async Task Pfp_genel_without_a_global_avatar_is_the_default_avatar_never_the_server_one()
    {
        var card = CardOf(await new Kit().WithMember(avatar: null, guildAvatar: Server).RunAsync(PfpKind.Avatar, PfpScopes.Global));
        card.Title.Should().Be("Yusuf — Varsayılan profil fotoğrafı");
        card.ImageUrl.Should().Be(DefaultUrl);
        card.Description.Should().Be("Bu kullanıcının kendi profil fotoğrafı yok; Discord’un varsayılan profil fotoğrafı gösteriliyor.",
            "Genel asks for the global avatar only, so the server avatar is not mentioned");
    }

    [Fact]
    public async Task Pfp_sunucu_shows_only_this_servers_avatar()
    {
        var card = CardOf(await new Kit().WithMember(avatar: Global, guildAvatar: Server).RunAsync(PfpKind.Avatar, PfpScopes.Server));
        card.Title.Should().Be("Yusuf — Sunucu profil fotoğrafı");
        card.ImageUrl.Should().Be(MemberAvatarUrl(Server));
    }

    [Fact]
    public async Task Pfp_sunucu_never_falls_back_to_the_global_avatar()
    {
        var text = TextOf(await new Kit().WithMember(avatar: Global, guildAvatar: null).RunAsync(PfpKind.Avatar, PfpScopes.Server));
        text.Should().Be("Discord bu üye için bu sunucuya özel bir profil fotoğrafı bildirmedi. Genel fotoğrafı için türü ‘Genel’ seçebilirsin.");
    }

    // ---- Banner: the three rules ----

    [Fact]
    public async Task Banner_auto_prefers_this_servers_banner_without_reading_the_user()
    {
        var kit = new Kit().WithMember(guildBanner: ServerBanner).WithUserBanner(GlobalBanner);
        var card = CardOf(await kit.RunAsync(PfpKind.Banner, null));
        card.Title.Should().Be("Yusuf — Sunucu profili banner’ı");
        card.ImageUrl.Should().Be(PfpCdn.MemberBanner(Guild, Target, ServerBanner));
        kit.Source.UserReads.Should().BeEmpty();
    }

    [Fact]
    public async Task Banner_auto_reads_the_global_banner_with_get_user_when_no_server_banner_was_reported()
    {
        var kit = new Kit().WithMember(guildBanner: null).WithUserBanner(GlobalBanner);
        var card = CardOf(await kit.RunAsync(PfpKind.Banner, PfpScopes.Auto));
        card.Title.Should().Be("Yusuf — Genel banner");
        card.ImageUrl.Should().Be(PfpCdn.UserBanner(Target, GlobalBanner));
        card.Description.Should().Be("Discord bu sunucuya özel bir profil banner’ı bildirmedi; genel banner gösteriliyor.");
        kit.Source.UserReads.Should().Equal(Target);

        var confirmed = new Kit().WithProfile(PfpHash.Of(Global), PfpHash.None, PfpHash.None).WithUserBanner(GlobalBanner);
        CardOf(await confirmed.RunAsync(PfpKind.Banner, null)).Description.Should().BeNull("past a confirmed absence the fallback needs no note");
    }

    [Fact]
    public async Task Banner_auto_without_either_banner_names_both_profiles()
    {
        var text = TextOf(await new Kit().WithMember(guildBanner: null).WithUserBanner(null).RunAsync(PfpKind.Banner, null));
        text.Should().Be("Discord bu kullanıcı için ne genel profilde ne de bu sunucuya özel profilde bir banner görseli bildirmedi.");
    }

    [Fact]
    public async Task Banner_genel_is_always_read_with_get_user_even_when_a_server_banner_exists()
    {
        var kit = new Kit().WithMember(guildBanner: ServerBanner).WithUserBanner(GlobalBanner);
        var card = CardOf(await kit.RunAsync(PfpKind.Banner, PfpScopes.Global));
        card.Title.Should().Be("Yusuf — Genel banner");
        card.ImageUrl.Should().Be(PfpCdn.UserBanner(Target, GlobalBanner));
        kit.Source.UserReads.Should().Equal(Target);
    }

    [Fact]
    public async Task Banner_genel_without_a_global_banner_says_so_and_never_shows_an_avatar()
    {
        var text = TextOf(await new Kit().WithMember(avatar: Global, guildBanner: ServerBanner).WithUserBanner(null).RunAsync(PfpKind.Banner, PfpScopes.Global));
        text.Should().Be("Discord bu kullanıcının genel profili için bir banner görseli bildirmedi.");
    }

    [Fact]
    public async Task Banner_sunucu_shows_only_this_servers_banner_and_never_falls_back()
    {
        var shown = new Kit().WithMember(guildBanner: ServerBanner).WithUserBanner(GlobalBanner);
        CardOf(await shown.RunAsync(PfpKind.Banner, PfpScopes.Server)).ImageUrl.Should().Be(PfpCdn.MemberBanner(Guild, Target, ServerBanner));
        shown.Source.UserReads.Should().BeEmpty();

        var missing = new Kit().WithMember(guildBanner: null).WithUserBanner(GlobalBanner);
        TextOf(await missing.RunAsync(PfpKind.Banner, PfpScopes.Server))
            .Should().Be("Discord bu üye için bu sunucuya özel bir profil banner’ı bildirmedi. Genel banner’ı için türü ‘Genel’ seçebilirsin.");
        missing.Source.UserReads.Should().BeEmpty("an explicit Sunucu never looks at the global banner");
    }

    [Fact]
    public async Task Animated_pictures_keep_their_animation()
    {
        var animated = "a_" + Server;
        CardOf(await new Kit().WithMember(guildAvatar: animated).RunAsync(PfpKind.Avatar, PfpScopes.Server)).ImageUrl
            .Should().Be($"https://cdn.discordapp.com/guilds/{Guild}/users/{Target}/avatars/{animated}.webp?size=2048&animated=true");
        CardOf(await new Kit().WithMember().WithUserBanner("a_" + GlobalBanner).RunAsync(PfpKind.Banner, PfpScopes.Global)).ImageUrl
            .Should().Be($"https://cdn.discordapp.com/banners/{Target}/a_{GlobalBanner}.webp?size=2048&animated=true");
    }

    // ---- Failures are never "no picture" ----

    [Fact]
    public async Task A_member_who_is_not_in_the_server_is_told_apart_from_a_missing_picture()
    {
        var kit = new Kit();
        kit.Source.Member = _ => Task.FromResult(PfpMemberLookup.NotMember);
        (await kit.RunAsync(PfpKind.Avatar, null)).Public.Single().Text.Should().Contain("bu sunucunun üyesi değil");
    }

    [Theory]
    [InlineData(PfpKind.Avatar, null)]
    [InlineData(PfpKind.Avatar, PfpScopes.Server)]
    [InlineData(PfpKind.Banner, PfpScopes.Global)]
    public async Task A_failed_member_read_is_unavailable(PfpKind kind, string? scope)
    {
        var kit = new Kit(); // member read: Failed (403, 429, 5xx, timeout in the adapter)
        TextOf(await kit.RunAsync(kind, scope)).Should().Be("Profil bilgisi şu anda alınamadı. Biraz sonra tekrar dene.");
        kit.Source.UserReads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PfpScopes.Global)]
    public async Task A_failed_user_read_is_unavailable_never_no_banner(string? scope)
    {
        var kit = new Kit().WithMember(guildBanner: null); // user read: Failed
        TextOf(await kit.RunAsync(PfpKind.Banner, scope)).Should().Be("Profil bilgisi şu anda alınamadı. Biraz sonra tekrar dene.");
    }

    [Fact]
    public async Task A_read_that_runs_past_the_budget_is_cancelled_and_still_completes_the_response()
    {
        var kit = new Kit();
        kit.Source.Member = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return PfpMemberLookup.Failed;
        };
        var responder = new FakeResponder();
        var run = kit.Flow.RunAsync(new PfpRequest(Guild, Invoker, Target, PfpKind.Avatar, null, "tr"), kit.Source, responder);
        responder.Events.Should().Equal("defer");
        kit.Clock.Advance(PfpCommandFlow.LookupBudget);
        await run;
        TextOf(responder).Should().Be("Profil bilgisi şu anda alınamadı. Biraz sonra tekrar dene.");
    }

    [Fact]
    public async Task An_unexpected_exception_still_completes_the_public_response_once()
    {
        var kit = new Kit();
        kit.Source.Member = _ => throw new InvalidOperationException("boom");
        TextOf(await kit.RunAsync(PfpKind.Banner, null)).Should().Be("Profil bilgisi şu anda alınamadı. Biraz sonra tekrar dene.");
    }

    [Theory]
    [InlineData("not-a-hash", null)]
    [InlineData(null, "https://example.com/avatar.png")]
    public async Task Unexpected_data_from_discord_is_unavailable_not_a_picture(string? guildAvatar, string? defaultUrl)
    {
        var kit = new Kit();
        kit.Source.Member = _ => Task.FromResult(PfpMemberLookup.Found(new PfpMemberProfile(Target, "Yusuf", PfpHash.None,
            PfpHash.FromOptionalField(guildAvatar), PfpHash.NotReported, defaultUrl ?? DefaultUrl)));
        TextOf(await kit.RunAsync(PfpKind.Avatar, null)).Should().Be("Profil bilgisi şu anda alınamadı. Biraz sonra tekrar dene.");
    }

    // ---- Lifecycle, cooldown, card ----

    [Fact]
    public async Task An_invalid_type_is_refused_privately_before_any_acknowledgement_or_read()
    {
        var kit = new Kit().WithMember();
        var responder = await kit.RunAsync(PfpKind.Avatar, "everyone");
        responder.Events.Should().Equal("private");
        kit.Source.MemberReads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_cooldown_is_shared_by_both_commands_per_guild_and_member_and_refuses_privately()
    {
        var kit = new Kit().WithMember().WithUserBanner(GlobalBanner);
        (await kit.RunAsync(PfpKind.Avatar, null)).Events.Should().Equal("defer", "public");

        var refused = await kit.RunAsync(PfpKind.Banner, null);
        refused.Events.Should().Equal("private");
        refused.Private.Single().Should().Be("Biraz bekle: 5 saniye sonra tekrar deneyebilirsin.");
        kit.Source.MemberReads.Should().HaveCount(1, "a refused command reads nothing");

        (await kit.RunAsync(PfpKind.Avatar, null, invoker: Invoker + 1)).Events.Should().Equal("defer", "public");
        (await kit.RunAsync(PfpKind.Avatar, null, guild: OtherGuild)).Events.Should().Equal("defer", "public");

        kit.Clock.Advance(TimeSpan.FromSeconds(4.2));
        (await kit.RunAsync(PfpKind.Avatar, null)).Private.Single().Should().Contain("1 saniye");
        kit.Clock.Advance(TimeSpan.FromSeconds(0.8));
        (await kit.RunAsync(PfpKind.Avatar, null)).Events.Should().Equal("defer", "public");
    }

    [Fact]
    public async Task The_cooldown_key_is_guild_and_invoker_never_the_chosen_target()
    {
        const ulong OtherTarget = 333333333333333333;
        var kit = new Kit().WithMember().WithUserBanner(GlobalBanner);
        (await kit.RunAsync(PfpKind.Avatar, null, target: Target)).Events.Should().Equal("defer", "public");
        (await kit.RunAsync(PfpKind.Banner, null, target: OtherTarget)).Events.Should().Equal(["private"], "choosing someone else does not reset the invoker's cooldown");
        (await kit.RunAsync(PfpKind.Avatar, null, target: Invoker)).Events.Should().Equal("private");

        (await kit.RunAsync(PfpKind.Avatar, null, invoker: Invoker + 1, target: Target)).Events.Should().Equal("defer", "public");
        (await kit.RunAsync(PfpKind.Banner, null, invoker: Invoker + 2, target: Target)).Events.Should().Equal(["defer", "public"],
            "other members asking for the same target are not blocked by each other");
        kit.Source.MemberReads.Should().Equal((Guild, Target), (Guild, Target), (Guild, Target));
    }

    [Fact]
    public async Task The_member_is_read_in_this_guild_for_the_chosen_target()
    {
        var kit = new Kit().WithMember();
        await kit.RunAsync(PfpKind.Avatar, null, guild: OtherGuild);
        kit.Source.MemberReads.Should().Equal((OtherGuild, Target));
        CardOf(await new Kit().WithMember(guildAvatar: Server).RunAsync(PfpKind.Avatar, null, guild: OtherGuild)).ImageUrl
            .Should().Contain($"/guilds/{OtherGuild}/", "the server picture belongs to the guild the command was used in");
    }

    [Fact]
    public void Uye_left_empty_means_the_member_who_ran_the_command()
    {
        PfpCommands.TargetId(null, Invoker).Should().Be(Invoker);
        PfpCommands.TargetId(InterfaceFake.Create<IUser>(new() { ["Id"] = Target }), Invoker).Should().Be(Target);
    }

    [Fact]
    public async Task The_card_shows_the_picture_large_with_one_link_button_to_the_same_url_and_pings_nobody()
    {
        var card = CardOf(await new Kit().WithMember(guildAvatar: Server, name: "@everyone <@1>").RunAsync(PfpKind.Avatar, null));
        card.Title.Should().NotContain("@everyone").And.NotContain("<@1>").And.EndWith("— Sunucu profil fotoğrafı");
        card.Color.Should().Be(PfpCards.Color);

        var embed = PfpCommands.ToEmbed(card);
        embed.Image!.Value.Url.Should().Be(MemberAvatarUrl(Server));
        embed.Thumbnail.Should().BeNull("the picture is the large image, never only a thumbnail");
        embed.Title.Should().Be(card.Title);

        var row = PfpCommands.ToComponents(card).Components.Should().ContainSingle().Subject.Should().BeOfType<ActionRowComponent>().Subject;
        var button = row.Components.Should().ContainSingle().Subject.Should().BeOfType<ButtonComponent>().Subject;
        (button.Label, button.Style, button.Url).Should().Be(("Tam boy aç", ButtonStyle.Link, embed.Image!.Value.Url));
        button.CustomId.Should().BeNull("a link button, nothing for the bot to handle");
    }

    [Fact]
    public void The_adapter_maps_only_the_members_own_fields()
    {
        var member = InterfaceFake.Create<IGuildUser>(new()
        {
            ["Id"] = Target,
            ["DisplayName"] = "Yusuf",
            ["AvatarId"] = Global,
            ["GuildAvatarId"] = Server,
            ["GuildBannerHash"] = ServerBanner,
            ["BannerId"] = "this-is-the-nested-users-banner-and-is-never-used",
            ["GetDefaultAvatarUrl"] = DefaultUrl,
        });
        DiscordPfpProfileSource.ToProfile(member).Should().Be(
            new PfpMemberProfile(Target, "Yusuf", PfpHash.Of(Global), PfpHash.Of(Server), PfpHash.Of(ServerBanner), DefaultUrl));

        var bare = InterfaceFake.Create<IGuildUser>(new() { ["Id"] = Target, ["DisplayName"] = "Yusuf", ["GetDefaultAvatarUrl"] = DefaultUrl });
        DiscordPfpProfileSource.ToProfile(bare).Should().Be(
            new PfpMemberProfile(Target, "Yusuf", PfpHash.None, PfpHash.NotReported, PfpHash.NotReported, DefaultUrl),
            "a null always-sent avatar is no avatar; a null optional server picture is only not reported");
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    public void Transport_failures_are_read_failures(Type type) =>
        DiscordPfpProfileSource.IsReadFailure((Exception)Activator.CreateInstance(type)!).Should().BeTrue();
}
