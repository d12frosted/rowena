using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.IPC;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// One window: what you are holding, and what it is worth turning into.
/// </summary>
/// <remarks>
/// Deliberately not a market browser, and deliberately not a control panel either. The only
/// questions it answers are the ones that need both halves of the picture, your balances and
/// the depth of the board, because either one alone is already covered by something else.
///
/// Everything shown is built into a <see cref="Model"/> a few times a second and then merely
/// rendered. Reading currencies, asking another plugin over IPC and allocating a shared order
/// book are all far too expensive to do per frame, and doing them per frame earned a hitch
/// warning from Dalamud.
/// </remarks>
internal sealed class MainWindow : Window
{
    private readonly ConversionCatalog catalog;
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly PricingScope scope;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly FurnishingSweep sweep;
    private readonly CraftTab crafts;
    private readonly ItemCells cells;
    private readonly Configuration config;
    private readonly Action save;

    private readonly uint[] boughtItems;
    private readonly uint[] soldItems;
    private readonly Resource[] spendableCurrencies;
    private readonly Conversion[] flips;

    private readonly Rebuilt<Model> model;

    private bool restoreAttempted;
    private long persistedSweepAt;

    public MainWindow(
        ConversionCatalog catalog,
        MarketCache market,
        Balances balances,
        PricingScope scope,
        GatherBuddyIpc gatherBuddy,
        FurnishingSweep sweep,
        CraftTab crafts,
        ItemCells cells,
        Configuration config,
        Action save)
        : base("Rowena###rowena-main")
    {
        this.catalog = catalog;
        this.market = market;
        this.balances = balances;
        this.scope = scope;
        this.gatherBuddy = gatherBuddy;
        this.sweep = sweep;
        this.crafts = crafts;
        this.cells = cells;
        this.config = config;
        this.save = save;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Split by side, because they are priced on different boards. An item appearing as both is
        // fetched twice, once for each, which is correct rather than wasteful: they are two numbers.
        boughtItems = ItemsOn(conversion => conversion.Inputs);
        soldItems = ItemsOn(conversion => conversion.Outputs);

        uint[] ItemsOn(Func<Conversion, IReadOnlyList<ResourceAmount>> side) =>
        [
            .. catalog.Conversions
                .SelectMany(side)
                .Select(amount => amount.Resource)
                .Where(resource => resource.Kind == ResourceKind.Item)
                .Select(resource => resource.Id)
                .Distinct(),
        ];

        spendableCurrencies =
        [
            .. catalog.Conversions
                .SelectMany(conversion => conversion.Inputs)
                .Select(amount => amount.Resource)
                .Where(resource => resource.Kind == ResourceKind.Currency)
                .Distinct(),
        ];

        // Trades with no bound currency in them: pure gil in, more gil out, no gameplay.
        flips =
        [
            .. catalog.Conversions
                .Where(conversion => conversion.Inputs.All(input => input.Resource.Kind == ResourceKind.Item)),
        ];

        model = new Rebuilt<Model>(Build);
    }

    public override void Draw()
    {
        var buying = scope.Buying;
        var selling = scope.Selling;

        DrawHeader(buying, selling);
        ImGui.Separator();

        if (buying is null || selling is null)
        {
            ImGui.TextColored(Palette.Bad, "Not logged in. Prices cannot be fetched.");
            return;
        }

        // Prices saved by a previous session, as soon as there is a board to compare them against.
        market.RestoreOnce(config.SweepAge());
        RestoreSweepOnce(buying, selling);
        PersistFinishedSweep();

        var current = model.Current;

        DrawWhatYouHold(current);
        ImGui.Separator();
        DrawSinks(current);
        ImGui.Spacing();
        DrawFlips(current);
        ImGui.Spacing();
        crafts.Draw(buying, selling);
    }

    private void DrawHeader(string? buying, string? selling)
    {
        // Both boards named, because they are different and the difference is the point.
        ImGui.TextUnformatted($"Buying on {buying ?? "nowhere"}, selling on {selling ?? "nowhere"}");
        ImGui.SameLine();

        if (ImGui.Button("Refresh"))
            RefreshCatalogue(buying, selling, force: true);

        ImGui.SameLine();

        if (market.Busy)
            ImGui.TextColored(Palette.Dim, "fetching...");
        else if (market.LastError is { } error)
            ImGui.TextColored(Palette.Bad, error);
        else if (market.LastRefresh is { } at)
            ImGui.TextColored(Palette.Dim, $"prices {Phrases.Ago(DateTimeOffset.UtcNow - at)} old");
        else
            ImGui.TextColored(Palette.Dim, "no prices yet");
    }

