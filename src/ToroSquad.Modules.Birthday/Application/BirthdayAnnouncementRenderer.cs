using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>
/// The day's announcement: one plain message for everyone celebrating that day (no embed, no spam). It pings exactly the
/// celebrants named in it — <see cref="MentionPolicy.ExplicitUsers"/> with the same ids the text mentions (from the
/// database, never parsed from text); no role, no @everyone/@here (architecture test: the only user-ping producer besides
/// the TSQ LFG notices). The wording needs no Turkish suffix after a name, so it reads right for any nickname.
/// </summary>
public sealed class BirthdayAnnouncementRenderer(ILocalizer localizer)
{
    /// <summary>More celebrants than this in one day are summarized as "and N more" (Discord's 2000-character limit).</summary>
    public const int MaxNamed = 60;

    public OutgoingMessage Render(IReadOnlyList<UserId> users, string language)
    {
        if (users.Count == 0)
            throw new ArgumentException("An announcement needs at least one member.", nameof(users));

        string L(string key, params object?[] args) => localizer.Get(language, key, args);

        var celebrants = users.Distinct().ToList();
        var named = celebrants.Take(MaxNamed).ToList();
        var names = named.Select(Mention).ToList();
        if (celebrants.Count > MaxNamed)
            names.Add(L("birthday.announce.others", celebrants.Count - MaxNamed));
        var list = names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + L("birthday.announce.and") + names[^1];
        var text = L(celebrants.Count == 1 ? "birthday.announce.one" : "birthday.announce.many", list);
        // Only the members mentioned in the text may ping — the same list the text was built from.
        return new OutgoingMessage(text, null, MentionPolicy.ExplicitUsers(named));
    }

    public static string Mention(UserId user) => "<@" + user.Value.ToString(CultureInfo.InvariantCulture) + ">";
}
