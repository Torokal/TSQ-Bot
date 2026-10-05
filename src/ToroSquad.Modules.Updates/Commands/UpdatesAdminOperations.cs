using System.Globalization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Updates.Commands;

/// <summary>
/// <c>/tsq-admin modul:updates</c> — Manage Server: the shared command is hidden by default_member_permissions AND every
/// operation is re-authorized in the service. Works while the module is disabled so the channel and the games can be set and
/// checked before activation (/modules enable updates). Every answer is private. Nothing here requests a provider or posts
/// to a channel, and there is deliberately no operation to post an update by hand.
/// <para>A game is always one of the registered definitions: the game operations open a private select that lists exactly
/// the registered games, and the picked value is checked against them again.</para>
/// </summary>
public sealed class UpdatesAdminOperations(UpdatesConfigService config) : IAdminFormHandler
{
    public const string GameAction = "game";
    private const string EnableOp = "game-enable";
    private const string DisableOp = "game-disable";
    private const string PreviewOp = "preview";

    public static readonly AdminModule Definition = AdminModule.For<UpdatesAdminOperations>(UpdatesModule.AdminId, UpdatesModule.ModuleIdTyped)
        .Op("configure", (h, c) => h.ConfigureAsync(c), AdminFields.Channel)
        .Op("games", (h, c) => h.GamesAsync(c))
        .Op(EnableOp, (h, c) => h.GameOperationAsync(c))
        .Op(DisableOp, (h, c) => h.GameOperationAsync(c))
        .Op("pause", (h, c) => h.PauseAsync(c, true))
        .Op("resume", (h, c) => h.PauseAsync(c, false))
        .Op(PreviewOp, (h, c) => h.GameOperationAsync(c))
        .Op("status", (h, c) => h.StatusAsync(c))
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Build();

    /// <summary>With <c>kanal</c> at once; without it a channel select (nothing is saved until a channel is picked).</summary>
    public async Task ConfigureAsync(AdminCall call)
    {
        if (call.Args.ChannelId is not { } channel)
        {
            await AdminForms.PickChannelAsync(call, "admin.updates.configure.pick");
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    /// <summary>game-enable, game-disable and preview: a select of the registered games (nothing happens until one is picked).</summary>
    public Task GameOperationAsync(AdminCall call) =>
        AdminForms.ChooseAsync(call, call.T("admin.updates.game.pick"), GameAction, config.RegisteredGames.Select(g => (GameLabel(g), g.Key, false)));

    public async Task OnFormAsync(AdminCall call, string action)
    {
        if (action == AdminForms.ChannelAction && call.Operation.Id == "configure")
        {
            if (AdminForms.ChosenChannel(call) is not { } channel)
            {
                await call.ReplyTextAsync("admin.error.channel_invalid");
                return;
            }

            if (await AdminForms.ClaimAsync(call))
                await call.FinishAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
            return;
        }

        // The value comes back from the client: only a registered game key is accepted (the service checks it again).
        if (action != GameAction || call.Operation.Id is not (EnableOp or DisableOp or PreviewOp) ||
            call.Input.FirstValue is not { } key || config.RegisteredGames.All(g => g.Key != key))
        {
            await call.ReplyTextAsync("updates.game.unknown");
            return;
        }

        if (!await AdminForms.ClaimAsync(call))
            return;
        if (call.Operation.Id != PreviewOp)
        {
            await call.FinishAsync(await config.SetGameEnabledAsync(call.Actor, key, call.Operation.Id == EnableOp, CancellationToken.None));
            return;
        }

        var (auth, preview) = await config.PreviewAsync(call.Actor, key, call.Language, CancellationToken.None);
        if (preview is null)
        {
            await call.FinishAsync(auth);
            return;
        }

        var note = call.T(preview.Synthetic ? "updates.preview.synthetic" : "updates.preview.real");
        var card = preview.Message.Embed!;
        await call.Respond.UpdateAsync(null, AdminCall.EmptyComponents, card with { Description = "_" + note + "_\n\n" + card.Description });
    }

    public async Task GamesAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, status) = await config.StatusAsync(call.Actor, CancellationToken.None);
        if (status is null)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var lines = status.Games.Select(g => call.T(g.Enabled ? "updates.games.line_on" : "updates.games.line_off",
            g.Game.DisplayName, g.ProviderName, g.Game.Key));
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("updates.games.title"), string.Join("\n", lines) + "\n\n" + call.T("updates.games.hint"),
            null, [], null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task PauseAsync(AdminCall call, bool paused)
    {
        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetPausedAsync(call.Actor, paused, CancellationToken.None));
    }

