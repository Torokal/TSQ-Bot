using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>What /tsq-admin modul:lfg islem:status shows for one guild; <see cref="TimeZoneId"/> is the guild setting custom start dates are read in.</summary>
public sealed record LfgGuildStatus(ulong? ChannelId, int Open, int Full, int PendingCardUpdates, string TimeZoneId);

/// <summary>
/// /tsq-admin modul:lfg: the optional listing channel of a guild. Every method authorizes the actor (Manage Server) and only touches
/// the row of <c>actor.GuildId</c>.
/// </summary>
public sealed class LfgConfigService(ToroDbContext db, IGuildGateway guilds, IGuildSettingsStore settings, TimeProvider clock)
{
    public async Task<LfgGuildConfigEntity?> GetAsync(GuildId guild, CancellationToken ct) =>
        await db.Set<LfgGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);

    /// <summary>Restricts /ekip to one channel, or (null) allows it in every channel again.</summary>
    public async Task<OperationResult> SetChannelAsync(ActorContext actor, ulong? channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var missing = GuildPermission.None;
        if (channelId is { } id)
        {
            // The channel must exist IN THIS GUILD as the bot sees it (the bot edits its cards there when they expire).
            var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(id), ct);
            if (!access.Exists || !access.IsTextBased)
                return OperationResult.Fail(OperationError.InvalidInput, "lfg.config.channel_invalid");
            missing = access.MissingRequired;
        }

        var set = db.Set<LfgGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        if (config is null)
        {
            config = new LfgGuildConfigEntity { GuildId = actor.GuildId.Value };
            set.Add(config);
        }

        config.ChannelId = channelId;
        config.UpdatedAt = clock.GetUtcNow();
        config.UpdatedBy = actor.UserId.Value;
        await db.SaveChangesAsync(ct);

        if (channelId is not { } channel)
            return OperationResult.Ok("lfg.config.channel_cleared");
        var mention = "<#" + channel.ToString(CultureInfo.InvariantCulture) + ">";
        return missing == GuildPermission.None
            ? OperationResult.Ok("lfg.config.channel_saved", mention)
            : OperationResult.Ok("lfg.config.channel_saved_missing_permissions", mention, missing.ToString());
    }

    public async Task<(OperationResult Auth, LfgGuildStatus? Status)> StatusAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);

        var guild = actor.GuildId.Value;
        var now = clock.GetUtcNow();
        var config = await GetAsync(actor.GuildId, ct);
        var listings = db.Set<LfgListingEntity>().AsNoTracking().Where(x => x.GuildId == guild);
        var open = await listings.CountAsync(x => x.Status == LfgStatus.Open && x.ExpiresAt > now, ct);
        var full = await listings.CountAsync(x => x.Status == LfgStatus.Full && x.ExpiresAt > now, ct);
        var pending = await listings.CountAsync(x => x.CardStale, ct);
        var zone = (await settings.GetAsync(actor.GuildId, ct)).TimeZoneId;
        return (OperationResult.Ok("lfg.status.title"), new LfgGuildStatus(config?.ChannelId, open, full, pending, zone));
    }
}
