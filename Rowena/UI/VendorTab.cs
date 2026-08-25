using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What is listed on the board for less than a vendor will pay for it.
/// </summary>
/// <remarks>
/// Its own tab because it runs on its own clock, like the furnishing sweep: sixteen thousand
/// items surveyed is minutes of small polite requests, and the answer stays useful for hours.
/// It is also the only thing here that is not a judgement. Every other table weighs a margin
/// against how long it takes to sell; this one is arithmetic with no market risk in it at
/// all, because the vendor is not a market.
///
/// The scan finds which items are worth watching. What they are worth is computed from the
/// cache every rebuild, so a row is as fresh as the last fetch rather than as old as the
/// scan, and a find that has been bought out by somebody else simply stops being shown.
/// </remarks>
internal sealed class VendorTab
{
    private readonly VendorSweep sweep;
    private readonly Boards boards;
    private readonly ItemCells cells;
    private readonly Configuration config;
    private readonly Action<IReadOnlyList<uint>> recheck;

    private readonly Rebuilt<Model> model;

    private static readonly string?[] Help =
    [
        null,
        "What a vendor hands over for one, from the item sheet. It never undercuts and never\n"
        + "runs out of appetite, which is what makes this riskless.",
        "The cheapest unit price on the buying board right now.",
        "How many units are listed cheaply enough to still pay once the board's 5% buyer's\n"
        + "cut is added.",
        "What buying those units and selling them to a vendor leaves, tax included.",
        "Where the units actually are. Buying is per world, so a find spread over several is\n"
        + "several trips: the one holding most of it is named, and the rest are on hover.",
    ];

    public VendorTab(
        VendorSweep sweep,
        Boards boards,
        ItemCells cells,
        Configuration config,
        Diagnostics diagnostics,
        Action<IReadOnlyList<uint>> recheck)
    {
        this.sweep = sweep;
        this.boards = boards;
        this.cells = cells;
        this.config = config;
        this.recheck = recheck;

        model = new Rebuilt<Model>("vendor", Build, diagnostics);
    }

    /// <summary>The tab's label, carrying how many finds are standing.</summary>
    public string Label =>
        model.Current.Finds.Length == 0 ? "Vendor###vendor" : $"Vendor ({model.Current.Finds.Length})###vendor";

    /// <summary>What the overview should say about the vendor scan.</summary>
    public Note? Headline()
    {
        var finds = model.Current.Finds;

        if (finds.Length == 0)
            return null;

        var best = finds[0];

        return new Note(
            Note.Waiting,
            Style.Good,
            $"{finds.Sum(find => find.Profit):N0} gil",
            "listed below what a vendor pays",
            $"Best is {best.Name}: {best.Units} units, {best.Profit:N0} gil, no market risk at all.",
            MainWindow.Tab.Vendor);
    }

    /// <inheritdoc cref="ConvertTab.Warmers"/>
    public IReadOnlyList<Action> Warmers => [() => _ = model.Current];

    /// <summary>What the table is claiming, for checking it against the board and the sheets.</summary>
    public string Dump() =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                buying = boards.Scope.Buying,

                // Which world the table is narrowed to, because a find on one world is a
                // different find from the same one across the data centre, and a checker that
                // does not know which is being asked will disagree about a correct answer.
                world = config.VendorWorld,
                buyerRate = boards.Tax.BuyerRate,
                scan = sweep.Current.Detail,
                finds = model.Current.Finds.Select(find => new
                {
                    item = find.ItemId,
                    name = find.Name,
                    vendorPays = find.VendorPrice,
                    cheapest = find.Cheapest,
                    units = find.Units,
                    profit = find.Profit,
                    listings = find.Listings,
                    unitsListed = find.UnitsListed,

                    // The split rather than the totals. This walk takes whole listings while
                    // each still pays, so the same units divided differently across the same
                    // number of listings buy a different number of them.
                    print = BookPrint.Of(boards.Buying(find.ItemId)),
                    seenAt = find.SeenAt,
                    byWorld = find.ByWorld.Select(share => new
                    {
                        world = share.World,
                        units = share.Units,
                        profit = share.Profit,
                    }),
                }),
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public void Draw(string buying)
    {
        Style.Muffled("Listed for less than a vendor pays: buy it, walk to any vendor, sell it.");

        DrawScan(buying);
        DrawTable();
    }

