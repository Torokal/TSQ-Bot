using Microsoft.Data.Sqlite;

namespace ToroSquad.Infrastructure.Persistence;

/// <summary>
/// The bot's pooled SQLite connection, opened one at a time.
/// </summary>
/// <remarks>
/// Microsoft.Data.Sqlite's pool (up to and including 10.0.12, the latest release) can lend one native handle to two live
/// owners (dotnet/efcore#39008). The pool hands a connection out under its lock, but <c>Activate</c> runs after that lock
/// and marks the connection active before it records the owner. A concurrent open that finds the pool empty looks for
/// "leaked" connections (active, no owner), can see that instant, reclaim the connection and lend the same handle again.
/// Two requests then share one transaction: "cannot start a transaction within a transaction", a write joining another
/// request's transaction, or "unable to delete/modify … due to active statements" when one of them returns it.
/// Checkout, activation and the leak scan all happen inside <see cref="SqliteConnection.Open"/>, so taking opens one at a
/// time closes that window while keeping the pool (an open is a pool pop or one native open). Fixed upstream after 10.0.12
/// (dotnet/efcore#39012); this can go once a release contains it.
/// </remarks>
public sealed class ToroSqliteConnection(string connectionString) : SqliteConnection(connectionString)
{
    private static readonly Lock OpenGate = new();

    public override void Open()
    {
        lock (OpenGate)
            base.Open();
    }
}
