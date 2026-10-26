using System.Globalization;
using System.Text;
using Discord;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Sor.Application;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Sor.Commands;

/// <summary>
/// <c>/tsq-admin modul:sor</c> — Manage Server required: the shared command is hidden by default_member_permissions AND
/// re-authorized in <see cref="SorConfigService"/>. Works while the module is disabled so access and limits can be set before
/// /modules enable sor. status, limits and context (forms), allow/remove channel and role, usage totals, and a doctor that
/// never makes an AI request. Nothing here reads a member's conversations or notes, and the model cannot be changed here.
/// </summary>
public sealed class SorAdminOperations(SorConfigService config) : IAdminFormHandler
{
    private const string LimitsAction = "limits";
    private const string ContextAction = "context";

    public static readonly AdminModule Definition = AdminModule.For<SorAdminOperations>(SorModule.AdminId, SorModule.ModuleIdTyped)
        .Op("status", (h, c) => h.StatusAsync(c))
        .Op("limits", (h, c) => h.LimitsAsync(c))
        .Op("context", (h, c) => h.ContextAsync(c))
        .Op("allow-channel", (h, c) => h.ChannelAsync(c, allow: true), AdminFields.Channel)
        .Op("remove-channel", (h, c) => h.ChannelAsync(c, allow: false), AdminFields.Channel)
        .Op("allow-role", (h, c) => h.RoleAsync(c, allow: true), AdminFields.Role)
        .Op("remove-role", (h, c) => h.RoleAsync(c, allow: false), AdminFields.Role)
        .Op("usage", (h, c) => h.UsageAsync(c))
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Build();

