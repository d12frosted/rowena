using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What to do with the pile in your bags.
/// </summary>
/// <remarks>
/// Materials accumulate faster than anybody decides about them, and the decision is dull
/// enough that the pile wins. It is not a hard question, only a repetitive one, and repetitive
/// is the thing a plugin is for: for each stack, does the board pay more than the vendor, will
/// the board take it in any reasonable time, and is it wanted for something anyway.
///
/// Priced from the cheap summaries rather than from full books. A bag of two hundred stacks
/// would be two hundred deep fetches for a question that only needs to know roughly what a
/// thing is worth and whether it moves, and the summary sweep has already been over most of
/// the game.
/// </remarks>
internal sealed class HoardTab
{
    private static readonly string?[] Help =
    [
        null,
        "How many you have, bags and saddlebags and retainers together.",
        "Where they are. A retainer's pages are only readable while it is open, so what a\n"
        + "retainer holds is remembered from the last look rather than asked for now.",
        "What one fetches on your world after the market's cut, and what a vendor hands over\n"
        + "for it. Whichever is larger is the one this counts.",
        "What the whole stack comes to at the better counter.",
        "How long the board would take to absorb the whole stack at the rate it has been\n"
        + "selling. Longer than your selling horizon is a storage problem rather than a listing.",
        null,
    ];

    private readonly Balances balances;
    /// <summary>A retainer's market slots. Twenty, always.</summary>
    private const int SlotsEach = 20;

    private readonly RetainerStock stock;
    private readonly BoardWatcher board;
    private readonly Boards boards;
    private readonly MarketCache market;
    private readonly ItemCells cells;
    private readonly Configuration config;
    private readonly Func<IReadOnlySet<uint>> wanted;

    private readonly Rebuilt<Model> model;
    private readonly HashSet<uint> asked = [];

    public HoardTab(
        Balances balances,
        RetainerStock stock,
        BoardWatcher board,
        Boards boards,
        MarketCache market,
        ItemCells cells,
        Configuration config,
        Diagnostics diagnostics,
        Func<IReadOnlySet<uint>> wanted)
    {
        this.balances = balances;
        this.stock = stock;
        this.board = board;
        this.boards = boards;
        this.market = market;
        this.cells = cells;
        this.config = config;
        this.wanted = wanted;

        model = new Rebuilt<Model>("hoard", Build, diagnostics);
    }

    /// <inheritdoc cref="ConvertTab.Warmers"/>
    public IReadOnlyList<Action> Warmers => [() => _ = model.Current];

    /// <summary>What the overview should say about the pile.</summary>
    public Note? Headline()
    {
        var current = model.Current;

        if (current.Rows.Length == 0 || current.Worth < 100_000)
            return null;

        return new Note(
            Note.Waiting,
            Palette.Plain,
            $"{current.Worth:N0} gil",
            "is sitting in your bags",
            $"{current.Rows.Length} stacks worth doing something with. "
            + $"{current.Rows.Count(row => row.Verdict.Call == HoardCall.Vendor)} are worth more to a vendor "
            + "than to the board.",
            MainWindow.Tab.Hoard);
    }

    /// <summary>What the table is claiming, for checking it against the board.</summary>
    public string Dump() =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                selling = boards.Scope.Selling,
                stacks = model.Current.Rows.Length,
                slots = model.Current.Plan.Count,
                slotsEarn = RetainerSlots.Earns(model.Current.Plan),
                plan = model.Current.Plan.Take(8).Select(pick => new
                {
                    name = cells.Name(pick.ItemId),
                    units = pick.Units,
                    realised = pick.Realised,
                    worth = pick.Worth,
                }),
                retainersKnown = stock.Seen.Known,
                worth = model.Current.Worth,
                unpriced = model.Current.Unpriced,
                rows = model.Current.Rows.Take(40).Select(row => new
                {
                    name = row.Name,
                    item = row.ItemId,
                    quantity = row.Quantity,
                    inBags = row.InBags,
                    inRetainers = row.InRetainers,
                    board = row.Verdict.EachOnBoard,
                    vendor = row.Verdict.EachAtVendor,
                    worth = row.Verdict.Worth,
                    days = row.Verdict.DaysToSell,
                    call = row.Verdict.Call.ToString(),
                }),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public void Draw(string selling)
    {
        var current = model.Current;

        ImGui.TextUnformatted($"What you are holding, and what to do with it on {selling}");
        DrawReach();

        if (current.Rows.Length == 0)
        {
            ImGui.TextColored(
                Palette.Dim,
                current.Unpriced > 0
                    ? $"\n    {current.Unpriced} stacks, none of them priced yet. The summary sweep covers most\n"
                      + "    of the game; press below to price what it has not."
                    : "\n    Nothing in your bags worth selling, which is a tidier answer than most.");
        }
        else
        {
            ImGui.TextColored(
                Palette.Good,
                $"    {current.Worth:N0} gil across {current.Rows.Length} stacks, best counter each.");
        }

        if (current.Unpriced > 0)
        {
            ImGui.TextColored(
                Palette.Dim,
                $"    {current.Unpriced} stacks are still being priced. They are asked for as they are found.");
        }

        if (current.Rows.Length > 0)
        {
            DrawPlan(current);
            DrawTable(current);
        }
    }

