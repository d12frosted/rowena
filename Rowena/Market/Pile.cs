using Rowena.Core.Market;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>One stack of mine, priced, with the call on what to do with it.</summary>
public readonly record struct PileStack(uint ItemId, int Quantity, HoardVerdict Verdict);

/// <summary>What a set of holdings comes to, and what is still unanswered about it.</summary>
/// <param name="Unpriced">
/// Things the board trades that nothing has a price for yet. Reported rather than counted as
/// worthless, and asked about in the background.
/// </param>
public sealed record PileReading(IReadOnlyList<PileStack> Stacks, IReadOnlyList<uint> Unpriced);

/// <summary>
/// What a pile of things is worth and what to do with it.
/// </summary>
/// <remarks>
/// Two surfaces ask this. The Bags tab asks it of everything I own, to rank the lot; the
/// retainer overlay asks it of what is within reach at the bell, which is my bags and the
/// pages of the retainer standing in front of me. Same question, different pile, and they had
/// better give the same answer about the same stack: a tab calling something junk while the
/// panel at the bell offers to list it would be worse than either answer alone.
///
/// Priced from the cheap summaries where no book has been fetched. A bag of two hundred stacks
/// would be two hundred deep fetches for a question that only needs to know roughly what a
/// thing is worth and whether it moves, and the summary sweep has already been over most of
/// the game.
/// </remarks>
internal sealed class Pile(
    Boards boards,
    MarketCache market,
    Configuration config,
    Items items,
    Keeping keeping,
    Unlocks unlocks)
{
    /// <summary>Items already asked about, so a rebuild twice a second is not a request twice a second.</summary>
    private readonly HashSet<uint> asked = [];

    /// <summary>
    /// Prices a set of holdings and calls each stack.
    /// </summary>
    /// <param name="holdings">How many of each item the pile holds.</param>
    /// <param name="wanted">What the craft table wants, which is not surplus whatever it fetches.</param>
    /// <param name="rate">
    /// The city rate to net at. The bell knows which retainer is standing there and so knows its
    /// city exactly; everywhere else the worst of my retainers' rates is the honest guess.
    /// </param>
    public PileReading Read(
        IReadOnlyDictionary<uint, int> holdings,
        IReadOnlySet<uint> wanted,
        MarketTax? rate = null)
    {
        if (boards.Scope.Selling is not { } selling)
            return new PileReading([], []);

        var tax = rate ?? boards.Tax;
        var stacks = new List<PileStack>();
        var missing = new List<uint>();

        foreach (var (itemId, quantity) in holdings)
        {
            if (quantity <= 0)
                continue;

            var vendor = boards.Vendor(itemId);

            // A full book when one has been fetched, the cheap summary otherwise. The book is
            // the better answer and is what the background fetch actually stores, so asking
            // only the summaries meant the stacks this had just asked about stayed unknown
            // however long it waited.
            var priced = Priced(selling, itemId);

            // No summary for something the board trades is an unanswered question, not a board
            // price of nothing. Treated as nothing it would read as a confident "vendor it" for
            // every stack the sweep has not reached, which is the one mistake this whole plugin
            // exists to avoid: a missing number is not a small number.
            if (priced is null && boards.Marketable(itemId))
            {
                missing.Add(itemId);
                continue;
            }

            if (priced is null && vendor <= 0)
                continue;

            var verdict = Liquidation.Of(
                quantity,
                priced?.Floor,
                priced?.SalesPerDay ?? 0d,
                vendor,
                tax,
                config.SellingHorizon(),
                config.SlotFloor,
                items.StackSize(itemId),
                Kept(itemId, wanted));

            if (verdict.Call == HoardCall.Worthless)
                continue;

            stacks.Add(new PileStack(itemId, quantity, verdict));
        }

        // The big survey runs against the board you buy on, and a bag is priced against the one
        // you sell on, so a handful of stacks are usually unknown here even after it. Asked for
        // once each rather than on every rebuild, at the priority that yields to anything a
        // person is waiting on.
        if (missing.Except(asked).ToArray() is { Length: > 0 } fresh)
        {
            asked.UnionWith(fresh);
            market.RefreshInBackground(selling, fresh, false, FetchPriority.Background);
        }

        return new PileReading([.. stacks], [.. missing]);
    }

    /// <summary>What to ask for a stack that is not listed yet, when the board has an opinion.</summary>
    public long? Ask(uint itemId, bool hq = false) => Undercut.Fresh(boards.Selling(itemId), config.UndercutBy, hq);

    /// <summary>
    /// Why a stack is not for sale, if it is not.
    /// </summary>
    /// <remarks>
    /// My own word first, because it is the only one of the three that was given deliberately:
    /// if I have said a thing is mine, nothing the sheets or the craft list say should talk me
    /// out of it. The game's word comes next, since it is the one that cannot be recovered from
    /// by earning the gil back.
    /// </remarks>
    private KeepWhy Kept(uint itemId, IReadOnlySet<uint> wanted) =>
        keeping.Mine(itemId) ? KeepWhy.Mine
            : unlocks.Learned(itemId) is false ? KeepWhy.Unlearned
                : wanted.Contains(itemId) ? KeepWhy.Wanted
                    : KeepWhy.Surplus;

    /// <summary>What a stack fetches and how fast, from whichever source has an answer.</summary>
    private readonly record struct Priceable(long? Floor, double SalesPerDay);

    /// <summary>
    /// The best price on hand, without asking for a new one.
    /// </summary>
    /// <remarks>
    /// A book refuses a floor no recent sale supports, which matters more here than anywhere:
    /// this is telling somebody what their own things are worth, and a fantasy listing would
    /// inflate a pile they might then decide to keep.
    ///
    /// A book that does not yet know how fast it moves is no use here either, whatever its
    /// floor: the whole verdict turns on whether the board will take the stack. Falling back to
    /// the summary covers it, and failing that the stack waits rather than being called dead
    /// and sent to a vendor.
    /// </remarks>
    private Priceable? Priced(string selling, uint itemId) =>
        boards.Selling(itemId) is { RateKnown: true } book
            ? new Priceable(book.CredibleFloor(), book.SaleVelocityPerDay)
            : market.Summary(selling, itemId) is { } summary
                ? new Priceable(summary.Floor, summary.SaleVelocityPerDay)
                : null;
}
