using Dalamud.Bindings.ImGui;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What to make: the swept furnishing market, ranked, and the list you are building from it.
/// </summary>
/// <remarks>
/// Its own tab because it answers a question on a different clock to everything else. Choosing what
/// to craft needs a rough map of nine hundred furnishings, which costs minutes of requests and stays
/// useful for hours; the conversion tables want depth that is minutes old. Stacking the two meant one
/// screen with two ages on it and no way to tell which number belonged to which.
/// </remarks>
internal sealed class CraftTab
{
    /// <summary>How many craft rows the table shows. The count it was trimmed from is shown too.</summary>
    private const int CraftsInTable = 25;

    /// <remarks>
    /// One entry per column, null where the header says enough on its own. The gil/day caveat is on
    /// the header as well as on every cell, because it is the column the whole table is ranked by and
    /// it is a ceiling rather than a forecast.
    /// </remarks>
    private static readonly string?[] Help =
    [
        null,
        "The crafting job the recipe belongs to.",
        "What the direct materials cost, walked down the book rather than multiplied out from\n"
        + "the cheapest listing, with the board's 5% buyer's cut included. Sub-crafts are\n"
        + "priced as bought, not as made.",
        "Gil left over from one craft, once it sells and the market has taken its cut.",
        "Profit over what the materials cost.",
        "How many the board is currently selling in a day, on your world rather than the whole\n"
        + "data centre: your retainer sells where it stands.",
        "Profit times sales a day, which is what the table is ranked by. A ceiling, not a\n"
        + "forecast: it assumes you take every sale at today's price.",
        null,
    ];

    private readonly FurnishingSweep sweep;
    private readonly Furnishings furnishings;
    private readonly Boards boards;
    private readonly ItemCells cells;
    private readonly CraftBasket basket;
    private readonly Configuration config;
    private readonly Action<Conversion> refreshTrade;
    private readonly Action recheck;

    private readonly Rebuilt<Model> model;

    private Column sortColumn = Column.GilPerDay;
    private bool sortDescending = true;

    public CraftTab(
        FurnishingSweep sweep,
        Furnishings furnishings,
        Boards boards,
        ItemCells cells,
        CraftBasket basket,
        Configuration config,
        Diagnostics diagnostics,
        Action<Conversion> refreshTrade,
        Action recheck)
    {
        this.recheck = recheck;
        this.sweep = sweep;
        this.furnishings = furnishings;
        this.boards = boards;
        this.cells = cells;
        this.basket = basket;
        this.config = config;
        this.refreshTrade = refreshTrade;

        model = new Rebuilt<Model>("crafts", Build, diagnostics);
    }

    /// <summary>
    /// The tab's own label, carrying the size of the list you are building.
    /// </summary>
    /// <remarks>
    /// A list left half-built is the one thing in here you can forget about, since the crafting
    /// happens in another plugin's window. The count says so from outside the tab; the id after ###
    /// keeps the tab the same tab as it changes.
    /// </remarks>
    public string Label => basket.Count == 0 ? "Craft###craft" : $"Craft ({basket.Count})###craft";

    /// <summary>What the ranking is claiming, for checking it against the board.</summary>
    public string Dump() =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                buying = boards.Scope.Buying,
                selling = boards.Scope.Selling,
                buyerRate = boards.Tax.BuyerRate,
                sellerRate = boards.Tax.SellerRate,
                crafts = model.Current.Crafts.Take(12).Select(row => new
                {
                    name = row.Item,
                    item = row.ItemId,
                    materials = row.Materials,
                    profit = row.Profit,
                    floor = boards.Selling(row.ItemId)?.Floor ?? 0,
                    credible = boards.Selling(row.ItemId)?.CredibleFloor(),
                    salesPerDay = row.SalesPerDay,
                    gilPerDay = row.GilPerDay,
                    inputs = row.Breakdown.Select(line => new
                    {
                        item = line.ItemId,
                        quantity = line.Quantity,
                        cost = line.Cost,
                        sourced = line.Sourced,
                    }),
                }),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>What the overview should say about crafting.</summary>
    public Note? Headline()
    {
        if (model.Current.Crafts is not { Length: > 0 } crafts)
            return null;

        var best = crafts[0];

        return new Note(
            Note.Waiting,
            Palette.Plain,
            $"{best.Item} is the best thing to make: {best.Profit:N0} a run",
            $"{best.Materials:N0} in materials, about {best.GilPerDay:N0} a day at what the board takes.",
            MainWindow.Tab.Craft);
    }

