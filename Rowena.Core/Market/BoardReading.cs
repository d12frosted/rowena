namespace Rowena.Core.Market;

/// <summary>One offer as the board's own packets carry it: a price, a stack, a quality.</summary>
/// <remarks>
/// No world, because the packet does not say one; the reading stamps every offer with the
/// world the request was made from, which is the fact that makes the packets usable at all.
/// </remarks>
public readonly record struct BoardOffer(long UnitPrice, int Quantity, bool IsHq);

/// <summary>
/// One item's board view, assembled from the pages the server sends.
/// </summary>
/// <remarks>
/// Asking the game about an item produces the answer in pages of ten, cheapest first, and
/// the last page announces itself only by being short. This gathers them into the same
/// <see cref="OrderBook"/> a Universalis response becomes, so everything downstream reads
/// exact data and crowdsourced data with one pair of eyes.
///
/// A page of ten might be the whole board or the first tenth of it, and the server does not
/// say which: <see cref="Ended"/> stays false and whether to stop waiting is the caller's
/// clock. A hundred listings is the most a request returns, so a reading that gets there is
/// treated as cut off, the same doubt a Universalis response earns by landing on its limit.
///
/// Sales come from the history packet the same request triggers, per-unit and newest first,
/// which is the shape <see cref="OrderBook.RecentSales"/> already speaks. What no packet
/// carries is a sale rate, so the book says it does not know and the summary imposes one
/// afterwards, exactly as it does on every other book.
/// </remarks>
public sealed class BoardReading(uint itemId, string world)
{
    /// <summary>How many listings one packet holds; a shorter one is the last.</summary>
    public const int PageSize = 10;

    /// <summary>The most listings one request returns. Landing here means a cut-off.</summary>
    public const int MostListings = 100;

    private readonly List<Listing> listings = [];
    private IReadOnlyList<Sale> sales = [];

    /// <summary>Whether the pages have said all they are going to.</summary>
    public bool Ended { get; private set; }

    /// <summary>Whether the history packet has arrived, an empty one included.</summary>
    public bool SalesSeen { get; private set; }

    /// <summary>
    /// Takes one page in.
    /// </summary>
    /// <remarks>
    /// A page arriving after the end is not a straggler, it is somebody else's request for
    /// the same item, and folding it in would double every listing it repeats.
    /// </remarks>
    public void Add(IReadOnlyList<BoardOffer> page)
    {
        if (Ended)
            return;

        foreach (var offer in page)
            listings.Add(new Listing(offer.UnitPrice, offer.Quantity, world, offer.IsHq));

        if (page.Count < PageSize || listings.Count >= MostListings)
            Ended = true;
    }

    /// <summary>Takes the sales in, per unit and newest first, as the history packet has them.</summary>
    public void Sales(IReadOnlyList<Sale> paid)
    {
        sales = paid;
        SalesSeen = true;
    }

    /// <summary>Everything gathered, as the book the rest of the plugin prices with.</summary>
    public OrderBook Book(DateTimeOffset retrieved) =>
        OrderBook
            .Create(
                itemId,
                listings,
                saleVelocityPerDay: 0d,
                retrieved,
                complete: listings.Count < MostListings,
                MarketSource.Game,
                sales)
            .WithoutRate();
}
