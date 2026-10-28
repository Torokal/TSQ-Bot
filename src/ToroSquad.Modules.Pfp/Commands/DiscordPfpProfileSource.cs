using Discord;
using Discord.Net;
using Discord.Rest;
using Microsoft.Extensions.Logging;
using ToroSquad.Modules.Pfp.Application;

namespace ToroSquad.Modules.Pfp.Commands;

/// <summary>
/// <see cref="IPfpProfileSource"/> over the bot's Discord.Net REST client (always a real REST request, never the gateway
/// cache): Get Guild then Get Guild Member for the member profile, Get User for the global banner. Discord.Net 3.20.1 turns
/// every 404 of these three routes into null, so the guild and the member are read as two separate steps: only a member null
/// is "not in this server"; a guild null, a user null and every failure — 403, 429 (after Discord.Net's own rate-limit
/// handling), 5xx, timeout, cancellation, network — is <see cref="PfpFetch.Failed"/>, never "no picture". Only the failure
/// kind and Discord's codes are logged (no exception text, no URLs, no tokens).
/// </summary>
public sealed class DiscordPfpProfileSource(DiscordRestClient rest, ILogger logger) : IPfpProfileSource
{
    public async Task<PfpMemberLookup> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken)
    {
        try
        {
            // The same two requests as DiscordRestClient.GetGuildUserAsync, but a guild Discord does not show is not "not a member".
            var guild = await rest.GetGuildAsync(guildId, withCounts: false, Options(cancellationToken));
            if (guild is null)
            {
                logger.LogWarning("Pfp: Discord guild read returned nothing");
                return PfpMemberLookup.Failed;
            }

            var member = await guild.GetUserAsync(userId, Options(cancellationToken));
            return member is null ? PfpMemberLookup.NotMember : PfpMemberLookup.Found(ToProfile(member));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            LogFailure("member", ex);
            return PfpMemberLookup.Failed;
        }
    }

    public async Task<PfpBannerLookup> GetUserBannerAsync(ulong userId, CancellationToken cancellationToken)
    {
        try
        {
            // Get User is the documented source of the global banner; a missing user here is uncertain, not "no banner".
            var user = await rest.GetUserAsync(userId, Options(cancellationToken));
            return user is null ? PfpBannerLookup.Failed : PfpBannerLookup.Found(PfpHash.FromOptionalField(user.BannerId));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            LogFailure("user", ex);
            return PfpBannerLookup.Failed;
        }
    }

    /// <summary>
    /// The member's own fields only: the global avatar hash (nested user object), this server's avatar and banner hashes and the
    /// SDK's default-avatar URL. The nested user's banner, the accent colour and the guild's banner are deliberately not read.
    /// Discord documents <c>user.avatar</c> as always sent (null = no avatar) but the member's <c>avatar?</c> / <c>banner?</c>
    /// as optional, and Discord.Net 3.20.1 keeps an unsent field at null (RestGuildUser.Update copies only specified fields), so
    /// a null there is <see cref="PfpHashState.NotReported"/>, never a confirmed absence.
    /// </summary>
    public static PfpMemberProfile ToProfile(IGuildUser member) =>
        new(member.Id, member.DisplayName, PfpHash.FromRequiredField(member.AvatarId), PfpHash.FromOptionalField(member.GuildAvatarId),
            PfpHash.FromOptionalField(member.GuildBannerHash), member.GetDefaultAvatarUrl());

    public static bool IsReadFailure(Exception ex) =>
        ex is HttpException or RateLimitedException or TimeoutException or HttpRequestException or OperationCanceledException or IOException;

    private static RequestOptions Options(CancellationToken cancellationToken) => new() { CancelToken = cancellationToken };

    private void LogFailure(string read, Exception ex)
    {
        var http = ex as HttpException;
        logger.LogWarning("Pfp: Discord {Read} read failed ({Error}) http={Http} discord={Code}", read, ex.GetType().Name, (int?)http?.HttpCode, (int?)http?.DiscordCode);
    }
}
