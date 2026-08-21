using Dalamud.Bindings.ImGui;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What a thing you are holding is worth turning into: bound currencies through their sinks, gil
/// through the trades that only want gil.
/// </summary>
/// <remarks>
/// Both halves of one question, which is why they share a tab. They are the same machinery pointed at
/// two different wallets, they are priced off the same fetch, and comparing them is the point: whether
/// to spend scrips or gil is a decision you make once, looking at both.
/// </remarks>
internal sealed class ConvertTab
{
    /// <summary>How many flip rows the table shows. The count it was trimmed from is shown too.</summary>
    private const int FlipsInTable = 25;

    /// <remarks>
    /// One entry per column, null where the header says enough on its own. Written out rather than
    /// built per frame, since a table redraws sixty times a second and none of this ever changes.
    /// </remarks>
    private static readonly string?[] SinkHelp =
    [
        null,
        "What one unit of the currency turns into: spent on this trade, with the result sold.\n"
        + "Not a price. The currency cannot be bought, only earned and spent.",
        "Gil left over from a single run, after the market's cut and after buying anything\n"
        + "the trade needs that you have not got.",
        "How many runs the balance you are holding pays for.",
        "How long the board would take to absorb the output of one run at the rate it\n"
        + "currently sells. A margin you cannot sell into is not a margin.",
        "The counter in the world where the trade actually happens.",
    ];

    private static readonly string?[] FlipHelp =
    [
        null,
        "How many runs your gil is best spent on, once every row has competed for the same\n"
        + "order book. A zero means the shared inputs pay more on another row.",
        "Runs your own stock already covers, retainers included. Not deducted from the outlay:\n"
        + "what you hold is still worth what the board would pay for it.",
        "What buying the inputs costs, walked down the book rather than multiplied out from\n"
        + "the cheapest listing, with the board's 5% buyer's cut included.",
        "Gil left over once the output is sold and the market has taken its cut.",
        "Profit over outlay. Worth reading next to the column beside it: a high return on a\n"
        + "trade that takes a month to sell is not a good trade.",
        "How long the board would take to absorb everything these runs would produce.",
    ];

    private readonly Trades trades;
    private readonly Boards boards;
    private readonly Balances balances;
    private readonly ItemCells cells;
    private readonly Configuration config;

    private readonly Rebuilt<Model> model;

    public ConvertTab(
        Trades trades,
        Boards boards,
        Balances balances,
        ItemCells cells,
        Configuration config)
    {
        this.trades = trades;
        this.boards = boards;
        this.balances = balances;
        this.cells = cells;
        this.config = config;

        model = new Rebuilt<Model>(Build);
    }

    public void Draw()
    {
        var current = model.Current;

        DrawSinks(current);
        ImGui.Spacing();
        DrawFlips(current);
    }

