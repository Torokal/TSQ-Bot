using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// A Skip/Take without an OrderBy picks arbitrary rows and makes EF log a warning at startup. Production only logs it;
/// every <see cref="TestHost"/> turns it into an error, so any such query fails the first test that runs it.
/// </summary>
public sealed class QueryOrderingTests
{
    [Fact]
    public async Task A_row_limit_without_an_order_fails_in_tests_and_an_ordered_one_runs()
    {
        await using var host = await TestHost.CreateAsync();
        await using var scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var unordered = () => db.Outbox.AsNoTracking().Where(o => o.ChannelId == 424242).Take(3).ToListAsync(ct);
        (await unordered.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*RowLimitingOperationWithoutOrderByWarning*");

        (await db.Outbox.AsNoTracking().Where(o => o.ChannelId == 424242).OrderBy(o => o.Id).Take(3).ToListAsync(ct)).Should().BeEmpty();
    }
}
