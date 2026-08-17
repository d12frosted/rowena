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
/// One window: what you are holding, what it is worth turning into, and who to hand the
/// legwork to.
/// </summary>
/// <remarks>
/// Deliberately not a market browser. The only questions it answers are the ones that need
/// both halves of the picture, your balances and the depth of the board, because either one
/// alone is already covered by something else.
/// </remarks>
internal sealed class MainWindow : Window
{
    /// <summary>Handoff hints this window knows how to act on. Anything else is ignored.</summary>
    private const string GatherCollectables = "gather-collectables";

    private static readonly Vector4 Dim = new(0.60f, 0.60f, 0.62f, 1f);
    private static readonly Vector4 Good = new(0.40f, 0.80f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.85f, 0.45f, 0.40f, 1f);

    private readonly ConversionCatalog catalog;
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly Configuration config;
    private readonly Action save;

    private readonly uint[] pricedItems;
    private readonly Resource[] spendableCurrencies;
    private readonly bool anythingToHandOff;

    public MainWindow(
        ConversionCatalog catalog,
        MarketCache market,
        Balances balances,
        GatherBuddyIpc gatherBuddy,
        Configuration config,
        Action save)
        : base("Rowena###rowena-main")
    {
        this.catalog = catalog;
        this.market = market;
        this.balances = balances;
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

        anythingToHandOff = catalog.Conversions.Any(conversion => conversion.Handoff is not null);
    }

    /// <summary>Where prices are read from: what you set, or wherever you are logged in.</summary>
    private string? Scope =>
        string.IsNullOrWhiteSpace(config.Scope) ? balances.DataCentre : config.Scope;

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        if (Scope is null)
        {
            ImGui.TextColored(Bad, "Not logged in, and no data centre set. Prices cannot be fetched.");
            return;
        }

        DrawWhatYouHold();
        DrawGathering();
        ImGui.Separator();
        DrawSinks();
        ImGui.Spacing();
        DrawFlips();
    }

    private void DrawHeader()
    {
        ImGui.TextUnformatted($"Pricing against {Scope ?? "nowhere"}");
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

    private void DrawGathering()
    {
        // Nothing in the catalogue wants gathering, so do not mention gathering.
        if (!anythingToHandOff)
            return;

        if (!gatherBuddy.Responding)
        {
            ImGui.TextColored(Dim, "GatherBuddyReborn not found, so there is nothing to hand the gathering to.");
            return;
        }

        if (gatherBuddy.TooOld)
            ImGui.TextColored(Bad, "GatherBuddyReborn is older than this was written against; some of it may not work.");

        var running = gatherBuddy.AutoGathering;

        if (ImGui.Button(running ? "Stop auto-gather" : "Start auto-gather"))
            gatherBuddy.SetAutoGathering(!running);

        ImGui.SameLine();

        if (ImGui.Button("Stop collecting"))
            gatherBuddy.StopCollectables();

        ImGui.SameLine();

        var status = gatherBuddy.Status;
        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextColored(Dim, gatherBuddy.Waiting ? $"waiting: {status}" : status);
        else
            ImGui.TextColored(Dim, running ? "auto-gather on" : "auto-gather off");
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

            if (!ImGui.BeginTable($"sinks-{currency.Id}", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                continue;

            ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("gil each", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("net per run", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("held covers", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 270);
            ImGui.TableSetupColumn("earn it", ImGuiTableColumnFlags.WidthFixed, 90);
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

                ImGui.TableNextColumn();
                DrawEarnIt(row.Conversion, cannotRunYet: held < perRun);
            }

            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The handoff button, when the catalogue says how this currency is earned and something
    /// is installed that can go and earn it.
    /// </summary>
    private void DrawEarnIt(Conversion conversion, bool cannotRunYet)
    {
        if (conversion.Handoff != GatherCollectables || !gatherBuddy.Responding)
        {
            ImGui.TextColored(Dim, "-");
            return;
        }

        // Offered whatever your balance, since topping up early is reasonable, but only
        // emphasised when you actually cannot run the trade yet.
        ImGui.PushID(conversion.Id);
        if (ImGui.Button(cannotRunYet ? "Collect" : "collect"))
            gatherBuddy.StartCollectables();
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs /gatherbuddy collect. It reports nothing back, so stop it from the button above.");
    }

    private void DrawFlips()
    {
        // Trades with no bound currency in them: pure gil in, more gil out, no gameplay.
        var flips = catalog.Conversions
            .Where(conversion => conversion.Inputs.All(input => input.Resource.Kind == ResourceKind.Item))
            .ToArray();

        if (flips.Length == 0)
            return;

        ImGui.TextUnformatted("Flips: buy the inputs, convert, sell the output");

        if (!ImGui.BeginTable("flips", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("Trade", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("outlay", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("profit", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("return", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("runs", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("to clear", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("or gather", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableHeadersRow();

        foreach (var conversion in flips)
        {
            var quote = ConversionEvaluator.Evaluate(conversion, 1, market.Lookup, MarketTax.Standard);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(conversion.Name);

            if (!quote.IsExecutable)
            {
                ImGui.TableNextColumn();
                var why = quote.Unsourced.Count > 0
                    ? $"short {string.Join(", ", quote.Unsourced)}"
                    : $"no price for {string.Join(", ", quote.Unpriced)}";
                ImGui.TextColored(Dim, why);

                // Skip to the last column so the gather offer still lands under its header.
                for (var column = 0; column < 4; column++)
                    ImGui.TableNextColumn();

                ImGui.TableNextColumn();
                DrawGatherShortfall(conversion, quote);
                continue;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{quote.GilOutlay:N0}");

            ImGui.TableNextColumn();
            ImGui.TextColored(quote.Profit > 0 ? Good : Bad, $"{quote.Profit:N0}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(quote.ReturnOnOutlay is { } roi ? $"{roi:P1}" : "-");

            ImGui.TableNextColumn();
            var size = ConversionEvaluator.LargestProfitableSize(
                conversion, market.Lookup, MarketTax.Standard, config.SizingCap);
            // The number the floor never tells you: how many the book can actually take.
            ImGui.TextUnformatted($"{size}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Absorb(quote.DaysToAbsorb));

            ImGui.TableNextColumn();
            DrawGatherShortfall(conversion, quote);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Offers to gather an input the board could not supply, when it is something that can
    /// be gathered at all.
    /// </summary>
    /// <remarks>
    /// The gatherable check matters. Without asking GatherBuddyReborn to identify the item
    /// first, this would cheerfully offer to go and gather a Mount Token.
    /// </remarks>
    private void DrawGatherShortfall(Conversion conversion, ConversionQuote quote)
    {
        if (!gatherBuddy.Responding)
        {
            ImGui.TextColored(Dim, "-");
            return;
        }

        var candidate = quote.Unsourced
            .Select(amount => amount.Resource)
            .FirstOrDefault(resource =>
                resource.Kind == ResourceKind.Item && gatherBuddy.IsGatherable(resource.Name));

        if (candidate.Id == 0)
        {
            ImGui.TextColored(Dim, "-");
            return;
        }

        ImGui.PushID($"{conversion.Id}-gather");
        if (ImGui.Button($"Gather {candidate.Name}"))
            gatherBuddy.Gather(candidate.Name);
        ImGui.PopID();
    }

    /// <summary>Called on open so a stale window fills itself in without being asked.</summary>
    public override void OnOpen() => market.RefreshInBackground(pricedItems);

    public override void OnClose() => save();

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
