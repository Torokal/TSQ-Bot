using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// Every economic change of TSQ Öngörü runs as ONE SQLite write transaction: <c>BEGIN IMMEDIATE</c> (EF's
/// BeginTransaction on Microsoft.Data.Sqlite) takes the database write lock before anything is read, so the checks inside
/// (status, deadline, balance, existing entry, active tournament) see the state no other writer can change until commit —
/// across processes and connections, not just threads. A busy database (SQLITE_BUSY/LOCKED after the connection's own busy
/// timeout) or a lost optimistic-concurrency race rolls the whole attempt back and retries it a bounded number of times;
/// a retried attempt starts from nothing committed, and the unique operation keys make a double booking impossible even
/// then. Discord is never called inside.
/// </summary>
public static class PredictionWrites
{
    public const int MaxAttempts = 4;

    public static async Task<T> RunAsync<T>(ToroDbContext db, Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var result = await work();
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsRetryable(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>A lost race or a busy database: nothing was committed, the attempt can safely run again.</summary>
    public static bool IsRetryable(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => true,
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6, // SQLITE_BUSY, SQLITE_LOCKED
        DbUpdateException { InnerException: SqliteException inner } => inner.SqliteErrorCode is 5 or 6,
        InvalidOperationException { InnerException: SqliteException inner } => inner.SqliteErrorCode is 5 or 6,
        _ => false,
    };

    /// <summary>A unique index refused the insert (SQLITE_CONSTRAINT_UNIQUE / PRIMARYKEY): the row exists already.</summary>
    public static bool IsUniqueViolation(Exception ex) =>
        ex is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 or 1555 } };
}
