using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Ai;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Sor;
using ToroSquad.Modules.Sor.Application;

namespace ToroSquad.Tests.Support;

/// <summary>
/// A scripted AI provider for TSQ Sor (never the real service): records every request it receives and answers with the next
/// scripted result; <see cref="During"/> runs while the "model" is thinking (to change memory or settings mid-request).
/// </summary>
public sealed class FakeSorAi : IAiChatClient
{
    private readonly Queue<Func<AiChatRequest, AiChatResult>> _script = new();

    public List<AiChatRequest> Requests { get; } = [];

    public bool IsConfigured { get; set; } = true;

    /// <summary>Runs inside the request, after it was recorded and before the answer is returned.</summary>
    public Func<Task>? During { get; set; }

    public FakeSorAi Answer(string text, string? memoryJson = null, string finish = "stop", AiUsage? usage = null)
    {
        var body = memoryJson is null ? text : text + "\n<<<HAFIZA>>>\n" + memoryJson;
        _script.Enqueue(_ => new AiChatResult(AiFailure.None, body, finish, usage ?? new AiUsage(900, 120, 0, 0), TimeSpan.FromMilliseconds(800), 200));
        return this;
    }

    public FakeSorAi Fail(AiFailure failure, int? status = null, TimeSpan? retryAfter = null)
    {
        _script.Enqueue(_ => AiChatResult.Failed(failure, TimeSpan.FromSeconds(1), status, null, retryAfter));
        return this;
    }

    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (During is { } during)
        {
            During = null;
            await during();
        }

        return _script.Count > 0 ? _script.Dequeue()(request) : new AiChatResult(AiFailure.None, "Tamam.", "stop", new AiUsage(10, 5, 0), TimeSpan.Zero, 200);
    }

    /// <summary>Everything the model saw in one request except the constant system message (all other messages joined).</summary>
    public static string Seen(AiChatRequest request) =>
        string.Join("\n---\n", request.Messages.Where(m => m.Role != AiRole.System).Select(m => m.Role + ": " + m.Content));

    public string LastSeen => Seen(Requests[^1]);
}

/// <summary>Records what /sor would have shown on Discord; delivery outcome configurable.</summary>
public sealed class RecordingSorResponder : ISorResponder
{
    public List<string> Refusals { get; } = [];
    public List<bool> Deferred { get; } = [];
    public List<(string Neutral, string Detail)> Failures { get; } = [];
    public List<SorReply> Delivered { get; } = [];
    public List<string> Footers { get; } = [];
    public List<(string Text, bool MemoryButton)> PrivateNotes { get; } = [];

    public SorDelivery Outcome { get; set; } = SorDelivery.Delivered;

    /// <summary>Runs right after a confirmed delivery, before anything is saved (a change "between Discord and the database").</summary>
    public Func<Task>? AfterDelivery { get; set; }

    /// <summary>The "saved" line edit fails on Discord (it must not repeat any saving or generation).</summary>
    public bool FooterFails { get; set; }

    public Task RefuseAsync(string text)
    {
        Refusals.Add(text);
        return Task.CompletedTask;
    }

    public Task DeferAsync(bool isPrivate)
    {
        Deferred.Add(isPrivate);
        return Task.CompletedTask;
    }

    public Task FailAsync(string neutral, string detail)
    {
        Failures.Add((neutral, detail));
        return Task.CompletedTask;
    }

    public async Task<SorDelivery> DeliverAsync(SorReply reply)
    {
        if (Outcome == SorDelivery.Delivered)
        {
            Delivered.Add(reply);
            if (AfterDelivery is { } after)
                await after();
        }

        return Outcome;
    }

    public Task AddFooterAsync(SorReply reply, string line)
    {
        if (FooterFails)
            throw new InvalidOperationException("Discord refused the edit (test)");
        Footers.Add(line);
        return Task.CompletedTask;
    }

    public Task PrivateNoteAsync(string text, bool memoryButton)
    {
        PrivateNotes.Add((text, memoryButton));
        return Task.CompletedTask;
    }

    public string Answer => string.Join("\n", Delivered[^1].Parts);
}

/// <summary>A test host with TSQ Sor enabled in a guild and the scripted provider in place of OpenCode Go.</summary>
public sealed class SorKit : IAsyncDisposable
{
    public static readonly GuildId Guild = new(4242);
    public static readonly GuildId OtherGuild = new(4343);
    public const ulong Channel = 7001;
    public const ulong OtherChannel = 7002;

    /// <summary>Interaction ids are unique across kits (a "restarted" kit must not repeat an earlier id).</summary>
    private static long NextInteractionId = 900_000;

    private SorKit(TestHost host, FakeSorAi ai)
    {
        Host = host;
        Ai = ai;
    }

    public TestHost Host { get; }
    public FakeSorAi Ai { get; }

    public static async Task<SorKit> CreateAsync(Dictionary<string, string?>? overrides = null, string? directory = null, FakeSorAi? ai = null,
        Action<IServiceCollection>? replace = null)
    {
        ai ??= new FakeSorAi();
        var settings = new Dictionary<string, string?>(overrides ?? []);
        settings.TryAdd("Sor:UserCooldownSeconds", "0"); // the fake clock stands still; cooldown tests set their own
        if (directory is not null)
            settings["Bot:DataDirectory"] = directory;
        var host = await TestHost.CreateAsync(settings, replace: s =>
        {
            s.AddKeyedSingleton<IAiChatClient>(SorModule.ModuleIdValue, ai);
            replace?.Invoke(s);
        });
        var kit = new SorKit(host, ai);
        foreach (var guild in new[] { Guild, OtherGuild })
            await host.InScopeAsync(async sp => (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(guild), SorModule.ModuleIdValue, true, CancellationToken.None))
                .Succeeded.Should().BeTrue());
        return kit;
    }

    public static ActorContext User(ulong id, GuildId? guild = null, params RoleId[] roles) => TestHost.Member(guild ?? Guild, id, roles);

    public ulong NextInteraction() => (ulong)Interlocked.Increment(ref NextInteractionId);

    public SorAsk Ask(ActorContext actor, string question, bool isPrivate = false, bool isNew = false, long? conversation = null, SorPerson? about = null,
        ulong channel = Channel, string name = "Ada", ulong? interaction = null, ulong? parent = null) =>
        new(actor, interaction ?? NextInteraction(), channel, parent, question, isPrivate, isNew, conversation, name, about, "tr",
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"));

    public async Task<RecordingSorResponder> AskAsync(SorAsk ask, SorDelivery outcome = SorDelivery.Delivered)
    {
        var responder = new RecordingSorResponder { Outcome = outcome };
        await Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(ask, responder));
        return responder;
    }

    public Task<RecordingSorResponder> AskAsync(ActorContext actor, string question, bool isPrivate = false, bool isNew = false, SorPerson? about = null,
        ulong channel = Channel, string name = "Ada") =>
        AskAsync(Ask(actor, question, isPrivate, isNew, null, about, channel, name));

    public Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action) => Host.InScopeAsync(action);

    public Task<SorMemoryPanel> PanelAsync(ActorContext actor) => InScopeAsync(sp => sp.GetRequiredService<SorMemoryService>().PanelAsync(actor, CancellationToken.None));

    public Task<OperationResult> MemoryAsync(Func<SorMemoryService, Task<OperationResult>> action) => InScopeAsync(sp => action(sp.GetRequiredService<SorMemoryService>()));

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}
