using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>A scripted daily-reward source: the queued values, then the lower bound.</summary>
public sealed class ScriptedPredictionRandom : IPredictionRandom
{
    public Queue<int> Next { get; } = new();
    public List<(int From, int To)> Calls { get; } = [];

    public int NextInclusive(int fromInclusive, int toInclusive)
    {
        lock (Calls)
        {
            Calls.Add((fromInclusive, toInclusive));
            return Next.TryDequeue(out var value) ? value : fromInclusive;
        }
    }
}

/// <summary>
/// TSQ Öngörü on the real SQLite database with the production wiring (fake Discord transport and guild gateway, fake clock,
/// scripted daily random). Actors carry exactly what Discord would send: roles and permissions of the interaction.
/// </summary>
public sealed class PredictionTestKit : IAsyncDisposable
{
    public static readonly GuildId Guild = new(777);
    public static readonly GuildId OtherGuild = new(778);
    public static readonly ChannelId Predictions = new(PredictionsOptions.DefaultChannelId);
    public static readonly ChannelId Commands = new(PredictionsOptions.DefaultCommandsChannelId);
    public static readonly ChannelId Elsewhere = new(1_111_111_111_111_111_111);
    public static readonly RoleId CreatorRole = new(PredictionsOptions.DefaultCreatorRoleId);
    public static readonly CancellationToken Ct = CancellationToken.None;

    public const GuildPermission ChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks |
                                                      GuildPermission.ReadMessageHistory;

    public const string Outcomes3 = "Galatasaray Kazanır | 1.10\nBeraberlik | 2.30\nFenerbahçe Kazanır | 3.10";

    private PredictionTestKit(TestHost host, ScriptedPredictionRandom random)
    {
        Host = host;
        Random = random;
    }

    public TestHost Host { get; }
    public ScriptedPredictionRandom Random { get; }

    public FakeMessageTransport Transport => Host.Transport;

    public static ActorContext Creator(ulong user = 10, GuildId? guild = null) =>
        new(guild ?? Guild, new UserId(user), GuildPermission.ViewChannel | GuildPermission.SendMessages, [CreatorRole], false, 5);

    public static ActorContext Admin(ulong user = 20) =>
        new(Guild, new UserId(user), GuildPermission.Administrator, [], false, 50);

    public static ActorContext Owner(ulong user = 30) =>
        new(Guild, new UserId(user), GuildPermission.ViewChannel, [], true, 0);

    public static ActorContext Member(ulong user = 100) =>
        new(Guild, new UserId(user), GuildPermission.ViewChannel | GuildPermission.SendMessages, [], false, 1);

    public static async Task<PredictionTestKit> CreateAsync(DateTimeOffset? start = null, Dictionary<string, string?>? settings = null, string? directory = null,
        FakeMessageTransport? transport = null, Action<IServiceCollection>? replace = null)
    {
        var random = new ScriptedPredictionRandom();
        var overrides = new Dictionary<string, string?>(settings ?? []);
        if (directory is not null)
            overrides["Bot:DataDirectory"] = directory;
        var host = await TestHost.CreateAsync(overrides, start ?? TestHost.T0, services =>
        {
            services.AddSingleton<IPredictionRandom>(random);
            if (transport is not null)
            {
                services.AddSingleton(transport);
                services.AddSingleton<IMessageTransport>(transport);
            }

            replace?.Invoke(services);
        });
        var kit = new PredictionTestKit(host, random);
        foreach (var guild in new[] { Guild, OtherGuild })
        {
            host.Guilds.SetChannel(guild, Predictions, new BotChannelAccess(true, true, ChannelPermissions));
            host.Guilds.SetChannel(guild, Commands, new BotChannelAccess(true, true, ChannelPermissions));
        }

        if (directory is null)
            await kit.SetEnabledAsync(true);
        return kit;
    }

