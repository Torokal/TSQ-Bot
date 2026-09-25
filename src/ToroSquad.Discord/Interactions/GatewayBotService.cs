using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Discord.Transport;

namespace ToroSquad.Discord.Interactions;

/// <summary>
/// Real Discord connection (only when Discord:Transport = Gateway). Intents: Guilds only — no privileged intents.
/// Never registers commands on Ready/reconnect; registration is the separate Sync-Commands step.
/// </summary>
public sealed class GatewayBotService(
    DiscordSocketClient client,
    InteractionHost host,
    IServiceProvider services,
    IOptions<DiscordOptions> options,
    ILogger<GatewayBotService> logger) : IHostedService
{
    public static DiscordSocketConfig CreateSocketConfig() => new()
    {
        GatewayIntents = GatewayIntents.Guilds,
        AlwaysDownloadUsers = false,
        MessageCacheSize = 0,
        LogGatewayIntentWarnings = true,
        // Only rate limits are retried by the SDK (honouring Retry-After). Timeouts/502 on message creation must
        // surface to the outbox as "ambiguous" instead of being silently retried into duplicates.
        DefaultRetryMode = RetryMode.RetryRatelimit,
        LogLevel = LogSeverity.Info,
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = options.Value.Token;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Discord:Transport is Gateway but no bot token is configured (TOROSQUAD_Discord__Token or user-secrets).");

        client.Log += OnLogAsync;
        host.Service.Log += OnLogAsync;
        client.InteractionCreated += OnInteractionCreatedAsync;
        client.JoinedGuild += g => TrackAsync(g, joined: true);
        client.GuildAvailable += g => TrackAsync(g, joined: true);
        client.LeftGuild += g => TrackAsync(g, joined: false);
        client.Ready += OnReadyAsync;

        await host.InitializeAsync(services);
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        client.InteractionCreated -= OnInteractionCreatedAsync;
        await client.StopAsync();
        await client.LogoutAsync();
    }

    private Task OnReadyAsync()
    {
        var expected = options.Value.ApplicationId;
        var actual = client.CurrentUser?.Id ?? 0;
        if (expected != 0 && actual != 0 && expected != actual)
            logger.LogCritical("Bot token belongs to application {Actual} but Discord:ApplicationId is {Expected}", actual, expected);
        logger.LogInformation("Discord gateway ready as {User} in {Guilds} guild(s). Commands are NOT auto-registered; use scripts/Sync-Commands.ps1.",
            client.CurrentUser?.Username, client.Guilds.Count);
        return Task.CompletedTask;
    }

    private Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        // Leave the gateway handler immediately; the interaction runs on the thread pool inside its own DI scope.
        _ = Task.Run(async () =>
        {
            await using var scope = services.CreateAsyncScope();
            if (!scope.ServiceProvider.GetRequiredService<DeploymentPolicy>().IsGuildAllowed(interaction.GuildId))
            {
                await RefuseOtherGuildAsync(interaction, scope.ServiceProvider);
                return;
            }

            var context = new SocketInteractionContext(client, interaction);
            try
            {
                var result = await host.Service.ExecuteCommandAsync(context, scope.ServiceProvider);
                if (!result.IsSuccess)
                    await ReportFailureAsync(interaction, result, scope.ServiceProvider);
            }
            catch (Exception ex)
            {
                await ReportFailureAsync(interaction, ExecuteResult.FromError(ex), scope.ServiceProvider);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Single-guild guard: nothing runs, nothing is read or written; the user only sees a short refusal.</summary>
    private async Task RefuseOtherGuildAsync(SocketInteraction interaction, IServiceProvider scoped)
    {
        logger.LogWarning("Refused {Type} interaction from guild {Guild}: not in Discord:AllowedGuildIds", interaction.Type, interaction.GuildId?.ToString(CultureInfo.InvariantCulture) ?? "DM");
        try
        {
            if (interaction is SocketAutocompleteInteraction autocomplete)
                await autocomplete.RespondAsync([]);
            else
                await interaction.RespondAsync(scoped.GetRequiredService<ILocalizer>().Get(Languages.Default, "error.guild_not_allowed"), ephemeral: true,
                    allowedMentions: DiscordConversions.ToAllowedMentions(Core.Messaging.MentionPolicy.None));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not answer a refused interaction");
        }
    }

    private async Task ReportFailureAsync(SocketInteraction interaction, IResult result, IServiceProvider scoped)
    {
        var traceCode = TraceCodes.New();
        if (interaction is SocketAutocompleteInteraction autocomplete)
        {
            // Autocomplete cannot show messages. Log the failure (found live: failures were invisible) and answer with an
            // empty list so Discord does not show 'Loading options failed'.
            var command = autocomplete.Data.CommandName + (autocomplete.Data.Options.FirstOrDefault()?.Name is { } sub ? " " + sub : "");
            var option = autocomplete.Data.Current.Name;
            if (result is ExecuteResult { Exception: { } acFailure })
                logger.LogWarning(acFailure, "Autocomplete failed [{TraceCode}] /{Command} option={Option} guild={Guild}", traceCode, command, option, interaction.GuildId);
            else
                logger.LogWarning("Autocomplete failed [{TraceCode}] /{Command} option={Option} guild={Guild}: {Error} {Reason}", traceCode, command, option, interaction.GuildId, result.Error, result.ErrorReason);
            try
            {
                if (!interaction.HasResponded)
                    await autocomplete.RespondAsync([]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not send empty autocomplete result [{TraceCode}]", traceCode);
            }

            return;
        }

        var key = result.Error switch
        {
            InteractionCommandError.UnmetPrecondition when result.ErrorReason?.StartsWith("error.", StringComparison.Ordinal) == true => result.ErrorReason,
            InteractionCommandError.UnmetPrecondition => "error.forbidden",
            InteractionCommandError.UnknownCommand => "error.unknown_command",
            InteractionCommandError.ConvertFailed or InteractionCommandError.BadArgs or InteractionCommandError.ParseFailed => "error.bad_input",
            _ => "error.internal",
        };

        if (result is ExecuteResult { Exception: { } failure })
            logger.LogError(failure, "Interaction failed [{TraceCode}] type={Type} guild={Guild}", traceCode, interaction.Type, interaction.GuildId);
        else if (key == "error.internal")
            logger.LogError("Interaction failed [{TraceCode}] {Error}: {Reason}", traceCode, result.Error, result.ErrorReason);

        var language = Languages.Default;
        if (interaction.GuildId is { } guildId)
            language = (await scoped.GetRequiredService<IGuildSettingsStore>().GetAsync(new GuildId(guildId), CancellationToken.None)).Language;
        var localizer = scoped.GetRequiredService<ILocalizer>();
        var text = localizer.Get(language, key);
        if (key == "error.internal")
            text += "\n" + localizer.Get(language, "error.trace_code", traceCode);

        try
        {
            var none = DiscordConversions.ToAllowedMentions(Core.Messaging.MentionPolicy.None);
            if (interaction.HasResponded)
                await interaction.FollowupAsync(text, ephemeral: true, allowedMentions: none);
            else
                await interaction.RespondAsync(text, ephemeral: true, allowedMentions: none);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not deliver error message [{TraceCode}]", traceCode);
        }
    }

    private async Task TrackAsync(SocketGuild guild, bool joined)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var tracker = scope.ServiceProvider.GetRequiredService<IGuildPresenceTracker>();
            if (joined)
                await tracker.MarkPresentAsync(new GuildId(guild.Id), CancellationToken.None);
            else
                await tracker.MarkLeftAsync(new GuildId(guild.Id), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Guild presence tracking failed for {Guild}", guild.Id);
        }
    }

    private Task OnLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}

/// <summary>Fake/offline mode: still loads all interaction modules and validates the manifest at startup.</summary>
public sealed class OfflineInteractionService(InteractionHost host, IServiceProvider services, ILogger<OfflineInteractionService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var manifest = await host.InitializeAsync(services);
        logger.LogInformation("Discord transport is FAKE: no Discord connection. {Count} slash commands validated offline: {Names}",
            manifest.Commands.Count, string.Join(", ", manifest.Commands.Select(c => "/" + c.Name)));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
