using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Bot;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;

namespace ToroSquad.Tests.Support;

/// <summary>
/// Builds the SAME service registration as production (ToroHost.AddToroSquad) against a real SQLite file in a temp
/// folder, with a fake clock, fake Discord transport and fake guild gateway. Each test gets its own database.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private TestHost(ServiceProvider services, string directory, FakeTimeProvider clock)
    {
        Services = services;
        Directory = directory;
        Clock = clock;
    }

    public ServiceProvider Services { get; }
    public string Directory { get; }
    public FakeTimeProvider Clock { get; }

    public FakeMessageTransport Transport => Services.GetRequiredService<FakeMessageTransport>();
    public FakeGuildGateway Guilds => Services.GetRequiredService<FakeGuildGateway>();

    public static async Task<TestHost> CreateAsync(Dictionary<string, string?>? overrides = null, DateTimeOffset? start = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "torosquad-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settings = new Dictionary<string, string?>
        {
            ["Bot:DataDirectory"] = directory,
            ["Discord:Transport"] = "Fake",
            ["Delivery:Mode"] = "Send",
            ["Esports:Provider:Mode"] = "Fixture",
            ["Esports:Liquipedia:UserAgent"] = "TSQBot-tests/0 (https://localhost; tests)",
            ["Esports:MatchPollMinutes"] = "10",
        };
        foreach (var (k, v) in overrides ?? [])
            settings[k] = v;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var clock = new FakeTimeProvider(start ?? T0);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        ToroHost.AddToroSquad(services, configuration, directory, longRunning: false);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using (var scope = provider.CreateAsyncScope())
            await DatabaseMaintenance.MigrateAsync(scope.ServiceProvider.GetRequiredService<ToroDbContext>(), CancellationToken.None);
        return new TestHost(provider, directory, clock);
    }

    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        try
        {
            await action(scope.ServiceProvider);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            // Surface the SQLite error text in the test report (EF only shows "see inner exception").
            throw new InvalidOperationException($"{ex.Message} --> {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}", ex);
        }
    }

    public static ActorContext Admin(GuildId guild, ulong user = 1) =>
        new(guild, new UserId(user), GuildPermission.ManageGuild | GuildPermission.ManageRoles, [], false, 50);

    public static ActorContext Member(GuildId guild, ulong user = 2, params RoleId[] roles) =>
        new(guild, new UserId(user), GuildPermission.ViewChannel | GuildPermission.SendMessages, roles, false, 1);

    /// <summary>Standard esports guild: channel usable by the bot, notification role mapped, module enabled.</summary>
    public async Task SetUpEsportsGuildAsync(GuildId guild, ChannelId channel, params RoleInfo[] extraRoles)
    {
        Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(guild, extraRoles));
        Guilds.SetChannel(guild, channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));
        await InScopeAsync(async sp =>
        {
            var admin = Admin(guild);
            (await sp.GetRequiredService<EsportsConfigService>().ConfigureAsync(admin, channel.Value, true, 30, true, false, CancellationToken.None))
                .Succeeded.Should().BeTrue();
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(admin, "esports", true, CancellationToken.None))
                .Succeeded.Should().BeTrue();
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows may keep the file briefly; temp cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
