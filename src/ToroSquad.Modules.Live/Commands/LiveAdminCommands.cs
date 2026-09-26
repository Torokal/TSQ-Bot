using Discord;
using Discord.Interactions;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Live.Application;
using DiscordPermission = Discord.GuildPermission;

namespace ToroSquad.Modules.Live.Commands;

/// <summary>
/// /live-admin — Manage Server required: hidden by default_member_permissions AND re-authorized in the service. Works while
/// the module is disabled so the configuration can be checked before activation. There is deliberately no command that
/// sends a live card or pings anyone.
/// </summary>
[ToroModule(LiveModule.ModuleIdValue, AllowWhenDisabled = true)]
[Group("live-admin", "TSQ Live stream announcements (admins)")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class LiveAdminCommands(InteractionServices services, LiveDoctor doctor) : ToroInteractionModule(services)
{
    [SlashCommand("doctor", "Diagnose Twitch/Kick tracking, Discord target and announcements")]
    public async Task DoctorAsync()
    {
        await DeferEphemeralAsync();
        var (auth, checks) = await doctor.RunAsync(Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await ReplyResultAsync(auth);
            return;
        }

        var lang = await LangAsync();
        var lines = checks.Select(c => $"{Icon(c.State)} **{Localizer.Get(lang, c.LabelKey)}** — {Localizer.Get(lang, c.DetailKey, c.Args.ToArray())}");
        await ReplyEmbedAsync(new MessageEmbed(await T("live.doctor.title"), Clip(string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax), null, [], null, null, NeutralColor));
    }

    private static string Icon(LiveCheckState state) => state switch
    {
        LiveCheckState.Ok => "✅",
        LiveCheckState.Warning => "⚠️",
        LiveCheckState.Problem => "❌",
        _ => "ℹ️",
    };

    private static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, max - 2));
        return (cut > 0 ? text[..cut] : text[..(max - 1)]) + "…";
    }
}
