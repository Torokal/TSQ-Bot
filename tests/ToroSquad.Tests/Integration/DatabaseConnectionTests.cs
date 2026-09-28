using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Every live owner of a connection to the bot's database has its own native SQLite handle. Microsoft.Data.Sqlite's pool
/// (≤ 10.0.12) can lend one handle to two concurrent owners (dotnet/efcore#39008); measured on 2026-09-28 with the bot's
/// connection string and plain <see cref="SqliteConnection"/>s: duplicate live handles in 12 of 15,000 waves of 16
/// concurrent opens (LfgLifecycleTests: 2 failed runs in 60). <see cref="ToroSqliteConnection"/> opens one at a time.
/// </summary>
public sealed class DatabaseConnectionTests
{
    [Fact]
    public async Task The_bot_reaches_its_pooled_database_only_through_serialized_opens()
    {
        await using var host = await TestHost.CreateAsync();
        SqliteConnection first, second;
        await using (var scope = host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            first = db.Database.GetDbConnection().Should().BeOfType<ToroSqliteConnection>().Subject;
            new SqliteConnectionStringBuilder(first.ConnectionString).Pooling.Should().BeTrue("the pool stays; only its opens are serialized");
            (await db.Database.ExecuteSqlRawAsync("PRAGMA user_version;", TestContext.Current.CancellationToken)).Should().Be(-1);
            await using (var other = host.Scope())
                second = (SqliteConnection)other.ServiceProvider.GetRequiredService<ToroDbContext>().Database.GetDbConnection();
            second.Should().NotBeSameAs(first, "one connection per context");
        }

        (first.State, second.State).Should().Be((ConnectionState.Closed, ConnectionState.Closed), "each context closes its own connection");
    }

    [Fact]
    public void Concurrent_opens_never_share_a_native_handle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "torosquad-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "handles.db");
        const int owners = 16;
        try
        {
            for (var wave = 0; wave < 500; wave++)
            {
                var connections = Enumerable.Range(0, owners).Select(_ => DatabaseMaintenance.CreateConnection(database)).ToArray();
                try
                {
                    using (var start = new Barrier(owners))
                    {
                        var threads = connections.Select(c => new Thread(() =>
                        {
                            start.SignalAndWait();
                            c.Open();
                        })).ToArray();
                        foreach (var thread in threads)
                            thread.Start();
                        foreach (var thread in threads)
                            thread.Join();
                    }

                    // All owners are open and strongly held here: each must own a distinct native handle.
                    connections.Select(c => c.Handle!.DangerousGetHandle()).Distinct().Should().HaveCount(owners, $"wave {wave}");
                }
                finally
                {
                    foreach (var connection in connections)
                        connection.Dispose();
                    SqliteConnection.ClearPool(connections[0]); // an empty pool each wave: the state where the leak scan runs
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
