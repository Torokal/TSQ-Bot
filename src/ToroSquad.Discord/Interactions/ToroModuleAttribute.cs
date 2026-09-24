using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;

namespace ToroSquad.Discord.Interactions;

/// <summary>
/// Marks which TSQ Bot module owns an interaction class AND acts as a precondition for every slash command,
/// button, select menu and modal in it: guild context is required, and a non-core module must be enabled in that
/// guild. Stale components from a disabled module therefore stop working too.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ToroModuleAttribute(string moduleId) : PreconditionAttribute
{
    public const string GuildOnlyError = "error.guild_only";
    public const string ModuleDisabledError = "error.module_disabled";

    public string ModuleId { get; } = moduleId;

    /// <summary>Only for setup flows that must work before the module is enabled (still guild-only, still admin-checked).</summary>
    public bool AllowWhenDisabled { get; set; }

    public override async Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        // Backend guild validation — never trust that Discord hid the command in DMs.
        if (context.Guild is null || context.User is not IGuildUser)
            return PreconditionResult.FromError(GuildOnlyError);

        var id = new ModuleId(ModuleId);
        if (id == Core.Modules.ModuleId.Core || AllowWhenDisabled)
            return PreconditionResult.FromSuccess();

        var gate = services.GetRequiredService<IModuleGate>();
        return await gate.IsEnabledAsync(new GuildId(context.Guild.Id), id, CancellationToken.None)
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(ModuleDisabledError);
    }
}
