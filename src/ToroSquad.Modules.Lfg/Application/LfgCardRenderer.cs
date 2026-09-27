using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// The one LFG card, for every game: "🎮 game", who is looking, players x / y, the creator's own details line, the player
/// list and the state. Players are rendered as user mentions (Discord shows each viewer the current display name) inside the
/// embed, and every message goes out with allowed_mentions = none, so neither the first post nor any edit pings anyone.
/// The countdown is Discord's native relative timestamp — the bot never edits the card just to tick a clock.
/// </summary>
public sealed class LfgCardRenderer(ILocalizer localizer)
{
    /// <summary>Stateless, restart-safe custom ids: the listing id is all a button carries; everything else is re-read.</summary>
    public const string JoinPrefix = "tsq:lfg:join:";
    public const string LeavePrefix = "tsq:lfg:leave:";
    public const string ClosePrefix = "tsq:lfg:close:";

    public const uint OpenColor = 0x57F287;
    public const uint FullColor = 0x5865F2;
    public const uint EndedColor = 0x747F8D;

    public OutgoingMessage Render(LfgListingView listing, string language)
    {
        string L(string key, params object?[] args) => localizer.Get(language, key, args);

        var lines = new List<string>
        {
            L("lfg.card.owner", Mention(listing.Owner)),
            L("lfg.card.count", listing.Players.Count, listing.MaxPlayers),
        };
        if (!string.IsNullOrEmpty(listing.Details))
            lines.Add("📝 " + DiscordText.Untrusted(listing.Details, 600));
        lines.Add("");
        lines.Add(L("lfg.card.players"));
        lines.Add(listing.Players.Count == 0 ? "—" : string.Join(" · ", listing.Players.Select(Mention)));
        lines.Add("");
        var expires = L("lfg.card.expires", DiscordText.Timestamp(listing.ExpiresAt, 'R'));
        lines.Add(listing.Status switch
        {
            LfgStatus.Open => expires,
            LfgStatus.Full => L("lfg.card.full") + "\n" + expires,
            LfgStatus.Expired => L("lfg.card.expired"),
            _ => L("lfg.card.closed"),
        });

        var color = listing.Status switch
        {
            LfgStatus.Open => OpenColor,
            LfgStatus.Full => FullColor,
            _ => EndedColor,
        };
        var embed = new MessageEmbed("🎮 " + DiscordText.UntrustedPlain(listing.GameName, 200), string.Join("\n", lines), null, [], L("lfg.card.footer"), null, color);

        var id = listing.Id.ToString(CultureInfo.InvariantCulture);
        var buttons = new List<MessageButton>
        {
            new(L("lfg.button.join"), JoinPrefix + id, null, Disabled: listing.Status != LfgStatus.Open, Style: MessageButtonStyle.Success),
            new(L("lfg.button.leave"), LeavePrefix + id, null, Disabled: !listing.IsActive),
            new(L("lfg.button.close"), ClosePrefix + id, null, Disabled: !listing.IsActive, Style: MessageButtonStyle.Danger),
        };
        return new OutgoingMessage(null, embed, MentionPolicy.None, buttons);
    }

    private static string Mention(UserId user) => string.Create(CultureInfo.InvariantCulture, $"<@{user.Value}>");
}
