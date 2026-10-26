using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Domain;
using ToroSquad.Modules.Sor.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>Synthetic proposals and database views for the TSQ Sor tests (no real member data anywhere).</summary>
public static class SorTestData
{
    /// <summary>One memory block as the model would write it after the separator.</summary>
    public static string Memory(params (string Op, string Kind, string Text, string Evidence, string? Target)[] items) =>
        JsonSerializer.Serialize(new
        {
            memory = items.Select(i => new { op = i.Op, kind = i.Kind, text = i.Text, evidence = i.Evidence, target = i.Target }).ToArray(),
        });

    public static (string, string, string, string, string?) Add(string kind, string text, string evidence) => ("add", kind, text, evidence, null);

    public static Task<List<SorMemoryEntity>> NotesAsync(SorKit kit, ulong user, GuildId? guild = null) =>
        kit.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<SorMemoryEntity>().AsNoTracking()
            .Where(m => m.GuildId == (guild ?? SorKit.Guild).Value && m.UserId == user).OrderBy(m => m.Id).ToListAsync());

    public static Task<List<SorTurnEntity>> TurnsAsync(SorKit kit, ulong user) =>
        kit.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<SorTurnEntity>().AsNoTracking()
            .Where(t => t.UserId == user).OrderBy(t => t.Id).ToListAsync());

    public static Task<List<SorConversationEntity>> ConversationsAsync(SorKit kit, ulong user) =>
        kit.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<SorConversationEntity>().AsNoTracking()
            .Where(c => c.OwnerUserId == user).OrderBy(c => c.Id).ToListAsync());

    public static Task<List<SorRequestEntity>> RequestsAsync(SorKit kit, ulong? user = null) =>
        kit.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<SorRequestEntity>().AsNoTracking()
            .Where(r => user == null || r.UserId == user).OrderBy(r => r.Id).ToListAsync());

    public static Task<List<SorGuildUsageEntity>> UsageAsync(SorKit kit) =>
        kit.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<SorGuildUsageEntity>().AsNoTracking().ToListAsync());

    /// <summary>A note written directly (as /hafizam or an earlier answer would have), for scope tests.</summary>
    public static Task<long> SeedNoteAsync(SorKit kit, ulong user, string text, SorMemoryScope scope, ulong? sourceChannel = null,
        SorMemoryKind kind = SorMemoryKind.Game, GuildId? guild = null) =>
        kit.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            var now = kit.Host.Clock.GetUtcNow();
            if (!await db.Set<SorUserEntity>().AnyAsync(u => u.GuildId == (guild ?? SorKit.Guild).Value && u.UserId == user))
                db.Add(new SorUserEntity { GuildId = (guild ?? SorKit.Guild).Value, UserId = user, CreatedAt = now, UpdatedAt = now, NoticeShownAt = now });
            var note = new SorMemoryEntity
            {
                GuildId = (guild ?? SorKit.Guild).Value,
                UserId = user,
                Kind = kind,
                Text = text,
                Scope = scope,
                SourceChannelId = sourceChannel,
                Origin = SorMemoryOrigin.Manual,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Add(note);
            await db.SaveChangesAsync();
            return note.Id;
        });
}
