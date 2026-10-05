using System.Globalization;

namespace ToroSquad.Tests.Support;

/// <summary>
/// Synthetic Deadlock posts in the shapes Valve's announcements have on Steam (made-up lines; the titles are the kinds of
/// titles the official feed carries): patch notes in the newer <c>[p]</c> layout and in the older plain-line layout, a
/// named update that links to its own page, and a hero reveal.
/// </summary>
public static class DeadlockNews
{
    public const uint AppId = 1422450;

    public static readonly string[] PatchTag = ["patchnotes"];

    /// <summary>Newer layout: every line its own <c>[p]</c> block, the section brackets escaped and bold.</summary>
    public static string Notes(params (string Section, string[] Lines)[] sections) =>
        string.Concat(sections.Select(s => "[p][b]\\[ " + s.Section + " ][/b][/p][p][/p]" + string.Concat(s.Lines.Select(l => "[p]- " + l + "[/p]")) + "[p][/p]"));

    /// <summary>Older layout: plain lines, the section brackets as they are.</summary>
    public static string PlainNotes(params (string Section, string[] Lines)[] sections) =>
        string.Join("\n\n", sections.Select(s => "[ " + s.Section + " ]\n" + string.Join("\n", s.Lines.Select(l => "- " + l))));

    public static readonly (string Section, string[] Lines)[] SmallPatch =
    [
        ("General", ["Synthetic change one", "Synthetic change two"]),
        ("Heroes", ["Hero A: Synthetic ability damage increased from 10 to 12", "Hero B: Synthetic cooldown reduced from 30s to 28s"]),
    ];

    public static readonly string MinorNotes = Notes(SmallPatch);

    /// <summary>A named update: an image that links to the update's own page, a summary, the link again, a closing line.</summary>
    public static string Named(string page, string summary = "A synthetic summary: a large visual update to the map, new heroes, HUD updates and more.") =>
        $"[url=https://www.playdeadlock.com/{page}][img]{{STEAM_CLAN_LOC_IMAGE}}/1/synthetic.png[/img][/url]\n\n{summary}\n\n" +
        $"[b]Check out everything that's new at: [url=https://www.playdeadlock.com/{page}]https://www.playdeadlock.com/{page}[/url][/b]\n\n" +
        "We hope you will like this update. Thank you all for your support.";

    /// <summary>A hero reveal as the feed carries them: an image, a paragraph about the hero, when the hero can be played.</summary>
    public const string HeroReveal =
        "[img]{STEAM_CLAN_LOC_IMAGE}/1/synthetic.png[/img]\n\nOut of the shadows and into the streets, a synthetic hero is here at last, with a " +
        "[i]Synthetic Ability[/i] and a grin.\n\nThe hero is available to play now! Our next hero will be joining us on Tuesday.";

    /// <summary>
    /// A staged hero rollout that ships with new systems, in the shape of the feed's "Six New Heroes": headed sections whose
    /// text states what changed in the game, and one hero spotlight at the end. No change list, no tag, no "update" title.
    /// </summary>
    public const string SystemsRollout =
        "[img]{STEAM_CLAN_IMAGE}/1/synthetic.png[/img][p]Six synthetic heroes have been spotted on the streets.[/p]" +
        "[p]We will be staggering their release, unlocking one every other day with the first starting today.[/p]" +
        "[h3]The Hideout[/h3][p]Welcome to the Hideout! The Hideout replaces the existing Dashboard UI and is your personal area while waiting for a match.[/p]" +
        "[h3]Map Update[/h3][p]The map has been updated with various visual improvements and lighting changes.[/p]" +
        "[h3]Character Select Screen[/h3][p]The character select screen has received a major visual overhaul.[/p]" +
        "[h3]First Hero: Hero Spotlight[/h3][p]A synthetic hero that delivers quick bursts of damage at range.[/p]";

    /// <summary>Several heroes added at once, in the shape of the feed's "Holliday, Vyper, Calico, and The Magnificent Sinclair".</summary>
    public const string RosterDrop =
        "Today's update adds four new heroes to matchmaking: Hero A, Hero B, Hero C, and Hero D.\n\nSo who are the new faces?\n\n" +
        "[h3]HERO A[/h3]\nArmed with a synthetic revolver, Hero A has come to town.\n\n[h3]HERO B[/h3]\nA synthetic assassin who uses the shadows.\n\nSee you in the city.";

    /// <summary>A hero spotlight with the hero's name as a heading and a link to the update it belongs to (the feed's Celeste / Apollo shape).</summary>
    public static string Spotlight(string hero, string extra = "") =>
        $"Come one, come all! Today, we're introducing...\n\n[h3]{hero}[/h3]\n[img]{{STEAM_CLAN_IMAGE}}/1/synthetic.png[/img]\n\n" +
        $"It's not every day that a synthetic star performs on these streets, with a [i]Synthetic Trick[/i] and a memorable final act.\n\n" +
        $"{hero} is available to play in-game now! Join us on Thursday for the conclusion of our [url=https://www.playdeadlock.com/oldgods]Old Gods, New Blood[/url] update.{extra}";

    public static SteamPost Post(long gid, string title, DateTimeOffset published, string contents, bool tagged = false) =>
        new(gid.ToString(CultureInfo.InvariantCulture), title, published, contents, tagged ? PatchTag : null, AppId: AppId);

    public static SteamPost Minor(long gid, DateTimeOffset published, string? contents = null) =>
        Post(gid, "Minor Update - " + published.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture), published, contents ?? MinorNotes, tagged: true);
}