    private void DrawSinks(Model current)
    {
        ImGui.TextUnformatted("Sinks: what a bound currency is worth once converted and sold");

        foreach (var group in current.Sinks)
        {
            if (group.Rows.Length == 0)
                continue;

            ImGui.Spacing();
            ImGui.TextColored(Palette.Dim, $"{group.Currency.Name} ({group.Held:N0} held)");

            if (!ImGui.BeginTable($"sinks-{group.Currency.Id}", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                continue;

            ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn($"a {group.Unit} earns", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("net per run", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("held covers", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 270);
            Cell.Headers(SinkHelp);

            foreach (var row in group.Rows)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (row.ItemId is { } sinkItem)
                    cells.Draw(row.Trade, sinkItem);
                else
                    ImGui.TextUnformatted(row.Trade);

                // An unpriced row has nothing to say, and saying 0.00 would be a confident
                // answer where there is no data at all.
                if (!row.Priced)
                {
                    ImGui.TableNextColumn();
                    Cell.Right(Palette.Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Palette.Dim, "no prices yet");
                    ImGui.TableNextColumn();
                    Cell.Right(Palette.Dim, "-");
                    ImGui.TableNextColumn();
                    Cell.Right(Palette.Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Palette.Dim, row.Venue);
                    continue;
                }

                ImGui.TableNextColumn();
                var leader = group.Best is { } best && Math.Abs(row.Rate!.Value - best) < 0.001d;
                // Only the leader is coloured. Marking everything defeats the point.
                // The unit is printed in the cell, not only in the header. Two decimals beside a
                // column of millions reads as millions, and this number really is under a hundred.
                Cell.Right(leader ? Palette.Good : Palette.Plain, $"{row.Rate!.Value:F2} gil");
                if (ImGui.IsItemHovered())
                {
                    // Said as a yield rather than a price, because the column was read as one and
                    // the objection was fair: a scrip has no price. Nobody sells them and nobody
                    // can buy them. This is what one turns into by being spent here.
                    ImGui.SetTooltip(
                        $"What one {group.Unit} turns into, spent on this trade and the result sold.\n"
                        + $"Not a price: {group.Unit} cannot be bought, only earned and spent.\n"
                        + $"\n"
                        + $"One run takes {row.PerRun:N0} and nets {row.Profit:N0} gil.\n"
                        + $"{row.PerRun:N0} x {row.Rate.Value:F2} gil is where that comes from.\n"
                        + $"The {group.Held:N0} you hold would earn about "
                        + $"{(long)(group.Held * row.Rate.Value):N0} gil this way.");
                }

                ImGui.TableNextColumn();
                Cell.Right($"{row.Profit:N0}");

                ImGui.TableNextColumn();
                Cell.Right(row.Covers is { } covers ? $"{covers:N0} runs" : "-");

                ImGui.TableNextColumn();
                Cell.Right(Phrases.Absorb(row.Absorb));

                ImGui.TableNextColumn();
                ImGui.TextColored(Palette.Dim, row.Venue);
            }

            ImGui.EndTable();
        }
    }

    private void DrawFlips(Model current)
    {
        if (current.Flips.Length == 0)
            return;

        ImGui.TextUnformatted("Flips: buy the inputs, convert, sell the output");

        if (current.TotalFlipProfit > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Palette.Good, $"  best split of your gil pays {current.TotalFlipProfit:N0}");
        }

        // Never a silent cap. The hidden rows are the ones that pay less than everything
        // shown, or that the board cannot price at all, and both counts are said.
        if (current.HiddenFlips > 0)
        {
            ImGui.TextColored(
                Palette.Dim,
                $"    the best {current.Flips.Length} of {current.Flips.Length + current.HiddenFlips}"
                + (current.Unpriceable > 0 ? $", {current.Unpriceable} unpriceable" : ""));
        }

        if (!ImGui.BeginTable("flips", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("runs", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("you hold", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("outlay", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("profit", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("return", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 85);
        Cell.Headers(FlipHelp);

        foreach (var row in current.Flips)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (row.ItemId is { } flipItem)
                cells.Draw(row.Trade, flipItem);
            else
                ImGui.TextUnformatted(row.Trade);

            if (row.Problem is { } problem)
            {
                ImGui.TableNextColumn();
                Cell.Right(Palette.Dim, "-");
                ImGui.TableNextColumn();
                Cell.Right(row.HeldCovers > 0 ? Palette.Good : Palette.Dim, $"{row.HeldCovers}");
                ImGui.TableNextColumn();
                ImGui.TextColored(Palette.Dim, problem);
                continue;
            }

            var tint = row.Idle ? Palette.Dim : Palette.Plain;

            ImGui.TableNextColumn();
            Cell.Right(tint, $"{row.Runs}");
            if (row.Idle && ImGui.IsItemHovered())
                ImGui.SetTooltip("The shared inputs pay more on another row, or your gil will not cover a run.");

            ImGui.TableNextColumn();
            Cell.Right(row.HeldCovers > 0 ? Palette.Good : Palette.Dim, $"{row.HeldCovers}");
            if (row.HeldCovers > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip("Runs your own stock already covers, retainers included. Not deducted from the outlay: what you hold is still worth what the board would pay for it.");

            ImGui.TableNextColumn();
            Cell.Right(tint, $"{row.Outlay:N0}");

            ImGui.TableNextColumn();
            Cell.Right(row.Idle ? Palette.Dim : row.Profit > 0 ? Palette.Good : Palette.Bad, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            Cell.Right(tint, row.Roi is { } roi ? $"{roi:P1}" : "-");

            ImGui.TableNextColumn();
            Cell.Right(tint, Phrases.Absorb(row.Absorb));
        }

        ImGui.EndTable();
    }

    private Model Build()
    {
        var tax = MarketTax.Standard;

        // Only the currencies in your pockets, plus the ones the file declares an interest
        // in. The generated catalogue knows about every event token ever minted, and a sink
        // you cannot feed is not a decision; a hand-written one is a standing question.
        var sinks = trades.Currencies
            .Where(currency => balances.Held(currency) > 0 || trades.IsWatched(currency))
            .Select(currency => BuildSinkGroup(currency, tax))
            .ToArray();

        // One quote per flip, reused for the row and for the allocation prefilter. A flip
        // that loses money on its first run only loses more on its second, so the allocator
        // is never asked about it.
        var singles = trades.Flips.ToDictionary(
            conversion => conversion.Id,
            conversion => ConversionEvaluator.Evaluate(conversion, 1, boards.Buying, boards.Selling, tax),
            StringComparer.Ordinal);

        var candidates = trades.Flips
            .Where(conversion => singles[conversion.Id] is { IsExecutable: true, Profit: > 0 })
            .ToArray();

        var allocated = ConversionAllocation
            .Allocate(candidates, boards.Buying, boards.Selling, tax, balances.Gil, config.SizingCap)
            .ToDictionary(allocation => allocation.Conversion.Id, StringComparer.Ordinal);

        // Working rows first, then priced-but-idle by what one run would pay, then the
        // unpriceable. The trim eats from the bottom, so what disappears is what the board
        // could not answer for anyway.
        var ordered = trades.Flips
            .Select(conversion => BuildFlipRow(conversion, singles[conversion.Id], allocated, tax))
            .OrderByDescending(row => row.Runs > 0 ? 2 : row.Problem is null ? 1 : 0)
            .ThenByDescending(row => row.Profit)
            .ToArray();

        var shown = ordered.Take(FlipsInTable).ToArray();

        return new Model(
            sinks,
            shown,
            allocated.Values.Sum(allocation => allocation.Profit),
            ordered.Length - shown.Length,
            ordered.Count(row => row.Problem is not null));
    }

    /// <summary>
    /// The tradable thing a conversion ends up with, when there is exactly one worth showing.
    /// </summary>
    private static uint? Produced(Conversion conversion) =>
        conversion.Outputs
            .Where(output => output.Resource.Kind == ResourceKind.Item)
            .Select(output => (uint?)output.Resource.Id)
            .FirstOrDefault();

    private SinkGroup BuildSinkGroup(Resource currency, MarketTax tax)
    {
        var held = balances.Held(currency);

        var rows = trades.All
            .Where(conversion => conversion.Consumes(currency) > 0)
            .Select(conversion =>
            {
                var quote = ConversionEvaluator.Evaluate(conversion, 1, boards.Buying, boards.Selling, tax);
                var perRun = conversion.Consumes(currency);

                return new SinkRow(
                    conversion.Name,
                    Produced(conversion),
                    quote.IsExecutable ? quote.GilPer(currency) : null,
                    perRun,
                    quote.Profit,
                    perRun == 0 ? null : held / perRun,
                    quote.DaysToAbsorb,
                    conversion.Venue,
                    quote.IsExecutable);
            })
            .OrderByDescending(row => row.Rate ?? double.MinValue)
            .ToArray();

        var best = rows
            .Where(row => row.Priced)
            .Select(row => row.Rate!.Value)
            .DefaultIfEmpty()
            .Max();

        return new SinkGroup(currency, Phrases.UnitOf(currency), held, rows, rows.Any(row => row.Priced) ? best : null);
    }

    private FlipRow BuildFlipRow(
        Conversion conversion,
        ConversionQuote single,
        IReadOnlyDictionary<string, Allocation> allocated,
        MarketTax tax)
    {
        // Runs your own stock already covers, counting retainers. Deliberately reported beside
        // the outlay rather than subtracted from it: materials you happen to own are not free,
        // they are worth what the board would pay for them, and pricing them at nothing would
        // flatter every row that touched something in a retainer.
        var covers = conversion.Inputs
            .Where(input => input.Resource.Kind == ResourceKind.Item)
            .Select(input => balances.Held(input.Resource) / input.Quantity)
            .DefaultIfEmpty(0)
            .Min();

        if (!single.IsExecutable)
        {
            var problem = single.Unsourced.Count > 0
                ? $"short {string.Join(", ", single.Unsourced)}"
                : $"no price for {string.Join(", ", single.Unpriced)}";

            return new FlipRow(conversion.Name, Produced(conversion), 0, covers, 0, 0, null, null, true, problem);
        }

        var allocation = allocated.GetValueOrDefault(conversion.Id);

        // Nothing allocated means the shared inputs earn more elsewhere. The row still shows
        // what one run would pay, dimmed, so the comparison is visible rather than absent.
        var idle = allocation is null || allocation.Runs == 0;

        return idle
            ? new FlipRow(
                conversion.Name, Produced(conversion), 0, covers, single.GilOutlay, single.Profit, single.ReturnOnOutlay,
                single.DaysToAbsorb, true, null)
            : new FlipRow(
                conversion.Name, Produced(conversion), allocation!.Runs, covers, allocation.GilOutlay, allocation.Profit,
                allocation.ReturnOnOutlay, Multiply(single.DaysToAbsorb, allocation.Runs), false, null);
    }

    private static double? Multiply(double? days, int factor) => days is { } value ? value * factor : null;

    private sealed record SinkRow(
        string Trade,
        uint? ItemId,
        double? Rate,
        long PerRun,
        long Profit,
        long? Covers,
        double? Absorb,
        string Venue,
        bool Priced);

    private sealed record SinkGroup(Resource Currency, string Unit, long Held, SinkRow[] Rows, double? Best);

    private sealed record FlipRow(
        string Trade,
        uint? ItemId,
        int Runs,
        long HeldCovers,
        long Outlay,
        long Profit,
        double? Roi,
        double? Absorb,
        bool Idle,
        string? Problem);

    /// <param name="TotalFlipProfit">
    /// What the best split of your gil across every flip pays, which is not the sum of the rows: the
    /// rows compete for one order book and the allocation decides between them.
    /// </param>
    /// <param name="HiddenFlips">Rows the trim removed, all paying less than anything shown.</param>
    /// <param name="Unpriceable">Flips the board could not price at all, hidden or not.</param>
    private sealed record Model(
        SinkGroup[] Sinks,
        FlipRow[] Flips,
        long TotalFlipProfit,
        int HiddenFlips,
        int Unpriceable);
}