    public async Task StatusAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, status) = await config.StatusAsync(call.Actor, CancellationToken.None);
        if (status is null)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var channel = status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : call.T("updates.status.no_channel");
        if (status.Paused)
            channel += " · " + call.T("updates.status.paused");
        var followed = status.Games.Where(g => g.Enabled).Select(g => g.Game.DisplayName).ToList();
        var fields = new List<EmbedField>
        {
            new(call.T("updates.status.mode"), call.T(status.Mode switch
            {
                UpdatesMode.Off => "updates.status.mode_off",
                UpdatesMode.DryRun => "updates.status.mode_dry",
                _ => status.EffectiveMode == UpdatesMode.Live ? "updates.status.mode_live" : "updates.status.mode_live_delivery_dry",
            }), true),
            new(call.T("updates.status.module"), call.T(status.ModuleEnabled ? "modules.state_on" : "modules.state_off"), true),
            new(call.T("updates.status.channel"), channel, true),
            new(call.T("updates.status.games"), followed.Count == 0 ? call.T("updates.status.games_none") : string.Join(", ", followed), false),
        };
        foreach (var game in status.Games.Where(g => g.Enabled).Take(DiscordLimits.EmbedFieldsMax - fields.Count))
        {
            var source = game.Source;
            var poll = source?.LastAttemptAt is { } attempt
                ? call.T("updates.status.source_value", source.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—", UpdatesConfigService.OutcomeName(source),
                    source.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—", DiscordText.Timestamp(attempt, 'R'), source.ConsecutiveFailures,
                    source.NextPollAt is { } next ? DiscordText.Timestamp(next, 'R') : "—")
                : call.T("updates.status.source_never");
            var discovered = game.LastDiscovered is { Title: { } title } item
                ? call.T("updates.status.discovered_value", DiscordText.Untrusted(title, 120), DiscordText.Timestamp(item.PublishedAt ?? item.FirstSeenAt, 'R'))
                : call.T("updates.status.discovered_none");
            var card = game.LastCardAt is { } at ? DiscordText.Timestamp(at, 'R') : "—";
            fields.Add(new(call.T("updates.status.game", game.Game.DisplayName, game.ProviderName),
                Clip(poll + "\n" + discovered + "\n" + call.T("updates.status.card_value", card), DiscordLimits.EmbedFieldValueMax), false));
        }

        await call.ReplyEmbedAsync(new MessageEmbed(call.T("updates.status.title"), null, null, fields, null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task DoctorAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, checks) = await config.DoctorAsync(call.Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var lines = checks.Select(c => $"{Icon(c.State)} **{call.T(c.LabelKey)}** — {call.T(c.DetailKey, c.Args.ToArray())}");
        var failed = checks.Count(c => c.State == UpdatesCheckState.Problem);
        var summary = call.T(failed == 0 ? "updates.doctor.summary_ok" : "updates.doctor.summary_failed", failed);
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("updates.doctor.title"), Clip(summary + "\n\n" + string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, failed == 0 ? ToroInteractionModule.NeutralColor : ToroInteractionModule.WarningColor));
    }

    private static string GameLabel(GameUpdateDefinition game) => game.DisplayName + " — " + game.Key;

    private static string Icon(UpdatesCheckState state) => state switch
    {
        UpdatesCheckState.Ok => "✅",
        UpdatesCheckState.Warning => "⚠️",
        UpdatesCheckState.Problem => "❌",
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
