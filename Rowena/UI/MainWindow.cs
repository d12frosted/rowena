using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
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
/// A shell, and nothing else: the strip that never scrolls away, a tab per question, and the
/// business of picking up where the last session left off. The questions themselves are asked one
/// tab at a time, which is also what makes the hidden ones free.
///
/// Tabbed rather than stacked because the questions run on different clocks and the tall one was
/// pushing the short ones off the screen. Nobody has ever needed the scrip table and the furnishing
/// ranking in view at once.
/// </remarks>
internal sealed class MainWindow : Window
{
    private readonly Trades trades;
    private readonly MarketCache market;
    private readonly PricingScope scope;
    private readonly FurnishingSweep sweep;
    private readonly StatusStrip strip;
    private readonly ConvertTab convert;
    private readonly CraftTab crafts;
    private readonly Configuration config;
    private readonly Action save;

    private bool restoreAttempted;
    private long persistedSweepAt;

    public MainWindow(
        Trades trades,
        MarketCache market,
        Balances balances,
        PricingScope scope,
        GatherBuddyIpc gatherBuddy,
        FurnishingSweep sweep,
        ConvertTab convert,
        CraftTab crafts,
        Configuration config,
        Action save)
        : base("Rowena###rowena-main")
    {
        this.trades = trades;
        this.market = market;
        this.scope = scope;
        this.sweep = sweep;
        this.convert = convert;
        this.crafts = crafts;
        this.config = config;
        this.save = save;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        strip = new StatusStrip(
            market,
            balances,
            trades,
            gatherBuddy,
            () => RefreshCatalogue(scope.Buying, scope.Selling, force: true));
    }

    public override void Draw()
    {
        var buying = scope.Buying;
        var selling = scope.Selling;

        strip.Draw(buying, selling);
        ImGui.Separator();

        if (buying is not null && selling is not null)
        {
            // Prices saved by a previous session, as soon as there is a board to compare them against.
            market.RestoreOnce(config.SweepAge());
            RestoreSweepOnce(buying, selling);
            PersistFinishedSweep();
        }

        if (!ImGui.BeginTabBar("rowena-tabs"))
            return;

        // The label carries the tab's identity in ImGui unless it is told otherwise, so every one of
        // these pins its own id after ###. Without that, a label that counts something would hand the
        // tab a new identity each time the count changed, and reset the selection along with it.
        if (ImGui.BeginTabItem("Convert###convert"))
        {
            if (buying is null || selling is null)
                NoBoard();
            else
                convert.Draw();

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(crafts.Label))
        {
            if (buying is { } craftBuying && selling is { } craftSelling)
                crafts.Draw(craftBuying, craftSelling);
            else
                NoBoard();

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    /// <summary>
    /// Said by a tab that cannot answer anything without a board to price against.
    /// </summary>
    /// <remarks>
    /// Inside the tab rather than in place of the tabs, so that a tab which does still work while
    /// logged out stays reachable. Typing a world in by hand is exactly what you would be doing here
    /// if the game were not running.
    /// </remarks>
    private static void NoBoard() =>
        ImGui.TextColored(Palette.Bad, "Not logged in, so there is no board to price against.");

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
        market.RefreshInBackground(buying, trades.Bought, force);
        market.RefreshInBackground(selling, trades.Sold, force);
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

    /// <summary>Called on open so a stale window fills itself in without being asked.</summary>
    public override void OnOpen() => RefreshCatalogue(scope.Buying, scope.Selling);

    public override void OnClose() => save();

}
