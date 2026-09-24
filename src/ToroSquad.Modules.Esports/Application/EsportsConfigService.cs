using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;

namespace ToroSquad.Modules.Esports.Application;

public sealed record EsportsConfigView(
    bool Configured,
    ulong? ChannelId,
    bool NotifyReminders,
    int ReminderLeadMinutes,
    bool NotifyResults,
    bool SpoilerMode,
    bool Paused,
    int? VrsTopN,
    string? ChannelProblem,
    GuildFilterSet Filters,
    IReadOnlyDictionary<(FilterDimension, string), string?> FilterLabels);

/// <summary>
/// /esports-admin configure|filters|pause|resume. Every method authorizes the actor (Manage Server) and only ever
/// touches rows of <c>actor.GuildId</c> — IDs supplied by the caller are never trusted to select the guild.
/// </summary>
public sealed class EsportsConfigService(ToroDbContext db, IGuildGateway guilds, TimeProvider clock)
{
    public const int MaxFiltersPerDimension = 25;

    public async Task<EsportsConfigView> GetAsync(GuildId guild, CancellationToken ct)
    {
        var config = await db.Set<EsportsGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        var filters = await db.Set<EsportsFilterEntity>().AsNoTracking().Where(f => f.GuildId == guild.Value).ToListAsync(ct);
        HashSet<string> Of(FilterDimension d) => filters.Where(r => r.Dimension == (int)d).Select(r => r.Value).ToHashSet(StringComparer.Ordinal);
        var set = new GuildFilterSet(Of(FilterDimension.Team), Of(FilterDimension.Tournament), Of(FilterDimension.Tier), config?.VrsTopN);
        var labels = filters.ToDictionary(f => ((FilterDimension)f.Dimension, f.Value), f => f.Label);
        return config is null
            ? new EsportsConfigView(false, null, true, 15, true, false, false, null, null, set, labels)
            : new EsportsConfigView(true, config.ChannelId, config.NotifyReminders, config.ReminderLeadMinutes, config.NotifyResults,
                config.SpoilerMode, config.Paused, config.VrsTopN, config.ChannelProblem, set, labels);
    }

    public async Task<OperationResult> ConfigureAsync(
        ActorContext actor,
        ulong? channelId,
        bool? reminders,
        int? leadMinutes,
        bool? results,
        bool? spoilers,
        CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        if (leadMinutes is < 1 or > 120)
            return OperationResult.Fail(OperationError.InvalidInput, "esports.config.lead_invalid");

        string? warning = null;
        if (channelId is { } cid)
        {
            // The channel must exist IN THIS GUILD as the bot sees it; foreign/unknown IDs are rejected.
            var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(cid), ct);
            if (!access.Exists || !access.IsTextBased)
                return OperationResult.Fail(OperationError.InvalidInput, "esports.config.channel_invalid");
            if (access.MissingRequired != GuildPermission.None)
                warning = access.MissingRequired.ToString();
        }

        var config = await GetOrCreateAsync(actor, ct);
        var reannounceRisk = (channelId is not null && channelId != config.ChannelId) ||
                             (reminders == true && !config.NotifyReminders) ||
                             (results == true && !config.NotifyResults);
        if (reannounceRisk)
        {
            // Outbox keys include the channel and kind: without this, recent results/reminders would be posted again
            // (with pings) to the new channel or after re-enabling a notification type.
            config.WatermarkUtc = clock.GetUtcNow();
        }

        if (channelId is not null)
        {
            config.ChannelId = channelId;
            config.ChannelProblem = null; // re-checked above; doctor re-verifies
            config.ChannelProblemAt = null;
        }

