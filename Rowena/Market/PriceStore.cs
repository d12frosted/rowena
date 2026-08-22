using System.IO.Compression;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Rowena.Core.Market;

namespace Rowena.Market;

/// <summary>One item's book as it goes to disk.</summary>
/// <remarks>
/// Deliberately terse. Names would triple the file for no benefit, and the world a listing sits
/// on is not used by anything that reads this back.
/// </remarks>
/// <param name="C">Whether the book was known to hold every listing there is.</param>
/// <param name="P">Where it came from, as <see cref="MarketSource"/>.</param>
/// <param name="W">The world each listing stands on, alongside L.</param>
/// <param name="H">What it recently sold for, which is how a fantasy floor is spotted.</param>
internal sealed record StoredBook(
    string S, uint I, long T, double V, long[][] L, bool C, int P, string[] W, long[] H);

internal sealed record StoredSweep(long At, int Candidates, string[] Shortlist);

/// <summary>One item's cheap summary as it goes to disk.</summary>
internal sealed record StoredSummary(string S, uint I, long T, long F, double V);

internal sealed record StoredPrices(
    int Version,
    StoredBook[] Books,
    StoredSummary[] Summaries,
    StoredSweep? Sweep);

/// <summary>
/// Keeps the swept prices across a reload.
/// </summary>
/// <remarks>
/// A sweep is minutes of deliberately slow requests, and a dev-mode reload threw all of it away.
/// With automatic reloading on, that happened every time the plugin was rebuilt.
///
/// Written gzipped because it is a few hundred kilobytes of digits that compress to almost
/// nothing, and beside the configuration rather than inside it: this is a cache, and losing it
/// should cost a sweep and never a setting.
///
/// Every entry carries the board it came from, because buying and selling happen on different ones
/// and both are held at once. Prices from Light say nothing useful about Chaos, and nothing about
/// what a retainer on Shiva can actually sell for, so they are never mixed.
/// </remarks>
internal sealed class PriceStore(string path, IPluginLog log)
{
    /// <remarks>
    /// Bumped whenever the shape changes. A file from an older version is discarded rather
    /// than guessed at: prices are cheap to fetch again and a wrong guess about, say, whether
    /// a book was complete would be believed for as long as the file lived.
    /// </remarks>
    private const int CurrentVersion = 4;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public void Save(
        IEnumerable<(string Scope, OrderBook Book, DateTimeOffset Fetched)> books,
        IEnumerable<(string Scope, MarketSummary Summary, DateTimeOffset Fetched)> summaries,
        StoredSweep? sweep)
    {
        try
        {
            var stored = new StoredPrices(
                CurrentVersion,
                [
                    .. books.Select(entry => new StoredBook(
                        entry.Scope,
                        entry.Book.ItemId,
                        entry.Fetched.ToUnixTimeMilliseconds(),
                        entry.Book.SaleVelocityPerDay,
                        [.. entry.Book.Listings.Select(listing => new[] { listing.UnitPrice, listing.Quantity })],
                        entry.Book.Complete,
                        (int)entry.Book.Source,
                        [.. entry.Book.Listings.Select(listing => listing.World)],
                        [.. entry.Book.RecentSales])),
                ],
                [
                    .. summaries.Select(entry => new StoredSummary(
                        entry.Scope,
                        entry.Summary.ItemId,
                        entry.Fetched.ToUnixTimeMilliseconds(),
                        entry.Summary.Floor ?? -1,
                        entry.Summary.SaleVelocityPerDay)),
                ],
                sweep);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal);
            JsonSerializer.Serialize(gzip, stored, Options);

            log.Information(
                $"Saved {stored.Books.Length} books and {stored.Summaries.Length} summaries "
                + $"to {Path.GetFileName(path)}.");
        }
        catch (Exception error)
        {
            // A cache that cannot be written is a slow next session, not a broken one.
            log.Warning(error, "Could not save prices.");
        }
    }

    /// <summary>
    /// Reads back what was saved, or null when there is nothing usable.
    /// </summary>
    /// <param name="maxAge">Entries older than this are dropped as they are read.</param>
    public (
        List<(string Scope, OrderBook Book, DateTimeOffset Fetched)> Books,
        List<(string Scope, MarketSummary Summary, DateTimeOffset Fetched)> Summaries,
        StoredSweep? Sweep)? Load(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);

            if (JsonSerializer.Deserialize<StoredPrices>(gzip, Options) is not { } stored)
                return null;

            if (stored.Version != CurrentVersion)
            {
                log.Information("Stored prices are from an older format; ignoring them.");
                return null;
            }

            var cutoff = DateTimeOffset.UtcNow - maxAge;
            var books = new List<(string, OrderBook, DateTimeOffset)>();

            foreach (var book in stored.Books ?? [])
            {
                var fetched = DateTimeOffset.FromUnixTimeMilliseconds(book.T);
                if (fetched < cutoff)
                    continue;

                var worlds = book.W ?? [];

                books.Add((
                    book.S ?? "",
                    OrderBook.Create(
                        book.I,
                        (book.L ?? [])
                            .Select((pair, index) => (pair, index))
                            .Where(entry => entry.pair.Length >= 2)
                            .Select(entry => new Listing(
                                entry.pair[0],
                                (int)entry.pair[1],
                                entry.index < worlds.Length ? worlds[entry.index] : "")),
                        book.V,
                        fetched,
                        book.C,
                        (MarketSource)book.P,
                        book.H ?? []),
                    fetched));
            }

            var summaries = new List<(string, MarketSummary, DateTimeOffset)>();

            foreach (var summary in stored.Summaries ?? [])
            {
                var fetched = DateTimeOffset.FromUnixTimeMilliseconds(summary.T);
                if (fetched < cutoff)
                    continue;

                // A floor of -1 is how "nothing listed" survives a round trip, since the field is
                // nullable in memory and a number on disk.
                summaries.Add((
                    summary.S ?? "",
                    new MarketSummary(summary.I, summary.F < 0 ? null : summary.F, summary.V),
                    fetched));
            }

            log.Information($"Restored {books.Count} books and {summaries.Count} summaries.");
            return (books, summaries, stored.Sweep);
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not read stored prices.");
            return null;
        }
    }
}
