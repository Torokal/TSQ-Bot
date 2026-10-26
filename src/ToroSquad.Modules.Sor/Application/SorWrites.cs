using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>
/// Every check-then-write of TSQ Sor (admission and quota, finalization, memory edits, deletions) runs as ONE short SQLite
/// write transaction: <c>BEGIN IMMEDIATE</c> (EF's BeginTransaction on Microsoft.Data.Sqlite) takes the write lock before
/// anything is read, so two requests for the last daily slot, or a deletion racing a finalization, are serialized across
/// connections. A busy database or a lost race rolls the attempt back and runs it again a bounded number of times. The AI
/// request and Discord are NEVER called inside.
/// </summary>
public static class SorWrites
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
