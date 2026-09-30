using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Quote.Application;
using ToroSquad.Modules.Quote.Commands;
using ToroSquad.Tests.Support;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Apps → Quote, the MESSAGE command next to /quote: it registers next to the slash command without touching any other
/// command, it takes the message from the interaction payload (never a REST message read), and from there it runs the same
/// checks' tail, text rules, card builder, renderer and privacy rules as /quote.
/// </summary>
public sealed class QuoteContextMenuTests
{
    private static readonly GuildId Guild = new(689812743242514448);
    private static readonly UserId Member = new(500000000000000001);
    private static readonly ChannelId Here = new(1200000000000000001);
    private static readonly ChannelId Other = new(1200000000000000002);
    private static readonly ChannelId PrivateThread = new(1200000000000000003);
    private static readonly ChannelId Nsfw = new(1200000000000000004);
    private static readonly ChannelId Forum = new(1200000000000000005);
    private const ulong Author = 500000000000000002;
    private const ulong Bot = 400000000000000001;
    private const string GuildAvatar = "https://cdn.discordapp.com/guilds/689812743242514448/users/500000000000000002/avatars/abc.png?size=1024";
    private const string AccountAvatar = "https://cdn.discordapp.com/avatars/500000000000000002/def.png?size=1024";
    private const CorePermission Read = CorePermission.ViewChannel | CorePermission.ReadMessageHistory;

    // ---------------------------------------------------------------- manifest and sync

