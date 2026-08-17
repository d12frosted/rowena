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

    private static readonly Vector4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
    private static readonly Vector4 Plain = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Good = new(0.40f, 0.80f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.85f, 0.45f, 0.40f, 1f);

    private readonly ConversionCatalog catalog;
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly PricingScope scope;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly Configuration config;
    private readonly Action save;

    private readonly uint[] pricedItems;
    private readonly Resource[] spendableCurrencies;
    private readonly Conversion[] flips;

    private Model? model;
    private DateTime builtAt;

    public MainWindow(
        ConversionCatalog catalog,
        MarketCache market,
        Balances balances,
        PricingScope scope,
        GatherBuddyIpc gatherBuddy,
        Configuration config,
        Action save)
        : base("Rowena###rowena-main")
    {
        this.catalog = catalog;
        this.market = market;
        this.balances = balances;
        this.scope = scope;
        this.gatherBuddy = gatherBuddy;
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

        var current = Current();

        DrawWhatYouHold(current);
        ImGui.Separator();
        DrawSinks(current);
        ImGui.Spacing();
        DrawFlips(current);
    }

    private void DrawHeader(string? where)
    {
        ImGui.TextUnformatted($"Pricing against {where ?? "nowhere"}");
        ImGui.SameLine();

        if (ImGui.Button("Refresh"))
            market.RefreshInBackground(where, pricedItems, force: true);

        ImGui.SameLine();

        if (market.Refreshing)
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

        if (!ImGui.BeginTable("flips", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("runs", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("outlay", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("profit", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("return", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableHeadersRow();

        foreach (var row in current.Flips)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Trade);

            if (row.Problem is { } problem)
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(Dim, "-");
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

        return new Model(
            balances.Gil,
            sinks,
            flipRows,
            allocated.Values.Sum(allocation => allocation.Profit),
            GatheringLine());
    }

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

        if (!single.IsExecutable)
        {
            var problem = single.Unsourced.Count > 0
                ? $"short {string.Join(", ", single.Unsourced)}"
                : $"no price for {string.Join(", ", single.Unpriced)}";

            return new FlipRow(conversion.Name, 0, 0, 0, null, null, true, problem);
        }

        var allocation = allocated.GetValueOrDefault(conversion.Id);

        // Nothing allocated means the shared inputs earn more elsewhere. The row still shows
        // what one run would pay, dimmed, so the comparison is visible rather than absent.
        var idle = allocation is null || allocation.Runs == 0;

        return idle
            ? new FlipRow(
                conversion.Name, 0, single.GilOutlay, single.Profit, single.ReturnOnOutlay,
                single.DaysToAbsorb, true, null)
            : new FlipRow(
                conversion.Name, allocation!.Runs, allocation.GilOutlay, allocation.Profit,
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
        double? Rate,
        long Profit,
        long? Covers,
        double? Absorb,
        string Venue,
        bool Priced);

    private sealed record SinkGroup(Resource Currency, long Held, SinkRow[] Rows, double? Best);

    private sealed record FlipRow(
        string Trade,
        int Runs,
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
