using Dalamud.Bindings.ImGui;
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
    private readonly Action refresh;

    private readonly Rebuilt<Wallet> wallet;

    public StatusStrip(
        MarketCache market,
        Balances balances,
        Trades trades,
        GatherBuddyIpc gatherBuddy,
        Action refresh)
    {
        this.market = market;
        this.balances = balances;
        this.trades = trades;
        this.gatherBuddy = gatherBuddy;
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
            ImGui.SameLine();

            // A capped currency shows its cap, because the distance to it is a decision:
            // near the top, earning silently stops. Near is coloured so it is not missed.
            if (cap is { } max)
            {
                var close = held >= max - max / 10;
                ImGui.TextColored(close ? Palette.Bad : Palette.Dim, $"   {currency} {held:N0}/{max:N0}");

                if (close && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Nearly capped. Anything earned past the cap is simply lost.");
            }
            else
            {
                ImGui.TextColored(Palette.Dim, $"   {currency} {held:N0}");
            }
        }

        if (current.Gathering is { } gathering)
            ImGui.TextColored(Palette.Dim, gathering);
    }

    /// <summary>What is in your pockets, and what you are doing about it.</summary>
    /// <remarks>
    /// Only the currencies actually in them, plus the file's own, zero or not. The generated
    /// catalogue knows about every token the game has ever minted, and a strip listing a
    /// hundred and fifty zeroes would bury the four balances that mean anything.
    /// </remarks>
    private Wallet Build() =>
        new(
            balances.Gil,
            [
                .. trades.Currencies
                    .Select(currency => (currency, Held: balances.Held(currency)))
                    .Where(entry => entry.Held > 0 || trades.IsWatched(entry.currency))
                    .Select(entry => (entry.currency.Name, entry.Held, balances.CapOf(entry.currency))),
            ],
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

    /// <param name="Currencies">
    /// Each spendable currency, how much of it you are holding, and the cap when the game
    /// enforces one.
    /// </param>
    private sealed record Wallet(long Gil, (string Name, long Held, long? Cap)[] Currencies, string? Gathering);
}
