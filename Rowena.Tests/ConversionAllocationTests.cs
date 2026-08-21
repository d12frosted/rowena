using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class ConversionAllocationTests
{
    private static readonly ConversionCatalog Catalog = ConversionCatalog.Default;

    private static readonly OrderBook Tokens = Fixtures.Book(Fixtures.MountToken);
    private static readonly OrderBook Rroneek = Fixtures.Book(Fixtures.RroneekHorn);
    private static readonly OrderBook Barreltender = Fixtures.Book(Fixtures.BarreltenderWhistle);

    private static readonly OrderBook[] All = [Tokens, Rroneek, Barreltender];

    private static readonly string[] Competing = ["tokens-to-rroneek", "tokens-to-barreltender"];

    private static Func<uint, OrderBook?> Books =>
        id => All.FirstOrDefault(book => book.ItemId == id);

    private static IReadOnlyList<Allocation> Allocate(long budget = long.MaxValue, int cap = 20) =>
        ConversionAllocation.Allocate(
            [.. Competing.Select(id => Catalog[id])],
            Books,
            MarketTax.Standard,
            budget,
            cap);

    private static long SingleRunOutlay(string id) =>
        ConversionEvaluator.Evaluate(Catalog[id], 1, Books, MarketTax.Standard).GilOutlay;

    private static long SingleRunProfit(string id) =>
        ConversionEvaluator.Evaluate(Catalog[id], 1, Books, MarketTax.Standard).Profit;

    [Fact]
    public void TheBetterTradeTakesTheWholeBook()
    {
        // Both want the same Mount Tokens, so this is not a split. Every token is worth more
        // through whichever mount pays more, and the answer is all of them, not half each.
        var byId = Allocate().ToDictionary(allocation => allocation.Conversion.Id);

        var winner = Competing.MaxBy(SingleRunProfit)!;
        var loser = Competing.Single(id => id != winner);

        Assert.True(byId[winner].Runs > 0);
        Assert.Equal(0, byId[loser].Runs);
        Assert.Equal(0, byId[loser].GilOutlay);
    }

    [Fact]
    public void SizedApartTheyClaimMoreRunsThanExist()
    {
        // The reason this class exists. On its own each conversion believes the whole book is
        // there for it, so the two together promise runs the market cannot supply.
        var apart = Competing.Sum(id =>
            ConversionEvaluator.LargestProfitableSize(Catalog[id], Books, MarketTax.Standard, 20));

        var together = Allocate().Sum(allocation => allocation.Runs);

        Assert.True(apart > together, $"apart claims {apart} runs, together only {together}");
        Assert.True(together * 100 <= Tokens.UnitsListed, "allocated more tokens than are listed");
    }

    [Fact]
    public void AGilBudgetCapsTheRuns()
    {
        // Exactly one run's worth of gil buys exactly one run: the next one reaches further up
        // the book and therefore costs more than the first did.
        var winner = Competing.MaxBy(SingleRunProfit)!;

        Assert.Equal(1, Allocate(budget: SingleRunOutlay(winner)).Sum(allocation => allocation.Runs));
    }

    [Fact]
    public void NoGilBuysNothing()
    {
        var allocations = Allocate(budget: 0);

        Assert.All(allocations, allocation => Assert.Equal(0, allocation.Runs));
        Assert.Equal(0, allocations.Sum(allocation => allocation.Profit));
    }

    [Fact]
    public void EveryAllocatedRunIsProfitable()
    {
        Assert.All(
            Allocate().Where(allocation => allocation.Runs > 0),
            allocation =>
            {
                Assert.True(allocation.Profit > 0);
                Assert.True(allocation.GilOutlay > 0);
                Assert.NotNull(allocation.ReturnOnOutlay);
            });
    }

    [Fact]
    public void TheCapLimitsRunsPerConversion()
    {
        Assert.All(Allocate(cap: 1), allocation => Assert.True(allocation.Runs <= 1));
    }

    [Fact]
    public void TradesThatSpendOnlyBoundCurrencyAreLeftOut()
    {
        // Allocation divides up a market. A trade that spends only scrips is not competing for
        // anything on it, and costing no gil it would stay profitable forever.
        var allocations = ConversionAllocation.Allocate(
            [Catalog["scrip-to-token"]],
            Books,
            MarketTax.Standard,
            long.MaxValue,
            20);

        Assert.Empty(allocations);
    }

    private static Conversion Flip(string id, uint input, uint output) =>
        new(
            id,
            id,
            [new ResourceAmount(Resource.Item(input, $"input {input}"), 1)],
            [new ResourceAmount(Resource.Item(output, $"output {output}"), 1)],
            "somewhere");

    private static Func<uint, OrderBook?> Synthetic(params OrderBook[] books) =>
        id => books.FirstOrDefault(book => book.ItemId == id);

    private static OrderBook Deep(uint id, long price, double velocity = 0d) =>
        OrderBook.Create(id, [new Listing(price, 10, "Phoenix")], velocity);

    [Fact]
    public void TradesSellingIntoTheSameBookShareItsAbsorption()
    {
        // Two trades, one output market. Each alone would claim its three units clear in a
        // day and a half; the board digests one queue of six, and both rows must say so.
        var books = Synthetic(Deep(1, 100), Deep(2, 100), Deep(9, 10_000, velocity: 2d));

        var byId = ConversionAllocation
            .Allocate([Flip("a", 1, 9), Flip("b", 2, 9)], books, MarketTax.None, long.MaxValue, 3)
            .ToDictionary(allocation => allocation.Conversion.Id);

        Assert.Equal(3, byId["a"].Runs);
        Assert.Equal(3, byId["b"].Runs);
        Assert.Equal(3d, byId["a"].DaysToAbsorb);
        Assert.Equal(3d, byId["b"].DaysToAbsorb);
    }

    [Fact]
    public void TradesWithTheirOwnMarketsAbsorbAlone()
    {
        var books = Synthetic(
            Deep(1, 100), Deep(2, 100),
            Deep(9, 10_000, velocity: 2d), Deep(8, 10_000, velocity: 1d));

        var byId = ConversionAllocation
            .Allocate([Flip("a", 1, 9), Flip("b", 2, 8)], books, MarketTax.None, long.MaxValue, 3)
            .ToDictionary(allocation => allocation.Conversion.Id);

        Assert.Equal(1.5d, byId["a"].DaysToAbsorb);
        Assert.Equal(3d, byId["b"].DaysToAbsorb);
    }

    [Fact]
    public void NothingAllocatedHasNothingToAbsorb()
    {
        // Selling at a loss: the row gets no runs, so there is no queue to report.
        var books = Synthetic(Deep(1, 100), Deep(9, 50, velocity: 2d));

        var only = ConversionAllocation
            .Allocate([Flip("a", 1, 9)], books, MarketTax.None, long.MaxValue, 3)
            .Single();

        Assert.Equal(0, only.Runs);
        Assert.Null(only.DaysToAbsorb);
    }

    [Fact]
    public void AllocationCostsMorePerRunAsItGoesDeeper()
    {
        // Sanity on the greedy assumption: marginal cost only ever rises, which is what makes
        // taking the best next run at each step the right answer rather than a heuristic.
        var winner = Competing.MaxBy(SingleRunProfit)!;

        var one = ConversionAllocation
            .Allocate([Catalog[winner]], Books, MarketTax.Standard, long.MaxValue, 1)
            .Single();

        var two = ConversionAllocation
            .Allocate([Catalog[winner]], Books, MarketTax.Standard, long.MaxValue, 2)
            .Single();

        Assert.Equal(1, one.Runs);
        Assert.Equal(2, two.Runs);
        Assert.True(two.GilOutlay - one.GilOutlay > one.GilOutlay, "the second run should cost more than the first");
    }
}
