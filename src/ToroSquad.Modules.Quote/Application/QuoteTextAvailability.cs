namespace ToroSquad.Modules.Quote.Application;

/// <summary>Whether the text Discord returned for a message is the message's text.</summary>
public enum QuoteTextAvailability
{
    /// <summary>Discord returned text.</summary>
    Available = 0,

    /// <summary>The message really has no text (attachment/embed/poll only, sticker only, forward, system message).</summary>
    NoTextInMessage = 1,

    /// <summary>
    /// A normal message came back completely empty. Discord cannot store such a message, so its content fields were most
    /// likely withheld (the application has no Message Content access). Not provable from the payload alone.
    /// </summary>
    ProbablyWithheld = 2,
}

/// <summary>
/// What the message object Discord returned says about an empty text. Message Content access covers <c>content</c>,
/// <c>embeds</c>, <c>attachments</c>, <c>components</c> and <c>poll</c> (discord-api-docs, gateway.mdx, "Message Content
/// Intent"); stickers, the message type and references are not covered.
/// </summary>
/// <param name="HasText">Non-empty <c>content</c>.</param>
/// <param name="IsRegularMessage">A member-written message (Default or Reply), not a system message.</param>
/// <param name="HasGatedContent">Attachments, embeds, components or a poll arrived — so content fields reach the bot.</param>
/// <param name="HasStickers">Sticker items (not gated).</param>
/// <param name="IsForward">A forwarded message: its text lives in the forwarded snapshot, not in <c>content</c>.</param>
/// <param name="ContentExempt">The bot's own message or one that mentions the bot: Discord always sends its content.</param>
/// <param name="ApplicationHasContentAccess">The application's Message Content flag (Developer Portal), null if unknown.</param>
public sealed record QuoteMessageShape(
    bool HasText,
    bool IsRegularMessage,
    bool HasGatedContent,
    bool HasStickers,
    bool IsForward,
    bool ContentExempt,
    bool? ApplicationHasContentAccess)
{
    public QuoteTextAvailability Classify()
    {
        if (HasText)
            return QuoteTextAvailability.Available;
        if (ContentExempt || HasGatedContent || IsForward || !IsRegularMessage)
            return QuoteTextAvailability.NoTextInMessage;
        // A sticker alone is a complete message, but a sticker with withheld text looks the same: only the flag decides.
        if (HasStickers && ApplicationHasContentAccess == true)
            return QuoteTextAvailability.NoTextInMessage;
        return QuoteTextAvailability.ProbablyWithheld;
    }
}
