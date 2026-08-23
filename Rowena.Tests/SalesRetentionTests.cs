using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class SalesRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static SaleRecord At(int daysAgo, uint item = 1) => new(item, 1, 100, Now.AddDays(-daysAgo), true);

    [Fact]
    public void SalesOlderThanTheWindowGo()
    {
        var kept = SalesRetention.Prune([At(1), At(179), At(181)], Now, keepDays: 180, cap: 1000);

        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, sale => sale.At == Now.AddDays(-181));
    }

    [Fact]
    public void TheCapKeepsTheNewest()
    {
        var kept = SalesRetention.Prune([At(1), At(2), At(3)], Now, keepDays: 180, cap: 2);

        Assert.Equal([Now.AddDays(-1), Now.AddDays(-2)], kept.Select(sale => sale.At));
    }

    [Fact]
    public void OrderIsNewestFirstWhateverCameIn()
    {
        var kept = SalesRetention.Prune([At(3), At(1), At(2)], Now, keepDays: 180, cap: 1000);

        Assert.Equal([Now.AddDays(-1), Now.AddDays(-2), Now.AddDays(-3)], kept.Select(sale => sale.At));
    }
}
