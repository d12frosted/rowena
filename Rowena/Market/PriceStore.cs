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
internal sealed record StoredBook(uint I, long T, double V, long[][] L);

internal sealed record StoredSweep(long At, int Candidates, string[] Shortlist);

/// <summary>One item's cheap summary as it goes to disk.</summary>
internal sealed record StoredSummary(uint I, long T, long F, double V);

internal sealed record StoredPrices(
    int Version,
    string Scope,
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
/// The scope is stored with it. Prices from Light say nothing useful about Chaos, so data saved
/// under a different one is discarded rather than quietly believed.
/// </remarks>
internal sealed class PriceStore(string path, IPluginLog log)
{
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public void Save(
        string scope,
        IEnumerable<(uint ItemId, OrderBook Book, DateTimeOffset Fetched)> books,
        IEnumerable<(MarketSummary Summary, DateTimeOffset Fetched)> summaries,
        StoredSweep? sweep)
    {
        try
        {
            var stored = new StoredPrices(
                CurrentVersion,
                scope,
                [
                    .. books.Select(entry => new StoredBook(
                        entry.ItemId,
                        entry.Fetched.ToUnixTimeMilliseconds(),
                        entry.Book.SaleVelocityPerDay,
                        [.. entry.Book.Listings.Select(listing => new[] { listing.UnitPrice, listing.Quantity })])),
                ],
                [
                    .. summaries.Select(entry => new StoredSummary(
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
    /// <param name="scope">Only data saved under this scope is returned.</param>
    /// <param name="maxAge">Books older than this are dropped as they are read.</param>
    public (
        List<(uint ItemId, OrderBook Book, DateTimeOffset Fetched)> Books,
        List<(MarketSummary Summary, DateTimeOffset Fetched)> Summaries,
        StoredSweep? Sweep)? Load(string scope, TimeSpan maxAge)
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

            if (!string.Equals(stored.Scope, scope, StringComparison.OrdinalIgnoreCase))
            {
                log.Information($"Stored prices are for {stored.Scope}, not {scope}; ignoring them.");
                return null;
            }

            var cutoff = DateTimeOffset.UtcNow - maxAge;
            var books = new List<(uint, OrderBook, DateTimeOffset)>();

            foreach (var book in stored.Books ?? [])
            {
                var fetched = DateTimeOffset.FromUnixTimeMilliseconds(book.T);
                if (fetched < cutoff)
                    continue;

                books.Add((
                    book.I,
                    OrderBook.Create(
                        book.I,
                        (book.L ?? []).Where(pair => pair.Length >= 2)
                            .Select(pair => new Listing(pair[0], (int)pair[1], "")),
                        book.V,
                        fetched),
                    fetched));
            }

            var summaries = new List<(MarketSummary, DateTimeOffset)>();

            foreach (var summary in stored.Summaries ?? [])
            {
                var fetched = DateTimeOffset.FromUnixTimeMilliseconds(summary.T);
                if (fetched < cutoff)
                    continue;

                // A floor of -1 is how "nothing listed" survives a round trip, since the field is
                // nullable in memory and a number on disk.
                summaries.Add((
                    new MarketSummary(summary.I, summary.F < 0 ? null : summary.F, summary.V),
                    fetched));
            }

            log.Information($"Restored {books.Count} books and {summaries.Count} summaries for {scope}.");
            return (books, summaries, stored.Sweep);
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not read stored prices.");
            return null;
        }
    }
}
