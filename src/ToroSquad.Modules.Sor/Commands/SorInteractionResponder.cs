using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Sor.Application;

namespace ToroSquad.Modules.Sor.Commands;

/// <summary>
/// <see cref="ISorResponder"/> on one Discord interaction (the slash command or a submitted form). Works on the
/// <see cref="IDiscordInteraction"/> interface only, so its exact calls and payloads are testable offline. Rules:
/// <list type="bullet">
/// <item>A refusal before the acknowledgement is an ephemeral first response.</item>
/// <item>The acknowledgement (<see cref="DeferAsync"/>) fixes the visibility; nothing later can change it: the answer edits
/// the original response, continuation parts and notes are follow-ups with the same (or private) visibility.</item>
/// <item>A failure after a channel acknowledgement ends the original response with a short non-personal line and sends the
/// detail privately; after a private one the detail replaces the original response. The loading state never stays open.</item>
/// <item>Every send and edit carries <see cref="MentionPolicy.None"/>.</item>
/// <item>An unconfirmed delivery is looked up once (<see cref="IDiscordInteraction.GetOriginalResponseAsync"/>) — a request
/// already sent to Discord cannot be taken back, and nothing is generated again.</item>
/// </list>
/// </summary>
public sealed class SorInteractionResponder(IDiscordInteraction interaction, Func<string, string> localize, ILogger logger) : ISorResponder
{
    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    private bool _private = true;

    public async Task RefuseAsync(string text)
    {
        if (interaction.HasResponded)
            await interaction.FollowupAsync(text, ephemeral: true, allowedMentions: NoPings);
        else
            await interaction.RespondAsync(text, ephemeral: true, allowedMentions: NoPings);
    }

    public Task DeferAsync(bool isPrivate)
    {
        _private = isPrivate;
        return interaction.HasResponded ? Task.CompletedTask : interaction.DeferAsync(ephemeral: isPrivate);
    }

    public async Task FailAsync(string neutral, string detail)
    {
        try
        {
            await interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = _private ? detail : neutral;
                m.Embeds = Array.Empty<Embed>();
                m.Components = new ComponentBuilder().Build();
                m.AllowedMentions = NoPings;
            });
            if (!_private)
                await interaction.FollowupAsync(detail, ephemeral: true, allowedMentions: NoPings);
        }
        catch (HttpException ex)
        {
            logger.LogWarning(ex, "Sor: the failure message could not be shown (Discord {Status})", ex.HttpCode);
        }
    }

    public async Task<SorDelivery> DeliverAsync(SorReply reply)
    {
        try
        {
            await interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = "";
                m.Embeds = new[] { SorUi.Card(reply, 0, null) };
                m.Components = SorUi.Buttons(reply, localize);
                m.AllowedMentions = NoPings;
            });
        }
        catch (HttpException ex)
        {
            logger.LogWarning(ex, "Sor: delivery refused (Discord {Status})", ex.HttpCode);
            return SorDelivery.Failed;
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException)
        {
            return await ConfirmDeliveredAsync() ? SorDelivery.Delivered : SorDelivery.Unknown;
        }

        for (var part = 1; part < reply.Parts.Count; part++)
        {
            try
            {
                await interaction.FollowupAsync(embed: SorUi.Card(reply, part, null), ephemeral: reply.IsPrivate, allowedMentions: NoPings);
            }
            catch (Exception ex) when (ex is HttpException or TimeoutException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Sor: continuation {Part} was not delivered", part);
                break;
            }
        }

        return SorDelivery.Delivered;
    }

    private async Task<bool> ConfirmDeliveredAsync()
    {
        try
        {
            var message = await interaction.GetOriginalResponseAsync();
            return message?.Embeds.Any(e => e.Title == SorUi.Title) == true;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Sor: delivery could not be confirmed");
            return false;
        }
    }

    public async Task AddFooterAsync(SorReply reply, string line)
    {
        try
        {
            await interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Embeds = new[] { SorUi.Card(reply, 0, line) };
                m.AllowedMentions = NoPings;
            });
        }
        catch (HttpException ex)
        {
            logger.LogDebug(ex, "Sor: the saved-note line could not be added");
        }
    }

    public async Task PrivateNoteAsync(string text, bool memoryButton) =>
        await interaction.FollowupAsync(text, components: memoryButton ? SorUi.MemoryButton(localize) : null, ephemeral: true, allowedMentions: NoPings);
}
