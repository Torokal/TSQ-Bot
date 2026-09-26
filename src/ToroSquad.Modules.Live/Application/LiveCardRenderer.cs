using System.Globalization;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Modules.Live.Application;

/// <summary>
/// TSQ Live cards in the compact TSQ style. Rules (tested): rendering is a pure function of persisted state (an unchanged
/// state never causes an edit); provider text (title, category) is untrusted — mentions, markdown and links are defused;
/// channel links and watch buttons come from configuration, never from provider data; only live platforms get a button;
/// the avatar is optional and only from an allow-listed provider image host (<see cref="ThumbnailPolicy"/>). Only the
/// first announcement carries "@everyone" (text AND allowed_mentions); a replacement card carries neither.
/// </summary>
public sealed class LiveCardRenderer(ILocalizer localizer)
{
    public const uint LiveColor = 0xE91916;
    public const uint EndedColor = 0x747F8D;

    /// <summary>Profile picture hosts of Twitch (static-cdn.jtvnw.net) and Kick (kick.com subdomains).</summary>
    public static readonly IReadOnlyCollection<string> AvatarHosts = ["static-cdn.jtvnw.net", "kick.com"];

    public OutgoingMessage Live(TrackedCreator creator, CreatorState state, IReadOnlyList<PlatformState> platforms, string language, bool withEveryone)
    {
        var live = LivePlatforms.All.Where(p => platforms.Any(x => x.Platform == p && x.Status == PlatformStatus.Live) && creator.Channel(p) is not null).ToList();
        var main = MainPlatform(platforms, live);
        var name = Name(creator);
        var lines = new List<string> { L(language, "live.card.live_on", string.Join(" + ", live.Select(p => p.Name()))) };
        if (main?.Category is { } category)
            lines.Add(L(language, "live.card.category", DiscordText.Untrusted(category, 100)));
        if (state.SessionStartedAt is { } started)
            lines.Add(L(language, "live.card.started", DiscordText.Timestamp(started, 'R')));

        var buttons = live.Select(p => new MessageButton(L(language, p == LivePlatform.Twitch ? "live.card.watch_twitch" : "live.card.watch_kick"), null, creator.Channel(p)!.Url)).ToList();
        var embed = new MessageEmbed(
            Title(main, name, language),
            string.Join("\n", lines),
            live.Count > 0 ? creator.Channel(live[0])!.Url : null,
            [],
            L(language, "live.card.footer", string.Join(", ", live.Select(p => p.Name()))),
            state.SessionStartedAt,
            LiveColor,
            Avatar(platforms, live));
        var content = L(language, withEveryone ? "live.card.content_everyone" : "live.card.content", name);
        return new OutgoingMessage(content, embed, withEveryone ? MentionPolicy.EveryoneOnly : MentionPolicy.None, buttons);
    }

    /// <summary>The same message after the session ended: no ping, no buttons, how long it lasted and where.</summary>
    public OutgoingMessage Ended(TrackedCreator creator, CreatorState state, IReadOnlyList<PlatformState> platforms, string language)
    {
        var used = LivePlatforms.All.Where(p => state.HasPlatformInSession(p) && creator.Channel(p) is not null).ToList();
        var main = MainPlatform(platforms, used);
        var name = Name(creator);
        var lines = new List<string>();
        if (state.SessionEndedAt is { } ended)
        {
            lines.Add(L(language, "live.card.ended", DiscordText.Timestamp(ended, 'R')));
            if (state.SessionStartedAt is { } started && ended > started)
                lines.Add(L(language, "live.card.duration", Duration(ended - started, language)));
        }

        if (used.Count > 0)
            lines.Add(L(language, "live.card.platforms", string.Join(" + ", used.Select(p => p.Name()))));
        var embed = new MessageEmbed(
            Title(main, name, language),
            string.Join("\n", lines),
            used.Count > 0 ? creator.Channel(used[0])!.Url : null,
            [],
            L(language, "live.card.footer", string.Join(", ", used.Select(p => p.Name()))),
            state.SessionStartedAt,
            EndedColor,
            Avatar(platforms, used));
        return new OutgoingMessage(L(language, "live.card.content_ended", name), embed, MentionPolicy.None, []);
    }

    /// <summary>
    /// The platform whose title the card shows: among the given (live / used) platforms the most recent title change wins
    /// (ties: Twitch first); without any title there, any platform's title.
    /// </summary>
    public static PlatformState? MainPlatform(IReadOnlyList<PlatformState> platforms, IReadOnlyList<LivePlatform> preferred)
    {
        static IEnumerable<PlatformState> Newest(IEnumerable<PlatformState> list) =>
            list.Where(p => p.Title is not null).OrderByDescending(p => p.TitleChangedAt ?? DateTimeOffset.MinValue).ThenBy(p => p.Platform);
        return Newest(platforms.Where(p => preferred.Contains(p.Platform))).FirstOrDefault()
               ?? Newest(platforms).FirstOrDefault()
               ?? platforms.FirstOrDefault(p => preferred.Contains(p.Platform));
    }

    private string Title(PlatformState? main, string name, string language) =>
        main?.Title is { } title ? DiscordText.UntrustedPlain(title, DiscordLimits.EmbedTitleMax) : L(language, "live.card.no_title", name);

    private static string? Avatar(IReadOnlyList<PlatformState> platforms, IReadOnlyList<LivePlatform> preferred) =>
        LivePlatforms.All.Where(preferred.Contains)
            .Concat(LivePlatforms.All)
            .Select(p => platforms.FirstOrDefault(x => x.Platform == p)?.AvatarUrl)
            .Select(url => ThumbnailPolicy.Check(url, AvatarHosts, out _))
            .FirstOrDefault(url => url is not null);

    /// <summary>Display names are configuration, still rendered safely (markdown/mentions defused).</summary>
    private static string Name(TrackedCreator creator) => DiscordText.Untrusted(creator.DisplayName, 40);

    private string Duration(TimeSpan duration, string language)
    {
        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return hours > 0
            ? L(language, "live.duration.hm", hours.ToString(CultureInfo.InvariantCulture), minutes.ToString(CultureInfo.InvariantCulture))
            : L(language, "live.duration.m", Math.Max(1, minutes).ToString(CultureInfo.InvariantCulture));
    }

    private string L(string language, string key, params object?[] args) => localizer.Get(language, key, args);
}
