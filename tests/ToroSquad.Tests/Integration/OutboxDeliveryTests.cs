using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Criteria 9-11 on a real SQLite database: duplicate suppression, crash/ambiguous delivery handling, rate limits,
/// permission loss, deleted channels/messages, and per-guild isolation of failures.
/// </summary>
public sealed class OutboxDeliveryTests : IAsyncLifetime
{
    private static readonly GuildId GuildA = new(111);
    private static readonly GuildId GuildB = new(222);
    private static readonly ChannelId ChannelA = new(1110);
    private static readonly ChannelId ChannelB = new(2220);

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await _host.SetUpEsportsGuildAsync(GuildA, ChannelA);
        await _host.SetUpEsportsGuildAsync(GuildB, ChannelB);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static NotificationRequest Request(GuildId guild, ChannelId channel, string source = "liquipedia:counterstrike:M1", string title = "A vs B",
        MentionPolicy? pings = null) =>
        new(guild, EsportsModule.ModuleIdTyped, source, channel, NotificationPlanner.KindResult,
            new OutgoingMessage(pings is null ? null : "<@&77>", new MessageEmbed(title, "body", null, [], "footer", null, null), pings ?? MentionPolicy.None),
            TestHost.T0.AddHours(6), IsDryRun: false);

    private Task<StageOutcome> StageAsync(NotificationRequest request) => _host.InScopeAsync(async sp =>
    {
        var outcome = await sp.GetRequiredService<INotificationOutbox>().StageAsync(request, CancellationToken.None);
        await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        return outcome;
    });

    private Task<List<OutboxMessageEntity>> RowsAsync() =>
        _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    private OutboxProcessor Processor => _host.Services.GetRequiredService<OutboxProcessor>();

    [Fact]
    public async Task Staging_the_same_logical_notification_twice_yields_one_row_and_one_message()
    {
        (await StageAsync(Request(GuildA, ChannelA))).Should().Be(StageOutcome.Created);
        (await StageAsync(Request(GuildA, ChannelA))).Should().Be(StageOutcome.Unchanged);
        await Processor.ProcessOnceAsync(CancellationToken.None);
        await Processor.ProcessOnceAsync(CancellationToken.None);

        (await RowsAsync()).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Sent);
        _host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Correction_after_delivery_edits_the_same_message_without_pinging()
    {
        await StageAsync(Request(GuildA, ChannelA, pings: new MentionPolicy([new RoleId(77)])));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        _host.Transport.Messages.Single().Pinged.Should().BeTrue("first send pings the configured role");

        (await StageAsync(Request(GuildA, ChannelA, title: "A vs B (corrected)", pings: new MentionPolicy([new RoleId(77)])))).Should().Be(StageOutcome.EditScheduled);
        await Processor.ProcessOnceAsync(CancellationToken.None);

        var message = _host.Transport.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().ContainSingle();
        message.Edits[0].Embed!.Title.Should().Be("A vs B (corrected)");
        message.Edits[0].Mentions.PingsAnything.Should().BeFalse("edits never re-ping");
        _host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task Ambiguous_timeout_is_reconciled_by_fingerprint_instead_of_resent()
    {
        await StageAsync(Request(GuildA, ChannelA));
        _host.Transport.ScriptAcceptedButTimedOut();
        await Processor.ProcessOnceAsync(CancellationToken.None);
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown);

        await Processor.ProcessOnceAsync(CancellationToken.None);
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown, "reconciliation waits for the reconcile delay");

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        var row = (await RowsAsync()).Single();
        row.Status.Should().Be(OutboxStatus.Sent);
        row.DiscordMessageId.Should().Be(_host.Transport.Messages.Single().Id.Value);
        _host.Transport.Messages.Should().ContainSingle("the accepted message was found — no duplicate");
        _host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task Ambiguous_delivery_that_really_failed_is_resent_once_after_verified_absence()
    {
        await StageAsync(Request(GuildA, ChannelA));
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout before Discord got it"));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Processor.ProcessOnceAsync(CancellationToken.None); // reconcile → not found → pending
        await Processor.ProcessOnceAsync(CancellationToken.None); // resend
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.Sent);
        _host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Verified_absence_allows_only_a_single_resend()
    {
        await StageAsync(Request(GuildA, ChannelA));
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout 1"));
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout 2"));
        _host.Transport.ScriptedReconcile = new ReconcileOutcome.NotFound();
        for (var i = 0; i < 8; i++)
        {
            await Processor.ProcessOnceAsync(CancellationToken.None);
            _host.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.Transport.SendCalls.Should().Be(2, "original + one resend, never a loop");
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.Failed);
    }

