using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Discord.Commands.Core;

/// <summary>Connection facts for /bot status (no secrets, no internal error details).</summary>
public interface IBotConnectionStatus
{
    string Mode { get; }
    bool Connected { get; }
    int? LatencyMs { get; }
    DateTimeOffset StartedAt { get; }
}

public sealed class FakeConnectionStatus(TimeProvider clock) : IBotConnectionStatus
{
    public string Mode => "fake";
    public bool Connected => false;
    public int? LatencyMs => null;
    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();
}

public sealed class GatewayConnectionStatus(DiscordSocketClient client, TimeProvider clock) : IBotConnectionStatus
{
    public string Mode => "gateway";
    public bool Connected => client.ConnectionState == ConnectionState.Connected;
    public int? LatencyMs => client.Latency;
    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();
}

[ToroModule("core")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class HelpCommands(InteractionServices services, InteractionHost host, ModuleRegistry registry, IModuleGate gate)
    : ToroInteractionModule(services)
{
    [SlashCommand("help", "Show the commands available to you in this server")]
    public async Task HelpAsync()
    {
        await DeferEphemeralAsync();
        var actor = Actor;
        var language = await LangAsync();
        var isAdmin = actor.Has(Authorize.ServerSettings);
        var lines = new List<string>();
        foreach (var command in (host.Manifest?.Commands ?? []).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (!registry.TryGet(command.OwnerModule, out var module))
                continue;
            if (!await gate.IsEnabledAsync(actor.GuildId, module.Descriptor.Id, CancellationToken.None))
                continue;
            var adminOnly = command.DefaultMemberPermissions is not null;
            if (adminOnly && !isAdmin)
                continue;
            var description = Localizer.HasKey(language, CommandLocalizationKeys.Description(command.Name))
                ? Localizer.Get(language, CommandLocalizationKeys.Description(command.Name))
                : command.Description;
            lines.Add($"`/{command.Name}` — {description}{(adminOnly ? " 🔒" : "")}");
        }

        var embed = new MessageEmbed(
            await T("help.title"),
            string.Join("\n", lines) + "\n\n" + await T(isAdmin ? "help.footer_admin" : "help.footer_user"),
            null, [], null, null, BrandColor);
        await ReplyEmbedAsync(embed);
    }
}

[ToroModule("core")]
[Group("bot", $"About {ProductInfo.ProductName}: status, version, source code")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class BotCommands(
    InteractionServices services,
    ProductInfo product,
    DeploymentPolicy deployment,
    IBotConnectionStatus connection,
    ModuleManagementService modules,
    IEnumerable<IModuleHealthCheck> healthChecks) : ToroInteractionModule(services)
{
    [SlashCommand("status", "Safe, public health overview of the bot")]
    public async Task StatusAsync()
    {
        await DeferEphemeralAsync();
        var language = await LangAsync();
        var fields = new List<EmbedField>
        {
            new(await T("status.connection"), connection.Connected
                ? await T("status.connected", connection.LatencyMs ?? 0)
                : await T(connection.Mode == "fake" ? "status.fake_mode" : "status.disconnected"), true),
            new(await T("status.uptime"), DiscordText.Timestamp(connection.StartedAt, 'R'), true),
            new(await T("status.deployment"), await T("status.deployment_value", deployment.Hosting,
                deployment.SingleGuild ? await T("status.single_guild")
                : deployment.GuildRestricted ? await T("status.guild_restricted", deployment.AllowedGuildIds!.Count)
                : await T("status.unrestricted")), true),
        };

        foreach (var status in await modules.ListAsync(Actor.GuildId, CancellationToken.None))
        {
            var state = await T(status.Enabled ? "modules.state_on" : "modules.state_off");
            fields.Add(new(Localizer.Get(language, status.Descriptor.NameKey), state, true));
        }

        foreach (var check in healthChecks)
        {
            var report = await check.CheckAsync(CancellationToken.None);
            foreach (var entry in report.Entries)
            {
                var args = entry.Args?.ToArray() ?? [];
                fields.Add(new(LocalizedOrRaw(language, entry.Component), $"{HealthIcon(entry.State)} {Localizer.Get(language, entry.DetailKey, args)}", false));
            }
        }

        var footer = product.Commit is { Length: > 0 } commit ? $"{product.Version} ({commit[..Math.Min(7, commit.Length)]})" : product.Version;
        await ReplyEmbedAsync(new MessageEmbed(await T("status.title"), null, null, fields.Take(25).ToList(), footer, null, NeutralColor));
    }

    [SlashCommand("about", $"What {ProductInfo.ProductName} is, its version and attributions")]
    public async Task AboutAsync()
    {
        var fields = new List<EmbedField>
        {
            new(await T("about.version"), Inv($"`{product.Version}`{(product.Commit is null ? "" : $" (`{product.Commit[..Math.Min(12, product.Commit.Length)]}`)")}"), true),
            new(await T("about.license"), product.License, true),
        };
        if (!string.IsNullOrWhiteSpace(product.OperatorContact))
            fields.Add(new(await T("about.operator"), DiscordText.Untrusted(product.OperatorContact, 200), false));
        var lang = await LangAsync();
        foreach (var a in product.Attributions)
            fields.Add(new(a.Name, $"{LocalizedOrRaw(lang, a.Note)}\n{LocalizedOrRaw(lang, a.License)} — {a.Url}", false));

        await ReplyEmbedAsync(new MessageEmbed(
            ProductInfo.ProductName,
            await T("about.description"),
            product.SourceUrl, fields.Take(25).ToList(), await T("about.not_official"), null, BrandColor));
    }

    [SlashCommand("source", "Where to get the source code of the exact version running here")]
    public async Task SourceAsync()
    {
        var commit = product.Commit ?? await T("source.commit_unknown");
        var description = product.SourceConfigured
            ? await T("source.available", product.SourceUrl!, product.Version, commit)
            : await T("source.not_configured", product.Version, commit);
        await ReplyEmbedAsync(new MessageEmbed(
            await T("source.title"),
            description + "\n\n" + await T("source.license_note", product.License),
            product.SourceConfigured ? product.SourceUrl : null, [], null, null, product.SourceConfigured ? NeutralColor : WarningColor));
    }

    /// <summary>Component names and attribution texts may be localization keys; plain text is shown as-is.</summary>
    private string LocalizedOrRaw(string language, string keyOrText) =>
        Localizer.HasKey(language, keyOrText) || Localizer.HasKey(Languages.Fallback, keyOrText) ? Localizer.Get(language, keyOrText) : keyOrText;

    private static string HealthIcon(HealthState state) => state switch
    {
        HealthState.Healthy => "🟢",
        HealthState.Degraded => "🟡",
        HealthState.NotConfigured => "⚪",
        _ => "🔴",
    };
}

internal static class FormatExtensions
{
    public static string Inv(this int value) => value.ToString(CultureInfo.InvariantCulture);
}
