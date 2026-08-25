using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class FreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ShelfLife = TimeSpan.FromMinutes(10);

    [Fact]
    public void NeverFetchedIsNotTheSameAsOld()
    {
        // The whole point: "nothing is under you" and "nobody has looked" read alike on a row,
        // and only one of them is a fact.
        var freshness = Freshness.Of(null, Now, ShelfLife);

        Assert.Equal(Standing.Unknown, freshness.Standing);
        Assert.Null(freshness.Age);
    }

    [Fact]
    public void JustFetchedIsFresh()
    {
        var freshness = Freshness.Of(Now.AddSeconds(-20), Now, ShelfLife);

        Assert.Equal(Standing.Fresh, freshness.Standing);
        Assert.Equal(TimeSpan.FromSeconds(20), freshness.Age);
    }

    [Fact]
    public void FetchedWithinTheShelfLifeIsStillFresh()
    {
        var freshness = Freshness.Of(Now.AddMinutes(-9), Now, ShelfLife);

        Assert.Equal(Standing.Fresh, freshness.Standing);
    }

    [Fact]
    public void ExactlyTheShelfLifeIsStillFresh()
    {
        // The shelf life is how long an answer is good for, so the last moment of it counts.
        var freshness = Freshness.Of(Now.AddMinutes(-10), Now, ShelfLife);

        Assert.Equal(Standing.Fresh, freshness.Standing);
    }

    [Fact]
    public void PastTheShelfLifeIsStale()
    {
        var freshness = Freshness.Of(Now.AddMinutes(-11), Now, ShelfLife);

        Assert.Equal(Standing.Stale, freshness.Standing);
        Assert.Equal(TimeSpan.FromMinutes(11), freshness.Age);
    }

    [Fact]
    public void TheShelfLifeIsTheCallersRatherThanOneNumber()
    {
        // Settings move it, and a row must age on the same clock the refetch runs on.
        var fetched = Now.AddMinutes(-30);

        Assert.Equal(Standing.Stale, Freshness.Of(fetched, Now, TimeSpan.FromMinutes(10)).Standing);
        Assert.Equal(Standing.Fresh, Freshness.Of(fetched, Now, TimeSpan.FromHours(1)).Standing);
    }

    [Fact]
    public void ASnapshotFromTheFutureIsNotOld()
    {
        // A clock that stepped backwards, or a machine that woke up. Better to read as new
        // than to report a negative age in a column.
        var freshness = Freshness.Of(Now.AddMinutes(5), Now, ShelfLife);

        Assert.Equal(Standing.Fresh, freshness.Standing);
        Assert.Equal(TimeSpan.Zero, freshness.Age);
    }
}
