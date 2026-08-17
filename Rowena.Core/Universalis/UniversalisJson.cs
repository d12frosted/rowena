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
    public static OrderBook ParseItem(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadItem(document.RootElement);
    }

    /// <summary>
    /// Parses a multi-item response, the <c>/api/v2/{scope}/{id},{id}</c> shape.
    /// </summary>
    /// <remarks>
    /// Items Universalis reports as unresolved are simply absent from the result. That is
    /// not an error worth throwing over: it usually means the item is untradable, which is
    /// a fact about the item and something the caller needs to handle anyway.
    /// </remarks>
    public static IReadOnlyDictionary<uint, OrderBook> ParseItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // A request for one id comes back in the single-item shape even from the
        // comma-separated endpoint, so accept both here rather than at the call site.
        if (!root.TryGetProperty("items", out var items))
        {
            var single = ReadItem(root);
            return new Dictionary<uint, OrderBook> { [single.ItemId] = single };
        }

        var books = new Dictionary<uint, OrderBook>();
        foreach (var entry in items.EnumerateObject())
        {
            var book = ReadItem(entry.Value);
            books[book.ItemId] = book;
        }

        return books;
    }

    /// <summary>
    /// Parses the aggregated endpoint, which summarises many items cheaply.
    /// </summary>
    /// <remarks>
    /// The shape nests every figure under a scope: a data-centre answer and a region-wide one. Only
    /// the data centre is read, because the region includes worlds you cannot list on, and a floor
    /// from one of those would look like an opportunity that does not exist.
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
                DataCentre(quality, "minListing", "price") is { } floor ? (long)floor : null,
                DataCentre(quality, "dailySaleVelocity", "quantity") ?? 0d);
        }

        return summaries;
    }

    /// <summary>Reads one figure out of the data-centre branch of a summary section.</summary>
    private static double? DataCentre(JsonElement quality, string section, string field)
    {
        if (quality.ValueKind != JsonValueKind.Object
            || !quality.TryGetProperty(section, out var scoped)
            || !scoped.TryGetProperty("dc", out var dc)
            || dc.ValueKind != JsonValueKind.Object
            || !dc.TryGetProperty(field, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.GetDouble();
    }

    private static OrderBook ReadItem(JsonElement item)
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

        var velocity = item.TryGetProperty("regularSaleVelocity", out var rate) && rate.ValueKind == JsonValueKind.Number
            ? rate.GetDouble()
            : 0d;

        // Prefer the upload time over the time we happened to parse it. How old the data
        // is matters, and the answer is "when a player last saw this board", not "now".
        var retrieved = item.TryGetProperty("lastUploadTime", out var uploaded) && uploaded.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(uploaded.GetInt64())
            : default;

        return OrderBook.Create(itemId, listings, velocity, retrieved);
    }
}
