using Rowena.Core.Market;

namespace Rowena.Core.Universalis;

/// <summary>Somewhere order books come from.</summary>
/// <remarks>
/// An interface so the plugin can serve books out of a cache, and so tests can serve them
/// out of recorded files. Nothing above this line should care which.
/// </remarks>
public interface IMarketDataSource
{
    /// <param name="scope">
    /// A world, data centre or region name, e.g. "Shiva", "Light", "Europe". Passed in rather
    /// than held, so that whoever knows the answer resolves it before the fetch starts.
    /// </param>
    Task<IReadOnlyDictionary<uint, OrderBook>> FetchAsync(
        string scope,
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken = default);
}

/// <summary>Fetches order books from Universalis.</summary>
/// <remarks>
/// Universalis is a free, crowdsourced service and it asks callers to be reasonable: batch
/// ids into one request rather than looping, and cache. The listing limit is deliberately
/// generous by default because a shallow book is exactly what this library refuses to
/// reason from, and asking for ten listings would quietly reintroduce the problem.
///
/// The scope is a parameter and not state, and that has been got wrong twice. Held on the
/// client, a plugin reloaded mid-session never learned where it was and every request 404'd.
/// Resolved lazily by the client, the lookup ran on the fetch thread, where reading the game's
/// object table throws. A parameter puts the question where it can actually be answered.
/// </remarks>
public sealed class UniversalisClient(HttpClient http, int listings = 40) : IMarketDataSource
{
    public async Task<IReadOnlyDictionary<uint, OrderBook>> FetchAsync(
        string scope,
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("No world or data centre to price against.", nameof(scope));

        if (itemIds.Count == 0)
            return new Dictionary<uint, OrderBook>();

        var ids = string.Join(',', itemIds);
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(scope)}/{ids}?listings={listings}";

        var json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return UniversalisJson.ParseItems(json);
    }
}