    public async Task StatusAsync(AdminCall call)
    {
        await call.DeferAsync();
        if (await config.GetAsync(call.Actor, CancellationToken.None) is not { } settings)
        {
            await call.ReplyResultAsync(OperationResult.Forbidden(Authorize.Require(call.Actor, call.Actor.GuildId, Authorize.ServerSettings)));
            return;
        }

        var options = config.Options;
        var limits = settings.Limits;
        var recent = await config.UsageAsync(call.Actor, 1, CancellationToken.None);
        var today = recent.Count > 0 ? recent[0] : null;
        var fields = new List<EmbedField>
        {
            new(call.T("admin.sor.status.provider"), call.T(config.IsConfigured ? "admin.sor.status.key_set" : "admin.sor.status.key_missing", options.Model), false),
            new(call.T("admin.sor.status.channels"), settings.AllowedChannels.Count == 0 ? call.T("admin.sor.status.everywhere") : string.Join(", ", settings.AllowedChannels.Select(AdminForms.Mention)), false),
            new(call.T("admin.sor.status.roles"), settings.AllowedRoles.Count == 0 ? call.T("admin.sor.status.everyone") : string.Join(", ", settings.AllowedRoles.Select(r => "<@&" + r.ToString(CultureInfo.InvariantCulture) + ">")), false),
            new(call.T("admin.sor.status.limits"), call.T("admin.sor.status.limits_value", limits.UserCooldownSeconds, limits.UserDailyLimit, limits.GuildDailyLimit), false),
            new(call.T("admin.sor.status.context"), call.T("admin.sor.status.context_value", limits.MaxContextTurns, limits.IdleMinutes, limits.HistoryHours,
                options.MaxMemoryItems, options.MaxMemoryItemLength, options.MaxMemoryItemsPerRequest), false),
            new(call.T("admin.sor.status.today"), today is null ? "0" : call.T("admin.sor.status.today_value", today.Requests, today.Completed,
                today.ProviderFailures + today.Timeouts + today.ParseFailures + today.DeliveryFailures), false),
        };
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("admin.sor.status.title"), call.T("admin.sor.status.note"), null, fields, null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task LimitsAsync(AdminCall call)
    {
        if (await config.GetAsync(call.Actor, CancellationToken.None) is not { Limits: var limits })
        {
            await call.ReplyResultAsync(OperationResult.Forbidden(Authorize.Require(call.Actor, call.Actor.GuildId, Authorize.ServerSettings)));
            return;
        }

        var draft = call.OpenDraft(new object());
        await call.Respond.ModalAsync(new ModalBuilder().WithTitle(AdminForms.Cut(call.T("admin.sor.limits.title"), 45)).WithCustomId(AdminCall.CustomId(draft, LimitsAction))
            .AddLabel(AdminForms.Cut(call.T("admin.sor.limits.cooldown"), 45), Number("cooldown", limits.UserCooldownSeconds))
            .AddLabel(AdminForms.Cut(call.T("admin.sor.limits.user_daily"), 45), Number("user", limits.UserDailyLimit))
            .AddLabel(AdminForms.Cut(call.T("admin.sor.limits.guild_daily"), 45), Number("guild", limits.GuildDailyLimit))
            .Build());
    }

    public async Task ContextAsync(AdminCall call)
    {
        if (await config.GetAsync(call.Actor, CancellationToken.None) is not { Limits: var limits })
        {
            await call.ReplyResultAsync(OperationResult.Forbidden(Authorize.Require(call.Actor, call.Actor.GuildId, Authorize.ServerSettings)));
            return;
        }

        var draft = call.OpenDraft(new object());
        await call.Respond.ModalAsync(new ModalBuilder().WithTitle(AdminForms.Cut(call.T("admin.sor.context.title"), 45)).WithCustomId(AdminCall.CustomId(draft, ContextAction))
            .AddLabel(AdminForms.Cut(call.T("admin.sor.context.turns"), 45), Number("turns", limits.MaxContextTurns))
            .AddLabel(AdminForms.Cut(call.T("admin.sor.context.idle"), 45), Number("idle", limits.IdleMinutes))
            .AddLabel(AdminForms.Cut(call.T("admin.sor.context.hours"), 45), Number("hours", limits.HistoryHours))
            .Build());
    }

    public async Task ChannelAsync(AdminCall call, bool allow)
    {
        if (call.Args.ChannelId is not { } channel)
        {
            await AdminForms.PickChannelAsync(call, allow ? "admin.sor.allow-channel.pick" : "admin.sor.remove-channel.pick", state: allow);
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.AllowChannelAsync(call.Actor, channel, allow, CancellationToken.None));
    }

    public async Task RoleAsync(AdminCall call, bool allow)
    {
        if (call.Args.RoleId is not { } role)
        {
            await call.ReplyTextAsync("admin.sor.role_missing");
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.AllowRoleAsync(call.Actor, role, allow, CancellationToken.None));
    }

    public async Task UsageAsync(AdminCall call)
    {
        await call.DeferAsync();
        var days = await config.UsageAsync(call.Actor, 7, CancellationToken.None);
        var text = new StringBuilder();
        if (days.Count == 0)
            text.Append(call.T("admin.sor.usage.none"));
        foreach (var d in days)
        {
            text.Append(call.T("admin.sor.usage.line", d.Day, d.Requests, d.Completed, d.ProviderFailures, d.Timeouts, d.ParseFailures, d.DeliveryFailures,
                d.Discarded, d.UnknownUsage, d.InputTokens, d.OutputTokens, d.ReasoningTokens, d.CachedInputTokens)).Append('\n');
        }

        text.Append('\n').Append(call.T("admin.sor.usage.note"));
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("admin.sor.usage.title"), text.ToString(), null, [], null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task DoctorAsync(AdminCall call)
    {
        await call.DeferAsync();
        var lines = await config.DoctorAsync(call.Actor, CancellationToken.None);
        var text = string.Join("\n", lines.Select(l => (l.Ok ? "✅ " : "⚠️ ") + call.T(l.Key, l.Args.ToArray())));
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("admin.sor.doctor.title"), text, null, [], null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task OnFormAsync(AdminCall call, string action)
    {
        switch (call.Operation.Id, action)
        {
            case ("allow-channel" or "remove-channel", AdminForms.ChannelAction):
                if (AdminForms.ChosenChannel(call) is not { } channel)
                {
                    await call.ReplyTextAsync("admin.error.channel_invalid");
                    return;
                }

                if (await AdminForms.ClaimAsync(call))
                    await call.FinishAsync(await config.AllowChannelAsync(call.Actor, channel, call.Operation.Id == "allow-channel", CancellationToken.None));
                return;
            case ("limits", LimitsAction):
                if (!await AdminForms.ClaimAsync(call))
                    return;
                if (Read(call, "cooldown", out var cooldown) && Read(call, "user", out var user) && Read(call, "guild", out var guild))
                    await call.FinishAsync(await config.SetLimitsAsync(call.Actor, cooldown, user, guild, null, null, null, CancellationToken.None));
                else
                    await call.FinishAsync(OperationResult.Fail(OperationError.InvalidInput, "admin.sor.number_invalid"));
                return;
            case ("context", ContextAction):
                if (!await AdminForms.ClaimAsync(call))
                    return;
                if (Read(call, "turns", out var turns) && Read(call, "idle", out var idle) && Read(call, "hours", out var hours))
                    await call.FinishAsync(await config.SetLimitsAsync(call.Actor, null, null, null, turns, idle, hours, CancellationToken.None));
                else
                    await call.FinishAsync(OperationResult.Fail(OperationError.InvalidInput, "admin.sor.number_invalid"));
                return;
            default:
                await call.ReplyTextAsync("admin.form.expired");
                return;
        }
    }

    private static TextInputBuilder Number(string field, int value) =>
        new TextInputBuilder().WithCustomId(field).WithStyle(TextInputStyle.Short).WithMaxLength(5).WithRequired(true)
            .WithValue(value.ToString(CultureInfo.InvariantCulture));

    private static bool Read(AdminCall call, string field, out int value) =>
        int.TryParse(call.Input.Text(field)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