    public async Task SetEnabledAsync(bool enabled) => await Host.InScopeAsync(async sp =>
        (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "predictions", enabled, Ct)).Succeeded.Should().BeTrue());

    public Task<T> Service<T>(Func<PredictionService, Task<T>> action) => Host.InScopeAsync(sp => action(sp.GetRequiredService<PredictionService>()));

    public Task<T> Economy<T>(Func<PredictionEconomy, Task<T>> action) => Host.InScopeAsync(sp => action(sp.GetRequiredService<PredictionEconomy>()));

    public Task<T> Db<T>(Func<ToroDbContext, Task<T>> action) => Host.InScopeAsync(sp => action(sp.GetRequiredService<ToroDbContext>()));

    // ---- create ----

    public async Task<string> OpenFormAsync(ActorContext? actor = null, ChannelId? channel = null)
    {
        var (refusal, id, _) = await Service(s => s.OpenFormAsync(actor ?? Creator(), channel ?? Predictions, Ct));
        refusal.Should().BeNull(refusal?.MessageKey);
        return id!;
    }

    public Task<PredictionReply> SubmitAsync(string draftId, string title = "Galatasaray - Fenerbahçe Maç Sonucu Ne Olur?", string outcomes = Outcomes3,
        string? lockDate = null, string? lockTime = null, string? rules = null, ActorContext? actor = null, ChannelId? channel = null) =>
        Service(s => s.SubmitFormAsync(actor ?? Creator(), channel ?? Predictions, draftId, new PredictionFormValues(title, outcomes, lockDate, lockTime, rules), "Kaan", Ct));

    public Task<PredictionReply> PublishAsync(string draftId, ActorContext? actor = null, ChannelId? channel = null) =>
        Service(s => s.PublishAsync(actor ?? Creator(), channel ?? Predictions, draftId, "Kaan", Ct));

    /// <summary>A published prediction (form → preview → publish) with its outcomes; <paramref name="lockAt"/> is "date time", typed into the two fields.</summary>
    public async Task<PredictionView> CreatePredictionAsync(string outcomes = Outcomes3, string? lockAt = null, ActorContext? creator = null, string? title = null)
    {
        var draft = await OpenFormAsync(creator);
        var lockParts = lockAt?.Split(' ');
        var preview = await SubmitAsync(draft, title ?? "Galatasaray - Fenerbahçe Maç Sonucu Ne Olur?", outcomes, lockParts?[0], lockParts?[1], actor: creator);
        preview.Result.Succeeded.Should().BeTrue(preview.View?.Content ?? preview.Result.MessageKey);
        var published = await PublishAsync(draft, creator);
        published.Result.MessageKey.Should().Be("predictions.publish.done", string.Join(",", published.Result.Args));
        var id = long.Parse((string)published.Result.Args[0], System.Globalization.CultureInfo.InvariantCulture);
        return (await Service(s => s.GetAsync(id, Ct)))!;
    }

    // ---- enter (🎯 Tahmin Yap → form → Submit), change, withdraw ----

    public static string Name(ActorContext actor) => "Üye " + actor.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>🎯 Tahmin Yap on the card: the entry form (checked), or the private answer instead (a refusal or the active entry).</summary>
    public Task<EntryStart> OpenEntryAsync(ActorContext actor, PredictionView prediction, ChannelId? channel = null, MessageId? card = null) =>
        Service(s => s.StartEntryAsync(actor, channel ?? Predictions, prediction.Id, card ?? prediction.Message, Ct));

    /// <summary>The entry form submitted, exactly as Discord delivers it (also a second time, or after the prediction changed): the submit IS the entry.</summary>
    public Task<PredictionReply> SubmitEntryAsync(ActorContext actor, PredictionView prediction, int outcomePosition, string amount, ChannelId? channel = null) =>
        Service(s => s.SubmitEntryAsync(actor, channel ?? Predictions, prediction.Id, OutcomeValue(prediction, outcomePosition), amount, Name(actor), Ct));

    /// <summary>🎯 Tahmin Yap, then the form submitted. Without a form (refused, or an active entry shown) that answer is returned.</summary>
    public async Task<PredictionReply> EnterAsync(ActorContext actor, PredictionView prediction, int outcomePosition, string amount, ChannelId? channel = null)
    {
        var start = await OpenEntryAsync(actor, prediction, channel);
        if (start.Form is not { } form)
            return start.Reply!;
        form.Outcomes.Select(o => o.Id).Should().Equal(prediction.Outcomes.Select(o => o.Id), "the form offers every outcome");
        form.IsChange.Should().BeFalse();
        return await SubmitEntryAsync(actor, prediction, outcomePosition, amount, channel);
    }

    /// <summary>✏️ Tahminimi Değiştir: the prefilled form (or the refusal instead).</summary>
    public Task<EntryStart> OpenChangeAsync(ActorContext actor, PredictionView prediction, ChannelId? channel = null) =>
        Service(s => s.StartChangeAsync(actor, channel ?? Predictions, prediction.Id, Ct));

    /// <summary>The change form submitted: the submit IS the change.</summary>
    public Task<PredictionReply> SubmitChangeAsync(ActorContext actor, PredictionView prediction, int outcomePosition, string amount, ChannelId? channel = null) =>
        Service(s => s.ChangeEntryAsync(actor, channel ?? Predictions, prediction.Id, OutcomeValue(prediction, outcomePosition), amount, Ct));

    /// <summary>✏️ Tahminimi Değiştir, then the form submitted.</summary>
    public async Task<PredictionReply> ChangeAsync(ActorContext actor, PredictionView prediction, int outcomePosition, string amount)
    {
        var start = await OpenChangeAsync(actor, prediction);
        if (start.Form is not { } form)
            return start.Reply!;
        form.IsChange.Should().BeTrue();
        return await SubmitChangeAsync(actor, prediction, outcomePosition, amount);
    }

    /// <summary>↩️ Tahminimi Geri Çek (the click is the decision).</summary>
    public Task<PredictionReply> WithdrawAsync(ActorContext actor, PredictionView prediction, ChannelId? channel = null) =>
        Service(s => s.WithdrawEntryAsync(actor, channel ?? Predictions, prediction.Id, Ct));

    private static string OutcomeValue(PredictionView prediction, int position) => Id(prediction.Outcomes.Single(o => o.Position == position).Id);

    public static string? Token(PredictionReply reply, string prefix) =>
        reply.View?.Buttons?.FirstOrDefault(b => b.CustomId!.StartsWith(prefix, StringComparison.Ordinal))?.CustomId?[prefix.Length..];

    public static string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- manage from the card: lock / settle / cancel ----

    /// <summary>🔒 Kilitle on the card, then the private confirmation.</summary>
    public async Task<OperationResult> LockAsync(ActorContext actor, PredictionView prediction, MessageId? card = null)
    {
        var prompt = await Service(s => s.PromptLockAsync(actor, Predictions, prediction.Id, card ?? prediction.Message, Ct));
        if (Token(prompt, PredictionMessages.LockConfirmPrefix) is not { } id)
            return prompt.Result;
        id.Should().Be(Id(prediction.Id), "the confirmation carries only the prediction number");
        return await Service(s => s.LockAsync(actor, Predictions, prediction.Id, Ct));
    }

    /// <summary>✅ Sonuçlandır on the card → outcome picker → preview; the confirmation value ("{prediction}.{outcome}") or the refusal.</summary>
    public async Task<(string? Confirm, PredictionReply Reply)> SettlePreviewAsync(ActorContext actor, PredictionView prediction, int outcomePosition)
    {
        var start = await Service(s => s.StartSettleAsync(actor, Predictions, prediction.Id, prediction.Message, Ct));
        if (start.View?.Select is not { } select)
            return (null, start);
        select.CustomId.Should().Be(PredictionMessages.SettlePickPrefix + Id(prediction.Id));
        var outcome = prediction.Outcomes.Single(o => o.Position == outcomePosition).Id;
        var preview = await Service(s => s.PreviewSettleAsync(actor, Predictions, prediction.Id, Id(outcome), Ct));
        return (Token(preview, PredictionMessages.SettleConfirmPrefix), preview);
    }

    public async Task<PredictionReply> SettleAsync(ActorContext actor, PredictionView prediction, int outcomePosition)
    {
        var (confirm, reply) = await SettlePreviewAsync(actor, prediction, outcomePosition);
        if (confirm is null)
            return reply;
        var parts = confirm.Split(PredictionMessages.Separator);
        return await Service(s => s.ConfirmSettleAsync(actor, Predictions, long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), Ct));
    }

    public Task<PredictionReply> ConfirmSettleAsync(ActorContext actor, PredictionView prediction, int outcomePosition) =>
        Service(s => s.ConfirmSettleAsync(actor, Predictions, prediction.Id, prediction.Outcomes.Single(o => o.Position == outcomePosition).Id, Ct));

    /// <summary>↩️ İptal / İade on the card → reason form → preview; the confirmation token or null (with the refusal).</summary>
    public async Task<(string? Token, PredictionReply Reply)> CancelPreviewAsync(ActorContext actor, PredictionView prediction, string reason = "Maç ertelendi")
    {
        if (await Service(s => s.StartCancelAsync(actor, Predictions, prediction.Id, prediction.Message, Ct)) is { } refusal)
            return (null, refusal);
        var preview = await Service(s => s.PreviewCancelAsync(actor, Predictions, prediction.Id, reason, Ct));
        return (Token(preview, PredictionMessages.CancelConfirmPrefix), preview);
    }

    public async Task<string?> CancelTokenAsync(ActorContext actor, PredictionView prediction, string reason = "Maç ertelendi") =>
        (await CancelPreviewAsync(actor, prediction, reason)).Token;

    public async Task<PredictionReply> CancelAsync(ActorContext actor, PredictionView prediction, string reason = "Maç ertelendi")
    {
        var (token, reply) = await CancelPreviewAsync(actor, prediction, reason);
        if (token is null)
            return reply;
        return await Service(s => s.ConfirmCancelAsync(actor, Predictions, token, Ct));
    }

    // ---- tournament ----

    public async Task<string?> EndTokenAsync(ActorContext actor) =>
        Token(await Economy(e => e.PreviewTournamentEndAsync(actor, Commands, Ct)), PredictionMessages.EndConfirmPrefix);

    public Task<PredictionReply> ConfirmEndAsync(ActorContext actor, string token) => Economy(e => e.ConfirmTournamentEndAsync(actor, Commands, token, Ct));

    public Task<PredictionReply> ClaimDailyAsync(ActorContext actor) => Economy(e => e.ClaimDailyAsync(actor, Commands, Name(actor), Ct));

    /// <summary>
    /// Makes <paramref name="users"/> leaderboard-eligible in the active tournament with nothing left unresolved: a prediction
    /// by <see cref="Creator"/> they each enter with 1 coin, then cancelled (stakes refunded).
    /// </summary>
    public async Task ParticipateAsync(params ulong[] users)
    {
        var prediction = await CreatePredictionAsync(title: "Katılım öngörüsü " + Guid.NewGuid().ToString("N")[..6]);
        foreach (var user in users)
            (await EnterAsync(Member(user), prediction, 1, "1")).Result.Succeeded.Should().BeTrue();
        (await CancelAsync(Creator(), prediction)).Result.Succeeded.Should().BeTrue();
    }

    // ---- reads ----

    public Task<PredictionEntity> RowAsync(long id) => Db(db => db.Set<PredictionEntity>().AsNoTracking().SingleAsync(p => p.Id == id));

    public Task<PredictionWalletEntity?> WalletAsync(ulong user, long? tournamentId = null) => Db(async db =>
    {
        var tournament = tournamentId ?? await db.Set<PredictionTournamentEntity>().Where(t => t.GuildId == Guild.Value && t.Status == PredictionTournamentStatus.Active)
            .Select(t => t.Id).SingleAsync();
        return await db.Set<PredictionWalletEntity>().AsNoTracking().SingleOrDefaultAsync(w => w.TournamentId == tournament && w.UserId == user);
    });

    public Task<List<PredictionLedgerEntity>> LedgerAsync(ulong user) => Db(db =>
        db.Set<PredictionLedgerEntity>().AsNoTracking().Where(l => l.UserId == user).OrderBy(l => l.Id).ToListAsync());

    public Task<List<PredictionEntryEntity>> EntriesAsync(long predictionId) => Db(db =>
        db.Set<PredictionEntryEntity>().AsNoTracking().Where(e => e.PredictionId == predictionId).OrderBy(e => e.Id).ToListAsync());

    public Task<int> CountAsync<T>() where T : class => Db(db => db.Set<T>().CountAsync());

    public FakeMessageTransport.FakeMessage Card(PredictionView prediction) => Transport.Messages.Single(m => m.Id == prediction.Message);

    public static OutgoingMessage Shown(FakeMessageTransport.FakeMessage card) => card.Edits.LastOrDefault() ?? card.Message;

    /// <summary>One worker pass (after moving the clock), then the outbox is drained into the fake Discord.</summary>
    public async Task TickAsync(TimeSpan? advance = null)
    {
        if (advance is { } by)
            Host.Clock.Advance(by);
        await Host.Services.GetRequiredService<PredictionWorker>().RunOnceAsync(Ct);
        var processor = Host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Starts every action at the same moment on its own scope (own DbContext, own connection).</summary>
    public async Task<T[]> TogetherAsync<T>(params Func<Task<T>>[] actions)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions.Select(a => Task.Run(async () =>
        {
            await start.Task;
            return await a();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
