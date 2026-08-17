using Rowena.Core.Market;

namespace Rowena.Core.Universalis;

/// <summary>Somewhere order books come from.</summary>
/// <remarks>
/// An interface so the plugin can serve books out of a cache, and so tests can serve them
/// out of recorded files. Nothing above this line should care which.
/// </remarks>
public interface IMarketDataSource
{
    Task<IReadOnlyDictionary<uint, OrderBook>> FetchAsync(
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken = default);
}

/// <summary>Fetches order books from Universalis.</summary>
/// <remarks>
/// Universalis is a free, crowdsourced service and it asks callers to be reasonable: batch
/// ids into one request rather than looping, and cache. The listing limit is deliberately
/// generous by default because a shallow book is exactly what this library refuses to
/// reason from, and asking for ten listings would quietly reintroduce the problem.
/// </remarks>
public sealed class UniversalisClient(HttpClient http, string scope, int listings = 40) : IMarketDataSource
{
    /// <summary>
    /// A world, data centre or region name, e.g. "Shiva", "Light", "Europe".
    /// </summary>
    /// <remarks>
    /// Settable because the interesting question is sometimes about a neighbouring data
    /// centre. You can carry items home from one even though you cannot list there, so
    /// pricing a board you are not standing on is a real thing to want.
    /// </remarks>
    public string Scope { get; set; } = scope;

    public async Task<IReadOnlyDictionary<uint, OrderBook>> FetchAsync(
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
            return new Dictionary<uint, OrderBook>();

        var ids = string.Join(',', itemIds);
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(Scope)}/{ids}?listings={listings}";

        var json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return UniversalisJson.ParseItems(json);
    }
}
