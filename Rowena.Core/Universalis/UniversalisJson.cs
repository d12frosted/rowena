using System.Text.Json;
using Rowena.Core.Market;

namespace Rowena.Core.Universalis;

/// <summary>
/// Turns Universalis responses into order books.
/// </summary>
/// <remarks>
/// Kept as a pure function over a string so the tests can run against recorded responses
/// and never touch the network. Parsing is also where a remote service's schema drift
/// shows up first, and that is much easier to see in a failing unit test than in a plugin.
/// </remarks>
public static class UniversalisJson
{
    /// <summary>Parses a single-item response, the <c>/api/v2/{scope}/{itemId}</c> shape.</summary>
    /// <param name="requested">
    /// How many listings were asked for, so a response holding exactly that many can be marked
    /// as possibly cut off. Zero to claim nothing either way.
    /// </param>
    public static OrderBook ParseItem(string json, int requested = 0)
    {
        using var document = JsonDocument.Parse(json);
        return ReadItem(document.RootElement, requested);
    }

    /// <summary>
    /// Parses a multi-item response, the <c>/api/v2/{scope}/{id},{id}</c> shape.
    /// </summary>
    /// <remarks>
    /// Items Universalis reports as unresolved are simply absent from the result. That is
    /// not an error worth throwing over: it usually means the item is untradable, which is
    /// a fact about the item and something the caller needs to handle anyway.
    /// </remarks>
    /// <inheritdoc cref="ParseItem"/>
    public static IReadOnlyDictionary<uint, OrderBook> ParseItems(string json, int requested = 0)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // A request for one id comes back in the single-item shape even from the
        // comma-separated endpoint, so accept both here rather than at the call site.
        if (!root.TryGetProperty("items", out var items))
        {
            var single = ReadItem(root, requested);
            return new Dictionary<uint, OrderBook> { [single.ItemId] = single };
        }

        var books = new Dictionary<uint, OrderBook>();
        foreach (var entry in items.EnumerateObject())
        {
            var book = ReadItem(entry.Value, requested);
            books[book.ItemId] = book;
        }

        return books;
    }

    /// <summary>
    /// Parses the aggregated endpoint, which summarises many items cheaply.
    /// </summary>
    /// <remarks>
    /// Every figure is nested under a scope, and which ones are present depends on what was asked
    /// for: a world request carries world, dc and region, while a data-centre request carries only
    /// dc and region. So the narrowest one present is the one that answers the question actually
    /// asked, and reading a fixed branch silently returns someone else's board.
    ///
    /// That is not hypothetical. Reading dc unconditionally made a Shiva survey report Light's
    /// numbers: 49,900 and 128 sales a day where the world itself had 56,997 and 10.
    ///
    /// Region is never read. It spans worlds you cannot list on, so a floor from one of those is an
    /// opportunity that does not exist.
    /// </remarks>
    public static IReadOnlyDictionary<uint, MarketSummary> ParseSurvey(string json)
    {
        using var document = JsonDocument.Parse(json);

        var summaries = new Dictionary<uint, MarketSummary>();

        if (!document.RootElement.TryGetProperty("results", out var results))
            return summaries;

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("itemId", out var id))
                continue;

            var itemId = id.GetUInt32();
            var quality = result.TryGetProperty("nq", out var nq) ? nq : default;

            summaries[itemId] = new MarketSummary(
                itemId,
                Narrowest(quality, "minListing", "price") is { } floor ? (long)floor : null,
                Narrowest(quality, "dailySaleVelocity", "quantity") ?? 0d);
        }

        return summaries;
    }

    /// <summary>
    /// Reads a figure from the tightest scope the answer carries: the world if it is there, the data
    /// centre otherwise.
    /// </summary>
    private static double? Narrowest(JsonElement quality, string section, string field)
    {
        if (quality.ValueKind != JsonValueKind.Object || !quality.TryGetProperty(section, out var scoped))
            return null;

        return Read(scoped, "world", field) ?? Read(scoped, "dc", field);
    }

    private static double? Read(JsonElement scoped, string branch, string field) =>
        scoped.TryGetProperty(branch, out var value)
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var found)
        && found.ValueKind == JsonValueKind.Number
            ? found.GetDouble()
            : null;

    private static OrderBook ReadItem(JsonElement item, int requested)
    {
        var itemId = item.TryGetProperty("itemID", out var id) ? id.GetUInt32() : 0u;

        var listings = new List<Listing>();
        if (item.TryGetProperty("listings", out var listingArray))
        {
            foreach (var listing in listingArray.EnumerateArray())
            {
                listings.Add(new Listing(
                    listing.GetProperty("pricePerUnit").GetInt64(),
                    listing.GetProperty("quantity").GetInt32(),
                    listing.TryGetProperty("worldName", out var world) ? world.GetString() ?? "" : ""));
            }
        }

        // Not the velocity this library means. The listings endpoint counts sales, and a sale is
        // a listing bought however many units were in it: for an ore that trades in tens it reads
        // about a tenth of what the board actually absorbs. Everything here compares a velocity
        // against units listed or units to sell, so taking this one would be a tenfold error in
        // a number that looks perfectly reasonable. The summary endpoint reports units a day and
        // the cache imposes it; until it has, a book says it does not know rather than guessing.
        const double velocity = 0d;

        // Prefer the upload time over the time we happened to parse it. How old the data
        // is matters, and the answer is "when a player last saw this board", not "now".
        var retrieved = item.TryGetProperty("lastUploadTime", out var uploaded) && uploaded.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(uploaded.GetInt64())
            : default;

        // listingsCount and unitsForSale are counted from what was returned, not from the board,
        // so neither can reveal a cut-off. Landing exactly on the limit is the only signal there
        // is, and it errs towards doubt, which is the safe direction.
        var complete = requested <= 0 || listings.Count < requested;

        // What it actually changed hands for, which is the only evidence that a listed price
        // is one anybody pays.
        var sales = new List<long>();

        if (item.TryGetProperty("recentHistory", out var history))
        {
            foreach (var sale in history.EnumerateArray())
            {
                if (sale.TryGetProperty("pricePerUnit", out var paid) && paid.ValueKind == JsonValueKind.Number)
                    sales.Add(paid.GetInt64());
            }
        }

        return OrderBook
            .Create(itemId, listings, velocity, retrieved, complete, MarketSource.Universalis, sales)
            .WithoutRate();
    }
}
