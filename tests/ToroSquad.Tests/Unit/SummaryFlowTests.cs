using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// /ozetle end to end without Discord or the AI provider: every refusal costs zero AI requests, one run costs at most one,
/// failures are never retried and never fall back to another model, cooldowns and the per-channel lock hold under
/// concurrency, the answer is posted publicly (and only then), and no log line ever contains message text, names, the
/// prompt or the answer.
/// </summary>
public sealed partial class SummaryFlowTests
{
    private const string Secret = "GİZLİ-MESAJ-7d3f çok özel bir cümle";
    private const string SecretName = "GizliİsimUye";
    private const string Answer = "# Son Mesajların Özeti\n\n## Ana konu\nCEVAP-METNİ-9x derbi konuşuldu.\n\n## Önemli noktalar\n- **Futbol:** Konuşuldu.\n\n## Genel atmosfer\nSamimi.";

    private static readonly GuildId Guild = new(689812743242514448);
    private static readonly ChannelId Here = new(1200000000000000001);
    private static readonly ChannelId Other = new(1200000000000000002);
    private static readonly ChannelId Third = new(1200000000000000003);
    private static readonly UserId Member = new(500000000000000001);
    private static readonly UserId Member2 = new(500000000000000002);
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    /// <summary>One of the six default allowed roles (any one is enough).</summary>
    private const ulong AllowedRole = 702465621992144926;

    private sealed class CapturingLoggers
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger<T> For<T>() => new Logger<T>(this);

