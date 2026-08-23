namespace Rowena.Core.Market;

/// <summary>One of my listings, with the retainer it sits on.</summary>
/// <param name="CityId">Where the retainer stands, which is what decides the seller's tax.</param>
public readonly record struct RetainerListing(
    ulong RetainerId,
    string Retainer,
    uint CityId,
    uint ItemId,
    long UnitPrice,
    int Quantity,
    bool IsHq);

/// <summary>A retainer's market slots as they stood when it was last opened.</summary>
public sealed record SeenRetainer(
    ulong RetainerId,
    string Name,
    uint CityId,
    DateTimeOffset SeenAt,
    IReadOnlyList<MarketSlot> Slots);

/// <summary>What one look at the board said about my listings of one item.</summary>
/// <param name="Mine">Every listing of mine in that view, across all my retainers. Empty means none were.</param>
public sealed record BoardSighting(uint ItemId, DateTimeOffset SeenAt, IReadOnlyList<RetainerListing> Mine);

/// <summary>
/// What I have listed, from the two places the game says so.
/// </summary>
/// <remarks>
/// A retainer's market slots are the complete answer for that retainer, every item at once,
/// but only as of the moment it was opened. A board view is the complete answer for that item,
/// every retainer at once, but only for that item. Each is stale in the direction the other is
/// fresh, so the newer one wins, per retainer and per item: a board view of an item taken
/// after a retainer was last opened speaks for that retainer's listings of that item, and
/// nothing else.
/// </remarks>
public static class KnownListings
{
    public static IReadOnlyList<RetainerListing> Merge(
        IReadOnlyList<SeenRetainer> retainers,
        IReadOnlyList<BoardSighting> sightings)
    {
        var byItem = sightings
            .GroupBy(sighting => sighting.ItemId)
            .ToDictionary(group => group.Key, group => group.MaxBy(sighting => sighting.SeenAt)!);

        var listings = new List<RetainerListing>();
        var seenAt = retainers.ToDictionary(retainer => retainer.RetainerId, retainer => retainer.SeenAt);

        foreach (var retainer in retainers)
        {
            foreach (var slot in retainer.Slots)
            {
                if (slot.ItemId == 0 || slot.Quantity <= 0)
                    continue;

                if (byItem.TryGetValue(slot.ItemId, out var sighting) && sighting.SeenAt > retainer.SeenAt)
                    continue;

                listings.Add(new RetainerListing(
                    retainer.RetainerId,
                    retainer.Name,
                    retainer.CityId,
                    slot.ItemId,
                    slot.UnitPrice,
                    slot.Quantity,
                    slot.IsHq));
            }
        }

        foreach (var sighting in byItem.Values)
        {
            foreach (var listing in sighting.Mine)
            {
                // A retainer opened since this view was taken has said something newer.
                if (seenAt.TryGetValue(listing.RetainerId, out var opened) && opened >= sighting.SeenAt)
                    continue;

                listings.Add(listing);
            }
        }

        return listings;
    }
}
