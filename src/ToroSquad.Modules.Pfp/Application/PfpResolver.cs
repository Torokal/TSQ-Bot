using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Pfp.Application;

public enum PfpResolutionKind
{
    /// <summary>A picture was found (<see cref="PfpResolution.Source"/>, <see cref="PfpResolution.Url"/>).</summary>
    Image = 0,

    /// <summary>Discord answered and the asked picture confirmed does not exist (<see cref="PfpResolution.MissingKey"/> says which).</summary>
    Missing = 1,

    /// <summary>Discord answered 404 for the member: the person is not (or no longer) in this server.</summary>
    NotMember = 2,

    /// <summary>The profile could not be read (timeout, 403, 429, 5xx, unexpected data): nothing is claimed about the picture.</summary>
    Unavailable = 3,
}

/// <summary>
/// The outcome. For an <see cref="PfpResolutionKind.Image"/>, <see cref="NoteKey"/> is set when the automatic order went past
/// a higher source that Discord did not report (not a confirmed absence), so the card says so instead of falling back silently.
/// </summary>
public sealed record PfpResolution(
    PfpResolutionKind Kind, string? DisplayName = null, PfpSource? Source = null, string? Url = null, string? MissingKey = null, string? NoteKey = null)
{
    public static readonly PfpResolution NotMember = new(PfpResolutionKind.NotMember);
    public static readonly PfpResolution Unavailable = new(PfpResolutionKind.Unavailable);

    public static PfpResolution Image(string displayName, PfpSource source, string url, string? noteKey = null) =>
        new(PfpResolutionKind.Image, displayName, source, url, NoteKey: noteKey);

    public static PfpResolution Missing(string key) => new(PfpResolutionKind.Missing, MissingKey: key);
}

/// <summary>
/// Picks the picture (docs/pfp/TSQ_PFP.md, selection table). The member is always read first (Get Guild Member: membership,
/// display name, this server's avatar and banner, the global avatar); the global banner only when it is needed, with Get User
/// — never from the member's nested user object or the gateway cache, where a missing banner field does not mean "no banner".
/// The automatic order falls back silently only past a source Discord confirmed as absent; past one Discord did not report
/// (<see cref="PfpHashState.NotReported"/>) the card says so (<see cref="PfpResolution.NoteKey"/>). An explicit
/// <see cref="PfpScope.Server"/> never falls back to the global picture, and the "no picture" notices only say what Discord
/// did not report. A read that failed, a hash that is not a Discord hash or a missing always-sent avatar field is
/// <see cref="PfpResolutionKind.Unavailable"/>, never "no picture". The guild's own banner and the profile accent colour are
/// never read: they are not the member's picture.
/// </summary>
public sealed class PfpResolver(ILogger<PfpResolver> logger)
{
    public const string MissingServerAvatarKey = "pfp.missing.server_avatar";
    public const string MissingServerBannerKey = "pfp.missing.server_banner";
    public const string MissingGlobalBannerKey = "pfp.missing.global_banner";
    public const string MissingAnyBannerKey = "pfp.missing.any_banner";
    public const string ServerAvatarNotReportedKey = "pfp.note.server_avatar_not_reported";
    public const string ServerBannerNotReportedKey = "pfp.note.server_banner_not_reported";

    public async Task<PfpResolution> ResolveAsync(IPfpProfileSource source, ulong guildId, ulong userId, PfpKind kind, PfpScope scope, CancellationToken cancellationToken)
    {
        var member = await source.GetMemberAsync(guildId, userId, cancellationToken);
        if (member.Status == PfpFetch.NotFound)
            return PfpResolution.NotMember;
        if (member.Status != PfpFetch.Ok || member.Profile is not { } profile)
            return PfpResolution.Unavailable;

        return kind == PfpKind.Avatar
            ? Avatar(guildId, profile, scope)
            : await BannerAsync(source, guildId, profile, scope, cancellationToken);
    }

    private PfpResolution Avatar(ulong guildId, PfpMemberProfile profile, PfpScope scope)
    {
        if (scope is PfpScope.Auto or PfpScope.Server && profile.GuildAvatar is { State: PfpHashState.Present, Value: { } guildHash })
            return Found(profile, PfpSource.ServerAvatar, PfpCdn.MemberAvatar(guildId, profile.UserId, guildHash));
        if (scope == PfpScope.Server)
            return PfpResolution.Missing(MissingServerAvatarKey);

        var note = scope == PfpScope.Auto && profile.GuildAvatar.State == PfpHashState.NotReported ? ServerAvatarNotReportedKey : null;
        return profile.Avatar switch
        {
            { State: PfpHashState.Present, Value: { } hash } => Found(profile, PfpSource.GlobalAvatar, PfpCdn.UserAvatar(profile.UserId, hash), note),
            { State: PfpHashState.None } => Found(profile, PfpSource.DefaultAvatar,
                PfpCdn.IsDefaultAvatarUrl(profile.DefaultAvatarUrl) ? profile.DefaultAvatarUrl : null, note),
            _ => Unexpected(PfpSource.GlobalAvatar),
        };
    }

    private async Task<PfpResolution> BannerAsync(IPfpProfileSource source, ulong guildId, PfpMemberProfile profile, PfpScope scope, CancellationToken cancellationToken)
    {
        if (scope is PfpScope.Auto or PfpScope.Server && profile.GuildBanner is { State: PfpHashState.Present, Value: { } guildHash })
            return Found(profile, PfpSource.ServerBanner, PfpCdn.MemberBanner(guildId, profile.UserId, guildHash));
        if (scope == PfpScope.Server)
            return PfpResolution.Missing(MissingServerBannerKey);

        var user = await source.GetUserBannerAsync(profile.UserId, cancellationToken);
        if (user.Status != PfpFetch.Ok)
            return PfpResolution.Unavailable;
        var note = scope == PfpScope.Auto && profile.GuildBanner.State == PfpHashState.NotReported ? ServerBannerNotReportedKey : null;
        if (user.Banner is { State: PfpHashState.Present, Value: { } hash })
            return Found(profile, PfpSource.GlobalBanner, PfpCdn.UserBanner(profile.UserId, hash), note);
        return PfpResolution.Missing(scope == PfpScope.Auto ? MissingAnyBannerKey : MissingGlobalBannerKey);
    }

    /// <summary>A present hash that does not make a URL is unexpected data from Discord: uncertain, never "no picture".</summary>
    private PfpResolution Found(PfpMemberProfile profile, PfpSource pictureSource, string? url, string? noteKey = null) =>
        url is not null ? PfpResolution.Image(profile.DisplayName, pictureSource, url, noteKey) : Unexpected(pictureSource);

    private PfpResolution Unexpected(PfpSource pictureSource)
    {
        logger.LogWarning("Pfp: Discord returned an unexpected {Source} value; the picture is not shown", pictureSource);
        return PfpResolution.Unavailable;
    }
}
