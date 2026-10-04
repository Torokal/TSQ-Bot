using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// <see cref="NotificationRequest.EarliestDeliveryAt"/> on a real SQLite outbox: a durable "not before" for the first
/// send. Without it nothing changes for any module; with it the row is staged at once and only becomes due at that time —
/// across restarts, without sliding, and still subject to every last-moment delivery gate.
/// </summary>
public sealed class OutboxEarliestDeliveryTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(111);
    private static readonly ChannelId Channel = new(1110);
    private static readonly DateTimeOffset Target = TestHost.T0.AddSeconds(30);

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await _host.SetUpEsportsGuildAsync(Guild, Channel);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static NotificationRequest Request(DateTimeOffset? earliest, string source = "m:1", string title = "A vs B") =>
        new(Guild, EsportsModule.ModuleIdTyped, source, Channel, NotificationPlanner.KindResult,
            new OutgoingMessage(null, new MessageEmbed(title, "body", null, [], "footer", null, null), MentionPolicy.None),
            TestHost.T0.AddHours(6), IsDryRun: false, EarliestDeliveryAt: earliest);

    private static Task<StageOutcome> StageAsync(TestHost host, NotificationRequest request) => host.InScopeAsync(async sp =>
    {
        var outcome = await sp.GetRequiredService<INotificationOutbox>().StageAsync(request, CancellationToken.None);
        await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        return outcome;
    });

    private static Task<List<OutboxMessageEntity>> RowsAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    private static Task ProcessAsync(TestHost host) => host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

    [Fact]
    public async Task Without_an_earliest_time_the_row_is_due_immediately_as_before()
    {
        (await StageAsync(_host, Request(earliest: null))).Should().Be(StageOutcome.Created);
        (await RowsAsync(_host)).Single().NextAttemptAt.Should().Be(TestHost.T0);

        await ProcessAsync(_host);

        _host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task An_earliest_time_in_the_past_does_not_hold_the_row()
    {
        await StageAsync(_host, Request(TestHost.T0.AddSeconds(-45)));
        (await RowsAsync(_host)).Single().NextAttemptAt.Should().Be(TestHost.T0, "already past: no extra wait");

        await ProcessAsync(_host);

        _host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task A_future_earliest_time_is_persisted_and_nothing_is_delivered_before_it()
    {
        (await StageAsync(_host, Request(Target))).Should().Be(StageOutcome.Created);
        var row = (await RowsAsync(_host)).Single();
        row.Status.Should().Be(OutboxStatus.Pending);
        row.NextAttemptAt.Should().Be(Target);

        await ProcessAsync(_host);
        _host.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        await ProcessAsync(_host);
        _host.Transport.SendCalls.Should().Be(0, "one millisecond before the target is still too early");

        _host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await ProcessAsync(_host);
        await ProcessAsync(_host);
        _host.Transport.Messages.Should().ContainSingle();
        (await RowsAsync(_host)).Single().Status.Should().Be(OutboxStatus.Sent);
    }

    [Fact]
    public async Task Staging_the_same_key_again_while_waiting_never_moves_the_delivery_time()
    {
        await StageAsync(_host, Request(Target));
        var title = "A vs B";
        foreach (var seconds in new[] { 5, 10, 15 })
        {
            _host.Clock.Advance(TimeSpan.FromSeconds(5));
            // Same payload, then a changed payload, each asking for "now + 30 s" again: neither may push the time back.
            (await StageAsync(_host, Request(_host.Clock.GetUtcNow().AddSeconds(30), title: title))).Should().Be(StageOutcome.Unchanged);
            title = "A vs B #" + seconds;
            (await StageAsync(_host, Request(_host.Clock.GetUtcNow().AddSeconds(30), title: title))).Should().Be(StageOutcome.UpdatedPending);
            (await RowsAsync(_host)).Single().NextAttemptAt.Should().Be(Target);
            await ProcessAsync(_host);
        }

        _host.Transport.SendCalls.Should().Be(0);
        _host.Clock.Advance(TimeSpan.FromSeconds(15));
        await ProcessAsync(_host);
        _host.Transport.Messages.Should().ContainSingle().Which.Message.Embed!.Title.Should().Be("A vs B #15", "the newest pending content is sent, once");
    }

    [Fact]
    public async Task A_restart_before_the_target_neither_sends_early_nor_loses_the_row()
    {
        await StageAsync(_host, Request(Target));
        _host.Clock.Advance(TimeSpan.FromSeconds(10));
        await ProcessAsync(_host);

        // New process on the same database and the same Discord, 18 s after staging.
        var transport = _host.Transport;
        await using var second = await TestHost.CreateAsync(new() { ["Bot:DataDirectory"] = _host.Directory }, start: TestHost.T0.AddSeconds(18), replace: s =>
        {
            s.AddSingleton(transport);
            s.AddSingleton<IMessageTransport>(transport);
        });
        await ProcessAsync(second);
        transport.SendCalls.Should().Be(0, "the hold is in the database, not in memory");
        (await RowsAsync(second)).Single().Should().Match<OutboxMessageEntity>(r => r.Status == OutboxStatus.Pending && r.NextAttemptAt == Target);

        second.Clock.Advance(TimeSpan.FromSeconds(12));
        await ProcessAsync(second);
        await ProcessAsync(second);
        transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Rows_become_due_in_the_order_of_their_earliest_times()
    {
        await StageAsync(_host, Request(TestHost.T0.AddSeconds(45), source: "m:later", title: "later"));
        await StageAsync(_host, Request(TestHost.T0.AddSeconds(30), source: "m:sooner", title: "sooner"));

        _host.Clock.Advance(TimeSpan.FromSeconds(30));
        await ProcessAsync(_host);
        _host.Transport.Messages.Select(m => m.Message.Embed!.Title).Should().Equal("sooner");

        _host.Clock.Advance(TimeSpan.FromSeconds(15));
        await ProcessAsync(_host);
        _host.Transport.Messages.Select(m => m.Message.Embed!.Title).Should().Equal("sooner", "later");
    }

    [Fact]
    public async Task Retries_after_the_hold_keep_their_normal_backoff()
    {
        await StageAsync(_host, Request(Target));
        _host.Clock.Advance(TimeSpan.FromSeconds(30));
        _host.Transport.ScriptSend(() => new SendOutcome.RateLimited(TimeSpan.FromSeconds(20)));
        await ProcessAsync(_host);
        (await RowsAsync(_host)).Single().Should().Match<OutboxMessageEntity>(r => r.Status == OutboxStatus.Pending && r.NextAttemptAt > Target.AddSeconds(20));

        _host.Clock.Advance(TimeSpan.FromSeconds(21));
        await ProcessAsync(_host);
        _host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Pause_or_module_off_during_the_hold_cancels_the_delivery()
    {
        await StageAsync(_host, Request(Target));
        await StageAsync(_host, Request(Target, source: "m:2"));
        await _host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().PauseAsync(TestHost.Admin(Guild), true, CancellationToken.None));

        _host.Clock.Advance(TimeSpan.FromSeconds(31));
        await ProcessAsync(_host);

        _host.Transport.SendCalls.Should().Be(0);
        (await RowsAsync(_host)).Should().OnlyContain(r => r.Status == OutboxStatus.Cancelled && r.LastError == "paused");
    }

    [Fact]
    public async Task A_held_row_that_expires_before_it_is_due_is_never_sent()
    {
        await StageAsync(_host, Request(TestHost.T0.AddHours(7))); // ExpiresAt is T0 + 6 h
        _host.Clock.Advance(TimeSpan.FromHours(7));
        await ProcessAsync(_host);

        _host.Transport.SendCalls.Should().Be(0);
        (await RowsAsync(_host)).Single().Status.Should().Be(OutboxStatus.Expired);
    }
}
