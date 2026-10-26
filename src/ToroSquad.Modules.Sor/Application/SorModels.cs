using ToroSquad.Core.Security;
using ToroSquad.Modules.Sor.Domain;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>A member a question is about, as Discord resolved them for this interaction (never from the model).</summary>
public sealed record SorPerson(ulong UserId, string DisplayName, bool IsBot);

/// <summary>
/// One question as the Discord layer received it. Identity, guild, channel, roles and visibility come from the interaction;
/// <see cref="ConversationId"/> only from a button the asker clicked (and is re-checked against the owner).
/// </summary>
public sealed record SorAsk(
    ActorContext Actor,
    ulong InteractionId,
    ulong ChannelId,
    ulong? ParentChannelId,
    string Question,
    bool IsPrivate,
    bool NewConversation,
    long? ConversationId,
    string AskerName,
    SorPerson? About,
    string Language,
    TimeZoneInfo Zone)
{
    public SorConversationKey Key => new(Actor.GuildId.Value, Actor.UserId.Value, ChannelId, IsPrivate);
}

/// <summary>A note as it may be used in one request.</summary>
public sealed record SorNote(long Id, ulong Owner, SorMemoryKind Kind, string Text, SorMemoryScope Scope, ulong? SourceChannel, string Stamp, DateTimeOffset UpdatedAt);

public sealed record SorHistoryTurn(int Seq, string Question, string Answer);

/// <summary>The version of one note a request used.</summary>
public sealed record SorNoteVersion(long Id, string Stamp);

/// <summary>
/// Everything a request's context depended on, recorded before the AI request and checked again before delivery and before
/// anything is saved: the asker's and the mentioned member's memory stamps, the conversation's stamp and every note used.
/// </summary>
public sealed record SorSnapshot(
    string AskerStamp,
    bool AskerMemory,
    ulong? OtherUser,
    string? OtherStamp,
    long ConversationId,
    string ConversationStamp,
    IReadOnlyList<SorNoteVersion> Notes);

/// <summary>The context selected for one request (already filtered by ownership and scope in the queries).</summary>
public sealed record SorContext(
    long ConversationId,
    string ConversationStamp,
    bool PreviousTopicClosed,
    IReadOnlyList<SorHistoryTurn> Turns,
    bool AskerMemory,
    string AskerStamp,
    IReadOnlyList<SorNote> AskerNotes,
    SorPerson? Other,
    string? OtherStamp,
    bool OtherShares,
    IReadOnlyList<SorNote> OtherNotes);

/// <summary>What an answer delivery looks like to the Discord layer.</summary>
/// <param name="Parts">The answer split for Discord (1–3 parts, same visibility).</param>
/// <param name="Notice">A short line above the answer (e.g. the previous topic had ended), or null.</param>
public sealed record SorReply(IReadOnlyList<string> Parts, string QuestionExcerpt, long ConversationId, ulong OwnerUserId, bool IsPrivate, string? Notice);

public enum SorDelivery
{
    Delivered = 0,

    /// <summary>Discord refused it: nothing was shown.</summary>
    Failed = 1,

    /// <summary>The call ended without a confirmation and the check could not find it either.</summary>
    Unknown = 2,
}

/// <summary>How a /sor answers on Discord. Implemented by the interaction; tests use a recording fake.</summary>
public interface ISorResponder
{
    /// <summary>A refusal before anything was acknowledged: always private.</summary>
    Task RefuseAsync(string text);

    /// <summary>Acknowledge within 3 seconds with the FINAL visibility (it can never change afterwards).</summary>
    Task DeferAsync(bool isPrivate);

    /// <summary>
    /// After the acknowledgement, nothing to deliver: a private acknowledgement becomes <paramref name="detail"/>; a channel one
    /// becomes the short, non-personal <paramref name="neutral"/> line and <paramref name="detail"/> follows privately.
    /// </summary>
    Task FailAsync(string neutral, string detail);

    /// <summary>The answer in the acknowledged message (and at most two follow-ups with the same visibility), with its buttons.</summary>
    Task<SorDelivery> DeliverAsync(SorReply reply);

    /// <summary>After saving succeeded: a short line added to the delivered answer (e.g. "🧠 1 note saved").</summary>
    Task AddFooterAsync(SorReply reply, string line);

    /// <summary>A private follow-up (the first-use note, an ambiguous "forget"), optionally with the Hafızam button.</summary>
    Task PrivateNoteAsync(string text, bool memoryButton);
}
