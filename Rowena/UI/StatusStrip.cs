using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;
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
    private readonly Configuration config;
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly Trades trades;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly ItemCells cells;
    private readonly Places places;
    private readonly LiveMarket live;
    private readonly Action refresh;

    private readonly Rebuilt<Wallet> wallet;

    private const uint GilItemId = 1;

    public StatusStrip(
        Configuration config,
        MarketCache market,
        Balances balances,
        Trades trades,
        GatherBuddyIpc gatherBuddy,
        ItemCells cells,
        Places places,
        LiveMarket live,
        Diagnostics diagnostics,
        Action refresh)
    {
        this.config = config;
        this.market = market;
        this.balances = balances;
        this.trades = trades;
        this.gatherBuddy = gatherBuddy;
        this.cells = cells;
        this.places = places;
        this.live = live;
        this.refresh = refresh;

        wallet = new Rebuilt<Wallet>("wallet", Build, diagnostics);
    }

    public void Draw(string? buying, string? selling)
    {
        // Both boards named, because they are different and the difference is the point.
        ImGui.TextUnformatted($"Buying on {buying ?? "nowhere"}, selling on {selling ?? "nowhere"}");
        ImGui.SameLine();

        // Named for what it fetches. The sweep is also a refresh and shares none of this button's
        // cost, so an unqualified "refresh" was an invitation to press the wrong one.
        if (Style.Row("refresh prices"))
            refresh();

        ImGui.SameLine();

        if (market.Busy)
            ImGui.TextColored(
                Style.Muted,
                market.Progress is { } progress ? $"fetching {progress.Done} of {progress.Total}" : "fetching...");
        else if (market.LastError is { } error)
            ImGui.TextColored(Style.Bad, error);
        else if (market.LastRefresh is { } at)
            ImGui.TextColored(Style.Muted, $"prices {Phrases.Ago(DateTimeOffset.UtcNow - at)} old");
        else
            ImGui.TextColored(Style.Muted, "no prices yet");

        // Said only when it is up, since the interesting state is following rather than not.
        if (live.Connected)
        {
            ImGui.SameLine();
            ImGui.TextColored(Style.Good, "  live");
            Style.Explain(
                $"Following the board as it changes. {live.Received:N0} changes seen, "
                + $"{live.Refetched:N0} worth refetching.");
        }

        var current = wallet.Current;

        // Gil is an item like the rest, so it gets the icon the rest get. Capless, so no warning.
        cells.Icon(GilItemId, 16f);
        ImGui.SameLine(0f, Style.Px(4f));
        ImGui.TextUnformatted($"{current.Gil:N0}");

        foreach (var row in current.Rows)
        {
            // Icon and number, name on hover. The icon is how a currency is recognised in the
            // game, and a row of them reads at a glance where a row of names was a paragraph.
            // A pinned currency always shows its cap: the few pixels saved by dropping it when
            // far away were paid for in a strip where the same currency kept changing shape.
            var text = row.Cap is { } limit ? $"{row.Held:N0}/{limit:N0}" : $"{row.Held:N0}";

            Flow(Style.Px(16f + 4f) + ImGui.CalcTextSize(text).X);
            cells.Icon(row.Currency.Id, 16f);
            ImGui.SameLine(0f, Style.Px(4f));
            ImGui.TextColored(row.NearCap ? Style.Bad : Style.Muted, text);
            Style.Explain(
                row.NearCap
                    ? $"{row.Currency.Name}: nearly capped. Anything earned past the cap is simply lost."
                    : row.Currency.Name);
        }

        if (current.Gathering is { } gathering)
            ImGui.TextColored(Style.Muted, gathering);

        // A journey under way, wherever you are looking. Read live rather than from the
        // snapshot, since it changes on its own clock and a stale "waiting" reads as stuck.
        if (places.Status is { } going)
        {
            ImGui.TextColored(Style.Muted, going);
            ImGui.SameLine();

            if (Style.Row("stop"))
                places.Cancel();
        }
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
        ImGui.SameLine(0f, Style.Px(14f));

        if (width > ImGui.GetWindowContentRegionMax().X - ImGui.GetCursorPosX())
            ImGui.NewLine();
    }

    /// <summary>What is in your pockets that every tab should be read against.</summary>
    /// <remarks>
    /// Not everything in them. Every currency you hold has a table in the Sinks tab, with its
    /// count beside its name, and listing all twenty here as well was a paragraph nobody read.
    /// What stays is what you pinned in Settings, and any other currency about to waste what
    /// it earns next. The choice itself lives in <see cref="WalletStrip"/>.
    /// </remarks>
    private Wallet Build() =>
        new(
            balances.Gil,
            WalletStrip.Pick(
                trades.Currencies.Select(currency => new Holding(currency, balances.Held(currency), balances.CapOf(currency))),
                config.PinnedCurrencies),
            GatheringLine());

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

    /// <param name="Rows">The currencies that earned a place, and why.</param>
    private sealed record Wallet(long Gil, IReadOnlyList<WalletRow> Rows, string? Gathering);
}
