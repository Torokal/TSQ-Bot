using System.Globalization;
using ToroSquad.Core;

namespace ToroSquad.Modules.Quote.Application;

/// <summary>
/// What the user typed into /quote message: a bare message id, or a Discord message link that also names the guild and
/// the channel. Parsing never touches Discord; the ids are only claims until <see cref="QuoteMessageResolver"/> verifies them.
/// </summary>
public abstract record QuoteReference
{
    private QuoteReference()
    {
    }

    /// <summary>A bare message id: the channel comes from the channel option or the channel the command ran in.</summary>
    public sealed record MessageOnly(MessageId Message) : QuoteReference;

    /// <summary>https://discord.com/channels/{guild}/{channel}/{message}.</summary>
    public sealed record Link(GuildId Guild, ChannelId Channel, MessageId Message) : QuoteReference;

    /// <summary>A link to a direct message (<c>/channels/@me/…</c>): never quotable from a server.</summary>
    public sealed record DirectMessageLink : QuoteReference;

    /// <summary>Neither a message id nor a Discord message link.</summary>
    public sealed record Invalid : QuoteReference;

    /// <summary>Longest input worth parsing: a link on the longest Discord host with three 20-digit ids is ~110 characters.</summary>
    public const int MaxInputLength = 200;

    /// <summary>Discord web hosts whose /channels links point to a message. Anything else (lookalikes, other ports, userinfo) is refused.</summary>
    public static readonly IReadOnlySet<string> DiscordHosts = new HashSet<string>(StringComparer.Ordinal)
    {
        "discord.com", "www.discord.com", "ptb.discord.com", "canary.discord.com",
        "discordapp.com", "www.discordapp.com", "ptb.discordapp.com", "canary.discordapp.com",
    };

    public static QuoteReference Parse(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length is 0 or > MaxInputLength)
            return new Invalid();
        // Links pasted with Discord's "suppress embed" brackets: <https://discord.com/channels/…>
        if (text.Length > 2 && text[0] == '<' && text[^1] == '>')
            text = text[1..^1].Trim();

        if (TryParseSnowflake(text, out var id))
            return new MessageOnly(new MessageId(id));
        return ParseLink(text);
    }

    private static QuoteReference ParseLink(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !DiscordHosts.Contains(uri.IdnHost.ToLowerInvariant()))
            return new Invalid();

        // Query strings and fragments are ignored; the path must be exactly channels/{guild}/{channel}/{message}.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 || segments[0] != "channels")
            return new Invalid();
        if (!TryParseSnowflake(segments[2], out var channel) || !TryParseSnowflake(segments[3], out var message))
            return new Invalid();
        if (segments[1] == "@me")
            return new DirectMessageLink();
        return TryParseSnowflake(segments[1], out var guild)
            ? new Link(new GuildId(guild), new ChannelId(channel), new MessageId(message))
            : new Invalid();
    }

    /// <summary>
    /// A Discord snowflake as ASCII digits only: 17-20 digits (every id since 2015; the 64-bit range ends at 20), non-zero.
    /// Signs, spaces, separators and non-ASCII digits are refused.
    /// </summary>
    public static bool TryParseSnowflake(string text, out ulong id)
    {
        id = 0;
        if (text.Length is < 17 or > 20 || !text.All(char.IsAsciiDigit))
            return false;
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }
}