    /// <summary>
    /// Every material the current ranking wants, so nothing offers to sell them.
    /// </summary>
    /// <remarks>
    /// Reads the shortlist that is already built rather than costing anything again. Telling
    /// somebody to vendor the materials for the thing this tab just told them to make would be
    /// the worst advice in the plugin.
    /// </remarks>
    public IReadOnlySet<uint> Wants() =>
        model.Current.Crafts
            .SelectMany(craft => craft.Breakdown.Select(line => line.ItemId))
            .ToHashSet();

    /// <inheritdoc cref="ConvertTab.Warmers"/>
    public IReadOnlyList<Action> Warmers => [() => _ = model.Current];

    public void Draw(string buying, string selling)
    {
        DrawBasket();
        DrawSweep(buying, selling);
        DrawTable();
    }

    /// <summary>
    /// Crafts picked out but not yet handed over.
    /// </summary>
    /// <remarks>
    /// Collected rather than exported per click, which would leave a list per furnishing, and sent as
    /// a Teamcraft link rather than any one plugin's format, which is the only thing they all read.
    /// </remarks>
    private void DrawBasket()
    {
        if (basket.Count == 0)
            return;

        // The expanded total, because "3 items" and "41 crafts" are very different pieces of news
        // and the second is the one that decides whether you want to start.
        var steps = basket.Steps();
        var subCrafts = steps.Count(step => step.Depth > 0);

        ImGui.TextUnformatted(
            subCrafts == 0
                ? $"List: {basket.Count} items, {basket.TotalCrafts} crafts"
                : $"List: {basket.Count} items, {steps.Sum(step => step.Crafts)} crafts "
                  + $"including {subCrafts} sub-crafts");

        ImGui.SameLine();

        // Two routes because they cost very different amounts of effort and only one is portable.
        if (ImGui.Button("Copy for Artisan"))
            basket.CopyForArtisan();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "One paste, straight into the plugin that crafts. Sub-crafts included,\n"
                + "in order, so the list works from the top down.\n"
                + "\n"
                + "1. Press this.\n"
                + "2. Open Artisan, Crafting Lists tab.\n"
                + "3. Press \"Import List From Clipboard (Artisan Export)\".");
        }

        ImGui.SameLine();

        if (ImGui.Button("Open in Teamcraft"))
            basket.Open();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "The long way round, but it works out the sub-crafts and reaches any tool.\n"
                + "\n"
                + "1. Press this; the list opens on ffxivteamcraft.com.\n"
                + "2. In Artisan: Crafting Lists, then the Teamcraft \"Import\" button.\n"
                + "3. On the site, find the pre-crafts section, press \"Copy as Text\",\n"
                + "   and paste into Artisan's Pre-craft Items box.\n"
                + "4. Do the same for the final items section.\n"
                + "5. Name the list and press Import.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Copy link"))
            basket.CopyLink();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The same Teamcraft link, for pasting into a browser yourself.");

        ImGui.SameLine();

        if (ImGui.Button("Clear"))
            basket.Clear();

        ImGui.TextColored(Palette.Dim, "    Artisan is one paste. Teamcraft is five steps but reaches any tool.");

        uint? removing = null;

        foreach (var item in basket.Items)
        {
            ImGui.PushID((int)item.RecipeId);

            if (ImGui.SmallButton("-"))
                basket.Adjust(item.RecipeId, -1);

            ImGui.SameLine(0f, 2f);

            if (ImGui.SmallButton("+"))
                basket.Adjust(item.RecipeId, 1);

            ImGui.SameLine(0f, 6f);
            cells.Icon(item.ItemId, 16f);
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(Palette.Dim, $"{item.Quantity}x {item.Name}");
            ImGui.SameLine();

            if (ImGui.SmallButton("remove"))
                removing = item.RecipeId;

            ImGui.PopID();
        }

        // Removed after the loop rather than during it, since the list is what is being walked.
        if (removing is { } recipeId)
            basket.Remove(recipeId);

        ImGui.Separator();
    }

    private void DrawSweep(string buying, string selling)
    {
        ImGui.TextUnformatted("Furnishings, ranked by what they would earn in a day");
        ImGui.SameLine();

        if (sweep.Running)
        {
            ImGui.TextColored(Palette.Dim, $"  {sweep.Detail}");

            // The ranking below is the previous one, and stays up rather than being replaced by an
            // empty screen for the several minutes a run takes. Said out loud, because a table that
            // quietly refers to a different fetch than the line above it is the worse failure.
            if (sweep.ReadyAt is { } previous)
            {
                ImGui.TextColored(
                    Palette.Dim,
                    $"    still the ranking from {Phrases.Ago(DateTimeOffset.UtcNow - previous)} ago, "
                    + "improving as prices arrive");
            }
        }
        else
        {
            if (ImGui.Button(sweep.ReadyAt is null ? "Sweep" : "Re-sweep"))
                sweep.Start(buying, selling, config.FurnishingShortlist, config.SweepAge());

            ImGui.SameLine();

            ImGui.SameLine();

            // The sweep decides what is worth costing and that holds for hours; what those
            // things cost does not. Asking again is a few requests rather than a few minutes.
            if (sweep.HasResults && ImGui.Button("Recheck prices"))
                recheck();

            if (sweep.State == FurnishingSweep.Phase.Failed)
                ImGui.TextColored(Palette.Bad, sweep.Detail);
            else if (sweep.ReadyAt is null)
                // Said plainly, because it is minutes of small polite requests and should not start
                // itself the first time the window happens to open.
                ImGui.TextColored(
                    Palette.Dim, "  not swept yet. Eight ids a request, so this takes a few minutes.");
            else
                DrawFinished();
        }

        // Which materials are doing the blocking. This is the evidence for whether following
        // recipes down to raw materials is worth building, so it belongs on screen and not
        // only in the log.
        if (sweep.Blockers.Count > 0)
        {
            var worst = string.Join(
                ", ",
                sweep.Blockers.Take(4).Select(blocker => $"{blocker.Material} ({blocker.Blocks})"));

            ImGui.TextColored(Palette.Dim, $"    blocked mostly by: {worst}");
        }
    }

    /// <summary>What the last completed run found, and how long ago it found it.</summary>
    /// <remarks>
    /// Never a silent cap: the table is trimmed for legibility and says by how much. The age is shown
    /// because a restored sweep can be hours old, and that is fine for choosing what to make but
    /// should not be mistaken for live depth.
    /// </remarks>
    private void DrawFinished()
    {
        var current = model.Current;

        var age = sweep.ReadyAt is { } at ? $"swept {Phrases.Ago(DateTimeOffset.UtcNow - at)} ago, " : "";

        var incomplete = sweep.State == FurnishingSweep.Phase.Partial;

        // Coloured when the run had holes in it, because "nothing was found" and "most of it never
        // arrived" must not look the same at a glance.
        ImGui.TextColored(
            incomplete ? Palette.Bad : Palette.Dim,
            $"  {age}{sweep.Detail}"
            + (current.Crafts.Length > 0
                ? $", showing {current.Crafts.Length} of {current.Ranked}"
                  + (current.Discarded > 0 ? $", {current.Discarded} unpriceable" : "")
                : ""));
    }

    private void DrawTable()
    {
        var current = model.Current;

        if (current.Crafts.Length == 0)
            return;

        // Money columns want their first click to sort downwards. Nobody opens a profit column to
        // find the worst one.
        const ImGuiTableColumnFlags NumberColumn =
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending;

        if (!ImGui.BeginTable(
                "crafts",
                8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Sortable))
            return;

        ImGui.TableSetupColumn("Furnishing", ImGuiTableColumnFlags.WidthStretch);

        // Nothing to learn from ordering by job, and the alternative reading of a click on it,
        // "show me only mine", is a filter rather than a sort.
        ImGui.TableSetupColumn(
            "job", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 62);

        ImGui.TableSetupColumn("materials", NumberColumn, 100);
        ImGui.TableSetupColumn("profit", NumberColumn, 100);
        ImGui.TableSetupColumn("return", NumberColumn, 70);
        ImGui.TableSetupColumn("sales/day", NumberColumn, 75);
        ImGui.TableSetupColumn("gil/day", NumberColumn | ImGuiTableColumnFlags.DefaultSort, 100);

        // The last column is a control, not a fact, so it has no name and no sort. Building
        // the list is what this table is for, and it should not live only in a menu.
        ImGui.TableSetupColumn(
            "", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 26);
        Cell.Headers(Help);
        ReadSort();

        foreach (var row in current.Crafts)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            cells.Draw(row.Item, row.ItemId, row.RecipeId, row.Breakdown, () => refreshTrade(row.Conversion));

            ImGui.TableNextColumn();
            cells.Job(row.JobId, row.Job);

            ImGui.TableNextColumn();
            Cell.Right($"{row.Materials:N0}");

            ImGui.TableNextColumn();
            Cell.Right(row.Profit > 0 ? Palette.Good : Palette.Bad, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            Cell.Right(row.Roi is { } roi ? $"{roi:P0}" : "-");

            ImGui.TableNextColumn();
            Cell.Right(Palette.Dim, $"{row.SalesPerDay:F1}");

            ImGui.TableNextColumn();
            Cell.Right(row.GilPerDay > 0 ? Palette.Good : Palette.Dim, $"{row.GilPerDay:N0}");
            if (ImGui.IsItemHovered())
            {
                // Worth saying out loud on every row. The figure is the whole market's daily
                // turnover, which you only earn by taking every sale from whoever has it now.
                ImGui.SetTooltip(
                    "A ceiling, not a forecast: it assumes you take every sale at today's price.\n"
                    + "Furnishings sit in thin books, often a wall of single units at a round\n"
                    + "number, so adding supply tends to move the price rather than join it.");
            }

            ImGui.TableNextColumn();
            if (row.RecipeId is { } addable)
            {
                ImGui.PushID((int)addable);

                if (ImGui.SmallButton("+"))
                    basket.Add(addable, row.ItemId, row.Item, 1);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Add one to the list you are building above.");

                ImGui.PopID();
            }
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Takes the order from the table's own header clicks.
    /// </summary>
    /// <remarks>
    /// The chosen column is remembered rather than applied here, because the ranking is trimmed to the
    /// rows worth showing and the order has to be decided before the trim. Sorting the visible
    /// twenty-five by profit would answer a different question, "the best gil per day, in profit
    /// order", while looking exactly like an answer to this one.
    /// </remarks>
    private void ReadSort()
    {
        var specs = ImGui.TableGetSortSpecs();

        if (!specs.SpecsDirty)
            return;

        // A count of zero is a table someone has sorted by nothing, which keeps the last order rather
        // than falling back to an arbitrary one.
        if (specs.SpecsCount > 0)
        {
            var first = specs.Specs[0];
            sortColumn = (Column)first.ColumnIndex;
            sortDescending = first.SortDirection == ImGuiSortDirection.Descending;
        }

        specs.SpecsDirty = false;
        model.Invalidate();
    }

    /// <summary>The shortlist in the order the headers asked for.</summary>
    private IEnumerable<ExpectedEarnings> Ordered(ExpectedEarnings[] priceable)
    {
        if (sortColumn == Column.Furnishing)
        {
            return sortDescending
                ? priceable.OrderByDescending(earnings => earnings.Conversion.Name, StringComparer.OrdinalIgnoreCase)
                : priceable.OrderBy(earnings => earnings.Conversion.Name, StringComparer.OrdinalIgnoreCase);
        }

        // An unpriceable return sorts as the worst there is rather than as zero, which would place it
        // above every genuine loss.
        Func<ExpectedEarnings, double> key = sortColumn switch
        {
            Column.Materials => earnings => earnings.Quote.GilOutlay,
            Column.Profit => earnings => earnings.Quote.Profit,
            Column.Return => earnings => earnings.Quote.ReturnOnOutlay ?? double.MinValue,
            Column.SalesPerDay => earnings => earnings.RunsPerDay,
            _ => earnings => earnings.GilPerDay,
        };

        return sortDescending ? priceable.OrderByDescending(key) : priceable.OrderBy(key);
    }

    /// <summary>Ranks the swept furnishings, trims the table, and counts what could not be priced.</summary>
    /// <remarks>
    /// The discard count is not decoration. It is the measurement that decides whether following
    /// recipe trees down to raw materials is worth building: if a handful of furnishings are lost
    /// to an untraded intermediate, direct ingredients are enough, and if a third of them are,
    /// they are not.
    /// </remarks>
    private Model Build()
    {
        if (!sweep.HasResults)
            return new Model([], 0, 0);

        var cap = config.CraftsPerDayCap > 0 ? config.CraftsPerDayCap : (double?)null;
        var ranked = ConversionRanking.ByGilPerDay(
            sweep.Shortlist, boards.Buying, boards.Selling, boards.Tax, cap, boards.Vendor);

        var priceable = ranked.Where(earnings => earnings.Quote.IsExecutable).ToArray();

        var rows = Ordered(priceable)
            .Take(CraftsInTable)
            .Select(earnings =>
            {
                var made = furnishings.Behind(earnings.Conversion.Id);

                return new CraftRow(
                    earnings.Conversion,
                    earnings.Conversion.Name,
                    made?.ItemId ?? 0,
                    made?.RecipeId,
                    made?.JobId ?? 0,
                    made?.Job ?? "",
                    earnings.Quote.GilOutlay,
                    earnings.Quote.Profit,
                    earnings.Quote.ReturnOnOutlay,
                    earnings.RunsPerDay,
                    earnings.GilPerDay,
                    Breakdown(earnings.Conversion));
            })
            .ToArray();

        return new Model(rows, priceable.Length, ranked.Count - priceable.Length);
    }

    /// <summary>What each material costs, for the tooltip.</summary>
    private ItemCells.MaterialLine[] Breakdown(Conversion conversion) =>
    [
        .. conversion.Inputs
            .Where(input => input.Resource.Kind == ResourceKind.Item)
            .Select(input =>
            {
                var quote = boards.Buying(input.Resource.Id)?.CostToBuy(input.Quantity, boards.Tax);

                return new ItemCells.MaterialLine(
                    input.Resource.Id,
                    input.Resource.Name,
                    input.Quantity,
                    quote?.Total ?? 0,
                    quote is { IsComplete: true });
            }),
    ];

    /// <summary>
    /// The columns, by the index the table reports a sort on.
    /// </summary>
    /// <remarks>
    /// Written out so the numbers stay tied to the order the columns are declared in. Reading a bare
    /// index out of a sort spec and switching on it works right up until somebody inserts a column.
    /// </remarks>
    private enum Column
    {
        Furnishing = 0,
        Job = 1,
        Materials = 2,
        Profit = 3,
        Return = 4,
        SalesPerDay = 5,
        GilPerDay = 6,
    }

    private sealed record CraftRow(
        Conversion Conversion,
        string Item,
        uint ItemId,
        uint? RecipeId,
        uint JobId,
        string Job,
        long Materials,
        long Profit,
        double? Roi,
        double SalesPerDay,
        long GilPerDay,
        ItemCells.MaterialLine[] Breakdown);

    private sealed record Model(CraftRow[] Crafts, int Ranked, int Discarded);
}
