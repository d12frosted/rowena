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

    private readonly FurnishingSweep sweep;
    private readonly Furnishings furnishings;
    private readonly Boards boards;
    private readonly ItemCells cells;
    private readonly CraftBasket basket;
    private readonly Configuration config;

    private readonly Rebuilt<Model> model;

    public CraftTab(
        FurnishingSweep sweep,
        Furnishings furnishings,
        Boards boards,
        ItemCells cells,
        CraftBasket basket,
        Configuration config)
    {
        this.sweep = sweep;
        this.furnishings = furnishings;
        this.boards = boards;
        this.cells = cells;
        this.basket = basket;
        this.config = config;

        model = new Rebuilt<Model>(Build);
    }

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
            return;
        }

        if (ImGui.Button(sweep.ReadyAt is null ? "Sweep" : "Re-sweep"))
            sweep.Start(
                buying, selling, config.PriceBatchSize, config.SurveyBatchSize,
                config.FurnishingShortlist, config.SweepAge());

        ImGui.SameLine();

        if (sweep.State == FurnishingSweep.Phase.Failed)
        {
            ImGui.TextColored(Palette.Bad, sweep.Detail);
            return;
        }

        if (sweep.ReadyAt is null)
        {
            // Said plainly, because it is minutes of small polite requests and should not start
            // itself the first time the window happens to open.
            ImGui.TextColored(
                Palette.Dim,
                "  not swept yet. Eight ids a request, so this takes a few minutes.");
            return;
        }

        var current = model.Current;

        // Never a silent cap: the table is trimmed for legibility and says by how much. The age is
        // shown because a restored sweep can be hours old, and that is fine for choosing what to
        // make but should not be mistaken for live depth.
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

    private void DrawTable()
    {
        var current = model.Current;

        if (current.Crafts.Length == 0)
            return;

        if (!ImGui.BeginTable("crafts", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Furnishing", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("job", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("materials", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("profit", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("return", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("sales/day", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("gil/day", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableHeadersRow();

        foreach (var row in current.Crafts)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            cells.Draw(row.Item, row.ItemId, row.RecipeId, row.Breakdown);

            ImGui.TableNextColumn();
            cells.Job(row.JobId, row.Job);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.Materials:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.Profit > 0 ? Palette.Good : Palette.Bad, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Roi is { } roi ? $"{roi:P0}" : "-");

            ImGui.TableNextColumn();
            ImGui.TextColored(Palette.Dim, $"{row.SalesPerDay:F1}");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.GilPerDay > 0 ? Palette.Good : Palette.Dim, $"{row.GilPerDay:N0}");
            if (ImGui.IsItemHovered())
            {
                // Worth saying out loud on every row. The figure is the whole market's daily
                // turnover, which you only earn by taking every sale from whoever has it now.
                ImGui.SetTooltip(
                    "A ceiling, not a forecast: it assumes you take every sale at today's price.\n"
                    + "Furnishings sit in thin books, often a wall of single units at a round\n"
                    + "number, so adding supply tends to move the price rather than join it.");
            }
        }

        ImGui.EndTable();
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
        if (!sweep.HasResults || sweep.Shortlist.Count == 0)
            return new Model([], 0, 0);

        var cap = config.CraftsPerDayCap > 0 ? config.CraftsPerDayCap : (double?)null;
        var ranked = ConversionRanking.ByGilPerDay(
            sweep.Shortlist, boards.Buying, boards.Selling, MarketTax.Standard, cap);

        var priceable = ranked.Where(earnings => earnings.Quote.IsExecutable).ToArray();

        var rows = priceable
            .Take(CraftsInTable)
            .Select(earnings =>
            {
                var made = furnishings.Behind(earnings.Conversion.Id);

                return new CraftRow(
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
                var quote = boards.Buying(input.Resource.Id)?.CostToBuy(input.Quantity);

                return new ItemCells.MaterialLine(
                    input.Resource.Id,
                    input.Resource.Name,
                    input.Quantity,
                    quote?.Total ?? 0,
                    quote is { IsComplete: true });
            }),
    ];

    private sealed record CraftRow(
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
