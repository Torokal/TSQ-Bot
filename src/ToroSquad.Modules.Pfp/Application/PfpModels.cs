namespace ToroSquad.Modules.Pfp.Application;

/// <summary>Which picture the command shows: /pfp = avatar, /banner = profile banner.</summary>
public enum PfpKind
{
    Avatar = 0,
    Banner = 1,
}

/// <summary>
/// The <c>tur</c> option. "Server" always means the member's profile in the guild the command was used in, never another
/// guild's profile.
/// </summary>
public enum PfpScope
{
    /// <summary>This server's own picture when the member has one, else the global one (the avatar then the default avatar).</summary>
    Auto = 0,

    /// <summary>The global profile only (an avatar falls back to Discord's default avatar, a banner to nothing).</summary>
    Global = 1,

    /// <summary>This server's own profile only: never a silent fallback to the global picture.</summary>
    Server = 2,
}

/// <summary>Where the shown picture comes from — named on the card exactly as it was chosen.</summary>
public enum PfpSource
{
    GlobalAvatar = 0,
    ServerAvatar = 1,
    DefaultAvatar = 2,
    GlobalBanner = 3,
    ServerBanner = 4,
}

/// <summary>The <c>tur</c> choice values (the option's <c>Choice</c> attributes use the same strings).</summary>
public static class PfpScopes
{
    public const string Auto = "otomatik";
    public const string Global = "genel";
    public const string Server = "sunucu";

    /// <summary>No value = <see cref="PfpScope.Auto"/>; anything else than the three choices is refused.</summary>
    public static bool TryParse(string? value, out PfpScope scope)
    {
        scope = value switch
        {
            null or Auto => PfpScope.Auto,
            Global => PfpScope.Global,
            Server => PfpScope.Server,
            _ => (PfpScope)(-1),
        };
        return Enum.IsDefined(scope);
    }
}

/// <summary>
/// How one Discord REST read ended. <see cref="Ok"/> means Discord answered and the fields below are its answer (each picture
/// field says itself how sure it is: <see cref="PfpHash"/>); <see cref="NotFound"/> means Discord answered 404 (for a member:
/// not in this server); <see cref="Failed"/> covers everything uncertain — timeout, 403, 429, 5xx, network — and is never read
/// as "no picture".
/// </summary>
public enum PfpFetch
{
    Ok = 0,
    NotFound = 1,
    Failed = 2,
}

public enum PfpHashState
{
    /// <summary>Discord sent a hash.</summary>
    Present = 0,

    /// <summary>Discord confirmed there is no picture: the field is documented as always sent, and it was null.</summary>
    None = 1,

    /// <summary>
    /// No hash, but not a confirmed absence: the field is optional in Discord's docs (<c>banner?</c>, member <c>avatar?</c>)
    /// and Discord.Net 3.20.1 turns "sent as null" and "not sent at all" into the same null.
    /// </summary>
    NotReported = 2,
}

/// <summary>A picture hash from a successful read, with how sure that answer is (<see cref="PfpHashState"/>).</summary>
public readonly record struct PfpHash(PfpHashState State, string? Value)
{
    public static readonly PfpHash None = new(PfpHashState.None, null);
    public static readonly PfpHash NotReported = new(PfpHashState.NotReported, null);

    public static PfpHash Of(string value) => new(PfpHashState.Present, value);

    /// <summary>A field Discord documents as always sent (<c>user.avatar</c>, type <c>?string</c>): null is "no picture".</summary>
    public static PfpHash FromRequiredField(string? value) => value is null ? None : Of(value);

    /// <summary>An optional field (<c>user.banner?</c>, member <c>avatar?</c> / <c>banner?</c>) as Discord.Net exposes it.</summary>
    public static PfpHash FromOptionalField(string? value) => value is null ? NotReported : Of(value);
}

/// <summary>
/// What Get Guild Member returned, SDK-free. <see cref="Avatar"/> comes from the member's nested user object (the avatar
/// field is always present there); <see cref="GuildAvatar"/> / <see cref="GuildBanner"/> are this server's own member
/// profile (optional fields: without a hash they are <see cref="PfpHashState.NotReported"/>). The nested user object's banner
/// is deliberately NOT carried: the global banner is always read with Get User instead
/// (<see cref="IPfpProfileSource.GetUserBannerAsync"/>). <see cref="DefaultAvatarUrl"/> comes from the SDK's own
/// default-avatar helper (new and legacy username systems), never computed here.
/// </summary>
public sealed record PfpMemberProfile(
    ulong UserId,
    string DisplayName,
    PfpHash Avatar,
    PfpHash GuildAvatar,
    PfpHash GuildBanner,
    string DefaultAvatarUrl);

public sealed record PfpMemberLookup(PfpFetch Status, PfpMemberProfile? Profile)
{
    public static PfpMemberLookup Found(PfpMemberProfile profile) => new(PfpFetch.Ok, profile);
    public static readonly PfpMemberLookup NotMember = new(PfpFetch.NotFound, null);
    public static readonly PfpMemberLookup Failed = new(PfpFetch.Failed, null);
}

/// <summary>The user's global banner from Get User (an optional field: without a hash it is <see cref="PfpHashState.NotReported"/>).</summary>
public sealed record PfpBannerLookup(PfpFetch Status, PfpHash Banner)
{
    public static PfpBannerLookup Found(PfpHash banner) => new(PfpFetch.Ok, banner);
    public static readonly PfpBannerLookup Failed = new(PfpFetch.Failed, PfpHash.NotReported);
}

/// <summary>
/// The only Discord reads of the module (implemented over the interaction's Discord.Net REST client in Commands, one per
/// command; faked in tests). One member read per
/// command, plus one user read when the global banner is needed. Nothing is listed, scanned, cached or stored.
/// </summary>
public interface IPfpProfileSource
{
    Task<PfpMemberLookup> GetMemberAsync(ulong guildId, ulong userId, CancellationToken cancellationToken);

    Task<PfpBannerLookup> GetUserBannerAsync(ulong userId, CancellationToken cancellationToken);
}

/// <summary>
/// The three ways /pfp and /banner answer (implemented by the Discord command class; faked in tests). Private replies are only
/// used before the acknowledgement; once the public acknowledgement is sent, every outcome completes that same response.
/// </summary>
public interface IPfpResponder
{
    /// <summary>Only the user who ran the command sees it; used for refusals before any acknowledgement. Never pings.</summary>
    Task ReplyPrivateAsync(string text);

    /// <summary>Public "thinking…" acknowledgement (Discord's 3-second window); the public answer replaces it.</summary>
    Task DeferPublicAsync();

    /// <summary>Completes the public acknowledgement with the card or a short notice. Never pings.</summary>
    Task ReplyPublicAsync(PfpReply reply);
}

/// <summary>What one command asks for, taken from the interaction (ids verified by Discord, never parsed from text).</summary>
public sealed record PfpRequest(ulong GuildId, ulong InvokerId, ulong TargetId, PfpKind Kind, string? Scope, string Language);

/// <summary>The public card: the picture large in the embed's image field and one "Tam boy aç" link button to the same URL.</summary>
public sealed record PfpCard(string Title, string? Description, string ImageUrl, string ButtonLabel, uint Color);

/// <summary>A public answer: the <see cref="Card"/> or a short <see cref="Text"/> notice, never both.</summary>
public sealed record PfpReply(PfpCard? Card, string? Text);