    private static async Task<(CommandManifest Manifest, IReadOnlySet<string> Admin)> ManifestAsync()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        return (await interactions.InitializeAsync(host.Services), interactions.AdminCommandNames);
    }

    [Fact]
    public async Task The_message_command_and_the_slash_command_coexist()
    {
        var (manifest, _) = await ManifestAsync();
        var slash = manifest.Find("quote")!;
        var apps = manifest.Find("Quote")!;

        slash.Type.Should().Be(CommandKind.ChatInput);
        slash.Options.Select(o => o.Name).Should().Equal("message", "channel");

        apps.Type.Should().Be(CommandKind.Message);
        apps.OwnerModule.Should().Be("quote");
        apps.Description.Should().BeEmpty();
        apps.DescriptionLocalizations.Should().BeEmpty();
        apps.Options.Should().BeEmpty();
        apps.Contexts.Should().Equal(0);
        apps.IntegrationTypes.Should().Equal(0);
        apps.DefaultMemberPermissions.Should().BeNull("everyone may quote once the module is on");
        apps.Display.Should().Be("Apps → Quote (message command)");
        CommandManifest.CanonicalJson(apps, includeGlobalOnlyFields: false).Should().Be(
            """{"type":3,"name":"Quote","description":"","description_localizations":null,"default_member_permissions":null,"nsfw":false,"options":[]}""");

        manifest.Commands.Where(c => c.Type == CommandKind.Message).Should().ContainSingle("Quote is the only context-menu command");
        manifest.Commands.Should().NotContain(c => c.Type == CommandKind.User);
    }

    [Fact]
    public async Task Dry_run_against_the_current_guild_creates_only_the_message_command_and_deletes_nothing()
    {
        var (manifest, admin) = await ManifestAsync();
        // What the main guild has today: every slash command, exactly as this manifest defines it, all created by this tool.
        var remote = manifest.Commands.Where(c => c.Type == CommandKind.ChatInput)
            .Select((c, i) => new RemoteCommand((ulong)(1000 + i), c.Name, CommandManifest.CanonicalJson(c, false))).ToList();
        var managed = remote.ToDictionary(r => r.Name, r => r.Id);

        foreach (var prune in new[] { false, true })
        {
            var plan = CommandSyncPlanner.Plan(manifest, remote, managed, Request(prune, admin));
            plan.IsBlocked.Should().BeFalse(string.Join("; ", plan.BlockingErrors));
            plan.Items.Where(i => i.Action == SyncAction.Create).Should().ContainSingle()
                .Which.Should().Be(new SyncPlanItem(SyncAction.Create, "Quote", null, Kind: CommandKind.Message));
            plan.Items.Where(i => i.Action != SyncAction.Create).Should().OnlyContain(i => i.Action == SyncAction.Unchanged && i.Kind == CommandKind.ChatInput);
            plan.Items.Count(i => i.Action == SyncAction.Unchanged).Should().Be(remote.Count);
            plan.Items.Single(i => i.Name == "quote").Action.Should().Be(SyncAction.Unchanged, "/quote itself does not change");
        }

        // After the create, Discord returns the message command; it maps back to the same payload, so the next run is a no-op.
        var created = InterfaceFake.Create<IApplicationCommand>(new()
        {
            ["Id"] = 2000UL,
            ["Type"] = ApplicationCommandType.Message,
            ["Name"] = "Quote",
            ["Description"] = "",
            ["Options"] = (IReadOnlyCollection<IApplicationCommandOption>)Array.Empty<IApplicationCommandOption>(),
        });
        var fromDiscord = DiscordCommandRegistrar.FromRemote(created);
        fromDiscord.Type.Should().Be(CommandKind.Message);
        var after = remote.Append(new RemoteCommand(2000, "Quote", CommandManifest.CanonicalJson(fromDiscord, false), CommandKind.Message)).ToList();
        var second = CommandSyncPlanner.Plan(manifest, after, managed, Request(prune: true, admin));
        second.Items.Should().OnlyContain(i => i.Action == SyncAction.Unchanged);
        second.HasChanges.Should().BeFalse();
    }

    private static SyncRequest Request(bool prune, IReadOnlySet<string> admin) =>
        new(new SyncScope.Guild(Guild.Value), 7, 7, new HashSet<ulong> { Guild.Value }, false, prune, admin);

    [Fact]
    public async Task Registration_payload_has_the_message_type_and_no_description()
    {
        var (manifest, _) = await ManifestAsync();
        var apps = DiscordCommandRegistrar.ToApplicationProperties(manifest.Find("Quote")!);
        apps.Should().BeOfType<MessageCommandProperties>();
        apps.Name.Value.Should().Be("Quote");
        DiscordCommandRegistrar.ToApplicationProperties(manifest.Find("quote")!).Should().BeOfType<SlashCommandProperties>();
    }

    [Fact]
    public void Validator_applies_discords_message_command_rules()
    {
        static ManifestCommand Apps(string name, string description = "", int options = 0) => new(name, description, new Dictionary<string, string>(),
            Enumerable.Range(0, options).Select(i => new ManifestOption(OptionType.String, $"o{i}", "d", new Dictionary<string, string> { ["tr"] = "d" },
                false, [], [], false, null, null, null, null, [])).ToList(), null, [0], [0], false, "quote", CommandKind.Message);
        IReadOnlyList<string> Validate(params ManifestCommand[] commands) => CommandManifestValidator.Validate(new CommandManifest(commands, []), new HashSet<string>());

        Validate(Apps("Quote")).Should().BeEmpty();
        Validate(Apps("Quote This Message")).Should().BeEmpty("MESSAGE commands may be mixed case and include spaces");
        Validate(Apps("Quote", description: "not allowed")).Should().Contain(e => e.Contains("no description", StringComparison.Ordinal));
        Validate(Apps("Quote", options: 1)).Should().Contain(e => e.Contains("no options", StringComparison.Ordinal));
        Validate(Apps(new string('Q', 33))).Should().Contain(e => e.Contains("invalid name", StringComparison.Ordinal));
        Validate(Apps(" Quote")).Should().Contain(e => e.Contains("invalid name", StringComparison.Ordinal));
        Validate(Enumerable.Range(0, 16).Select(i => Apps($"Q{i}")).ToArray())
            .Should().Contain(e => e.Contains("too many message commands", StringComparison.Ordinal));

        // A slash command keeps its own rules: lowercase only.
        var slashUpper = new ManifestCommand("Quote", "d", new Dictionary<string, string> { ["tr"] = "d" }, [], null, [0], [0], false, "quote");
        Validate(slashUpper).Should().Contain(e => e.Contains("invalid name", StringComparison.Ordinal));
        // One name, one command, whatever the type (the sync is keyed by name).
        var slashSame = slashUpper with { Name = "quote" };
        Validate(slashSame, Apps("quote")).Should().Contain(e => e.Contains("duplicate command name", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the payload message → the module's model

    private static IUserMessage Message(string content, ChannelId? channel = null, bool webhook = false,
        IAttachment[]? attachments = null, IStickerItem[]? stickers = null, MessageType type = MessageType.Default, ulong[]? mentions = null) =>
        InterfaceFake.Create<IUserMessage>(new()
        {
            ["Id"] = 1300000000000000002UL,
            ["Content"] = content,
            ["Type"] = type,
            ["Channel"] = InterfaceFake.Create<IMessageChannel>(new() { ["Id"] = (channel ?? Here).Value }),
            ["Author"] = InterfaceFake.Create<IUser>(new()
            {
                ["Id"] = Author,
                ["Username"] = "kiral.",
                ["GlobalName"] = "Kıral",
                ["IsWebhook"] = webhook,
                ["GetDisplayAvatarUrl"] = AccountAvatar,
            }),
            ["Attachments"] = (IReadOnlyCollection<IAttachment>)(attachments ?? []),
            ["Embeds"] = (IReadOnlyCollection<IEmbed>)Array.Empty<IEmbed>(),
            ["Components"] = (IReadOnlyCollection<IMessageComponent>)Array.Empty<IMessageComponent>(),
            ["Stickers"] = (IReadOnlyCollection<IStickerItem>)(stickers ?? []),
            ["MentionedUserIds"] = (IReadOnlyCollection<ulong>)(mentions ?? []),
        });

    private static IGuildUser Nicknamed() => InterfaceFake.Create<IGuildUser>(new()
    {
        ["Id"] = Author,
        ["DisplayName"] = "KEŞKE",
        ["GetDisplayAvatarUrl"] = GuildAvatar,
    });

    private static QuoteSourceMessage Selected(IUserMessage message, IGuildUser? member = null) =>
        QuoteMessageMapper.ToSource(message, QuoteMessageMapper.Author(message.Author, member), QuoteMentionNames.Empty,
            QuoteMessageMapper.Shape(message, Bot, deliveredByInteraction: true, applicationHasContentAccess: null));

    [Fact]
    public void The_payload_message_maps_to_the_same_model_as_slash()
    {
        var mapped = Selected(Message("istediğiniz kadar saat alın"), Nicknamed());
        mapped.Id.Should().Be(new MessageId(1300000000000000002));
        mapped.Channel.Should().Be(Here);
        mapped.RawContent.Should().Be("istediğiniz kadar saat alın", "the content comes from the payload as delivered");
        mapped.Author.Should().Be(new QuoteAuthor(new UserId(Author), "KEŞKE", "kiral.", GuildAvatar), "server nickname and server avatar first");
        mapped.TextAvailability.Should().Be(QuoteTextAvailability.Available);

        Selected(Message("x")).Author.Should().Be(new QuoteAuthor(new UserId(Author), "Kıral", "kiral.", AccountAvatar), "not a member: global name and account avatar");
    }

    [Fact]
    public void An_empty_payload_message_is_never_reported_as_withheld()
    {
        // Discord always sends the content of the message a context-menu command is used on; empty means there is no text.
        Selected(Message("")).TextAvailability.Should().Be(QuoteTextAvailability.NoTextInMessage);
        Selected(Message("", attachments: [InterfaceFake.Create<IAttachment>(new() { ["Id"] = 1UL })])).TextAvailability
            .Should().Be(QuoteTextAvailability.NoTextInMessage);
        Selected(Message("", stickers: [InterfaceFake.Create<IStickerItem>(new() { ["Id"] = 1UL })])).TextAvailability
            .Should().Be(QuoteTextAvailability.NoTextInMessage);

        // The same empty message read by id (/quote) without Message Content access is the "withheld" case — only there.
        var viaSlash = QuoteMessageMapper.Shape(Message(""), Bot, deliveredByInteraction: false, applicationHasContentAccess: false);
        viaSlash.Classify().Should().Be(QuoteTextAvailability.ProbablyWithheld);
        QuoteMessageMapper.NeedsContentAccessFlag(Message(""), Bot).Should().BeTrue();
        QuoteMessageMapper.NeedsContentAccessFlag(Message("", mentions: [Bot]), Bot).Should().BeFalse("a message mentioning the bot is always delivered");
    }

    // ---------------------------------------------------------------- resolution: no fetch, same tail

    private sealed class CountingDiscord : IQuoteDiscord
    {
        public Dictionary<ChannelId, QuoteChannel> Channels { get; } = new()
        {
            [Here] = new QuoteChannel(Here, true, false, Here, false),
            [Other] = new QuoteChannel(Other, true, false, Other, false),
            [PrivateThread] = new QuoteChannel(PrivateThread, true, true, Here, false),
            [Nsfw] = new QuoteChannel(Nsfw, true, false, Nsfw, true),
            [Forum] = new QuoteChannel(Forum, false, false, Forum, false),
        };

        public CorePermission? MemberPermissions { get; set; } = Read;

        public int MessageReads { get; private set; }

        public QuoteChannel? GetChannel(GuildId guild, ChannelId channel) => guild == Guild && Channels.TryGetValue(channel, out var c) ? c : null;

        public Task<CorePermission?> GetMemberPermissionsAsync(GuildId guild, UserId member, ChannelId channel, CancellationToken cancellationToken) =>
            Task.FromResult(MemberPermissions);

        public Task<QuoteFetch> GetMessageAsync(GuildId guild, ChannelId channel, MessageId message, CancellationToken cancellationToken)
        {
            MessageReads++;
            return Task.FromResult(QuoteFetch.Failed);
        }
    }

    private readonly CountingDiscord _discord = new();
    private readonly FakeGuildGateway _gateway = new();
    private readonly CapturingLoggers _logs = new();

    public QuoteContextMenuTests()
    {
        foreach (var channel in new[] { Here, Other, Nsfw, Forum })
            _gateway.SetChannel(Guild, channel, new BotChannelAccess(true, true, CorePermission.ViewChannel | CorePermission.AttachFiles));
    }

    private Task<QuoteResolution> ResolveSelected(QuoteSourceMessage message, ChannelId? invokedIn = null) =>
        new QuoteMessageResolver(_gateway, _logs.For<QuoteMessageResolver>()).ResolveSelectedAsync(_discord,
            new QuoteSelection(Guild, Member, invokedIn ?? message.Channel, message),
            CultureInfo.GetCultureInfo("tr-TR"), TimeZoneInfo.Utc, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_selected_message_resolves_without_any_rest_message_read()
    {
        var mentions = new QuoteMentionNames(new Dictionary<ulong, string> { [Author] = "KEŞKE" }, new Dictionary<ulong, string>(), new Dictionary<ulong, string>());
        var message = Selected(Message("**İstediğiniz** kadar saat alın\nmaalesef 24 saatten fazla <@500000000000000002> ışığı çağ öğün"), Nicknamed())
            with
        { Mentions = mentions };

        var result = await ResolveSelected(message);

        result.Succeeded.Should().BeTrue();
        result.Text.Should().Be("İstediğiniz kadar saat alın\nmaalesef 24 saatten fazla @KEŞKE ışığı çağ öğün",
            "the same QuoteText rules: markdown out, mention as name, newline and Turkish letters kept");
        result.Message!.Author.DisplayName.Should().Be("KEŞKE");
        _discord.MessageReads.Should().Be(0, "Apps → Quote never reads the message from Discord");
        _logs.Lines.Should().Contain(l => l.Contains("via=apps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_and_attachment_only_messages_get_the_no_text_answer()
    {
        (await ResolveSelected(Selected(Message("")))).Failure.Should().Be(QuoteFailure.NoText);
        (await ResolveSelected(Selected(Message("", attachments: [InterfaceFake.Create<IAttachment>(new() { ["Id"] = 1UL })]))))
            .MessageKey.Should().Be("quote.no_text");
        (await ResolveSelected(Selected(Message("", type: MessageType.ChannelPinnedMessage)))).Failure.Should().Be(QuoteFailure.NoText);
        _discord.MessageReads.Should().Be(0);
    }

    [Fact]
    public async Task Without_attach_files_or_view_channel_for_the_bot_nothing_is_drawn()
    {
        _gateway.SetChannel(Guild, Here, new BotChannelAccess(true, true, Read | CorePermission.SendMessages));
        (await ResolveSelected(Selected(Message("metin")))).Failure.Should().Be(QuoteFailure.BotCannotAttach);
        _gateway.SetChannel(Guild, Here, new BotChannelAccess(true, true, CorePermission.AttachFiles));
        (await ResolveSelected(Selected(Message("metin")))).MessageKey.Should().Be("quote.bot_cannot_attach");
        _discord.MessageReads.Should().Be(0);
    }

    [Theory]
    [InlineData(CorePermission.None)]
    [InlineData(CorePermission.ViewChannel)]
    [InlineData(CorePermission.ReadMessageHistory)]
    public async Task A_member_without_view_and_history_is_refused_server_side(CorePermission permissions)
    {
        _discord.MemberPermissions = permissions;
        (await ResolveSelected(Selected(Message("metin")))).Failure.Should().Be(QuoteFailure.NotFound);
        _discord.MemberPermissions = null; // not a member (any more)
        (await ResolveSelected(Selected(Message("metin")))).Failure.Should().Be(QuoteFailure.NotFound);
        _discord.MessageReads.Should().Be(0);
    }

    [Fact]
    public async Task The_payload_message_must_be_in_the_channel_the_command_was_used_in()
    {
        (await ResolveSelected(Selected(Message("başka kanal", Other)), invokedIn: Here)).Failure.Should().Be(QuoteFailure.NotFound);
        (await ResolveSelected(Selected(Message("forum", Forum)))).Failure.Should().Be(QuoteFailure.NotFound, "not a message channel");
        (await ResolveSelected(Selected(Message("bilinmeyen", new ChannelId(1299999999999999999))))).Failure.Should().Be(QuoteFailure.NotFound);
        _discord.MessageReads.Should().Be(0);
    }

    [Fact]
    public async Task Private_threads_and_age_restricted_channels_work_because_the_card_stays_where_the_message_is()
    {
        // /quote refuses both (the card could leave the thread / the age-restricted channel); here source = destination.
        var inThread = await ResolveSelected(Selected(Message("özel thread", PrivateThread)));
        inThread.Succeeded.Should().BeTrue();
        var nsfw = await ResolveSelected(Selected(Message("+18 kanal", Nsfw)));
        nsfw.Succeeded.Should().BeTrue();

        // The thread's parent decides whether the bot may attach the card.
        _gateway.SetChannel(Guild, Here, new BotChannelAccess(true, true, Read));
        (await ResolveSelected(Selected(Message("özel thread", PrivateThread)))).Failure.Should().Be(QuoteFailure.BotCannotAttach);
        _discord.MessageReads.Should().Be(0);
    }

    // ---------------------------------------------------------------- same card, same privacy

    private sealed class ThrowingRenderer : IQuoteRenderer
    {
        public QuoteCard Render(QuoteRenderModel model) => throw new InvalidOperationException("could not draw: " + model.Text + " by " + model.DisplayName);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private QuoteCardBuilder Builder(IQuoteRenderer? renderer = null) => new(
        new QuoteAvatarClient(new Factory(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))),
            new FakeTimeProvider(), _logs.For<QuoteAvatarClient>()),
        renderer ?? new QuoteImageRenderer(QuoteFonts.Load()), _logs.For<QuoteCardBuilder>());

    [Fact]
    public async Task A_selected_message_becomes_the_same_png_and_nothing_personal_is_logged()
    {
        const string Body = "GİZLİ istediğiniz kadar saat alın maalesef 24 saatten fazla vaktiniz yok";
        var resolution = await ResolveSelected(Selected(Message(Body), Nicknamed()));
        var built = await Builder().BuildAsync(resolution.Message!, resolution.Text, TestContext.Current.CancellationToken);

        built.Card.Should().NotBeNull();
        var info = SixLabors.ImageSharp.Image.Identify(built.Card!.Png);
        (info.Width, info.Height).Should().Be((1600, 800));
        built.Card.Layout.AvatarUsed.Should().BeFalse("the avatar download answered 404: the fallback panel is used");

        var failed = await Builder(new ThrowingRenderer()).BuildAsync(resolution.Message!, resolution.Text, TestContext.Current.CancellationToken);
        failed.Card.Should().BeNull();
        failed.TraceCode.Should().StartWith("TS-");

        _logs.Lines.Should().NotBeEmpty();
        _logs.Lines.Should().NotContain(l => l.Contains("GİZLİ", StringComparison.Ordinal) || l.Contains("vaktiniz", StringComparison.Ordinal),
            "no message body, even when drawing fails with an exception that repeats it");
        _logs.Lines.Should().NotContain(l => l.Contains("KEŞKE", StringComparison.Ordinal) || l.Contains("kiral.", StringComparison.Ordinal)
                                             || l.Contains(GuildAvatar, StringComparison.Ordinal) || l.Contains(AccountAvatar, StringComparison.Ordinal),
            "no names, no avatar urls");
        _discord.MessageReads.Should().Be(0);
    }

    // ---------------------------------------------------------------- gate, guild guard, interaction

    [Fact]
    public async Task The_message_command_is_behind_the_quote_module_gate()
    {
        var gate = typeof(QuoteCommands).GetCustomAttribute<ToroModuleAttribute>()!;
        gate.ModuleId.Should().Be("quote");
        gate.AllowWhenDisabled.Should().BeFalse();
        typeof(QuoteCommands).GetMethod(nameof(QuoteCommands.QuoteMessageAsync))!.GetCustomAttribute<global::Discord.Interactions.MessageCommandAttribute>()!
            .Name.Should().Be("Quote");

        await using var host = await TestHost.CreateAsync();
        var context = InterfaceFake.Create<IInteractionContext>(new()
        {
            ["Guild"] = InterfaceFake.Create<IGuild>(new() { ["Id"] = Guild.Value, ["OwnerId"] = 1UL }),
            ["User"] = InterfaceFake.Create<IGuildUser>(new() { ["Id"] = Member.Value, ["RoleIds"] = (IReadOnlyCollection<ulong>)Array.Empty<ulong>() }),
        });
        await host.InScopeAsync(async sp =>
        {
            (await gate.CheckRequirementsAsync(context, null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError, "off by default");
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "quote", true, CancellationToken.None))
                .Succeeded.Should().BeTrue();
            (await gate.CheckRequirementsAsync(context, null!, sp)).IsSuccess.Should().BeTrue();
            var dm = InterfaceFake.Create<IInteractionContext>(new() { ["User"] = InterfaceFake.Create<IUser>(new() { ["Id"] = Member.Value }) });
            (await gate.CheckRequirementsAsync(dm, null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.GuildOnlyError);
        });

        // Unsupported guilds never reach any command, slash or context menu: the gateway refuses the interaction first.
        new DeploymentPolicy(true, new HashSet<ulong>(), new HashSet<ulong> { Guild.Value }, "Railway").IsGuildAllowed(618763184815472651UL).Should().BeFalse();
        var gateway = File.ReadAllText(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Discord", "Interactions", "GatewayBotService.cs"));
        gateway.IndexOf("IsGuildAllowed(interaction.GuildId)", StringComparison.Ordinal).Should()
            .BeLessThan(gateway.IndexOf("ExecuteCommandAsync", StringComparison.Ordinal), "the guild guard runs before any command");
    }

    [Fact]
    public void The_handler_defers_maps_the_payload_and_posts_through_the_shared_path()
    {
        var root = Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Quote", "Commands");
        var commands = File.ReadAllText(Path.Combine(root, "QuoteCommands.cs"));
        var handler = Body(commands, "public async Task QuoteMessageAsync(IMessage message)");
        handler.IndexOf("await DeferEphemeralAsync();", StringComparison.Ordinal).Should().Be(handler.IndexOf("await", StringComparison.Ordinal), "it defers first");
        handler.Should().Contain("DescribeSelectedAsync(Actor.GuildId, message,").And.Contain("ResolveSelectedAsync(").And.Contain("await PostAsync(resolution, settings);");
        handler.Should().NotContain("GetMessageAsync").And.NotContain("ResolveAsync(");

        var describe = Body(File.ReadAllText(Path.Combine(root, "DiscordQuoteSource.cs")), "public async Task<QuoteSourceMessage> DescribeSelectedAsync(");
        describe.Should().NotContain("GetMessageAsync", "the payload message is mapped, never re-read").And.Contain("deliveredByInteraction: true");

        // One way out for both commands: the single public follow-up with the file and no pings.
        Regex.Matches(commands, @"FollowupWithFileAsync\(").Should().ContainSingle();
        Body(commands, "private async Task PostAsync(").Should().Contain("FollowupWithFileAsync(png, FileName, ephemeral: false, allowedMentions: NoPings)");
    }

    /// <summary>The text of the member that starts with <paramref name="signature"/>, up to its closing brace.</summary>
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, signature);
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
                return code[start..(i + 1)];
        }

        return code[start..];
    }
}
