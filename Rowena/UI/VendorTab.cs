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

    public VendorTab(VendorSweep sweep, Boards boards, ItemCells cells, Configuration config, Diagnostics diagnostics)
    {
        this.sweep = sweep;
        this.boards = boards;
        this.cells = cells;
        this.config = config;

        model = new Rebuilt<Model>("vendor", Build, diagnostics);
    }

    /// <summary>The tab's label, carrying how many finds are standing.</summary>
    public string Label =>
        model.Current.Finds.Length == 0 ? "Vendor###vendor" : $"Vendor ({model.Current.Finds.Length})###vendor";

    /// <inheritdoc cref="ConvertTab.Warmers"/>
    public IReadOnlyList<Action> Warmers => [() => _ = model.Current];

    /// <summary>What the table is claiming, for checking it against the board and the sheets.</summary>
    public string Dump() =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                buying = boards.Scope.Buying,
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
        ImGui.TextUnformatted("Listed for less than a vendor pays: buy it, walk to any vendor, sell it");

        DrawScan(buying);
        DrawTable();
    }

    private void DrawScan(string buying)
    {
        var scan = sweep.Current;

        if (scan.Running)
        {
            ImGui.TextColored(Palette.Dim, $"  {scan.Detail}");
            return;
        }

        if (ImGui.Button(scan.ReadyAt is null ? "Scan the board" : "Scan again"))
        {
            sweep.Start(buying, config.VendorCandidatesToCost, config.SweepAge());
        }

        ImGui.SameLine();

        if (scan.State == VendorSweep.Phase.Failed)
        {
            ImGui.TextColored(Palette.Bad, scan.Detail);
            return;
        }

        if (scan.ReadyAt is null && !scan.HasResults)
        {
            // Said plainly: it is a hundred and seventy polite requests and should not start
            // itself the first time the tab happens to be opened.
            ImGui.TextColored(
                Palette.Dim,
                "  not scanned yet. Every marketable item, a hundred a request, so this takes a few minutes.");
            return;
        }

        var age = scan.ReadyAt is { } at ? $"{Phrases.Ago(DateTimeOffset.UtcNow - at)} old, " : "";

        ImGui.TextColored(scan.State == VendorSweep.Phase.Partial ? Palette.Bad : Palette.Dim, $"  {age}{scan.Detail}");
    }

    private void DrawTable()
    {
        var current = model.Current;

        if (current.Finds.Length == 0)
        {
            if (sweep.Current.HasResults)
            {
                ImGui.TextColored(
                    Palette.Dim,
                    current.Uncosted > 0
                        ? $"    {current.Uncosted} items are worth a look but have no book yet. Scan to cost them."
                        : current.Hidden > 0
                            ? $"    nothing over {config.VendorFindFloor:N0} gil. {current.Hidden} smaller finds are being hidden."
                            : "    nothing is listed under its vendor price right now. This is the usual answer.");
            }

            return;
        }

        if (current.Hidden > 0)
            ImGui.TextColored(Palette.Dim, $"    {current.Hidden} more under {config.VendorFindFloor:N0} gil, hidden.");

        if (!ImGui.BeginTable("vendor-finds", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("vendor pays", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("listed at", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("units", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("profit", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 140);
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
            Cell.Right(Palette.Good, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            DrawWhere(row);
        }

        ImGui.EndTable();
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
            ImGui.TextColored(Palette.Dim, "unknown");
            return;
        }

        var others = row.ByWorld.Count - 1;

        ImGui.TextColored(
            Palette.Dim,
            others == 0
                ? $"{best.World} (all {best.Units})"
                : $"{best.World} {best.Units} of {row.Units}, +{others} more");

        if (!ImGui.IsItemHovered())
            return;

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
            return new Model([], 0, 0);

        var found = new List<Find>();
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

            if (arbitrage.Profit < config.VendorFindFloor)
            {
                hidden++;
                continue;
            }

            found.Add(new Find(
                id,
                cells.Name(id),
                vendorPrice,
                book.Floor ?? 0,
                arbitrage.Units,
                arbitrage.Profit,
                arbitrage.ByWorld));
        }

        return new Model([.. found.OrderByDescending(find => find.Profit)], hidden, uncosted);
    }

    private sealed record Find(
        uint ItemId,
        string Name,
        long VendorPrice,
        long Cheapest,
        int Units,
        long Profit,
        IReadOnlyList<VendorArbitrage.WorldShare> ByWorld);

    /// <param name="Hidden">Finds under the floor, counted rather than dropped silently.</param>
    /// <param name="Uncosted">Shortlisted items with no book yet, which is not the same as no find.</param>
    private sealed record Model(Find[] Finds, int Hidden, int Uncosted);
}
