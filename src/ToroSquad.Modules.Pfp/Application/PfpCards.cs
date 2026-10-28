using System.Globalization;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Modules.Pfp.Application;

/// <summary>
/// Renders a <see cref="PfpResolution"/>: one card with the picture in the embed's image field (shown large — never only as a
/// thumbnail), the member's name and the source that was actually used in the title, and one "Tam boy aç" link button to the
/// same URL; or a short notice for a missing picture, a non-member or an unreadable profile. The name is defused (no mention
/// syntax, no links); replies are sent without allowed mentions anyway.
/// </summary>
public sealed class PfpCards(ILocalizer localizer)
{
    public const uint Color = 0xE8590C; // the TSQ brand colour, as on every other public card
    public const int DisplayNameMax = 64;

    public const string NotMemberKey = "pfp.not_member";
    public const string UnavailableKey = "pfp.unavailable";
    public const string CooldownKey = "pfp.cooldown";
    public const string InvalidScopeKey = "pfp.invalid_scope";
    public const string OpenFullKey = "pfp.open_full";
    public const string DefaultNoteKey = "pfp.default_note";

    public static string SourceKey(PfpSource source) => source switch
    {
        PfpSource.GlobalAvatar => "pfp.source.global_avatar",
        PfpSource.ServerAvatar => "pfp.source.server_avatar",
        PfpSource.DefaultAvatar => "pfp.source.default_avatar",
        PfpSource.GlobalBanner => "pfp.source.global_banner",
        PfpSource.ServerBanner => "pfp.source.server_banner",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    public PfpReply Render(string lang, PfpResolution resolution) => resolution.Kind switch
    {
        PfpResolutionKind.Image when resolution is { Source: { } source, Url: { } url } =>
            new PfpReply(Card(lang, resolution.DisplayName, source, url, resolution.NoteKey), null),
        PfpResolutionKind.Missing when resolution.MissingKey is { } key => Text(lang, key),
        PfpResolutionKind.NotMember => Text(lang, NotMemberKey),
        _ => Text(lang, UnavailableKey),
    };

    /// <summary>"Biraz bekle" with whole seconds, rounded up (never "0 saniye").</summary>
    public string Cooldown(string lang, TimeSpan wait) =>
        localizer.Get(lang, CooldownKey, Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture));

    public string InvalidScope(string lang) => localizer.Get(lang, InvalidScopeKey);

    /// <summary>The description holds only notes: why the automatic order went past a source, and the default-avatar note.</summary>
    private PfpCard Card(string lang, string? displayName, PfpSource source, string url, string? noteKey)
    {
        var name = DiscordText.UntrustedPlain(displayName, DisplayNameMax);
        var title = localizer.Get(lang, "pfp.title", name, localizer.Get(lang, SourceKey(source)));
        string?[] notes = [noteKey is null ? null : localizer.Get(lang, noteKey), source == PfpSource.DefaultAvatar ? localizer.Get(lang, DefaultNoteKey) : null];
        var description = notes.Any(n => n is not null) ? string.Join('\n', notes.OfType<string>()) : null;
        return new PfpCard(title, description, url, localizer.Get(lang, OpenFullKey), Color);
    }

    private PfpReply Text(string lang, string key) => new(null, localizer.Get(lang, key));
}