        config.NotifyReminders = reminders ?? config.NotifyReminders;
        config.ReminderLeadMinutes = leadMinutes ?? config.ReminderLeadMinutes;
        config.NotifyResults = results ?? config.NotifyResults;
        config.SpoilerMode = spoilers ?? config.SpoilerMode;
        Touch(config, actor);
        await db.SaveChangesAsync(ct);
        return warning is null
            ? OperationResult.Ok("esports.config.saved")
            : OperationResult.Ok("esports.config.saved_with_warning", warning);
    }

    public async Task<OperationResult> SetFilterAsync(ActorContext actor, FilterDimension dimension, string value, string? label, bool add, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        value = value.Trim();
        if (value.Length is 0 or > 200)
            return OperationResult.Fail(OperationError.InvalidInput, "esports.filters.invalid_value");
        if (dimension == FilterDimension.Tier && value is not ("1" or "2" or "3" or "4" or "5"))
            return OperationResult.Fail(OperationError.InvalidInput, "esports.filters.invalid_tier");

        await GetOrCreateAsync(actor, ct);
        var set = db.Set<EsportsFilterEntity>();
        var existing = await set.FirstOrDefaultAsync(f => f.GuildId == actor.GuildId.Value && f.Dimension == (int)dimension && f.Value == value, ct);
        if (add)
        {
            if (existing is not null)
                return OperationResult.Ok("esports.filters.already_present");
            if (await set.CountAsync(f => f.GuildId == actor.GuildId.Value && f.Dimension == (int)dimension, ct) >= MaxFiltersPerDimension)
                return OperationResult.Fail(OperationError.InvalidInput, "esports.filters.too_many", MaxFiltersPerDimension);
            set.Add(new EsportsFilterEntity { GuildId = actor.GuildId.Value, Dimension = (int)dimension, Value = value, Label = label?[..Math.Min(label.Length, 200)] });
        }
        else
        {
            if (existing is null)
                return OperationResult.Ok("esports.filters.not_present");
            set.Remove(existing);
        }

        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(add ? "esports.filters.added" : "esports.filters.removed");
    }

    public async Task<OperationResult> SetVrsTopNAsync(ActorContext actor, int? topN, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        if (topN is < 1 or > 400)
            return OperationResult.Fail(OperationError.InvalidInput, "esports.filters.vrs_invalid");
        var config = await GetOrCreateAsync(actor, ct);
        config.VrsTopN = topN;
        Touch(config, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(topN is null ? "esports.filters.vrs_cleared" : "esports.filters.vrs_set", topN ?? 0);
    }

    public async Task<OperationResult> ClearFiltersAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        await db.Set<EsportsFilterEntity>().Where(f => f.GuildId == actor.GuildId.Value).ExecuteDeleteAsync(ct);
        var config = await GetOrCreateAsync(actor, ct);
        config.VrsTopN = null;
        Touch(config, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok("esports.filters.cleared");
    }

    public async Task<OperationResult> PauseAsync(ActorContext actor, bool pause, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        var config = await GetOrCreateAsync(actor, ct);
        if (config.Paused == pause)
            return OperationResult.Ok(pause ? "esports.pause.already" : "esports.resume.already");
        config.Paused = pause;
        if (!pause)
            config.WatermarkUtc = clock.GetUtcNow(); // controlled restart: nothing that became due while paused is sent
        Touch(config, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(pause ? "esports.pause.done" : "esports.resume.done");
    }

    private async Task<EsportsGuildConfigEntity> GetOrCreateAsync(ActorContext actor, CancellationToken ct)
    {
        var set = db.Set<EsportsGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        if (config is null)
        {
            config = new EsportsGuildConfigEntity { GuildId = actor.GuildId.Value, WatermarkUtc = clock.GetUtcNow() };
            set.Add(config);
        }

        return config;
    }

    private void Touch(EsportsGuildConfigEntity config, ActorContext actor)
    {
        config.UpdatedAt = clock.GetUtcNow();
        config.UpdatedBy = actor.UserId.Value;
    }
}

/// <summary>Enable/disable hooks: enabling (again) moves the watermark so old items are never delivered in bulk.</summary>
public sealed class EsportsLifecycle(ToroDbContext db, TimeProvider clock) : IModuleLifecycleHandler
{
    public ModuleId Module => EsportsModule.ModuleIdTyped;

    public async Task OnEnabledAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var set = db.Set<EsportsGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
        {
            config = new EsportsGuildConfigEntity { GuildId = guild.Value };
            set.Add(config);
        }

        config.WatermarkUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task OnDisabledAsync(GuildId guild, CancellationToken cancellationToken) =>
        // Nothing is deleted. Pending outbox rows are cancelled at dispatch time by the module gate.
        Task.CompletedTask;
}

/// <summary>Checked by the dispatcher immediately before each send/edit.</summary>
public sealed class EsportsDeliveryPolicy(ToroDbContext db, TimeProvider clock) : IDeliveryPolicy
{
    public ModuleId Module => EsportsModule.ModuleIdTyped;

    public async Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<EsportsGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
            return new DeliveryDecision.Cancel("not_configured");
        if (config.Paused)
            return new DeliveryDecision.Cancel("paused");
        if (config.ChannelId != channel.Value)
            return new DeliveryDecision.Cancel("channel_changed");
        if (config.ChannelProblem is not null)
            return new DeliveryDecision.Cancel("channel_problem");
        // Schedule-related cards (reminder, started, postponed, rescheduled, cancelled) follow the reminders switch.
        var scheduleKind = kind is NotificationPlanner.KindReminder or NotificationPlanner.KindStarted or NotificationPlanner.KindPostponed or NotificationPlanner.KindCancelled ||
                           kind.StartsWith(NotificationPlanner.KindRescheduledPrefix, StringComparison.Ordinal);
        if (scheduleKind && !config.NotifyReminders)
            return new DeliveryDecision.Cancel("reminders_disabled");
        if (kind == NotificationPlanner.KindResult && !config.NotifyResults)
            return new DeliveryDecision.Cancel("results_disabled");
        return DeliveryDecision.Allowed;
    }

    public async Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<EsportsGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null || config.ChannelId != channel.Value)
            return;
        // Stop sending to this guild's channel until an admin fixes it (no retry storm, other guilds unaffected).
        config.ChannelProblem = kind.ToString();
        config.ChannelProblemAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
