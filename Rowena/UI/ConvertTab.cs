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
/// One class, two tabs. They are the same machinery pointed at two different wallets and priced
/// off the same fetch, which is why the code is shared; they used to share a screen as well, and
/// that stopped working the moment both tables got long. Nobody compared the bottom of one with
/// the top of the other, they scrolled. Each tab has its own snapshot, so the one not being looked
/// at costs nothing: in particular the flip allocation is never run for somebody reading sinks.
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
        "What one run of the trade costs, in the currency. The fixed rate at the counter.",
        "What one unit of the currency turns into: spent on this trade, with the result sold.\n"
        + "Not a price. The currency cannot be bought, only earned and spent.",
        "Gil left over from a single run, after the market's cut and after buying anything\n"
        + "the trade needs that you have not got.",
        "What spending here would actually bank within the selling horizon: the runs you can\n"
        + "afford, capped by the runs the board would absorb in the time, times the net per\n"
        + "run. The table ranks by this. A rate nobody buys at is theory; this is the gil.",
        "How many runs the balance you are holding pays for. Under one run, how far\n"
        + "along the next one is.",
        "How long the board would take to absorb the output of one run at the rate it\n"
        + "currently sells. A margin you cannot sell into is not a margin.\n"
        + "Green within a day, yellow within three, orange within a week, red beyond or never.",
        "Who to hand it to and where. Plain when in the zone you are standing in, dim when it\n"
        + "is a trip. Right-click to flag it on the map, or to walk there with vnavmesh.",
    ];

    private static readonly string?[] FlipHelp =
    [
        "What to buy on the board and what it becomes. Hover for each input and its cost.",
        "Who to hand it to and where. Plain when in the zone you are standing in, dim when it\n"
        + "is a trip. Right-click to flag it on the map, or to walk there with vnavmesh.",
        "How many runs your gil is best spent on, once every row has competed for the same\n"
        + "order book, and no run is counted that the board would not absorb within the\n"
        + "selling horizon. A zero means the inputs pay more elsewhere or nothing would sell\n"
        + "in time.",
        "Runs your own stock already covers, retainers included. Not deducted from the outlay:\n"
        + "what you hold is still worth what the board would pay for it.",
        "What buying the inputs costs, walked down the book rather than multiplied out from\n"
        + "the cheapest listing, with the board's 5% buyer's cut included.",
        "Gil left over once the output is sold and the market has taken its cut.",
        "Profit over outlay. Worth reading next to the column beside it: a high return on a\n"
        + "trade that takes a month to sell is not a good trade.",
        "How long the board would take to absorb everything these runs would produce,\n"
        + "counting every row that sells into the same book: one market, one queue.\n"
        + "Green within a day, yellow within three, orange within a week, red beyond or never.",
    ];

    private readonly Trades trades;
    private readonly Boards boards;
    private readonly Balances balances;
    private readonly ItemCells cells;
    private readonly Configuration config;
    private readonly MarketCache market;
    private readonly VenueCell venues;
    private readonly Action<Conversion> refreshTrade;

    private readonly Rebuilt<SinkModel> sinks;
    private readonly Rebuilt<FlipModel> flips;

    public ConvertTab(
        Trades trades,
        Boards boards,
        Balances balances,
        ItemCells cells,
        Configuration config,
        MarketCache market,
        VenueCell venues,
        Action<Conversion> refreshTrade)
    {
        this.trades = trades;
        this.boards = boards;
        this.balances = balances;
        this.cells = cells;
        this.config = config;
        this.market = market;
        this.venues = venues;
        this.refreshTrade = refreshTrade;

        sinks = new Rebuilt<SinkModel>(BuildSinks);
        flips = new Rebuilt<FlipModel>(BuildFlips);
    }

    /// <summary>The Sinks tab: what a bound currency in your pockets is worth spending.</summary>
    public void DrawSinks()
    {
        var current = sinks.Current;

        DrawReadiness(current.Readiness);
        ImGui.Spacing();
        DrawSinks(current);
    }

    /// <summary>The Flips tab: what buying, converting and selling would pay.</summary>
    public void DrawFlips()
    {
        var current = flips.Current;

        DrawReadiness(current.Readiness);
        ImGui.Spacing();
        DrawFlips(current);
    }

    /// <summary>
    /// Whether the numbers below are ready to be believed.
    /// </summary>
    /// <remarks>
    /// The strip says how old prices are; this says whether this tab has them at all. They
    /// are different questions. Prices can be a minute old and still missing for half of what
    /// this tab wants, because a currency that entered your pockets since the last fetch
    /// brought its sinks with it, and a table of "no prices yet" under an unremarkable strip
    /// read as a plugin that had quietly stopped working.
    /// </remarks>
    private void DrawReadiness(Readiness readiness)
    {
        if (market.Busy)
        {
            var progress = market.Progress is { } p ? $" {p.Done} of {p.Total}" : "...";
            ImGui.TextColored(Palette.Dim, $"Fetching prices{progress}. The tables fill in as answers arrive.");
            return;
        }

        if (readiness.Total == 0)
        {
            ImGui.TextColored(Palette.Dim, "Nothing here needs a price.");
            return;
        }

        if (readiness.Missing == 0)
        {
            ImGui.TextColored(Palette.Good, $"Ready: all {readiness.Total} items this tab needs are priced.");
            return;
        }

        ImGui.TextColored(
            Palette.Bad,
            $"Not ready: {readiness.Missing} of {readiness.Total} items have no price yet. "
            + "Refresh prices to fetch them.");
    }

    private void DrawSinks(SinkModel current)
    {
        ImGui.TextUnformatted(
            $"Sinks: what a bound currency is worth once converted and sold, "
            + $"ranked by what {config.SellingHorizon()} days of selling would bank");

        if (current.Choices.Length == 0)
        {
            ImGui.TextColored(Palette.Dim, "You hold none of the currencies anything here will take.");
            return;
        }

        DrawChooser(current);

        if (current.Selected is not { } group || group.Rows.Length == 0)
            return;

        ImGui.Spacing();

        if (!ImGui.BeginTable($"sinks-{group.Currency.Id}", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("costs", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn($"a {group.Unit} earns", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("net per run", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn($"banks in {config.SellingHorizon()}d", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("held covers", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 300);
        Cell.Headers(SinkHelp);

        foreach (var row in group.Rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (row.ItemId is { } sinkItem)
                cells.Draw(row.Trade, sinkItem, refreshTrade: () => refreshTrade(row.Conversion));
            else
                ImGui.TextUnformatted(row.Trade);

            // The price in the currency. It is the one number on the row the board cannot
            // change, so it is shown whether or not anything else could be priced.
            ImGui.TableNextColumn();
            Cell.Right(Palette.Plain, $"{row.PerRun:N0} {group.Unit}");

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
                Cell.Right(Palette.Dim, "-");
                ImGui.TableNextColumn();
                venues.Draw(row.Conversion.Id, row.Venue, trades.Where(row.Conversion));
                continue;
            }

            ImGui.TableNextColumn();
            // The unit is printed in the cell, not only in the header. Two decimals beside a
            // column of millions reads as millions, and this number really is under a hundred.
            Cell.Right(Palette.Plain, $"{row.Rate!.Value:F2} gil");
            if (ImGui.IsItemHovered())
            {
                // Said as a yield rather than a price, because the column was read as one and
                // the objection was fair: a scrip has no price. Nobody sells them and nobody
                // can buy them. This is what one turns into by being spent here.
                //
                // The whole-balance figure is floor times everything, the exact optimism the
                // rest of this plugin exists to correct, so it never appears without the time
                // the board would need to make it real. A hundred thousand scrips through a
                // mount that sells three times a week is a year of undercutting, not a number.
                ImGui.SetTooltip(
                    $"What one {group.Unit} turns into, spent on this trade and the result sold.\n"
                    + $"Not a price: {group.Unit} cannot be bought, only earned and spent.\n"
                    + $"\n"
                    + $"One run takes {row.PerRun:N0} and nets {row.Profit:N0} gil.\n"
                    + $"{row.PerRun:N0} x {row.Rate.Value:F2} gil is where that comes from.\n"
                    + WholeBalance(group, row));
            }

            ImGui.TableNextColumn();
            Cell.Right($"{row.Profit:N0}");

            // Only the leader is coloured. Marking everything defeats the point, and the
            // leader is the row that banks the most, not the one with the prettiest rate.
            ImGui.TableNextColumn();
            var leader = group.Best is { } best && best > 0 && row.Banks == best;
            Cell.Right(row.Banks > 0 ? leader ? Palette.Good : Palette.Plain : Palette.Dim, row.Banks > 0 ? $"{row.Banks:N0}" : "-");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(BanksExplained(group, row));

            ImGui.TableNextColumn();

            // Under one run, the useful answer is not "0 runs" but how far along the
            // next one is: 3.9% of a mount is news a dash would have hidden.
            if (row.Covers is { } covers && covers > 0)
            {
                Cell.Right($"{covers:N0} runs");
            }
            else if (row.Covers is 0 && row.PerRun > 0 && group.Held > 0)
            {
                Cell.Right(Palette.Dim, $"{(double)group.Held / row.PerRun:P1}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{group.Held:N0} of the {row.PerRun:N0} one run takes.");
            }
            else
            {
                Cell.Right(Palette.Dim, "-");
            }

            ImGui.TableNextColumn();
            Cell.Absorb(row.Absorb, vendored: row.Vendored);

            ImGui.TableNextColumn();
            venues.Draw(row.Conversion.Id, row.Venue, trades.Where(row.Conversion));
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Which currency the sink table is about.
    /// </summary>
    /// <remarks>
    /// One table at a time. With the generated catalogue in, every currency in your pockets has
    /// a table, and a dozen tables stacked is a scroll, not a comparison; comparing across
    /// currencies is not a decision anyone makes, since each one can only be spent on its own
    /// sinks. The choice is remembered, because the one you were looking at is the one you
    /// will want again, and a fresh rebuild does not get to forget it.
    /// </remarks>
    private void DrawChooser(SinkModel current)
    {
        var chosen = current.Selected;
        var label = chosen is null ? "" : Choice(chosen.Currency, chosen.Held);

        // The icon sits beside the combo rather than inside its preview, which is plain text.
        // Currencies are items, so the same icon the tables use is the right one here.
        if (chosen is not null)
        {
            cells.Icon(chosen.Currency.Id);
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(340f);

        if (!ImGui.BeginCombo("##sink-currency", label))
            return;

        foreach (var (currency, held) in current.Choices)
        {
            var selected = chosen is not null && chosen.Currency.Id == currency.Id;

            cells.Icon(currency.Id, 16f);
            ImGui.SameLine();

            if (ImGui.Selectable(Choice(currency, held), selected))
            {
                config.SinkCurrency = currency.Id;
                sinks.Invalidate();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static string Choice(Resource currency, long held) => $"{currency.Name} ({held:N0} held)";

    private void DrawFlips(FlipModel current)
    {
        ImGui.TextUnformatted(
            "Flips: buy the inputs on the board, hand them in, sell what comes out, "
            + $"sized to what sells within {config.SellingHorizon()} days");

        if (current.Flips.Length == 0)
        {
            ImGui.TextColored(Palette.Dim, "Nothing in the catalogue trades items for items.");
            return;
        }

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

        if (!ImGui.BeginTable("flips", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("buy, then sell", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 300);
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

            // Named as the transaction rather than as the product, because the product alone
            // said nothing about what to go and buy. The tooltip carries each input with its
            // cost, the way a craft carries its materials.
            ImGui.TableNextColumn();
            if (row.ItemId is { } flipItem)
                cells.Draw(
                    row.Trade, flipItem, materials: row.Inputs, inputsHeading: "buy",
                    refreshTrade: () => refreshTrade(row.Conversion));
            else
                ImGui.TextUnformatted(row.Trade);

            ImGui.TableNextColumn();
            venues.Draw(row.Conversion.Id, row.Venue, trades.Where(row.Conversion));

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
                ImGui.SetTooltip(
                    "The shared inputs pay more on another row, your gil will not cover a run, or the\n"
                    + $"board would not absorb even one within {config.SellingHorizon()} days.");

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
            Cell.Absorb(row.Absorb, dim: row.Idle, vendored: row.Vendored);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// How much of what this tab needs the cache already has.
    /// </summary>
    private Readiness MeasureReadiness()
    {
        var (bought, sold) = trades.Relevant(balances.Held);

        return new Readiness(
            bought.Length + sold.Length,
            bought.Count(id => boards.Buying(id) is null) + sold.Count(id => boards.Selling(id) is null));
    }

    private SinkModel BuildSinks()
    {
        var tax = boards.Tax;

        // Only the currencies in your pockets, plus the ones the file declares an interest
        // in. The generated catalogue knows about every event token ever minted, and a sink
        // you cannot feed is not a decision; a hand-written one is a standing question.
        var choices = trades.Currencies
            .Select(currency => (Currency: currency, Held: balances.Held(currency)))
            .Where(entry => entry.Held > 0 || trades.IsWatched(entry.Currency))
            .OrderByDescending(entry => entry.Held > 0)
            .ThenBy(entry => entry.Currency.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Only the chosen currency's table is priced. The others are a list of names until
        // picked, which is what keeps a catalogue of a thousand trades cheap to redraw.
        var chosen = choices
            .Where(entry => entry.Currency.Id == config.SinkCurrency)
            .Concat(choices)
            .Select(entry => (Resource?)entry.Currency)
            .FirstOrDefault();

        var selected = chosen is { } currency ? BuildSinkGroup(currency, tax) : null;

        return new SinkModel(MeasureReadiness(), choices, selected);
    }

    private FlipModel BuildFlips()
    {
        var tax = boards.Tax;

        // One quote per flip, reused for the row and for the allocation prefilter. A flip
        // that loses money on its first run only loses more on its second, so the allocator
        // is never asked about it.
        var singles = trades.Flips.ToDictionary(
            conversion => conversion.Id,
            conversion => ConversionEvaluator.Evaluate(
                conversion, 1, boards.Buying, boards.Selling, tax, boards.Vendor),
            StringComparer.Ordinal);

        var candidates = trades.Flips
            .Where(conversion => singles[conversion.Id] is { IsExecutable: true, Profit: > 0 })
            .ToArray();

        var allocated = ConversionAllocation
            .Allocate(
                candidates, boards.Buying, boards.Selling, tax, balances.Gil, config.SizingCap,
                config.SellingHorizon(), boards.Vendor)
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

        return new FlipModel(
            MeasureReadiness(),
            shown,
            allocated.Values.Sum(allocation => allocation.Profit),
            ordered.Length - shown.Length,
            ordered.Count(row => row.Problem is not null));
    }

    /// <summary>
    /// What spending the whole balance here would earn, with the time that claim needs.
    /// </summary>
    /// <remarks>
    /// The gil figure extrapolates a one-run rate across every run the balance covers, which
    /// values every sale at today's floor. Absorption scales the same way, so the correction
    /// is exact where the optimism is not: the board's appetite is measured, the price
    /// holding is hoped.
    /// </remarks>
    private static string WholeBalance(SinkGroup group, SinkRow row)
    {
        // A watched currency shows at a balance of zero, where "the 0 you hold would earn
        // 0 gil" answers nothing. The question at zero is what earning some would pay.
        if (group.Held == 0)
            return "You hold none yet: this is what earning some would be worth.";

        var earns =
            $"The {group.Held:N0} you hold would earn about "
            + $"{(long)(group.Held * row.Rate!.Value):N0} gil this way";

        if (row.Absorb is not { } perRun || row.PerRun == 0)
            return earns + ",\nthough nothing is selling right now, so there is no knowing when.";

        var whole = perRun * group.Held / row.PerRun;

        return whole < 1d
            ? earns + ", sold within the day."
            : earns + $",\nsold over the {Phrases.Absorb(whole)} the board would need to absorb it.";
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
                var quote = ConversionEvaluator.Evaluate(
                    conversion, 1, boards.Buying, boards.Selling, tax, boards.Vendor);
                var perRun = conversion.Consumes(currency);

                var covers = perRun == 0 ? 0 : held / perRun;
                var (sellable, banks) = Banks(quote, covers, config.SellingHorizon());

                return new SinkRow(
                    conversion,
                    conversion.Name,
                    Produced(conversion),
                    quote.IsExecutable ? quote.GilPer(currency) : null,
                    perRun,
                    quote.Profit,
                    perRun == 0 ? null : covers,
                    quote.DaysToAbsorb,
                    sellable,
                    banks,
                    conversion.Venue,
                    quote.IsExecutable,
                    quote.Vendored.Count > 0);
            })
            // What you would bank first; the rate breaks ties, and orders everything when the
            // balance is zero and nothing can be banked at all.
            .OrderByDescending(row => row.Banks)
            .ThenByDescending(row => row.Rate ?? double.MinValue)
            .ToArray();

        var best = rows.Select(row => row.Banks).DefaultIfEmpty().Max();

        return new SinkGroup(currency, Phrases.UnitOf(currency), held, rows, best > 0 ? best : null);
    }

    /// <summary>
    /// What a sink would actually bank within the horizon, and over how many runs.
    /// </summary>
    /// <remarks>
    /// Runs you can afford, capped by runs the board would absorb in the time, times the net
    /// per run. This is the number the table ranks by, and it is a volume limit rather than a
    /// price discount: the price stays at the floor, as everywhere, and what gives is how much
    /// of it you get to sell. A sink whose output nobody buys banks nothing, however handsome
    /// its rate, which is exactly why a rate alone was the wrong thing to rank on.
    /// </remarks>
    private static (long Sellable, long Banks) Banks(ConversionQuote quote, long covers, int horizonDays)
    {
        if (!quote.IsExecutable || quote.Profit <= 0 || covers <= 0)
            return (0, 0);

        // Per-run absorption is the slowest output's; null means nothing sells.
        if (quote.DaysToAbsorb is not { } daysPerRun)
            return (0, 0);

        var absorbable = daysPerRun <= 0 ? covers : (long)Math.Floor(horizonDays / daysPerRun);
        var sellable = Math.Min(covers, absorbable);

        return (sellable, sellable * quote.Profit);
    }

    private string BanksExplained(SinkGroup group, SinkRow row)
    {
        var days = config.SellingHorizon();

        if (row.Absorb is null)
            return "Nothing. Nobody is buying the output, so there is no selling it in any number of days.";

        if (row.Covers is not { } covers || covers == 0)
            return $"Nothing. The {group.Held:N0} you hold do not cover one run.";

        if (row.Sellable == 0)
            return $"Nothing within {days} days: the board would take longer than that to absorb even one run.";

        var limit = row.Sellable < covers
            ? $"the board would only absorb {row.Sellable:N0} of them in {days} days"
            : $"all {row.Sellable:N0} of them would sell within {days} days";

        return $"You can afford {covers:N0} runs and {limit}.\n{row.Sellable:N0} x {row.Profit:N0} gil.";
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
            // Three different failures, and they ask for three different things. Short means
            // the board really has no more. Unseen means the fetch was cut off and the board
            // may well have more, which is fixed by asking for more listings rather than by
            // giving up on the trade.
            var problem = single.Unsourced.Count > 0
                ? $"short {string.Join(", ", single.Unsourced)}"
                : single.Unseen.Count > 0
                    ? $"only saw part of the book for {string.Join(", ", single.Unseen.Select(a => a.Resource.Name))}"
                    : $"no price for {string.Join(", ", single.Unpriced)}";

            return new FlipRow(
                conversion, Transaction(conversion), Produced(conversion), conversion.Venue, Inputs(conversion),
                0, covers, 0, 0, null, null, true, problem, false);
        }

        var allocation = allocated.GetValueOrDefault(conversion.Id);

        // Nothing allocated means the shared inputs earn more elsewhere. The row still shows
        // what one run would pay, dimmed, so the comparison is visible rather than absent.
        var idle = allocation is null || allocation.Runs == 0;

        var vendored = single.Vendored.Count > 0;

        return idle
            ? new FlipRow(
                conversion, Transaction(conversion), Produced(conversion), conversion.Venue, Inputs(conversion),
                0, covers, single.GilOutlay, single.Profit, single.ReturnOnOutlay, single.DaysToAbsorb, true, null,
                vendored)
            : new FlipRow(
                conversion, Transaction(conversion), Produced(conversion), conversion.Venue, Inputs(conversion),
                allocation!.Runs, covers, allocation.GilOutlay, allocation.Profit, allocation.ReturnOnOutlay,
                allocation.DaysToAbsorb, false, null, vendored);
    }

    /// <summary>
    /// A flip as the thing you would do: what goes in, what comes out.
    /// </summary>
    /// <remarks>
    /// The conversion's own name is the product for generated trades, which told you what you
    /// would end up with and nothing about what to go and buy. The file's names already read
    /// this way; composing the label for every row keeps the column consistent.
    /// </remarks>
    private static string Transaction(Conversion conversion)
    {
        var inputs = string.Join(
            " + ",
            conversion.Inputs.Select(input => $"{input.Quantity:N0}x {input.Resource.Name}"));

        var outputs = string.Join(
            " + ",
            conversion.Outputs.Select(output =>
                output.Quantity == 1 ? output.Resource.Name : $"{output.Quantity:N0}x {output.Resource.Name}"));

        return $"{inputs} -> {outputs}";
    }

    /// <summary>Each input with what it costs off the book, for the tooltip.</summary>
    private ItemCells.MaterialLine[] Inputs(Conversion conversion) =>
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

    /// <param name="Sellable">Runs that both the balance and the board's appetite allow within the horizon.</param>
    /// <param name="Banks">What those runs net, which the table ranks by.</param>
    private sealed record SinkRow(
        Conversion Conversion,
        string Trade,
        uint? ItemId,
        double? Rate,
        long PerRun,
        long Profit,
        long? Covers,
        double? Absorb,
        long Sellable,
        long Banks,
        string Venue,
        bool Priced,
        bool Vendored);

    /// <param name="Best">The most any row banks, or null when nothing can be banked.</param>
    private sealed record SinkGroup(Resource Currency, string Unit, long Held, SinkRow[] Rows, long? Best);

    private sealed record FlipRow(
        Conversion Conversion,
        string Trade,
        uint? ItemId,
        string Venue,
        ItemCells.MaterialLine[] Inputs,
        int Runs,
        long HeldCovers,
        long Outlay,
        long Profit,
        double? Roi,
        double? Absorb,
        bool Idle,
        string? Problem,
        bool Vendored);

    /// <param name="TotalFlipProfit">
    /// What the best split of your gil across every flip pays, which is not the sum of the rows: the
    /// rows compete for one order book and the allocation decides between them.
    /// </param>
    /// <param name="HiddenFlips">Rows the trim removed, all paying less than anything shown.</param>
    /// <param name="Unpriceable">Flips the board could not price at all, hidden or not.</param>
    /// <param name="Choices">Every currency the table could be about, and how much of each is held.</param>
    /// <param name="Selected">The one it is about, priced.</param>
    /// <param name="Total">Items this tab wants a book for, both boards counted.</param>
    /// <param name="Missing">Of those, how many no book has been fetched for.</param>
    private sealed record Readiness(int Total, int Missing);

    /// <param name="Choices">Every currency the table could be about, and how much of each is held.</param>
    /// <param name="Selected">The one it is about, priced.</param>
    private sealed record SinkModel(
        Readiness Readiness,
        (Resource Currency, long Held)[] Choices,
        SinkGroup? Selected);

    /// <param name="TotalFlipProfit">
    /// What the best split of your gil across every flip pays, which is not the sum of the rows: the
    /// rows compete for one order book and the allocation decides between them.
    /// </param>
    /// <param name="HiddenFlips">Rows the trim removed, all paying less than anything shown.</param>
    /// <param name="Unpriceable">Flips the board could not price at all, hidden or not.</param>
    private sealed record FlipModel(
        Readiness Readiness,
        FlipRow[] Flips,
        long TotalFlipProfit,
        int HiddenFlips,
        int Unpriceable);
}