    private void DrawScan(string buying)
    {
        var scan = sweep.Current;

        if (scan.Running)
        {
            ImGui.TextColored(Style.Muted, $"  {scan.Detail}");
            return;
        }

        if (Style.Commit(scan.ReadyAt is null ? "scan the board" : "scan again"))
        {
            sweep.Start(buying, config.VendorCandidatesToCost, config.SweepAge());
        }

        ImGui.SameLine();

        if (scan.State == VendorSweep.Phase.Failed)
        {
            ImGui.TextColored(Style.Bad, scan.Detail);
            return;
        }

        if (scan.ReadyAt is null && !scan.HasResults)
        {
            // Said plainly: it is a hundred and seventy polite requests and should not start
            // itself the first time the tab happens to be opened.
            ImGui.TextColored(
                Style.Muted,
                "  not scanned yet; every marketable item, a hundred a request, so this takes a few minutes");
            return;
        }

        var age = scan.ReadyAt is { } at ? $"{Phrases.Ago(DateTimeOffset.UtcNow - at)} old, " : "";

        ImGui.TextColored(scan.State == VendorSweep.Phase.Partial ? Style.Bad : Style.Muted, $"  {age}{scan.Detail}");
    }

    private void DrawTable()
    {
        var current = model.Current;

        if (current.Finds.Length == 0)
        {
            if (sweep.Current.HasResults)
            {
                Style.Nothing(
                    current.Uncosted > 0
                        ? $"{current.Uncosted} items are worth a look but have no book yet; scan to cost them"
                        : current.Hidden > 0
                            ? $"nothing over {config.VendorFindFloor:N0} gil; {current.Hidden} smaller finds are being hidden"
                            : "nothing is listed under its vendor price right now, which is the usual answer");
            }

            return;
        }

        DrawWorlds(current);

        // These are underpriced by definition, so somebody else can see them too and they do
        // not last. Worth refetching the moment before travelling rather than trusting a
        // number the scan left behind.
        if (Style.Row("recheck these", "These are underpriced by definition, so they do not last. Refetch before travelling."))
            recheck([.. current.Finds.Select(find => find.ItemId)]);

        ImGui.SameLine();

        var oldest = current.Finds.Min(find => find.SeenAt);
        ImGui.TextColored(
            Style.Muted,
            oldest == default ? "  " : $"  prices {Phrases.Ago(DateTimeOffset.UtcNow - oldest)} old");

        if (current.Hidden > 0)
            ImGui.TextColored(Style.Muted, $"    {current.Hidden} more under {config.VendorFindFloor:N0} gil, hidden.");

        if (!ImGui.BeginTable("vendor-finds", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("vendor pays", ImGuiTableColumnFlags.WidthFixed, Style.Px(100));
        ImGui.TableSetupColumn("listed at", ImGuiTableColumnFlags.WidthFixed, Style.Px(100));
        ImGui.TableSetupColumn("units", ImGuiTableColumnFlags.WidthFixed, Style.Px(60));
        ImGui.TableSetupColumn("profit", ImGuiTableColumnFlags.WidthFixed, Style.Px(110));
        ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, Style.Px(230));
        Cell.Headers(Help);

        foreach (var row in current.Finds)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            cells.Draw(row.Name, row.ItemId);

            ImGui.TableNextColumn();
            Cell.Right($"{row.VendorPrice:N0}");

            ImGui.TableNextColumn();
            Cell.Right($"{row.Cheapest:N0}");

            ImGui.TableNextColumn();
            Cell.Right($"{row.Units}");

            ImGui.TableNextColumn();
            Cell.Right(Style.Good, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            DrawWhere(row);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Which world to bother with.
    /// </summary>
    /// <remarks>
    /// Buying means travelling to whoever is selling, so a find on a world I will not visit is
    /// not an opportunity at all. Picking one narrows the table to what is actually there and
    /// recuts the numbers to that world alone: what one trip is worth, rather than what all
    /// five would be.
    /// </remarks>
    private void DrawWorlds(Model current)
    {
        var chosen = config.VendorWorld;
        var label = string.IsNullOrEmpty(chosen) ? "Any world" : chosen;

        ImGui.SetNextItemWidth(Style.Px(200f));

        if (!ImGui.BeginCombo("##vendor-world", label))
            return;

        if (ImGui.Selectable("Any world", string.IsNullOrEmpty(chosen)))
        {
            config.VendorWorld = "";
            model.Invalidate();
        }

        foreach (var world in current.Worlds)
        {
            if (ImGui.Selectable(world, world == chosen))
            {
                config.VendorWorld = world;
                model.Invalidate();
            }
        }

        ImGui.EndCombo();
    }

    /// <summary>
    /// Which worlds the units are on, since a trip is per world.
    /// </summary>
    /// <remarks>
    /// The world of the cheapest listing used to be the whole answer, and it was often the
    /// wrong one: a find of a hundred and ninety-seven units named the world holding five of
    /// them. What is named now is the world holding most, with the rest counted beside it, so
    /// a find that is really five errands does not read as one.
    /// </remarks>
    private static void DrawWhere(Find row)
    {
        if (row.ByWorld is not [var best, ..])
        {
            ImGui.TextColored(Style.Muted, "unknown");
            return;
        }

        var others = row.ByWorld.Count - 1;

        ImGui.TextColored(
            Style.Muted,
            others == 0
                ? $"{best.World} (all {best.Units})"
                : $"{best.World} {best.Units} of {row.Units}, +{others} more");

        if (!ImGui.IsItemHovered())
            return;

        // Built only when hovered, since the join walks every world share.
        ImGui.SetTooltip(
            "Buying is per world, so each of these is its own trip:\n"
            + string.Join(
                "\n",
                row.ByWorld.Select(share => $"  {share.World}: {share.Units} units, {share.Profit:N0} gil")));
    }

    /// <summary>What the shortlist is worth against the books the cache holds now.</summary>
    private Model Build()
    {
        var scan = sweep.Current;

        if (!scan.HasResults)
            return new Model([], 0, 0, []);

        var found = new List<Find>();
        var worlds = new SortedSet<string>(StringComparer.Ordinal);
        var hidden = 0;

        var uncosted = 0;

        foreach (var id in scan.Shortlist)
        {
            // A shortlist rebuilt from a previous session's survey knows which items are worth
            // a look and nothing about how many units are cheap: that needs the full book, and
            // the books are only fetched by a scan. Counted rather than skipped, because
            // "nothing is under its vendor price" and "nobody has looked yet" must not read
            // the same.
            if (boards.Buying(id) is not { } book)
            {
                uncosted++;
                continue;
            }

            if (book.Listings.Count == 0)
                continue;

            var vendorPrice = boards.Vendor(id);
            var arbitrage = VendorArbitrage.Find(book, vendorPrice, boards.Tax);

            if (arbitrage.Units == 0)
                continue;

            foreach (var world in arbitrage.ByWorld)
                worlds.Add(world.World);

            // Narrowed to one world, a find is only what stands on that world: the units and
            // the gil of one trip, not of the five it would otherwise take.
            var shares = string.IsNullOrEmpty(config.VendorWorld)
                ? arbitrage.ByWorld
                : [.. arbitrage.ByWorld.Where(share => share.World == config.VendorWorld)];

            if (shares.Count == 0)
                continue;

            var units = shares.Sum(share => share.Units);
            var profit = shares.Sum(share => share.Profit);

            if (profit < config.VendorFindFloor)
            {
                hidden++;
                continue;
            }

            found.Add(new Find(
                id,
                cells.Name(id),
                vendorPrice,
                book.Floor ?? 0,
                units,
                profit,
                shares,
                book.Listings.Count,
                book.UnitsListed,
                book.Retrieved));
        }

        return new Model([.. found.OrderByDescending(find => find.Profit)], hidden, uncosted, [.. worlds]);
    }

    private sealed record Find(
        uint ItemId,
        string Name,
        long VendorPrice,
        long Cheapest,
        int Units,
        long Profit,
        IReadOnlyList<VendorArbitrage.WorldShare> ByWorld,
        int Listings,
        int UnitsListed,
        DateTimeOffset SeenAt);

    /// <param name="Hidden">Finds under the floor, counted rather than dropped silently.</param>
    /// <param name="Uncosted">Shortlisted items with no book yet, which is not the same as no find.</param>
    /// <param name="Worlds">Every world any find stands on, for the filter to offer.</param>
    private sealed record Model(Find[] Finds, int Hidden, int Uncosted, string[] Worlds);
}
