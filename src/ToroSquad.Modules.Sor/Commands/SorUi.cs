using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Sor.Application;

namespace ToroSquad.Modules.Sor.Commands;

/// <summary>
/// Custom ids and builders of /sor's components. A custom id only points (conversation id, its owner, its visibility); every
/// click is checked again on the server — the clicker against the conversation's owner in the database, never against the id
/// alone. The Hafızam button carries nothing: it always opens the CLICKER's own memory.
/// </summary>
public static class SorUi
{
    public const string Title = "TSQ Sor";
    public const uint Color = ToroInteractionModule.BrandColor;

    public const string ContinuePrefix = "tsq:sor:c:";
    public const string NewPrefix = "tsq:sor:n:";
    public const string MemoryId = "tsq:sor:mem:open";
    public const string ContinueModalPrefix = "tsq:sor:cm:";
    public const string NewModalPrefix = "tsq:sor:nm:";
    public const string QuestionField = "soru";

    /// <summary>Discord: modal titles and labels at most 45 characters.</summary>
    public const int MaxTitleLength = 45;

    /// <summary>"&lt;conversation&gt;:&lt;owner&gt;:&lt;p|c&gt;" — pointers only, re-checked on the server.</summary>
    public static string Args(long conversation, ulong owner, bool isPrivate) =>
        conversation.ToString(CultureInfo.InvariantCulture) + ":" + owner.ToString(CultureInfo.InvariantCulture) + ":" + (isPrivate ? "p" : "c");

    public static bool TryParse(string args, out long conversation, out ulong owner, out bool isPrivate)
    {
        conversation = 0;
        owner = 0;
        isPrivate = false;
        var parts = args.Split(':');
        if (parts.Length != 3 || parts[2] is not ("p" or "c"))
            return false;
        isPrivate = parts[2] == "p";
        return long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out conversation) &&
               ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out owner);
    }

    public static MessageComponent Buttons(SorReply reply, Func<string, string> L) => new ComponentBuilder()
        .WithButton(L("sor.button.continue"), ContinuePrefix + Args(reply.ConversationId, reply.OwnerUserId, reply.IsPrivate), ButtonStyle.Primary, new Emoji("💬"))
        .WithButton(L("sor.button.new"), NewPrefix + Args(reply.ConversationId, reply.OwnerUserId, reply.IsPrivate), ButtonStyle.Secondary, new Emoji("✨"))
        .WithButton(L("sor.button.memory"), MemoryId, ButtonStyle.Secondary, new Emoji("🧠"))
        .Build();

    public static MessageComponent MemoryButton(Func<string, string> L) =>
        new ComponentBuilder().WithButton(L("sor.button.memory"), MemoryId, ButtonStyle.Secondary, new Emoji("🧠")).Build();

    /// <summary>The answer card: a small consistent title, the answer, and a short excerpt of the question (never the full question).</summary>
    public static Embed Card(SorReply reply, int part, string? extraFooter)
    {
        var builder = new EmbedBuilder().WithColor(Color);
        var text = reply.Parts[part];
        if (part == 0)
        {
            builder.WithTitle(Title);
            if (reply.Notice is { } notice)
                text = "-# " + notice + "\n" + text;
        }

        builder.WithDescription(text);
        if (part == 0)
            builder.WithFooter(Cut("❓ " + reply.QuestionExcerpt + (extraFooter is null ? "" : " · " + extraFooter), 2048));

        return builder.Build();
    }

    public static Modal QuestionModal(string customId, string title, string label, string placeholder, int maxLength) => new ModalBuilder()
        .WithTitle(Cut(title, MaxTitleLength))
        .WithCustomId(customId)
        .AddLabel(Cut(label, MaxTitleLength), new TextInputBuilder()
            .WithCustomId(QuestionField)
            .WithStyle(TextInputStyle.Paragraph)
            .WithPlaceholder(Cut(placeholder, 100))
            .WithMinLength(1)
            .WithMaxLength(maxLength)
            .WithRequired(true))
        .Build();

    public static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>The follow-up / new-question form, bound by field custom id.</summary>
public sealed class SorQuestionModal : IModal
{
    public string Title => SorUi.Title;

    [ModalTextInput(SorUi.QuestionField, TextInputStyle.Paragraph)]
    public string? Question { get; set; }
}
