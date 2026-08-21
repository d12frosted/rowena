using Dalamud.Bindings.ImGui;
using Rowena.Core.Conversions;
using Rowena.Game;
using Rowena.IPC;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// Where you are pricing, what you are holding, and how old the answer is.
/// </summary>
/// <remarks>
/// Above the tabs rather than inside one, because every tab is read against it. A rate in gil per
/// scrip means nothing without the scrip count beside it, and no number here means anything without
/// knowing which boards produced it or how long ago.
///
/// It carries one clock, the price cache's. The sweep has its own and it stays next to the sweep
/// button, since a strip claiming a single age for two things fetched hours apart would be worse than
/// silent.
/// </remarks>
internal sealed class StatusStrip
{
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly Trades trades;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly ItemCells cells;
    private readonly Action refresh;

    private readonly Rebuilt<Wallet> wallet;

    public StatusStrip(
        MarketCache market,
        Balances balances,
        Trades trades,
        GatherBuddyIpc gatherBuddy,
        ItemCells cells,
        Action refresh)
    {
        this.market = market;
        this.balances = balances;
        this.trades = trades;
        this.gatherBuddy = gatherBuddy;
        this.cells = cells;
        this.refresh = refresh;

        wallet = new Rebuilt<Wallet>(Build);
    }

    public void Draw(string? buying, string? selling)
    {
        // Both boards named, because they are different and the difference is the point.
        ImGui.TextUnformatted($"Buying on {buying ?? "nowhere"}, selling on {selling ?? "nowhere"}");
        ImGui.SameLine();

        // Named for what it fetches. The sweep is also a refresh and shares none of this button's
        // cost, so an unqualified "Refresh" was an invitation to press the wrong one.
        if (ImGui.Button("Refresh prices"))
            refresh();

        ImGui.SameLine();

        if (market.Busy)
            ImGui.TextColored(
                Palette.Dim,
                market.Progress is { } progress ? $"fetching {progress.Done} of {progress.Total}" : "fetching...");
        else if (market.LastError is { } error)
            ImGui.TextColored(Palette.Bad, error);
        else if (market.LastRefresh is { } at)
            ImGui.TextColored(Palette.Dim, $"prices {Phrases.Ago(DateTimeOffset.UtcNow - at)} old");
        else
            ImGui.TextColored(Palette.Dim, "no prices yet");

        var current = wallet.Current;

        ImGui.TextUnformatted($"Gil {current.Gil:N0}");

        foreach (var (currency, held, cap) in current.Currencies)
        {
            // Icon and number, name on hover. The icon is how a currency is recognised in the
            // game, and a row of them reads at a glance where a row of names was a paragraph.
            var close = cap is { } max && held >= max - max / 10;
            var text = cap is { } limit ? $"{held:N0}/{limit:N0}" : $"{held:N0}";

            Flow(16f + 4f + ImGui.CalcTextSize(text).X);
            cells.Icon(currency.Id, 16f);
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(close ? Palette.Bad : Palette.Dim, text);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    close
                        ? $"{currency.Name}: nearly capped. Anything earned past the cap is simply lost."
                        : currency.Name);
            }
        }

        if (current.Gathering is { } gathering)
            ImGui.TextColored(Palette.Dim, gathering);
    }

    /// <summary>
    /// Continues the current line if a piece this wide fits, and starts a new one if it does not.
    /// </summary>
    /// <remarks>
    /// ImGui wraps text, not items. A chain of SameLine that runs off the right edge gives the
    /// window a horizontal scroll and drags the tables into it, so the line is broken by hand
    /// before the piece that would not fit. Rarely reached now that the strip is short, and
    /// kept so that it cannot happen again.
    /// </remarks>
    private static void Flow(float width)
    {
        ImGui.SameLine(0f, 14f);

        if (width > ImGui.GetWindowContentRegionMax().X - ImGui.GetCursorPosX())
            ImGui.NewLine();
    }

    /// <summary>What is in your pockets that every tab should be read against.</summary>
    /// <remarks>
    /// Not everything in them. Every currency you hold has a table in the Sinks tab, with its
    /// count beside its name, and listing all twenty here as well was a paragraph nobody read.
    /// What stays is what changes how the rest of the window is read: the currencies the file
    /// declares an interest in, and any currency within sight of its cap, since a cap is a
    /// decision wherever you are looking.
    /// </remarks>
    private Wallet Build() =>
        new(
            balances.Gil,
            [
                .. trades.Currencies
                    .Select(currency => (Currency: currency, Held: balances.Held(currency), Cap: balances.CapOf(currency)))
                    .Where(entry => trades.IsWatched(entry.Currency)
                        || entry.Cap is { } cap && entry.Held * 2 >= cap)
                    .Select(entry => (entry.Currency, entry.Held, InSight(entry.Held, entry.Cap))),
            ],
            GatheringLine());

    /// <summary>The cap, once it is close enough to be worth the width of printing.</summary>
    private static long? InSight(long held, long? cap) => cap is { } limit && held * 2 >= limit ? limit : null;

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

    /// <param name="Currencies">
    /// Each spendable currency, how much of it you are holding, and the cap when the game
    /// enforces one.
    /// </param>
    private sealed record Wallet(long Gil, (Resource Currency, long Held, long? Cap)[] Currencies, string? Gathering);
}
