using System.Data;
using System.Data.Common;
using System.Reflection;
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
/// concurrent opens (LfgLifecycleTests: 2 failed runs in 60). <see cref="ToroSqliteConnection"/> opens one at a time —
/// and only opens: queries and transactions stay concurrent.
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

    /// <summary>
    /// EF opens with <c>OpenAsync</c>. Microsoft.Data.Sqlite (10.0.12 source) does not override it, so
    /// <see cref="DbConnection.OpenAsync(CancellationToken)"/> runs the virtual <c>Open()</c> — the serialized one. Fails if a
    /// package update adds an async override that would bypass the gate.
    /// </summary>
    [Fact]
    public void Async_opens_go_through_the_serialized_open()
    {
        typeof(ToroSqliteConnection).GetMethod(nameof(DbConnection.Open), Type.EmptyTypes)!.DeclaringType.Should().Be<ToroSqliteConnection>();
        typeof(ToroSqliteConnection).GetMethod(nameof(DbConnection.OpenAsync), [typeof(CancellationToken)])!.DeclaringType
            .Should().Be<DbConnection>("the base OpenAsync calls the virtual Open()");
        typeof(ToroSqliteConnection).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name)
            .Should().Equal([nameof(DbConnection.Open)], "the connection changes nothing but opening");
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("OpenAsync")]
    [InlineData("Mixed")]
    public void Concurrent_opens_never_share_a_native_handle(string how)
    {
        var directory = Path.Combine(Path.GetTempPath(), "torosquad-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "handles.db");
        const int owners = 16;
        try
        {
            for (var wave = 0; wave < 300; wave++)
            {
                var connections = Enumerable.Range(0, owners).Select(_ => DatabaseMaintenance.CreateConnection(database)).ToArray();
                try
                {
                    using (var start = new Barrier(owners))
                    {
                        var threads = connections.Select((c, i) => new Thread(() =>
                        {
                            start.SignalAndWait();
                            if (how == "Open" || (how == "Mixed" && i % 2 == 0))
                                c.Open();
                            else
                                c.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
                        })).ToArray();
                        foreach (var thread in threads)
                            thread.Start();
                        foreach (var thread in threads)
                            thread.Join();
                    }

                    // All owners are open and strongly held here: each must own a distinct native handle.
                    connections.Select(c => c.Handle!.DangerousGetHandle()).Distinct().Should().HaveCount(owners, $"wave {wave} ({how})");
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
            Delete(directory);
        }
    }

    /// <summary>
    /// Only opening is serialized: while one connection holds SQLite's write lock inside a transaction, another connection
    /// opens and reads at once (WAL). The database is not turned into one connection at a time.
    /// </summary>
    [Fact]
    public async Task Opening_is_the_only_serialized_step_queries_and_transactions_stay_concurrent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "torosquad-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "concurrent.db");
        try
        {
            await using (var setup = DatabaseMaintenance.CreateConnection(database))
            {
                await setup.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = setup.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (v INTEGER); INSERT INTO t VALUES (1);";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var writer = DatabaseMaintenance.CreateConnection(database);
            await writer.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = (SqliteTransaction)await writer.BeginTransactionAsync(TestContext.Current.CancellationToken); // BEGIN IMMEDIATE
            await using (var insert = writer.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO t VALUES (2);";
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var read = Task.Run(async () =>
            {
                await using var reader = DatabaseMaintenance.CreateConnection(database);
                await reader.OpenAsync();
                await using var count = reader.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM t;";
                return (long)(await count.ExecuteScalarAsync())!;
            });

            (await read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Should().Be(1,
                "another connection opens and reads the committed state while the writer holds its transaction");
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(DatabaseMaintenance.ConnectionString(database)));
            Delete(directory);
        }
    }

    private static void Delete(string directory)
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
