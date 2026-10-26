using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Ai;
using ToroSquad.Core;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>One day of the guild's totals (counts only — nobody's question, answer or note).</summary>
public sealed record SorUsageDay(string Day, int Requests, int Completed, int ProviderFailures, int Timeouts, int ParseFailures, int DeliveryFailures,
    int Discarded, int UnknownUsage, long InputTokens, long OutputTokens, long ReasoningTokens, long CachedInputTokens);

/// <summary>A configuration check of the doctor (no AI request: a check must not spend quota).</summary>
public sealed record SorDoctorLine(bool Ok, string Key, IReadOnlyList<object> Args);

/// <summary>
/// /tsq-admin modul:sor — Manage Server, checked here with <see cref="Authorize.Require"/> on every call (the command being
/// hidden is not the control). Allowed channels and roles, limits within <see cref="SorLimits"/> bounds, the guild's usage
/// totals and a doctor. The model and provider are configuration only — never a member or admin option. There is no
/// operation that reads anyone's conversations or notes.
/// </summary>
public sealed class SorConfigService(ToroDbContext db, SorSettingsReader reader, [Microsoft.Extensions.DependencyInjection.FromKeyedServices(SorModule.ModuleIdValue)] IAiChatClient ai,
    IOptions<SorOptions> options, TimeProvider clock)
{
    public SorOptions Options => options.Value;

    public bool IsConfigured => ai.IsConfigured;

    /// <summary>The guild's settings for an admin screen; null when the caller may not manage the server.</summary>
    public async Task<SorGuildSettings?> GetAsync(ActorContext actor, CancellationToken ct) =>
        Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings).IsAllowed ? await reader.GetAsync(actor.GuildId, ct) : null;

    public Task<OperationResult> AllowChannelAsync(ActorContext actor, ulong channel, bool allow, CancellationToken ct) =>
        UpdateAsync(actor, row =>
        {
            var ids = SorGuildSettings.ParseIds(row.AllowedChannelIds).ToList();
            if (allow && !ids.Contains(channel))
            {
                if (ids.Count >= SorSettingsReader.MaxListedIds)
                    return OperationResult.Fail(OperationError.InvalidInput, "admin.sor.list_full", SorSettingsReader.MaxListedIds);
                ids.Add(channel);
            }
            else if (!allow && !ids.Remove(channel))
            {
                return OperationResult.Ok("admin.sor.channel_not_listed", "<#" + channel + ">");
            }

            row.AllowedChannelIds = SorGuildSettings.FormatIds(ids);
            return OperationResult.Ok(allow ? "admin.sor.channel_allowed" : "admin.sor.channel_removed", "<#" + channel + ">", ids.Count);
        }, ct);

    public Task<OperationResult> AllowRoleAsync(ActorContext actor, ulong role, bool allow, CancellationToken ct) =>
        UpdateAsync(actor, row =>
        {
            var ids = SorGuildSettings.ParseIds(row.AllowedRoleIds).ToList();
            if (allow && !ids.Contains(role))
            {
                if (ids.Count >= SorSettingsReader.MaxListedIds)
                    return OperationResult.Fail(OperationError.InvalidInput, "admin.sor.list_full", SorSettingsReader.MaxListedIds);
                ids.Add(role);
            }
            else if (!allow && !ids.Remove(role))
            {
                return OperationResult.Ok("admin.sor.role_not_listed", "<@&" + role + ">");
            }

            row.AllowedRoleIds = SorGuildSettings.FormatIds(ids);
            return OperationResult.Ok(allow ? "admin.sor.role_allowed" : "admin.sor.role_removed", "<@&" + role + ">", ids.Count);
        }, ct);

    /// <summary>Changes only the given values (null = keep); the whole set must stay within <see cref="SorLimits"/> bounds.</summary>
    public Task<OperationResult> SetLimitsAsync(ActorContext actor, int? cooldown, int? userDaily, int? guildDaily, int? turns, int? idle, int? hours, CancellationToken ct) =>
        UpdateAsync(actor, row =>
        {
            var current = SorSettingsReader.From(row, options.Value).Limits;
            var next = new SorLimits(cooldown ?? current.UserCooldownSeconds, userDaily ?? current.UserDailyLimit, guildDaily ?? current.GuildDailyLimit,
                turns ?? current.MaxContextTurns, idle ?? current.IdleMinutes, hours ?? current.HistoryHours);
            if (next.HistoryHours > options.Value.HistoryRetentionHours)
                return OperationResult.Fail(OperationError.InvalidInput, "admin.sor.limits_invalid", $"HistoryRetentionHours ≤ {options.Value.HistoryRetentionHours}");
            if (next.Problems() is { Count: > 0 } problems)
                return OperationResult.Fail(OperationError.InvalidInput, "admin.sor.limits_invalid", string.Join("; ", problems));
            row.UserCooldownSeconds = next.UserCooldownSeconds;
            row.UserDailyLimit = next.UserDailyLimit;
            row.GuildDailyLimit = next.GuildDailyLimit;
            row.MaxContextTurns = next.MaxContextTurns;
            row.IdleMinutes = next.IdleMinutes;
            row.HistoryHours = next.HistoryHours;
            return OperationResult.Ok("admin.sor.limits_saved");
        }, ct);

    public async Task<IReadOnlyList<SorUsageDay>> UsageAsync(ActorContext actor, int days, CancellationToken ct)
    {
        if (!Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings).IsAllowed)
            return [];
        var rows = await db.Set<SorGuildUsageEntity>().AsNoTracking().Where(u => u.GuildId == actor.GuildId.Value)
            .OrderByDescending(u => u.DayKey).Take(days).ToListAsync(ct);
        return rows.Select(u => new SorUsageDay(u.DayKey, u.Requests, u.Completed, u.ProviderFailures, u.Timeouts, u.ParseFailures, u.DeliveryFailures,
            u.Discarded, u.UnknownUsage, u.InputTokens, u.OutputTokens, u.ReasoningTokens, u.CachedInputTokens)).ToList();
    }

    /// <summary>Configuration only — key present (never its value), endpoint, model id, limits, the tables — and no paid request.</summary>
    public async Task<IReadOnlyList<SorDoctorLine>> DoctorAsync(ActorContext actor, CancellationToken ct)
    {
        if (!Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings).IsAllowed)
            return [];
        var settings = options.Value;
        var lines = new List<SorDoctorLine>
        {
            new(ai.IsConfigured, ai.IsConfigured ? "admin.sor.doctor.key_ok" : "admin.sor.doctor.key_missing", [OpenCodeGo.ApiKeyVariable]),
            new(OpenCodeGo.IsValidBaseUrl(settings.BaseUrl), "admin.sor.doctor.endpoint", [settings.BaseUrl]),
            new(OpenCodeGo.IsValidModel(settings.Model), "admin.sor.doctor.model", [settings.Model, settings.DisableThinking ? "disabled" : "enabled", settings.MaxOutputTokens, settings.RequestTimeoutSeconds]),
        };
        var problems = settings.Validate();
        lines.Add(new SorDoctorLine(problems.Count == 0, problems.Count == 0 ? "admin.sor.doctor.options_ok" : "admin.sor.doctor.options_bad", [string.Join("; ", problems)]));
        var guild = await reader.GetAsync(actor.GuildId, ct);
        lines.Add(new SorDoctorLine(true, "admin.sor.doctor.limits", [guild.Limits.UserCooldownSeconds, guild.Limits.UserDailyLimit, guild.Limits.GuildDailyLimit]));
        try
        {
            var count = await db.Set<SorConversationEntity>().CountAsync(c => c.GuildId == actor.GuildId.Value, ct);
            lines.Add(new SorDoctorLine(true, "admin.sor.doctor.storage_ok", [count]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lines.Add(new SorDoctorLine(false, "admin.sor.doctor.storage_bad", [ex.GetType().Name]));
        }

        lines.Add(new SorDoctorLine(true, "admin.sor.doctor.no_request", []));
        return lines;
    }

    private Task<OperationResult> UpdateAsync(ActorContext actor, Func<SorGuildConfigEntity, OperationResult> change, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return Task.FromResult(OperationResult.Forbidden(auth));
        return SorWrites.RunAsync(db, async () =>
        {
            var row = await db.Set<SorGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
            if (row is null)
            {
                row = new SorGuildConfigEntity { GuildId = actor.GuildId.Value };
                db.Add(row);
            }

            var result = change(row);
            if (!result.Succeeded)
                return result;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBy = actor.UserId.Value;
            await db.SaveChangesAsync(ct);
            return result;
        }, ct);
    }
}