    private void DrawTable(Model current)
    {
        if (!ImGui.BeginTable("hoard", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("held", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("board / vendor", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("worth", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("clears in", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 90);
        Cell.Headers(Help);

        foreach (var row in current.Rows)
        {
            var verdict = row.Verdict;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            cells.Draw(row.Name, row.ItemId);

            ImGui.TableNextColumn();
            Cell.Right(Palette.Dim, $"{row.Quantity:N0}");

            ImGui.TableNextColumn();
            DrawWhere(row);

            ImGui.TableNextColumn();
            Cell.Right(
                verdict.Call == HoardCall.Vendor ? Palette.Plain : Palette.Dim,
                $"{verdict.EachOnBoard:N0} / {verdict.EachAtVendor:N0}");

            ImGui.TableNextColumn();
            Cell.Right(Palette.Good, $"{verdict.Worth:N0}");

            ImGui.TableNextColumn();
            Cell.Right(verdict.Slow ? Palette.Bad : Palette.Dim, Phrases.Absorb(verdict.DaysToSell));

            ImGui.TableNextColumn();
            DrawCall(verdict);
        }

        ImGui.EndTable();
    }

    private static void DrawCall(HoardVerdict verdict)
    {
        var (colour, label, why) = verdict.Call switch
        {
            HoardCall.List when verdict.Slow => (
                Palette.Plain, "list some",
                $"The board pays {verdict.EachOnBoard:N0} against the vendor's {verdict.EachAtVendor:N0}, but it\n"
                + "would take longer than your selling horizon to absorb this many. List what it\n"
                + "will take and the rest is a storage decision rather than a pricing one."),

            HoardCall.List => (
                Palette.Good, "list it",
                $"{verdict.EachOnBoard:N0} each after the city's cut, against {verdict.EachAtVendor:N0} from a vendor,\n"
                + "and the board gets through this many comfortably."),

            HoardCall.Vendor => (
                Palette.Plain, "vendor it",
                $"A vendor pays {verdict.EachAtVendor:N0} and the board would leave you {verdict.EachOnBoard:N0}.\n"
                + "No listing fee, no waiting, and nobody undercuts a vendor."),

            HoardCall.Keep => (
                Palette.Dim, "keep it",
                "Wanted for something the craft table thinks is worth making, so this is not\n"
                + "surplus. Nothing here will offer to sell the materials for the thing it just\n"
                + "recommended."),

            _ => (
                Palette.Dim, "nothing doing",
                "Nobody pays anything for this: no vendor price, and either nothing listed or\n"
                + "nothing selling. It is a bag slot rather than an asset."),
        };

        ImGui.TextColored(colour, label);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(why);
    }

    /// <summary>
    /// What to put in the slots there are.
    /// </summary>
    /// <remarks>
    /// A retainer has twenty market slots and there is always more worth selling than slots to
    /// sell it from. Ranked on what a stack is worth, the big slow piles take every slot and sit
    /// in them for months, so the slots are judged by what they turn over instead.
    /// </remarks>
    private void DrawPlan(Model current)
    {
        if (current.Plan is not { Count: > 0 } plan)
            return;

        ImGui.TextColored(
            Palette.Good,
            $"    Best use of {plan.Count} free {(plan.Count == 1 ? "slot" : "slots")}: "
            + $"{RetainerSlots.Earns(plan):N0} gil over {config.SellingHorizon()} days, "
            + $"starting with {cells.Name(plan[0].ItemId)}.");

        if (!ImGui.IsItemHovered())
            return;

        ImGui.SetTooltip(
            "A slot is a rate rather than a lump: what it earns is what sells while it holds\n"
            + "something. A stack worth a fortune that takes four months to clear earns a week of\n"
            + "a slot a fraction of its price, and a smaller one that goes by morning earns all of\n"
            + "it and then frees the slot.\n"
            + "\n"
            + string.Join(
                "\n",
                plan.Take(12).Select(pick =>
                    $"    {pick.Units}x {cells.Name(pick.ItemId)}: {pick.Realised:N0} of {pick.Worth:N0}")));
    }

    /// <summary>
    /// How much of the pile this can actually see.
    /// </summary>
    /// <remarks>
    /// Said plainly, because a total that quietly covers half of what I own is worse than one
    /// that admits what it is missing. A retainer is only readable while it is open, so until
    /// one has been opened it is not empty, it is unknown, and those are different answers.
    /// </remarks>
    private void DrawReach()
    {
        var (known, oldest) = stock.Seen;

        if (known == 0)
        {
            ImGui.TextColored(
                Palette.Dim,
                "    Bags and saddlebags only. Open a retainer once and what it holds is remembered\n"
                + "    after that, which is the only way any of this is readable.");

            return;
        }

        var age = oldest is { } at ? $", the oldest looked at {Phrases.Ago(DateTimeOffset.UtcNow - at)} ago" : "";

        ImGui.TextColored(
            Palette.Dim,
            $"    Bags, saddlebags and {known} {(known == 1 ? "retainer" : "retainers")}{age}.");
    }

    /// <summary>
    /// Whether it is to hand or out with a retainer.
    /// </summary>
    /// <remarks>
    /// Worth knowing before setting off. Something split across four retainers is four errands
    /// even when the row says it is worth a hundred thousand.
    /// </remarks>
    private void DrawWhere(Row row)
    {
        if (row.InRetainers == 0)
        {
            ImGui.TextColored(Palette.Dim, "bags");
            return;
        }

        var holders = stock.Where(row.ItemId);

        ImGui.TextColored(
            Palette.Plain,
            row.InBags > 0
                ? $"{row.InBags} here, {row.InRetainers} out"
                : holders.Count == 1 ? holders[0].Retainer : $"{holders.Count} retainers");

        if (!ImGui.IsItemHovered())
            return;

        var where = string.Join("\n", holders.Select(holder => $"    {holder.Quantity} with {holder.Retainer}"));

        ImGui.SetTooltip(
            (row.InBags > 0 ? $"    {row.InBags} in your bags\n" : "")
            + where
            + "\n\nWhat a retainer holds is remembered from the last time it was opened.");
    }

    /// <summary>Prices the bags off the cheap summaries.</summary>
    private Model Build()
    {
        if (boards.Scope.Selling is not { } selling)
            return new Model([], 0, 0, [], []);

        var tax = boards.Tax;
        var needed = wanted();
        var rows = new List<Row>();
        var missing = new List<uint>();

        var carried = balances.Carrying();
        var stored = stock.Held();

        foreach (var itemId in carried.Keys.Concat(stored.Keys).Distinct())
        {
            var inBags = carried.GetValueOrDefault(itemId);
            var inRetainers = stored.GetValueOrDefault(itemId);
            var quantity = inBags + inRetainers;

            var vendor = boards.Vendor(itemId);

            // A full book when one has been fetched, the cheap summary otherwise. The book is
            // the better answer and is what the background fetch actually stores, so asking
            // only the summaries meant the stacks this tab had just asked about stayed unknown
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
                needed.Contains(itemId));

            if (verdict.Call == HoardCall.Worthless)
                continue;

            rows.Add(new Row(itemId, cells.Name(itemId), quantity, inBags, inRetainers, verdict));
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

        // Twenty a retainer, less whatever is already out. Only what the board is the better
        // counter for competes for a slot: a vendor needs no slot and no waiting.
        var slots = Math.Max(0, stock.Seen.Known * SlotsEach - board.Known().Count);

        var plan = RetainerSlots.Fill(
            rows.Where(row => row.Verdict.Call == HoardCall.List)
                .Select(row => new SlotCandidate(
                    row.ItemId, row.Quantity, row.Verdict.Worth, row.Verdict.DaysToSell)),
            slots,
            config.SellingHorizon());

        return new Model(
            [.. rows.OrderByDescending(row => row.Verdict.Worth)],
            rows.Sum(row => row.Verdict.Call == HoardCall.Keep ? 0 : row.Verdict.Worth),
            missing.Count,
            [.. missing],
            plan);
    }

    /// <summary>What a stack fetches and how fast, from whichever source has an answer.</summary>
    private readonly record struct Priceable(long? Floor, double SalesPerDay);

    /// <summary>
    /// The best price on hand, without asking for a new one.
    /// </summary>
    /// <remarks>
    /// A book refuses a floor no recent sale supports, which matters more here than anywhere:
    /// this table is telling somebody what their own things are worth, and a fantasy listing
    /// would inflate a pile they might then decide to keep.
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

    private sealed record Row(
        uint ItemId,
        string Name,
        int Quantity,
        int InBags,
        int InRetainers,
        HoardVerdict Verdict);

    private sealed record Model(
        Row[] Rows,
        long Worth,
        int Unpriced,
        uint[] Missing,
        IReadOnlyList<SlotPick> Plan);
}
