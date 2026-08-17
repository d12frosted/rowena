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
/// <param name="scope">
/// Asked again on every fetch, never captured.
/// </param>
public sealed class UniversalisClient(HttpClient http, Func<string?> scope, int listings = 40) : IMarketDataSource
{
    /// <summary>
    /// A world, data centre or region name, e.g. "Shiva", "Light", "Europe".
    /// </summary>
    /// <remarks>
    /// Resolved per call rather than held, and that is not a style preference. Holding it meant
    /// a plugin reloaded mid-session never learned where it was, because the login it was
    /// waiting on had already happened, and every request went to a URL with an empty path
    /// segment and came back 404. One live answer, asked for when needed.
    /// </remarks>
    public string? Scope => scope();

    public async Task<IReadOnlyDictionary<uint, OrderBook>> FetchAsync(
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
            return new Dictionary<uint, OrderBook>();

        var where = Scope;
        if (string.IsNullOrWhiteSpace(where))
        {
            // Said plainly, because the alternative is a 404 with a stack trace for what is
            // really just "I do not know which board you mean yet".
            throw new InvalidOperationException(
                "No world or data centre to price against yet. Log in, or set one explicitly.");
        }

        var ids = string.Join(',', itemIds);
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(where)}/{ids}?listings={listings}";

        var json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return UniversalisJson.ParseItems(json);
    }
}
