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
        "The world holding the cheapest listing. You have to travel there to buy it.",
    ];

    public VendorTab(VendorSweep sweep, Boards boards, ItemCells cells, Configuration config)
    {
        this.sweep = sweep;
        this.boards = boards;
        this.cells = cells;
        this.config = config;

        model = new Rebuilt<Model>(Build);
    }

    /// <summary>The tab's label, carrying how many finds are standing.</summary>
    public string Label =>
        model.Current.Finds.Length == 0 ? "Vendor###vendor" : $"Vendor ({model.Current.Finds.Length})###vendor";

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
                    current.Hidden > 0
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
            ImGui.TextColored(Palette.Dim, row.World);
        }

        ImGui.EndTable();
    }

    /// <summary>What the shortlist is worth against the books the cache holds now.</summary>
    private Model Build()
    {
        var scan = sweep.Current;

        if (!scan.HasResults)
            return new Model([], 0);

        var found = new List<Find>();
        var hidden = 0;

        foreach (var id in scan.Shortlist)
        {
            if (boards.Buying(id) is not { Listings.Count: > 0 } book)
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
                book.Listings[0].World));
        }

        return new Model([.. found.OrderByDescending(find => find.Profit)], hidden);
    }

    private sealed record Find(
        uint ItemId,
        string Name,
        long VendorPrice,
        long Cheapest,
        int Units,
        long Profit,
        string World);

    /// <param name="Hidden">Finds under the floor, counted rather than dropped silently.</param>
    private sealed record Model(Find[] Finds, int Hidden);
}