    private void DrawWhatYouHold(Model current)
    {
        ImGui.TextUnformatted($"Gil {current.Gil:N0}");

        foreach (var group in current.Sinks)
        {
            ImGui.SameLine();
            ImGui.TextColored(Palette.Dim, $"   {group.Currency.Name} {group.Held:N0}");
        }

        if (current.Gathering is { } gathering)
            ImGui.TextColored(Palette.Dim, gathering);
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
            ImGui.TableHeadersRow();

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
                    ImGui.TextColored(Palette.Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Palette.Dim, "no prices yet");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Palette.Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Palette.Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Palette.Dim, row.Venue);
                    continue;
                }

                ImGui.TableNextColumn();
                var leader = group.Best is { } best && Math.Abs(row.Rate!.Value - best) < 0.001d;
                // Only the leader is coloured. Marking everything defeats the point.
                // The unit is printed in the cell, not only in the header. Two decimals beside a
                // column of millions reads as millions, and this number really is under a hundred.
                ImGui.TextColored(leader ? Palette.Good : Palette.Plain, $"{row.Rate!.Value:F2} gil");
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
                ImGui.TextUnformatted($"{row.Profit:N0}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Covers is { } covers ? $"{covers:N0} runs" : "-");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Phrases.Absorb(row.Absorb));

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

        if (!ImGui.BeginTable("flips", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("runs", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("you hold", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("outlay", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("profit", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("return", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableHeadersRow();

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
                ImGui.TextColored(Palette.Dim, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(row.HeldCovers > 0 ? Palette.Good : Palette.Dim, $"{row.HeldCovers}");
                ImGui.TableNextColumn();
                ImGui.TextColored(Palette.Dim, problem);
                continue;
            }

            var tint = row.Idle ? Palette.Dim : Palette.Plain;

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, $"{row.Runs}");
            if (row.Idle && ImGui.IsItemHovered())
                ImGui.SetTooltip("The shared inputs pay more on another row, or your gil will not cover a run.");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.HeldCovers > 0 ? Palette.Good : Palette.Dim, $"{row.HeldCovers}");
            if (row.HeldCovers > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip("Runs your own stock already covers, retainers included. Not deducted from the outlay: what you hold is still worth what the board would pay for it.");

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, $"{row.Outlay:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.Idle ? Palette.Dim : row.Profit > 0 ? Palette.Good : Palette.Bad, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, row.Roi is { } roi ? $"{roi:P1}" : "-");

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, Phrases.Absorb(row.Absorb));
        }

        ImGui.EndTable();
    }

    /// <summary>Where the inputs come from.</summary>
    private Func<uint, OrderBook?> Buying => market.Lookup(scope.Buying ?? "");

    /// <summary>Where the outputs go.</summary>
    private Func<uint, OrderBook?> Selling => market.Lookup(scope.Selling ?? "");

    /// <summary>
    /// Rebuilds the last sweep's shortlist from what came back off disk.
    /// </summary>
    /// <remarks>
    /// Attempted once. A restore that finds nothing usable leaves the sweep idle, and retrying that
    /// every frame would walk the recipe sheet forever.
    /// </remarks>
    private void RestoreSweepOnce(string buying, string selling)
    {
        if (restoreAttempted || market.RestoredSweep is not { } stored)
            return;

        restoreAttempted = true;
        sweep.Restore(stored, buying, selling);
    }

    /// <summary>
    /// Refreshes the catalogue's items, each on the board it belongs to.
    /// </summary>
    /// <remarks>
    /// Inputs and outputs go to different places, and an item can be both, so this is two passes
    /// rather than one over a merged list.
    /// </remarks>
    private void RefreshCatalogue(string? buying, string? selling, bool force = false)
    {
        market.RefreshInBackground(buying, boughtItems, force);
        market.RefreshInBackground(selling, soldItems, force);
    }

    /// <summary>
    /// Writes a finished sweep out once, rather than trusting it to survive to Dispose.
    /// </summary>
    /// <remarks>
    /// On a background task: it is a few hundred kilobytes of gzip and has no business inside a
    /// frame.
    /// </remarks>
    private void PersistFinishedSweep()
    {
        if (sweep.Snapshot() is not { } snapshot || snapshot.At == persistedSweepAt)
            return;

        persistedSweepAt = snapshot.At;
        _ = Task.Run(() => market.Persist(snapshot));
    }

    private Model Build()
    {
        var tax = MarketTax.Standard;

        var sinks = spendableCurrencies
            .Select(currency => BuildSinkGroup(currency, tax))
            .ToArray();

        var allocated = ConversionAllocation
            .Allocate(flips, Buying, Selling, tax, balances.Gil, config.SizingCap)
            .ToDictionary(allocation => allocation.Conversion.Id, StringComparer.Ordinal);

        var flipRows = flips.Select(conversion => BuildFlipRow(conversion, allocated, tax)).ToArray();

        return new Model(
            balances.Gil,
            sinks,
            flipRows,
            allocated.Values.Sum(allocation => allocation.Profit),
            GatheringLine());
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

        var rows = catalog.Conversions
            .Where(conversion => conversion.Consumes(currency) > 0)
            .Select(conversion =>
            {
                var quote = ConversionEvaluator.Evaluate(conversion, 1, Buying, Selling, tax);
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
        IReadOnlyDictionary<string, Allocation> allocated,
        MarketTax tax)
    {
        var single = ConversionEvaluator.Evaluate(conversion, 1, Buying, Selling, tax);

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

    /// <summary>
    /// What GatherBuddyReborn is up to, reported and not driven.
    /// </summary>
    /// <remarks>
    /// Starting it from here would duplicate its own window with less capability, since that is
    /// where the list lives. This is read because it is the clock any measured earning rate
    /// will have to run against.
    /// </remarks>
    private string? GatheringLine()
    {
        if (!gatherBuddy.Responding)
            return null;

        if (gatherBuddy.TooOld)
            return "GatherBuddyReborn is older than this was written against.";

        if (!gatherBuddy.AutoGathering)
            return "GatherBuddyReborn idle";

        var status = gatherBuddy.Status;

        return string.IsNullOrWhiteSpace(status)
            ? "GatherBuddyReborn gathering"
            : gatherBuddy.Waiting
                ? $"GatherBuddyReborn waiting: {status}"
                : $"GatherBuddyReborn: {status}";
    }

    /// <summary>Called on open so a stale window fills itself in without being asked.</summary>
    public override void OnOpen() => RefreshCatalogue(scope.Buying, scope.Selling);

    public override void OnClose() => save();

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

    private sealed record Model(
        long Gil,
        SinkGroup[] Sinks,
        FlipRow[] Flips,
        long TotalFlipProfit,
        string? Gathering);
}