    [Fact]
    public async Task Crash_while_in_flight_becomes_delivery_unknown_on_restart_never_blind_resend()
    {
        await StageAsync(Request(GuildA, ChannelA));
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            var row = await db.Outbox.SingleAsync();
            row.Status = OutboxStatus.InFlight; // simulate: claimed, request possibly sent, process died
            row.DeliveredPayloadHash = row.PayloadHash;
            await db.SaveChangesAsync();
        });

        (await Processor.RecoverAsync(CancellationToken.None)).Should().Be(1);
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown);
        await Processor.ProcessOnceAsync(CancellationToken.None);
        _host.Transport.SendCalls.Should().Be(0, "no resend before reconciliation");
    }

    [Fact]
    public async Task Reconciliation_impossible_is_surfaced_not_retried_forever()
    {
        await StageAsync(Request(GuildA, ChannelA));
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        _host.Transport.ScriptedReconcile = new ReconcileOutcome.NotPossible("missing Read Message History");
        await Processor.ProcessOnceAsync(CancellationToken.None);
        for (var i = 0; i < 10; i++)
        {
            _host.Clock.Advance(TimeSpan.FromHours(1));
            await Processor.ProcessOnceAsync(CancellationToken.None);
        }

        var row = (await RowsAsync()).Single();
        row.Status.Should().Be(OutboxStatus.DeliveryUnknown);
        row.NextAttemptAt.Should().BeNull("after bounded attempts it waits for an operator (doctor)");
        row.ReconcileAttempts.Should().Be(3);
        _host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task Rate_limit_reschedules_after_retry_after()
    {
        await StageAsync(Request(GuildA, ChannelA));
        _host.Transport.ScriptSend(() => new SendOutcome.RateLimited(TimeSpan.FromSeconds(30)));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        var row = (await RowsAsync()).Single();
        row.Status.Should().Be(OutboxStatus.Pending);
        row.NextAttemptAt.Should().BeAfter(TestHost.T0.AddSeconds(30));

        await Processor.ProcessOnceAsync(CancellationToken.None);
        _host.Transport.SendCalls.Should().Be(1, "not before Retry-After");
        _host.Clock.Advance(TimeSpan.FromSeconds(31));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.Sent);
    }

    [Fact]
    public async Task Transient_errors_are_retried_with_bounded_attempts()
    {
        await StageAsync(Request(GuildA, ChannelA));
        for (var i = 0; i < 10; i++)
            _host.Transport.ScriptSend(() => new SendOutcome.Transient("503"));
        for (var i = 0; i < 10; i++)
        {
            await Processor.ProcessOnceAsync(CancellationToken.None);
            _host.Clock.Advance(TimeSpan.FromMinutes(40));
        }

        var row = (await RowsAsync()).Single();
        row.Status.Should().Be(OutboxStatus.Failed);
        row.Attempts.Should().Be(5);
        _host.Transport.SendCalls.Should().Be(5);
    }

    [Fact]
    public async Task Lost_permission_fails_permanently_flags_the_channel_and_does_not_affect_other_guilds()
    {
        await StageAsync(Request(GuildA, ChannelA, "liquipedia:counterstrike:M1"));
        await StageAsync(Request(GuildA, ChannelA, "liquipedia:counterstrike:M2"));
        await StageAsync(Request(GuildB, ChannelB, "liquipedia:counterstrike:M1"));
        _host.Transport.ScriptSend(() => new SendOutcome.Permanent(PermanentFailureKind.MissingPermissions, "Missing Permissions"));

        await Processor.ProcessOnceAsync(CancellationToken.None);

        var rows = await RowsAsync();
        rows.Single(r => r.GuildId == GuildA.Value && r.SourceKey.EndsWith("M1", StringComparison.Ordinal)).Status.Should().Be(OutboxStatus.Failed);
        rows.Single(r => r.GuildId == GuildA.Value && r.SourceKey.EndsWith("M2", StringComparison.Ordinal)).Status.Should().Be(OutboxStatus.Cancelled,
            "after the channel is flagged, further sends to it are cancelled (no retry storm)");
        rows.Single(r => r.GuildId == GuildB.Value).Status.Should().Be(OutboxStatus.Sent, "guild B is unaffected");
        _host.Transport.SendCalls.Should().Be(2);
        var problem = await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<EsportsGuildConfigEntity>().AsNoTracking()
            .Where(c => c.GuildId == GuildA.Value).Select(c => c.ChannelProblem).SingleAsync());
        problem.Should().Be(nameof(PermanentFailureKind.MissingPermissions));
    }

    [Fact]
    public async Task Deleted_message_on_edit_is_not_replaced_by_a_new_message()
    {
        await StageAsync(Request(GuildA, ChannelA));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        _host.Transport.DeleteMessage(_host.Transport.Messages.Single().Id);
        await StageAsync(Request(GuildA, ChannelA, title: "changed"));
        await Processor.ProcessOnceAsync(CancellationToken.None);

        var row = (await RowsAsync()).Single();
        row.EditPending.Should().BeFalse();
        row.LastError.Should().Be("edit_target_deleted");
        _host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task Module_disabled_or_paused_right_before_sending_cancels_delivery()
    {
        await StageAsync(Request(GuildA, ChannelA));
        await StageAsync(Request(GuildB, ChannelB));
        await _host.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(GuildA), "esports", false, CancellationToken.None);
            await sp.GetRequiredService<EsportsConfigService>().PauseAsync(TestHost.Admin(GuildB), true, CancellationToken.None);
        });

        await Processor.ProcessOnceAsync(CancellationToken.None);
        var rows = await RowsAsync();
        rows.Should().OnlyContain(r => r.Status == OutboxStatus.Cancelled);
        rows.Select(r => r.LastError).Should().BeEquivalentTo("module_disabled", "paused");
        _host.Transport.SendCalls.Should().Be(0);

        // Re-enabling does not resurrect cancelled items (no backlog flood).
        await _host.InScopeAsync(sp => sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(GuildA), "esports", true, CancellationToken.None));
        (await StageAsync(Request(GuildA, ChannelA))).Should().Be(StageOutcome.Unchanged);
        await Processor.ProcessOnceAsync(CancellationToken.None);
        _host.Transport.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Expired_notifications_are_not_sent_late()
    {
        await StageAsync(Request(GuildA, ChannelA));
        _host.Clock.Advance(TimeSpan.FromHours(7));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.Expired);
        _host.Transport.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Dry_run_rows_are_never_sent()
    {
        await StageAsync(Request(GuildA, ChannelA) with { IsDryRun = true });
        await Processor.ProcessOnceAsync(CancellationToken.None);
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.Simulated);
        _host.Transport.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Sent_messages_show_no_internal_reference_the_fingerprint_is_kept_in_the_database()
    {
        await StageAsync(Request(GuildA, ChannelA));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        var row = (await RowsAsync()).Single();
        var sent = _host.Transport.Messages.Single().Message;
        sent.Embed!.Footer.Should().Be("footer", "the footer is exactly what the module rendered");
        sent.Embed.Footer.Should().NotContain("ref").And.NotContain(row.Marker);
        row.DeliveredFingerprint.Should().Be(MessageFingerprint.Of(sent));
        row.Marker.Should().NotBeNullOrEmpty("the reference stays for logs/support");
    }

    [Fact]
    public async Task A_payload_replaced_while_delivery_is_unknown_is_still_reconciled_then_edited()
    {
        await StageAsync(Request(GuildA, ChannelA));
        _host.Transport.ScriptAcceptedButTimedOut();
        await Processor.ProcessOnceAsync(CancellationToken.None);
        (await StageAsync(Request(GuildA, ChannelA, title: "A vs B (corrected)"))).Should().Be(StageOutcome.UpdatedPending);

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Processor.ProcessOnceAsync(CancellationToken.None); // reconcile: finds what was SENT, not the new payload
        await Processor.ProcessOnceAsync(CancellationToken.None); // then edits it
        var message = _host.Transport.Messages.Should().ContainSingle("no duplicate").Subject;
        message.Edits.Should().ContainSingle().Which.Embed!.Title.Should().Be("A vs B (corrected)");
        _host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task An_identical_message_owned_by_another_delivery_is_never_taken()
    {
        // Two different notifications with identical visible content in one channel.
        await StageAsync(Request(GuildA, ChannelA, source: "liquipedia:counterstrike:M1"));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        await StageAsync(Request(GuildA, ChannelA, source: "liquipedia:counterstrike:M2"));
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout before Discord got it"));
        await Processor.ProcessOnceAsync(CancellationToken.None);

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Processor.ProcessOnceAsync(CancellationToken.None); // reconcile: the look-alike belongs to M1 → absent
        await Processor.ProcessOnceAsync(CancellationToken.None); // single resend
        var rows = await RowsAsync();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.Status == OutboxStatus.Sent);
        rows.Select(r => r.DiscordMessageId).Should().OnlyHaveUniqueItems();
        _host.Transport.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task Messages_sent_before_the_change_are_still_found_by_their_footer_reference()
    {
        await StageAsync(Request(GuildA, ChannelA));
        var marker = (await RowsAsync()).Single().Marker;
        // Legacy message: its footer carried "ref <marker>" and the row predates DeliveredFingerprint.
        await StageAsync(new NotificationRequest(GuildA, EsportsModule.ModuleIdTyped, "liquipedia:counterstrike:M1", ChannelA, NotificationPlanner.KindResult,
            new OutgoingMessage(null, new MessageEmbed("A vs B", "body", null, [], "footer • ref " + marker, null, null), MentionPolicy.None),
            TestHost.T0.AddHours(6), IsDryRun: false));
        _host.Transport.ScriptAcceptedButTimedOut();
        await Processor.ProcessOnceAsync(CancellationToken.None);
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            var row = await db.Outbox.SingleAsync();
            row.DeliveredFingerprint = null;
            await db.SaveChangesAsync();
        });
        (await StageAsync(Request(GuildA, ChannelA, title: "A vs B (new)"))).Should().Be(StageOutcome.UpdatedPending);

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await Processor.ProcessOnceAsync(CancellationToken.None);
        (await RowsAsync()).Single().Status.Should().Be(OutboxStatus.Sent);
        _host.Transport.SendCalls.Should().Be(1, "found by the legacy footer reference, not resent");
    }

    [Fact]
    public void Discord_side_fingerprint_of_the_converted_embed_equals_the_recorded_one()
    {
        var message = new OutgoingMessage("<@&77>",
            new MessageEmbed("Natus Vincere vs Aurora", "🕒 Maçın saati değişti", "https://www.hltv.org/matches/1/x",
                [new EmbedField("Etkinlik", "StarLadder", true), new EmbedField("​", "[Maç Sayfası](https://www.hltv.org/matches/1/x)")],
                "Kaynak: PandaScore", new DateTimeOffset(2026, 9, 25, 16, 0, 0, 123, TimeSpan.Zero), 0xF59F00),
            new MentionPolicy([new RoleId(77)]));
        ToroSquad.Discord.Transport.DiscordMessageTransport.Fingerprint(message.Content, ToroSquad.Discord.Transport.DiscordConversions.ToEmbed(message.Embed))
            .Should().Be(MessageFingerprint.Of(message));
        MessageFingerprint.Of(message with { Embed = message.Embed! with { Footer = "Kaynak: PandaScore • ref x" } })
            .Should().NotBe(MessageFingerprint.Of(message));
    }
}
