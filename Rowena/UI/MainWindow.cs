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
/// </remarks>
internal sealed class MainWindow : Window
{
    private static readonly Vector4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
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
        DrawHeader();
        ImGui.Separator();

        if (scope.Current is null)
        {
            ImGui.TextColored(Bad, "Not logged in, and no data centre set. Prices cannot be fetched.");
            return;
        }

        DrawWhatYouHold();
        DrawGatheringState();
        ImGui.Separator();
        DrawSinks();
        ImGui.Spacing();
        DrawFlips();
    }

    private void DrawHeader()
    {
        ImGui.TextUnformatted($"Pricing against {scope.Current ?? "nowhere"}");
        ImGui.SameLine();

        if (ImGui.Button("Refresh"))
            market.RefreshInBackground(pricedItems, force: true);

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

    private void DrawWhatYouHold()
    {
        ImGui.TextUnformatted($"Gil {balances.Gil:N0}");

        foreach (var currency in spendableCurrencies)
        {
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"   {currency.Name} {balances.Held(currency):N0}");
        }
    }

    /// <summary>
    /// What GatherBuddyReborn is up to, reported and not driven.
    /// </summary>
    /// <remarks>
    /// One line, no buttons. Starting it from here would duplicate its own window with less
    /// capability, since that is where the list lives. This is here because it is the clock
    /// any measured earning rate will have to run against.
    /// </remarks>
    private void DrawGatheringState()
    {
        if (!gatherBuddy.Responding)
            return;

        if (gatherBuddy.TooOld)
        {
            ImGui.TextColored(Bad, "GatherBuddyReborn is older than this was written against.");
            return;
        }

        var status = gatherBuddy.Status;

        if (!gatherBuddy.AutoGathering)
        {
            ImGui.TextColored(Dim, "GatherBuddyReborn idle");
            return;
        }

        ImGui.TextColored(
            Dim,
            string.IsNullOrWhiteSpace(status)
                ? "GatherBuddyReborn gathering"
                : gatherBuddy.Waiting ? $"GatherBuddyReborn waiting: {status}" : $"GatherBuddyReborn: {status}");
    }

    private void DrawSinks()
    {
        ImGui.TextUnformatted("Sinks: what a bound currency is worth once converted and sold");

        foreach (var currency in spendableCurrencies)
        {
            var held = balances.Held(currency);

            var rows = catalog.Conversions
                .Where(conversion => conversion.Consumes(currency) > 0)
                .Select(conversion => new
                {
                    Conversion = conversion,
                    Quote = ConversionEvaluator.Evaluate(conversion, 1, market.Lookup, MarketTax.Standard),
                })
                .Select(row => new { row.Conversion, row.Quote, Rate = row.Quote.GilPer(currency) })
                .Where(row => row.Rate is not null)
                .OrderByDescending(row => row.Rate!.Value)
                .ToArray();

            if (rows.Length == 0)
                continue;

            ImGui.Spacing();
            ImGui.TextColored(Dim, $"{currency.Name} ({held:N0} held)");

            if (!ImGui.BeginTable($"sinks-{currency.Id}", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                continue;

            ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("gil each", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("net per run", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("held covers", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 270);
            ImGui.TableHeadersRow();

            var best = rows[0].Rate!.Value;

            foreach (var row in rows)
            {
                var perRun = row.Conversion.Consumes(currency);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Conversion.Name);

                ImGui.TableNextColumn();
                var rate = row.Rate!.Value;
                // Only the leader is coloured. Marking everything defeats the point.
                if (Math.Abs(rate - best) < 0.001d)
                    ImGui.TextColored(Good, $"{rate:F2}");
                else
                    ImGui.TextUnformatted($"{rate:F2}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{row.Quote.Profit:N0}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(perRun == 0 ? "-" : $"{held / perRun:N0} runs");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Absorb(row.Quote.DaysToAbsorb));

                ImGui.TableNextColumn();
                ImGui.TextColored(Dim, row.Conversion.Venue);
            }

            ImGui.EndTable();
        }
    }

    private void DrawFlips()
    {
        if (flips.Length == 0)
            return;

        // Sized together, not one at a time. Two trades wanting the same tokens would each
        // report that the book covers them, when between them it covers one.
        var allocated = ConversionAllocation
            .Allocate(flips, market.Lookup, MarketTax.Standard, balances.Gil, config.SizingCap)
            .ToDictionary(allocation => allocation.Conversion.Id, StringComparer.Ordinal);

        var total = allocated.Values.Sum(allocation => allocation.Profit);

        ImGui.TextUnformatted("Flips: buy the inputs, convert, sell the output");

        if (total > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Good, $"  best split of your gil pays {total:N0}");
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

        foreach (var conversion in flips)
        {
            var single = ConversionEvaluator.Evaluate(conversion, 1, market.Lookup, MarketTax.Standard);
            var allocation = allocated.GetValueOrDefault(conversion.Id);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(conversion.Name);

            if (!single.IsExecutable)
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(Dim, "-");

                ImGui.TableNextColumn();
                ImGui.TextColored(
                    Dim,
                    single.Unsourced.Count > 0
                        ? $"short {string.Join(", ", single.Unsourced)}"
                        : $"no price for {string.Join(", ", single.Unpriced)}");
                continue;
            }

            // Nothing allocated means the shared inputs earn more elsewhere. The row still
            // shows what one run would pay, dimmed, so the comparison is visible rather than
            // just absent.
            var idle = allocation is null || allocation.Runs == 0;
            var runs = idle ? 0 : allocation!.Runs;
            var outlay = idle ? single.GilOutlay : allocation!.GilOutlay;
            var profit = idle ? single.Profit : allocation!.Profit;
            var roi = idle ? single.ReturnOnOutlay : allocation!.ReturnOnOutlay;

            ImGui.TableNextColumn();
            if (idle)
            {
                ImGui.TextColored(Dim, "0");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("The shared inputs pay more on another row, or your gil will not cover a run.");
            }
            else
            {
                ImGui.TextUnformatted($"{runs}");
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(idle ? Dim : new Vector4(1f, 1f, 1f, 1f), $"{outlay:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(idle ? Dim : profit > 0 ? Good : Bad, $"{profit:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(idle ? Dim : new Vector4(1f, 1f, 1f, 1f), roi is { } value ? $"{value:P1}" : "-");

            ImGui.TableNextColumn();
            // Absorption for what is actually allocated, since three mounts take three sales.
            var absorbing = idle ? single.DaysToAbsorb : Multiply(single.DaysToAbsorb, runs);
            ImGui.TextColored(idle ? Dim : new Vector4(1f, 1f, 1f, 1f), Absorb(absorbing));
        }

        ImGui.EndTable();
    }

    /// <summary>Called on open so a stale window fills itself in without being asked.</summary>
    public override void OnOpen() => market.RefreshInBackground(pricedItems);

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
}
