using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Live.Application;

namespace ToroSquad.Modules.Live.Commands;

/// <summary>
/// <c>/tsq-admin modul:live</c> — Manage Server: the shared command is hidden by default_member_permissions AND the doctor
/// re-authorizes. Works while the module is disabled so the configuration can be checked before activation. There is
/// deliberately no operation that sends a live card or pings anyone.
/// </summary>
public sealed class LiveAdminOperations(LiveDoctor doctor)
{
    public static readonly AdminModule Definition = AdminModule.For<LiveAdminOperations>(LiveModule.AdminId, LiveModule.ModuleIdTyped)
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Build();

    public async Task DoctorAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, checks) = await doctor.RunAsync(call.Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var lines = checks.Select(c => $"{Icon(c.State)} **{call.T(c.LabelKey)}** — {call.T(c.DetailKey, c.Args.ToArray())}");
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("live.doctor.title"), Clip(string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax), null, [], null, null,
            ToroInteractionModule.NeutralColor));
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
