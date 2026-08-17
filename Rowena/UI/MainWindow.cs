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
    /// <summary>
    /// How often the numbers are recomputed. Nothing here changes faster than the eye, and a
    /// scrip balance that lags by a fraction of a second has never misled anyone.
    /// </summary>
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>How many craft rows the table shows. The count it was trimmed from is shown too.</summary>
    private const int CraftsInTable = 25;

    private static readonly Vector4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
    private static readonly Vector4 Plain = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Good = new(0.40f, 0.80f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.85f, 0.45f, 0.40f, 1f);

    private readonly ConversionCatalog catalog;
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly PricingScope scope;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly FurnishingSweep sweep;
    private readonly Furnishings furnishings;
    private readonly ItemCells cells;
    private readonly Configuration config;
    private readonly Action save;

    private readonly uint[] pricedItems;
    private readonly Resource[] spendableCurrencies;
    private readonly Conversion[] flips;

    private Model? model;
    private DateTime builtAt;
    private bool restoreAttempted;
    private long persistedSweepAt;

    public MainWindow(
        ConversionCatalog catalog,
        MarketCache market,
        Balances balances,
        PricingScope scope,
        GatherBuddyIpc gatherBuddy,
        FurnishingSweep sweep,
        Furnishings furnishings,
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
        this.furnishings = furnishings;
        this.cells = cells;
        this.config = config;
        this.save = save;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Every tradable thing the catalogue mentions, on either side of any trade. This is
        // the whole fetch list, and it is small enough to be one batched request.
        pricedItems =
        [
            .. catalog.Conversions
                .SelectMany(conversion => conversion.Inputs.Concat(conversion.Outputs))
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
    }

    public override void Draw()
    {
        var where = scope.Current;

        DrawHeader(where);
        ImGui.Separator();

        if (where is null)
        {
            ImGui.TextColored(Bad, "Not logged in, and no data centre set. Prices cannot be fetched.");
            return;
        }

        // Prices saved by a previous session, as soon as there is a board to compare them against.
        market.RestoreOnce(where, SweepMaxAge);
        RestoreSweepOnce();
        PersistFinishedSweep(where);

        var current = Current();

        DrawWhatYouHold(current);
        ImGui.Separator();
        DrawSinks(current);
        ImGui.Spacing();
        DrawFlips(current);
        ImGui.Spacing();
        DrawCrafts(current, where);
    }

    private void DrawCrafts(Model current, string where)
    {
        ImGui.TextUnformatted("Crafts: furnishings, ranked by what they would earn in a day");
        ImGui.SameLine();

        if (sweep.Running)
        {
            ImGui.TextColored(Dim, $"  {sweep.Detail}");
            return;
        }

        if (ImGui.Button(sweep.ReadyAt is null ? "Sweep" : "Re-sweep"))
            sweep.Start(where, config.PriceBatchSize, config.FurnishingShortlist, SweepMaxAge);

        ImGui.SameLine();

        if (sweep.State == FurnishingSweep.Phase.Failed)
        {
            ImGui.TextColored(Bad, sweep.Detail);
            return;
        }

        if (sweep.ReadyAt is null)
        {
            // Said plainly, because it is minutes of small polite requests and should not start
            // itself the first time the window happens to open.
            ImGui.TextColored(
                Dim,
                $"  not swept yet. Twenty ids a request, so this takes a few minutes.");
            return;
        }

        // Never a silent cap: the table is trimmed for legibility and says by how much. The age is
        // shown because a restored sweep can be hours old, and that is fine for choosing what to
        // make but should not be mistaken for live depth.
        var age = sweep.ReadyAt is { } at ? $"swept {Ago(DateTimeOffset.UtcNow - at)} ago, " : "";

        ImGui.TextColored(
            Dim,
            $"  {age}{sweep.Detail}, showing {current.Crafts.Length} of {current.CraftsRanked}"
            + (current.CraftsDiscarded > 0 ? $", {current.CraftsDiscarded} unpriceable" : ""));

        // Which materials are doing the blocking. This is the evidence for whether following
        // recipes down to raw materials is worth building, so it belongs on screen and not
        // only in the log.
        if (sweep.Blockers.Count > 0)
        {
            var worst = string.Join(
                ", ",
                sweep.Blockers.Take(4).Select(blocker => $"{blocker.Material} ({blocker.Blocks})"));

            ImGui.TextColored(Dim, $"    blocked mostly by: {worst}");
        }

        if (current.Crafts.Length == 0)
            return;

        if (!ImGui.BeginTable("crafts", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Furnishing", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("job", ImGuiTableColumnFlags.WidthFixed, 45);
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
            ImGui.TextColored(Dim, row.Job);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.Materials:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.Profit > 0 ? Good : Bad, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Roi is { } roi ? $"{roi:P0}" : "-");

            ImGui.TableNextColumn();
            ImGui.TextColored(Dim, $"{row.SalesPerDay:F1}");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.GilPerDay > 0 ? Good : Dim, $"{row.GilPerDay:N0}");
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

    private void DrawHeader(string? where)
    {
        ImGui.TextUnformatted($"Pricing against {where ?? "nowhere"}");
        ImGui.SameLine();

        if (ImGui.Button("Refresh"))
            market.RefreshInBackground(where, pricedItems, force: true);

        ImGui.SameLine();

        if (market.Busy)
            ImGui.TextColored(Dim, "fetching...");
        else if (market.LastError is { } error)
            ImGui.TextColored(Bad, error);
        else if (market.LastRefresh is { } at)
            ImGui.TextColored(Dim, $"prices {Ago(DateTimeOffset.UtcNow - at)} old");
        else
            ImGui.TextColored(Dim, "no prices yet");
    }

    private void DrawWhatYouHold(Model current)
    {
        ImGui.TextUnformatted($"Gil {current.Gil:N0}");

        foreach (var group in current.Sinks)
        {
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"   {group.Currency.Name} {group.Held:N0}");
        }

        if (current.Gathering is { } gathering)
            ImGui.TextColored(Dim, gathering);
    }

    private void DrawSinks(Model current)
    {
        ImGui.TextUnformatted("Sinks: what a bound currency is worth once converted and sold");

        foreach (var group in current.Sinks)
        {
            if (group.Rows.Length == 0)
                continue;

            ImGui.Spacing();
            ImGui.TextColored(Dim, $"{group.Currency.Name} ({group.Held:N0} held)");

            if (!ImGui.BeginTable($"sinks-{group.Currency.Id}", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                continue;

            ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("gil each", ImGuiTableColumnFlags.WidthFixed, 80);
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
                    ImGui.TextColored(Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Dim, "no prices yet");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Dim, "-");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Dim, row.Venue);
                    continue;
                }

                ImGui.TableNextColumn();
                var leader = group.Best is { } best && Math.Abs(row.Rate!.Value - best) < 0.001d;
                // Only the leader is coloured. Marking everything defeats the point.
                ImGui.TextColored(leader ? Good : Plain, $"{row.Rate!.Value:F2}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{row.Profit:N0}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Covers is { } covers ? $"{covers:N0} runs" : "-");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Absorb(row.Absorb));

                ImGui.TableNextColumn();
                ImGui.TextColored(Dim, row.Venue);
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
            ImGui.TextColored(Good, $"  best split of your gil pays {current.TotalFlipProfit:N0}");
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
                ImGui.TextColored(Dim, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(row.HeldCovers > 0 ? Good : Dim, $"{row.HeldCovers}");
                ImGui.TableNextColumn();
                ImGui.TextColored(Dim, problem);
                continue;
            }

            var tint = row.Idle ? Dim : Plain;

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, $"{row.Runs}");
            if (row.Idle && ImGui.IsItemHovered())
                ImGui.SetTooltip("The shared inputs pay more on another row, or your gil will not cover a run.");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.HeldCovers > 0 ? Good : Dim, $"{row.HeldCovers}");
            if (row.HeldCovers > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip("Runs your own stock already covers, retainers included. Not deducted from the outlay: what you hold is still worth what the board would pay for it.");

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, $"{row.Outlay:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(row.Idle ? Dim : row.Profit > 0 ? Good : Bad, $"{row.Profit:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, row.Roi is { } roi ? $"{roi:P1}" : "-");

            ImGui.TableNextColumn();
            ImGui.TextColored(tint, Absorb(row.Absorb));
        }

        ImGui.EndTable();
    }

    private TimeSpan SweepMaxAge => TimeSpan.FromHours(Math.Max(1, config.SweepMaxAgeHours));

    /// <summary>
    /// Rebuilds the last sweep's shortlist from what came back off disk.
    /// </summary>
    /// <remarks>
    /// Attempted once. A restore that finds nothing usable leaves the sweep idle, and retrying that
    /// every frame would walk the recipe sheet forever.
    /// </remarks>
    private void RestoreSweepOnce()
    {
        if (restoreAttempted || market.RestoredSweep is not { } stored)
            return;

        restoreAttempted = true;
        sweep.Restore(stored);
    }

    /// <summary>
    /// Writes a finished sweep out once, rather than trusting it to survive to Dispose.
    /// </summary>
    /// <remarks>
    /// On a background task: it is a few hundred kilobytes of gzip and has no business inside a
    /// frame.
    /// </remarks>
    private void PersistFinishedSweep(string where)
    {
        if (sweep.Snapshot() is not { } snapshot || snapshot.At == persistedSweepAt)
            return;

        persistedSweepAt = snapshot.At;
        _ = Task.Run(() => market.Persist(where, snapshot));
    }

    /// <summary>The current numbers, rebuilt only when they have had time to change.</summary>
    private Model Current()
    {
        if (model is not null && DateTime.UtcNow - builtAt < RebuildEvery)
            return model;

        model = Build();
        builtAt = DateTime.UtcNow;
        return model;
    }

    private Model Build()
    {
        var tax = MarketTax.Standard;

        var sinks = spendableCurrencies
            .Select(currency => BuildSinkGroup(currency, tax))
            .ToArray();

        var allocated = ConversionAllocation
            .Allocate(flips, market.Lookup, tax, balances.Gil, config.SizingCap)
            .ToDictionary(allocation => allocation.Conversion.Id, StringComparer.Ordinal);

        var flipRows = flips.Select(conversion => BuildFlipRow(conversion, allocated, tax)).ToArray();

        var (crafts, ranked, discarded) = BuildCrafts(tax);

        return new Model(
            balances.Gil,
            sinks,
            flipRows,
            allocated.Values.Sum(allocation => allocation.Profit),
            GatheringLine(),
            crafts,
            ranked,
            discarded);
    }

    /// <summary>Ranks the swept furnishings, trims the table, and counts what could not be priced.</summary>
    /// <remarks>
    /// The discard count is not decoration. It is the measurement that decides whether following
    /// recipe trees down to raw materials is worth building: if a handful of furnishings are lost
    /// to an untraded intermediate, direct ingredients are enough, and if a third of them are,
    /// they are not.
    /// </remarks>
    private (CraftRow[] Rows, int Ranked, int Discarded) BuildCrafts(MarketTax tax)
    {
        if (sweep.State != FurnishingSweep.Phase.Ready || sweep.Shortlist.Count == 0)
            return ([], 0, 0);

        var cap = config.CraftsPerDayCap > 0 ? config.CraftsPerDayCap : (double?)null;
        var ranked = ConversionRanking.ByGilPerDay(sweep.Shortlist, market.Lookup, tax, cap);

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
                    earnings.Conversion.Venue,
                    earnings.Quote.GilOutlay,
                    earnings.Quote.Profit,
                    earnings.Quote.ReturnOnOutlay,
                    earnings.RunsPerDay,
                    earnings.GilPerDay,
                    Breakdown(earnings.Conversion));
            })
            .ToArray();

        return (rows, priceable.Length, ranked.Count - priceable.Length);
    }

    /// <summary>
    /// The tradable thing a conversion ends up with, when there is exactly one worth showing.
    /// </summary>
    private static uint? Produced(Conversion conversion) =>
        conversion.Outputs
            .Where(output => output.Resource.Kind == ResourceKind.Item)
            .Select(output => (uint?)output.Resource.Id)
            .FirstOrDefault();

    /// <summary>What each material costs, for the tooltip.</summary>
    private ItemCells.MaterialLine[] Breakdown(Conversion conversion) =>
    [
        .. conversion.Inputs
            .Where(input => input.Resource.Kind == ResourceKind.Item)
            .Select(input =>
            {
                var quote = market.Book(input.Resource.Id)?.CostToBuy(input.Quantity);

                return new ItemCells.MaterialLine(
                    input.Resource.Id,
                    input.Resource.Name,
                    input.Quantity,
                    quote?.Total ?? 0,
                    quote is { IsComplete: true });
            }),
    ];

    private SinkGroup BuildSinkGroup(Resource currency, MarketTax tax)
    {
        var held = balances.Held(currency);

        var rows = catalog.Conversions
            .Where(conversion => conversion.Consumes(currency) > 0)
            .Select(conversion =>
            {
                var quote = ConversionEvaluator.Evaluate(conversion, 1, market.Lookup, tax);
                var perRun = conversion.Consumes(currency);

                return new SinkRow(
                    conversion.Name,
                    Produced(conversion),
                    quote.IsExecutable ? quote.GilPer(currency) : null,
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

        return new SinkGroup(currency, held, rows, rows.Any(row => row.Priced) ? best : null);
    }

    private FlipRow BuildFlipRow(
        Conversion conversion,
        IReadOnlyDictionary<string, Allocation> allocated,
        MarketTax tax)
    {
        var single = ConversionEvaluator.Evaluate(conversion, 1, market.Lookup, tax);

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
    public override void OnOpen() => market.RefreshInBackground(scope.Current, pricedItems);

    public override void OnClose() => save();

    private static double? Multiply(double? days, int factor) => days is { } value ? value * factor : null;

    private static string Absorb(double? days) => days switch
    {
        null => "never",
        < 1d => "<1 day",
        _ => $"{days.Value:F1} days",
    };

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "seconds",
        { TotalHours: < 1 } => $"{span.TotalMinutes:F0} min",
        _ => $"{span.TotalHours:F0} h",
    };

    private sealed record SinkRow(
        string Trade,
        uint? ItemId,
        double? Rate,
        long Profit,
        long? Covers,
        double? Absorb,
        string Venue,
        bool Priced);

    private sealed record SinkGroup(Resource Currency, long Held, SinkRow[] Rows, double? Best);

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

    private sealed record CraftRow(
        string Item,
        uint ItemId,
        uint? RecipeId,
        string Job,
        long Materials,
        long Profit,
        double? Roi,
        double SalesPerDay,
        long GilPerDay,
        ItemCells.MaterialLine[] Breakdown);

    private sealed record Model(
        long Gil,
        SinkGroup[] Sinks,
        FlipRow[] Flips,
        long TotalFlipProfit,
        string? Gathering,
        CraftRow[] Crafts,
        int CraftsRanked,
        int CraftsDiscarded);
}
