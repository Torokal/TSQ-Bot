using System.Globalization;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// The channel, role and module gates every step goes through (commands, autocomplete, form submits, previews, confirm
/// clicks and card selects alike). Channels match by EXACT id: a thread of the allowed channel, another channel, another
/// guild or a DM never pass, and there is no exception for administrators. A refusal changes nothing: no wallet, no coin,
/// no row, no public message. Mentions in the refusal text are clickable and never ping (allowed_mentions none).
/// </summary>
public sealed class PredictionGuards(IOptions<PredictionsOptions> options, IModuleGate gate)
{
    public ChannelId PredictionsChannel => new(options.Value.ChannelId);
    public ChannelId CommandsChannel => new(options.Value.CommandsChannelId);
    public RoleId CreatorRole => new(options.Value.CreatorRoleId);

    public OperationResult? InPredictionsChannel(ChannelId here) => here == PredictionsChannel ? null : WrongChannel(PredictionsChannel);

    public OperationResult? InCommandsChannel(ChannelId here) => here == CommandsChannel ? null : WrongChannel(CommandsChannel);

    public async Task<OperationResult?> EnabledAsync(GuildId guild, CancellationToken ct) =>
        await gate.IsEnabledAsync(guild, PredictionsModule.ModuleIdTyped, ct) ? null : OperationResult.Fail(OperationError.ModuleDisabled, "error.module_disabled");

    /// <summary>Creating: the predictions channel, then the creator role, then the module.</summary>
    public async Task<OperationResult?> CreatorAsync(ActorContext actor, ChannelId here, CancellationToken ct) =>
        InPredictionsChannel(here)
        ?? (PredictionAccess.CanCreate(actor, CreatorRole) ? null : MissingRole())
        ?? await EnabledAsync(actor.GuildId, ct);

    public static OperationResult WrongChannel(ChannelId expected) =>
        OperationResult.Fail(OperationError.Forbidden, "predictions.wrong_channel", ChannelMention(expected));

    public OperationResult MissingRole() =>
        OperationResult.Fail(OperationError.Forbidden, "predictions.missing_role", RoleMention(CreatorRole));

    public static OperationResult NotManager() => OperationResult.Fail(OperationError.Forbidden, "predictions.not_manager");

    public static string ChannelMention(ChannelId channel) => string.Create(CultureInfo.InvariantCulture, $"<#{channel.Value}>");

    public static string RoleMention(RoleId role) => string.Create(CultureInfo.InvariantCulture, $"<@&{role.Value}>");
}
