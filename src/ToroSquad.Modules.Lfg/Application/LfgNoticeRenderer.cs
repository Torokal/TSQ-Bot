using System.Globalization;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// The event notices (30-minute reminder, start). The ONLY LFG messages that ping — and the only place in the bot that uses
/// <see cref="MentionPolicy.ExplicitUsers"/> (architecture test): exactly the listing's Joined player ids from the database
/// are allowed to ping; nothing is parsed from the text, no role, @everyone or @here. The game name is untrusted text and is
/// defused like on the card. With a voice channel the notice carries the same voice button as the card.
/// </summary>
public sealed class LfgNoticeRenderer(ILocalizer localizer)
{
    public OutgoingMessage Render(LfgListingView listing, string kind, string language)
    {
        string L(string key, params object?[] args) => localizer.Get(language, key, args);

        var eventAt = listing.EventAt ?? throw new ArgumentException("Only scheduled listings have event notices.", nameof(listing));
        var game = DiscordText.Untrusted(listing.GameName, 120);
        var lines = new List<string>
        {
            kind == LfgNoticePlanner.KindReminder
                ? L("lfg.notice.reminder", game, DiscordText.Timestamp(eventAt, 'R'))
                : L("lfg.notice.start", game),
            string.Join(" ", listing.Players.Select(LfgCardRenderer.Mention)),
        };
        if (kind == LfgNoticePlanner.KindReminder)
            lines.Add(L("lfg.notice.details", listing.Players.Count, listing.MaxPlayers, DiscordText.Timestamp(eventAt, 't')));
        IReadOnlyList<MessageButton>? buttons = null;
        if (listing.VoiceChannel is { } voice)
        {
            lines.Add(L("lfg.card.voice", LfgCardRenderer.ChannelMention(voice)));
            buttons = [new MessageButton(L("lfg.button.voice_join"), LfgCardRenderer.VoicePrefix + listing.Id.ToString(CultureInfo.InvariantCulture), null)];
        }

        return new OutgoingMessage(string.Join("\n", lines), null, MentionPolicy.ExplicitUsers(listing.Players), buttons);
    }
}
