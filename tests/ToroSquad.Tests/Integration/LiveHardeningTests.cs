using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Application;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Support.LiveBed;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Live hardening: the outbox-level @everyone guarantee (independent of the Live planner), transport classification of
/// create failures, malformed provider answers during a live session, and the doctor's Discord permission checks.
/// </summary>
public sealed class LiveHardeningTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private static async Task<long> StageAsync(TestHost host, OutgoingMessage message)
    {
        await host.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<INotificationOutbox>().StageAsync(new NotificationRequest(Guild, ModuleId.Core, "src", Channel, "kind", message,
                host.Clock.GetUtcNow() + TimeSpan.FromHours(1), IsDryRun: false), CancellationToken.None);
            await sp.GetRequiredService<ToroDbContext>().SaveChangesAsync();
        });
        return await host.InScopeAsync(async sp => (await sp.GetRequiredService<ToroDbContext>().Outbox.SingleAsync()).Id);
    }

    private static async Task DeliverAsync(TestHost host, TimeSpan advance)
    {
        host.Clock.Advance(advance);
        var processor = host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(CancellationToken.None) > 0)
        {
        }
    }

    [Fact]
    public async Task The_outbox_never_resends_an_everyone_opt_in_after_an_uncertain_attempt_whatever_the_payload_says()
    {
        await using var host = await TestHost.CreateAsync();
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, ChannelPermissions));
        host.Transport.ScriptAcceptedButTimedOut();
        host.Transport.ScriptedReconcile = new ReconcileOutcome.NotFound();
        await StageAsync(host, new OutgoingMessage("@everyone x", null, new MentionPolicy([new RoleId(5)], Everyone: true)));

        await DeliverAsync(host, TimeSpan.Zero);
        await DeliverAsync(host, TimeSpan.FromMinutes(1));

        host.Transport.Messages.Should().HaveCount(2);
        host.Transport.Messages.Count(m => m.Message.Mentions.Everyone).Should().Be(1, "only the first (uncertain) attempt carried @everyone");
        host.Transport.Messages[1].Message.Mentions.Roles.Should().Equal([new RoleId(5)], "role pings of other modules are unchanged by this rule");
    }

    [Fact]
    public async Task Role_ping_rows_keep_their_existing_resend_behaviour()
    {
        await using var host = await TestHost.CreateAsync();
        host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        await StageAsync(host, new OutgoingMessage("<@&5>", null, new MentionPolicy([new RoleId(5)])));

        await DeliverAsync(host, TimeSpan.Zero);
        await DeliverAsync(host, TimeSpan.FromMinutes(1));

        host.Transport.Messages.Should().ContainSingle().Which.Message.Mentions.Roles.Should().Equal([new RoleId(5)]);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Every_5xx_on_create_is_ambiguous_never_a_blind_retry(HttpStatusCode status)
    {
        DiscordMessageTransport.Classify(status, null, "x", isCreate: true).Should().BeOfType<SendOutcome.Ambiguous>();
        DiscordMessageTransport.Classify(status, null, "x", isCreate: false).Should().BeOfType<SendOutcome.Transient>("edits are idempotent");
        DiscordMessageTransport.Classify(HttpStatusCode.TooManyRequests, null, "x", isCreate: true).Should().BeOfType<SendOutcome.RateLimited>();
    }

    [Fact]
    public async Task A_malformed_kick_answer_during_a_live_session_never_closes_it()
    {
        await using var bed = await CreateAsync();
        await bed.PollAsync();
        bed.Kick.GoLive(Toro, Stream("k1", bed.Now, "Kick yayını"));
        await bed.StepAsync(Poll);
        bed.Kick.Undescribed.Add(Toro); // answers no longer describe the channel (stream null / missing / unknown slug)
        bed.Kick.GoOffline(Toro);
        for (var i = 0; i < 20; i++)
            await bed.StepAsync(Poll);

        (await bed.PlatformAsync(Toro, LivePlatform.Kick)).Status.Should().Be(PlatformStatus.Live);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.Live);
        bed.Messages.Should().ContainSingle().Which.Edits.Should().BeEmpty("no premature 'ended' card");
    }

    [Fact]
    public async Task A_tracked_platform_that_stays_unknown_never_lets_the_session_end()
    {
        await using var bed = await CreateAsync();
        bed.Kick.Undescribed.Add(Toro); // Kick answers never describe LORDTORO (malformed / unknown channel) → UNKNOWN
        await bed.PollAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Twitch yayını"));
        await bed.StepAsync(Poll);
        bed.Twitch.GoOffline(Toro);
        for (var i = 0; i < 20; i++)
            await bed.StepAsync(Poll); // grace long over, Twitch confirmed offline many times

        (await bed.PlatformAsync(Toro, LivePlatform.Kick)).Status.Should().Be(PlatformStatus.Unknown);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.ReconnectGrace);
        bed.Messages.Should().ContainSingle().Which.Edits.Should().BeEmpty("no '⚫ yayını sona erdi' while Kick is UNKNOWN");

        // Kick explicitly confirms offline: the session ends normally.
        bed.Kick.Undescribed.Remove(Toro);
        await bed.StepAsync(Poll);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.Offline);
        bed.Messages[0].Edits.Should().ContainSingle().Which.Content.Should().Be("⚫ **LORDTORO** yayını sona erdi.");
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task Doctor_reports_every_required_discord_permission_and_fails_without_mention_everyone()
    {
        await using var bed = await CreateAsync();
        var ok = await DoctorAsync(bed);
        foreach (var perm in new[] { "ViewChannel", "SendMessages", "EmbedLinks", "ReadMessageHistory", "MentionEveryone" })
            ok.Should().Contain(c => c.LabelKey == "live.doctor.perm" && c.State == LiveCheckState.Ok && c.Args.Contains(perm), perm);

        bed.Host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks));
        var missing = await DoctorAsync(bed);
        missing.Should().Contain(c => c.LabelKey == "live.doctor.perm" && c.State == LiveCheckState.Problem && c.DetailKey == "live.doctor.perm_missing" && c.Args.Contains("MentionEveryone"));
        missing.Should().Contain(c => c.LabelKey == "live.doctor.perm" && c.State == LiveCheckState.Warning && c.Args.Contains("ReadMessageHistory"));

        var member = await bed.Host.InScopeAsync(sp => sp.GetRequiredService<LiveDoctor>().RunAsync(TestHost.Member(Guild), CancellationToken.None));
        member.Auth.Succeeded.Should().BeFalse("Manage Server only");
    }

    private static async Task<IReadOnlyList<LiveDoctorCheck>> DoctorAsync(LiveBed bed)
    {
        var (auth, checks) = await bed.Host.InScopeAsync(sp => sp.GetRequiredService<LiveDoctor>().RunAsync(TestHost.Admin(Guild), CancellationToken.None));
        auth.Succeeded.Should().BeTrue();
        checks.SelectMany(c => c.Args).OfType<string>().Should().NotContain(a => a.Contains("secret", StringComparison.OrdinalIgnoreCase));
        return checks;
    }
}
