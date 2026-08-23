using Rowena.Core.Market;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>
/// How much of the ceiling I actually reach.
/// </summary>
/// <remarks>
/// Every ranking in this plugin is in gil a day and every one of them says the figure is a
/// ceiling that assumes taking every sale at today's price. That is honest, it is not a
/// forecast, and the gap was never measured.
///
/// Now it can be. What each board turns over is known and what I sold is recorded, so the
/// share is a measurement. It is deliberately one number rather than one per item: a share
/// worked out from the handful of sales any single item has is noise, and what a ranking needs
/// is a scale factor it can apply to rows it has never sold anything of.
/// </remarks>
internal sealed class Realised(SalesLog sales, Boards boards, MarketCache market, Configuration config)
{
    /// <summary>
    /// How many sales are wanted before quoting a share.
    /// </summary>
    /// <remarks>
    /// Twenty. A ratio off three sales is not a measurement, and a bad measurement is worse
    /// than the ceiling it replaces because a ceiling announces itself.
    /// </remarks>
    private const int Enough = 20;

    /// <summary>
    /// How much of what I sold must have a rate before the share means anything.
    /// </summary>
    /// <remarks>
    /// Half, not most. Some items never get a rate at all: Universalis reports one per world,
    /// per data centre and per region, and it has none of the first two for some things and
    /// nothing whatever for others. Holding out for near-total coverage would mean never
    /// answering, so the bar sits where the answer stops being drawn from a handful of items,
    /// and the coverage is quoted beside it rather than hidden.
    /// </remarks>
    private const double MinCoverage = 0.5d;

    /// <summary>How far back to look. The same horizon the rest of the plugin plans over.</summary>
    private int Days => Math.Max(1, config.SellingHorizon());

    /// <summary>The share of a market my sales come to, or null when too little is recorded.</summary>
    public double? Share
    {
        get
        {
            var since = DateTimeOffset.UtcNow.AddDays(-Days);

            var observed = sales.Since(since)
                .GroupBy(sale => sale.ItemId)
                .Select(byItem => new Captured(
                    byItem.Key,
                    byItem.Sum(sale => sale.Quantity),
                    boards.Selling(byItem.Key) is { RateKnown: true } book ? book.SaleVelocityPerDay : 0d,
                    Days));

            var seen = observed.ToArray();

            // Ask for what is missing. Nothing else will: a book fetch carries no usable rate,
            // and an item sold out of is never fetched again, so waiting would wait forever.
            if (seen.Where(one => one.MarketPerDay <= 0).Select(one => one.ItemId).ToArray() is { Length: > 0 } gaps)
                market.SurveyInBackground(boards.Scope.Selling, gaps);

            return CaptureRate.Of(seen, Enough, MinCoverage);
        }
    }

    /// <summary>Item by item, for checking the share against the boards by hand.</summary>
    public string Detail =>
        string.Join(
            "; ",
            sales.Since(DateTimeOffset.UtcNow.AddDays(-Days))
                .GroupBy(sale => sale.ItemId)
                .OrderByDescending(byItem => byItem.Sum(sale => sale.Quantity))
                .Select(byItem =>
                {
                    var book = boards.Selling(byItem.Key);
                    var rate = book is { RateKnown: true } ? $"{book.SaleVelocityPerDay:F1}/day" : "no rate";
                    return $"{byItem.Key}:{byItem.Sum(sale => sale.Quantity)}@{rate}";
                }));

    /// <summary>
    /// How many of my own sales the share rests on.
    /// </summary>
    /// <remarks>
    /// Only the ones with a rate to weigh against, since the rest contribute nothing and
    /// counting them would overstate how much the measurement knows.
    /// </remarks>
    public int Seen =>
        sales.Since(DateTimeOffset.UtcNow.AddDays(-Days))
            .Where(sale => boards.Selling(sale.ItemId) is { RateKnown: true, SaleVelocityPerDay: > 0 })
            .Sum(sale => sale.Quantity);

    /// <summary>What share of my recent sales the measurement could weigh at all.</summary>
    public double Coverage
    {
        get
        {
            var window = sales.Since(DateTimeOffset.UtcNow.AddDays(-Days)).Sum(sale => sale.Quantity);
            return window > 0 ? (double)Seen / window : 0d;
        }
    }

    /// <summary>Sales still wanted before a share is worth quoting.</summary>
    public int Wanted => Math.Max(0, Enough - Seen);

    /// <summary>
    /// Why there is no share yet, in a phrase, or null when there is one.
    /// </summary>
    /// <remarks>
    /// Two different shortfalls and they want different words. Saying nothing more is wanted
    /// while still refusing to answer reads as a fault rather than as waiting for prices.
    /// </remarks>
    public string? Missing
    {
        get
        {
            if (Share is not null)
                return null;

            var window = sales.Since(DateTimeOffset.UtcNow.AddDays(-Days)).Sum(sale => sale.Quantity);
            var unpriced = window - Seen;

            if (Wanted > 0 && unpriced == 0)
                return $"{Wanted} more sales";

            return unpriced > 0
                ? $"{unpriced} of your {window} recent sales have no sale rate yet"
                : "not enough sales with a rate to weigh them against";
        }
    }

    /// <summary>
    /// A ceiling brought down to what I have actually been managing.
    /// </summary>
    /// <remarks>
    /// Returns the ceiling untouched when there is no measurement, so a caller never has to
    /// decide what to do about not knowing.
    /// </remarks>
    public long Expect(long ceiling) => Share is { } share ? (long)(ceiling * share) : ceiling;
}