        private sealed class Logger<T>(CapturingLoggers owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                owner.Lines.Enqueue(formatter(state, exception));
                if (exception is not null)
                    owner.Lines.Enqueue(exception.ToString());
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var (_, value) in values)
                        owner.Lines.Enqueue(value?.ToString() ?? "");
                }
            }
        }
    }

    private sealed class FakeAi : ISummaryAiClient
    {
        private int _calls;

        public Func<SummaryPromptMessages, Task<SummaryAiResult>> Respond { get; set; } =
            _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, Answer, "stop", new SummaryAiUsage(3000, 800, 200), TimeSpan.FromSeconds(6), 200));

        public ConcurrentQueue<SummaryPromptMessages> Prompts { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public string Model => "deepseek-v4.1-flash";

        public bool IsConfigured { get; set; } = true;

        public Task<SummaryAiResult> SummarizeAsync(SummaryPromptMessages prompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Prompts.Enqueue(prompt);
            return Respond(prompt);
        }
    }

    /// <summary>
    /// A guild whose channels each have their own history (<see cref="Channels"/>; <see cref="Messages"/> for any other
    /// channel), served newest first in pages of 100 like Discord. Counts every history page read.
    /// </summary>
    private sealed class FakeDiscord : ISummaryDiscord
    {
        private int _fetches;

        public bool Supported { get; set; } = true;

        public GuildPermission? Invoker { get; set; } = GuildPermission.ViewChannel | GuildPermission.ReadMessageHistory | GuildPermission.SendMessages;

        public SummaryFetchStatus Status { get; set; } = SummaryFetchStatus.Ok;

        public List<SummarySourceMessage> Messages { get; set; } = Conversation(12);

        public Dictionary<ulong, List<SummarySourceMessage>> Channels { get; } = [];

        public Dictionary<ulong, string> Roles { get; } = new()
        {
            [1338605015417487440] = "TSQ Yönetim",
            [1254401028359458887] = "Moderatör",
            [700799880549105674] = "VIP",
            [702465621992144926] = "Destekçi",
            [1066826260803764234] = "Oyuncu",
            [1333687724669931602] = "Yayıncı",
        };

        public bool? ContentAccess { get; set; } = true;

        public int Fetches => Volatile.Read(ref _fetches);

        public ConcurrentQueue<ulong> ReadChannels { get; } = new();

        /// <summary><see cref="Thread"/> is a thread of <see cref="Here"/>: access is decided in the parent, history is its own.</summary>
        public SummaryChannel? GetChannel(GuildId guild, ChannelId channel) => new(channel, Supported, channel == Thread ? Here : channel);

        public GuildPermission? InvokerPermissions(ChannelId channel) => Invoker;

        public string? RoleName(GuildId guild, ulong role) => Roles.GetValueOrDefault(role);

        public Task<SummaryHistoryPage> ReadHistoryPageAsync(GuildId guild, ChannelId channel, ulong? before, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetches);
            ReadChannels.Enqueue(channel.Value);
            if (Status != SummaryFetchStatus.Ok)
                return Task.FromResult(new SummaryHistoryPage(Status, [], false));
            var page = (Channels.GetValueOrDefault(channel.Value) ?? Messages)
                .Where(m => before is null || m.Id < before).OrderByDescending(m => m.Id).Take(100).ToList();
            return Task.FromResult(new SummaryHistoryPage(SummaryFetchStatus.Ok, page, page.Count < 100));
        }

        public Task<SummaryNames> ResolveNamesAsync(GuildId guild, IReadOnlyList<SummarySourceMessage> messages, CancellationToken cancellationToken) =>
            Task.FromResult(SummaryNames.Empty);

        public Task<bool?> HasMessageContentAccessAsync() => Task.FromResult(ContentAccess);
    }

    private sealed class FakeResponder : ISummaryResponder
    {
        public ConcurrentQueue<string> Private { get; } = new();

        public ConcurrentQueue<IReadOnlyList<string>> Public { get; } = new();

        public bool Deferred { get; private set; }

        public bool PostSucceeds { get; set; } = true;

        public Task ReplyPrivateAsync(string text)
        {
            Private.Enqueue(text);
            return Task.CompletedTask;
        }

        public Task DeferPrivateAsync()
        {
            Deferred = true;
            return Task.CompletedTask;
        }

        public Task<bool> PostPublicAsync(IReadOnlyList<string> parts)
        {
            if (PostSucceeds)
                Public.Enqueue(parts);
            return Task.FromResult(PostSucceeds);
        }
    }

    private sealed class World
    {
        public FakeAi Ai { get; } = new();
        public FakeDiscord Discord { get; } = new();
        public FakeGuildGateway Guilds { get; } = new();
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero));
        public CapturingLoggers Logs { get; } = new();
        public SummaryOptions Options { get; } = new();
        public SummaryService Service { get; }

        public World()
        {
            var localizer = new LocalizationCatalog(
            [
                new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
                new LocalizationSource(typeof(SummaryModule).Assembly, "ToroSquad.Modules.Summary.Localization"),
            ]);
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            foreach (var channel in new[] { Here, Other, Third })
                Guilds.SetChannel(Guild, channel, new BotChannelAccess(true, true, SummaryService.RequiredToRead | GuildPermission.SendMessages));
            Service = new SummaryService(Ai, new SummaryThrottle(options, Clock), Guilds, localizer, options, Clock, Logs.For<SummaryService>());
        }

        public Task<SummaryOutcome> RunAsync(FakeResponder responder, ChannelId? channel = null, UserId? member = null, ulong[]? roles = null) =>
            Service.RunAsync(new SummaryRequest(Guild, channel ?? Here, member ?? Member, "tr", Istanbul, roles ?? [AllowedRole]), Discord, responder);

        public string AllLogs => string.Join("\n", Logs.Lines);
    }

    private static List<SummarySourceMessage> Conversation(int count) =>
        Enumerable.Range(0, count).Select(i => new SummarySourceMessage((ulong)(9000 + i), new DateTimeOffset(2026, 9, 29, 17, i, 0, TimeSpan.Zero),
            SummaryAuthorKind.Member, i == 0 ? SecretName : "Üye" + (i % 4), i == 0 ? Secret : "derbi mesajı " + i, [], [])).ToList();

    [Fact]
    public async Task A_summary_costs_exactly_one_inference_and_is_posted_publicly()
    {
        var world = new World();
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(1);
        world.Discord.Fetches.Should().Be(1);
        responder.Deferred.Should().BeTrue();
        responder.Private.Should().BeEmpty();
        responder.Public.Should().ContainSingle().Which.Should().Equal(Answer);
        world.Ai.Prompts.Single().User.Should().Contain(Secret, "the transcript reaches the model").And.Contain(SecretName);
        world.Ai.Prompts.Single().System.Should().Be(SummaryPrompt.System);
    }

    [Fact]
    public async Task Too_few_member_messages_means_no_inference()
    {
        var world = new World();
        world.Discord.Messages = Conversation(4)
            .Append(new SummarySourceMessage(1, DateTimeOffset.UnixEpoch, SummaryAuthorKind.Bot, "TSQ Bot", "bot", [], []))
            .ToList();
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.NotEnoughMessages);

        world.Ai.Calls.Should().Be(0);
        responder.Private.Should().ContainSingle().Which.Should().Be("Özetlemek için bu kanalda yeterli mesaj yok (en az 5 mesaj gerekiyor).");
        responder.Public.Should().BeEmpty();
    }

    [Fact]
    public async Task Withheld_message_content_is_named_and_costs_no_inference()
    {
        var world = new World();
        world.Discord.Messages = Conversation(20).Select(m => m with { Content = "" }).ToList();
        world.Discord.ContentAccess = false;
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.ContentUnavailable);

        world.Ai.Calls.Should().Be(0);
        responder.Private.Single().Should().Contain("Message Content");
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("member")]
    [InlineData("bot")]
    [InlineData("not-configured")]
    [InlineData("fetch")]
    public async Task Refusals_before_the_ai_cost_zero_inference(string refusal)
    {
        var world = new World();
        switch (refusal)
        {
            case "unsupported":
                world.Discord.Supported = false;
                break;
            case "member":
                world.Discord.Invoker = GuildPermission.ViewChannel; // may see the channel but not its history
                break;
            case "bot":
                world.Guilds.SetChannel(Guild, Here, new BotChannelAccess(true, true, GuildPermission.ViewChannel | GuildPermission.SendMessages));
                break;
            case "not-configured":
                world.Ai.IsConfigured = false;
                break;
            default:
                world.Discord.Status = SummaryFetchStatus.Failed;
                break;
        }

        var responder = new FakeResponder();
        await world.RunAsync(responder);

        world.Ai.Calls.Should().Be(0);
        responder.Private.Should().ContainSingle();
        responder.Public.Should().BeEmpty();
        if (refusal != "fetch")
        {
            world.Discord.Fetches.Should().Be(0, "nothing is read before every check passed");
            responder.Deferred.Should().BeFalse("refusals are immediate and private");
        }
    }

    [Fact]
    public async Task Cooldowns_cost_zero_inference_and_expire()
    {
        var world = new World();

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);

        var sameUser = new FakeResponder();
        (await world.RunAsync(sameUser, channel: Other)).Should().Be(SummaryOutcome.Throttled);
        sameUser.Private.Single().Should().Be("Yeni bir özet için 30 saniye bekleyin.");

        var sameChannel = new FakeResponder();
        (await world.RunAsync(sameChannel, member: Member2)).Should().Be(SummaryOutcome.Throttled);
        sameChannel.Private.Single().Should().Be("Bu kanalda tekrar özet oluşturmak için **2 dk** beklemelisin.");
        world.Ai.Calls.Should().Be(1);

        world.Clock.Advance(TimeSpan.FromSeconds(31));
        (await world.RunAsync(new FakeResponder(), channel: Other)).Should().Be(SummaryOutcome.Posted, "the member cooldown is over; another channel");
        var stillWaiting = new FakeResponder();
        (await world.RunAsync(stillWaiting, member: Member2)).Should().Be(SummaryOutcome.Throttled, "the channel still waits");
        stillWaiting.Private.Single().Should().Be("Bu kanalda tekrar özet oluşturmak için **1 dk 29 sn** beklemelisin.");
        world.Clock.Advance(TimeSpan.FromSeconds(89));
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Posted, "exactly 120 s after the summary");
        world.Ai.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_second_request_for_a_channel_being_summarized_starts_no_inference()
    {
        var world = new World();
        var release = new TaskCompletionSource<SummaryAiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Ai.Respond = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };

        var first = world.RunAsync(new FakeResponder());
        await started.Task;
        var second = new FakeResponder();
        (await world.RunAsync(second, member: Member2)).Should().Be(SummaryOutcome.Throttled);
        second.Private.Single().Should().Be("Bu kanal için zaten bir özet hazırlanıyor.");

        release.SetResult(new SummaryAiResult(SummaryAiFailure.None, Answer, "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(5), 200));
        (await first).Should().Be(SummaryOutcome.Posted);
        world.Ai.Calls.Should().Be(1);
    }

    [Fact]
    public async Task At_most_two_summaries_run_bot_wide_and_the_third_is_refused_not_queued()
    {
        var world = new World();
        var release = new TaskCompletionSource<SummaryAiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var twoRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Ai.Respond = _ =>
        {
            if (Interlocked.Increment(ref running) == 2)
                twoRunning.TrySetResult();
            return release.Task;
        };

        var a = world.RunAsync(new FakeResponder(), channel: Here, member: Member);
        var b = world.RunAsync(new FakeResponder(), channel: Other, member: Member2);
        await twoRunning.Task;
        var third = new FakeResponder();
        (await world.RunAsync(third, channel: Third, member: new UserId(500000000000000003))).Should().Be(SummaryOutcome.Throttled);
        third.Private.Single().Should().Be("Şu anda başka özetler hazırlanıyor. Birazdan tekrar deneyin.");

        release.SetResult(new SummaryAiResult(SummaryAiFailure.None, Answer, "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(5), 200));
        await Task.WhenAll(a, b);
        world.Ai.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(SummaryAiFailure.Rejected, 400, "Özet servisi şu anda kullanılamıyor.")]
    [InlineData(SummaryAiFailure.RateLimited, 429, "Özet servisi şu anda yoğun.")]
    [InlineData(SummaryAiFailure.ServerError, 500, "Özet servisi şu anda kullanılamıyor.")]
    [InlineData(SummaryAiFailure.Timeout, null, "Özet zamanında hazırlanamadı.")]
    [InlineData(SummaryAiFailure.Unauthorized, 403, "Özet servisi şu anda kullanılamıyor.")]
    public async Task A_failed_inference_is_not_retried_posts_nothing_and_locks_only_briefly(SummaryAiFailure failure, int? status, string message)
    {
        var world = new World();
        world.Ai.Respond = _ => Task.FromResult(SummaryAiResult.Failed(failure, TimeSpan.FromSeconds(2), status, "server_error"));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1, "no retry, no fallback model");
        responder.Public.Should().BeEmpty("nothing half-done in the channel");
        responder.Private.Single().Should().StartWith(message).And.Contain("TS-");
        world.AllLogs.Should().Contain("failure=" + failure).And.Contain("inference failed");

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Throttled, "a short cooldown after a failure");
        world.Clock.Advance(SummaryThrottle.FailureCooldown);
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, Answer, "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(5), 200));
        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted, "but not the full 30/60 seconds");
        world.Ai.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_unusable_answer_is_a_failure_not_a_post()
    {
        var world = new World();
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, "```\n```", "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(1), 200));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        responder.Public.Should().BeEmpty();
        world.Ai.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Model_generated_mentions_cannot_ping()
    {
        var world = new World();
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None,
            "# Son Mesajların Özeti\n\n## Ana konu\n@everyone @here duyuru konuşuldu.", "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(1), 200));
        var responder = new FakeResponder();

        await world.RunAsync(responder);

        var posted = string.Join("\n", responder.Public.Single());
        posted.Should().NotMatchRegex("@(everyone|here)");
    }

    [Fact]
    public async Task A_long_answer_is_posted_in_parts_within_discords_limit()
    {
        var world = new World();
        var bullets = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"- **Konu {i}:** " + string.Join(" ", Enumerable.Repeat("ayrıntı", 10))));
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None,
            "# Son Mesajların Özeti\n\n## Ana konu\nUzun.\n\n## Önemli noktalar\n" + bullets, "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(1), 200));
        var responder = new FakeResponder();

        await world.RunAsync(responder);

        var parts = responder.Public.Single();
        parts.Should().HaveCountGreaterThan(1).And.OnlyContain(p => p.Length <= 2000);
        parts[0].Should().StartWith("# Son Mesajların Özeti");
        world.Ai.Calls.Should().Be(1);
    }

    [Fact]
    public async Task No_log_line_contains_message_text_names_the_prompt_or_the_answer()
    {
        var world = new World();
        await world.RunAsync(new FakeResponder());
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        world.Ai.Respond = _ => Task.FromResult(SummaryAiResult.Failed(SummaryAiFailure.ServerError, TimeSpan.FromSeconds(1), 500, "server_error"));
        await world.RunAsync(new FakeResponder());
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        world.Discord.Messages = Conversation(3);
        await world.RunAsync(new FakeResponder());

        var logs = world.AllLogs;
        logs.Should().Contain("message_count=12").And.Contain("input_tokens=3000").And.Contain("latency_ms=6000").And.Contain("model=deepseek-v4.1-flash");
        logs.Should().NotContain(Secret).And.NotContain("GİZLİ").And.NotContain(SecretName);
        logs.Should().NotContain("derbi mesajı").And.NotContain("CEVAP-METNİ").And.NotContain("Son Mesajların Özeti");
        logs.Should().NotContain("özetleyicisisin", "the prompt is never logged");
    }

    [Fact]
    public async Task Discord_refusing_the_post_is_not_a_successful_summary_and_locks_only_briefly()
    {
        var world = new World();
        var responder = new FakeResponder { PostSucceeds = false };

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.PostFailed);

        world.Ai.Calls.Should().Be(1);
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Throttled, "the short failure cooldown");
        world.Clock.Advance(SummaryThrottle.FailureCooldown);
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Posted, "no 120 s cooldown: nothing reached the channel");
    }

    [Fact]
    public async Task Health_reports_configuration_and_the_last_run_without_calling_the_provider()
    {
        var world = new World();
        var health = new SummaryHealthCheck(world.Service);

        (await health.CheckAsync(CancellationToken.None)).Overall.Should().Be(ToroSquad.Core.Modules.HealthState.Healthy);
        world.Ai.Respond = _ => Task.FromResult(SummaryAiResult.Failed(SummaryAiFailure.RateLimited, TimeSpan.Zero, 429));
        await world.RunAsync(new FakeResponder());
        (await health.CheckAsync(CancellationToken.None)).Overall.Should().Be(ToroSquad.Core.Modules.HealthState.Degraded);
        world.Ai.IsConfigured = false;
        (await health.CheckAsync(CancellationToken.None)).Overall.Should().Be(ToroSquad.Core.Modules.HealthState.NotConfigured);
        world.Ai.Calls.Should().Be(1, "only the /ozetle run called the provider");
    }
}
