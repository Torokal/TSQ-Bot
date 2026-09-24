namespace ToroSquad.Core.Security;

/// <summary>
/// Discord permission bits (subset we reason about), mirrored here so business rules do not depend on the Discord SDK.
/// Values from https://docs.discord.com/developers/topics/permissions (verified 2026-09-24, see docs/research).
/// </summary>
[Flags]
#pragma warning disable CA1028 // Discord permission bitfield is a 64-bit unsigned value by definition.
public enum GuildPermission : ulong
#pragma warning restore CA1028
{
    None = 0,
    CreateInstantInvite = 1UL << 0,
    KickMembers = 1UL << 1,
    BanMembers = 1UL << 2,
    Administrator = 1UL << 3,
    ManageChannels = 1UL << 4,
    ManageGuild = 1UL << 5,
    AddReactions = 1UL << 6,
    ViewAuditLog = 1UL << 7,
    PrioritySpeaker = 1UL << 8,
    Stream = 1UL << 9,
    ViewChannel = 1UL << 10,
    SendMessages = 1UL << 11,
    SendTtsMessages = 1UL << 12,
    ManageMessages = 1UL << 13,
    EmbedLinks = 1UL << 14,
    AttachFiles = 1UL << 15,
    ReadMessageHistory = 1UL << 16,
    MentionEveryone = 1UL << 17,
    UseExternalEmojis = 1UL << 18,
    ViewGuildInsights = 1UL << 19,
    Connect = 1UL << 20,
    Speak = 1UL << 21,
    MuteMembers = 1UL << 22,
    DeafenMembers = 1UL << 23,
    MoveMembers = 1UL << 24,
    UseVoiceActivity = 1UL << 25,
    ChangeNickname = 1UL << 26,
    ManageNicknames = 1UL << 27,
    ManageRoles = 1UL << 28,
    ManageWebhooks = 1UL << 29,
    ManageGuildExpressions = 1UL << 30,
    UseApplicationCommands = 1UL << 31,
    RequestToSpeak = 1UL << 32,
    ManageEvents = 1UL << 33,
    ManageThreads = 1UL << 34,
    CreatePublicThreads = 1UL << 35,
    CreatePrivateThreads = 1UL << 36,
    UseExternalStickers = 1UL << 37,
    SendMessagesInThreads = 1UL << 38,
    UseEmbeddedActivities = 1UL << 39,
    ModerateMembers = 1UL << 40,
}

public static class GuildPermissionExtensions
{
    /// <summary>Administrator implicitly grants every permission (Discord semantics).</summary>
    public static bool Grants(this GuildPermission granted, GuildPermission required) =>
        (granted & GuildPermission.Administrator) != 0 || (granted & required) == required;

    /// <summary>Discord's default_member_permissions wire format is a decimal string.</summary>
    public static string ToDiscordBitfieldString(this GuildPermission permission) =>
        ((ulong)permission).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
