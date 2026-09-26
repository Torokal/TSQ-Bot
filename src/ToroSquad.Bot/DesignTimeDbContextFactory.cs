using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Volleyball.Persistence;

namespace ToroSquad.Bot;

/// <summary>
/// Used only by `dotnet ef migrations add`. Must list the same model contributors as the running bot
/// (every compiled-in module that owns tables). Tests assert the model has no pending changes.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ToroDbContext>
{
    public static IReadOnlyList<IModelContributor> AllContributors() => [new EsportsModelContributor(), new Formula1ModelContributor(), new VolleyballModelContributor()];

    public ToroDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ToroDbContext>()
            .UseSqlite("Data Source=design-time.db", o => o.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            .Options;
        return new ToroDbContext(options, AllContributors());
    }
}
