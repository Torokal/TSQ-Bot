using System.Text;
using Discord;
using Discord.Interactions;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Privacy;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;

namespace ToroSquad.Discord.Commands.Core;

/// <summary>
/// /privacy export and /privacy delete. Scope is always: the calling user, in the current guild. Delete requires a
/// preview + explicit confirmation button bound to the same user and guild (expires after 5 minutes, single use).
/// </summary>
[ToroModule("core")]
[Group("privacy", "Export or delete the data ToroSquad Bot stores about you")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class PrivacyCommands(InteractionServices services, PrivacyService privacy) : ToroInteractionModule(services)
{
    public const string ConfirmPrefix = "tsq:privacy:confirm:";
    public const string CancelId = "tsq:privacy:cancel";

    [SlashCommand("export", "Download your own stored data from this server as JSON")]
    public async Task ExportAsync()
    {
        await DeferEphemeralAsync();
        var json = await privacy.ExportAsync(Actor, CancellationToken.None);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await FollowupWithFileAsync(stream, $"torosquad-export-{Actor.GuildId}-{Actor.UserId}.json",
            text: await T("privacy.export_ready"), ephemeral: true,
            allowedMentions: DiscordConversions.ToAllowedMentions(MentionPolicy.None));
    }

    [SlashCommand("delete", "Delete your own stored data from this server (preview first)")]
    public async Task DeleteAsync()
    {
        await DeferEphemeralAsync();
        var (items, confirmationId) = await privacy.PreviewDeleteAsync(Actor, CancellationToken.None);
        var language = await LangAsync();
        var lines = items.Count == 0
            ? [await T("privacy.nothing_stored")]
            : items.Select(i => $"• {Localizer.Get(language, i.LabelKey, i.Count)}").ToList();
        var components = new ComponentBuilder()
            .WithButton(await T("privacy.confirm_button"), ConfirmPrefix + confirmationId, ButtonStyle.Danger)
            .WithButton(await T("privacy.cancel_button"), CancelId, ButtonStyle.Secondary)
            .Build();
        await ReplyEmbedAsync(new MessageEmbed(await T("privacy.delete_title"),
            string.Join("\n", lines) + "\n\n" + await T("privacy.delete_warning"), null, [], null, null, WarningColor), components);
    }

    [ComponentInteraction(ConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmAsync(string confirmationId)
    {
        await DeferEphemeralAsync();
        // The confirmation is bound to guild + user in the store; another user's click cannot consume it.
        var (result, reports) = await privacy.ConfirmDeleteAsync(Actor, confirmationId, CancellationToken.None);
        await ReplyResultAsync(result);
        var warnings = reports.SelectMany(r => r.Warnings).ToList();
        if (warnings.Count > 0)
            await ReplyTextAsync("privacy.delete_warnings", string.Join("\n", warnings.Take(10)));
    }

    [ComponentInteraction(CancelId, ignoreGroupNames: true)]
    public Task CancelAsync() => ReplyTextAsync("privacy.cancelled");
}
