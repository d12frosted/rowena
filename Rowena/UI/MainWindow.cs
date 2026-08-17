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
/// Everything shown is built into a <see cref="Model"/> a few times a second and then merely
/// rendered. Reading currencies, asking another plugin over IPC and allocating a shared order
/// book are all far too expensive to do per frame, and doing them per frame earned a hitch
/// warning from Dalamud.
/// </remarks>
internal sealed class MainWindow : Window
{
    private readonly Trades trades;
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly PricingScope scope;
    private readonly GatherBuddyIpc gatherBuddy;
    private readonly FurnishingSweep sweep;
    private readonly ConvertTab convert;
    private readonly CraftTab crafts;
    private readonly Configuration config;
    private readonly Action save;

    private readonly Rebuilt<Wallet> model;

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
        this.balances = balances;
        this.scope = scope;
        this.gatherBuddy = gatherBuddy;
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

        model = new Rebuilt<Wallet>(Build);
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
        convert.Draw();
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

    private void DrawWhatYouHold(Wallet current)
    {
        ImGui.TextUnformatted($"Gil {current.Gil:N0}");

        foreach (var (currency, held) in current.Currencies)
        {
            ImGui.SameLine();
            ImGui.TextColored(Palette.Dim, $"   {currency} {held:N0}");
        }

        if (current.Gathering is { } gathering)
            ImGui.TextColored(Palette.Dim, gathering);
    }

    /// <summary>What is in your pockets, and what you are doing about it.</summary>
    private Wallet Build() =>
        new(
            balances.Gil,
            [.. trades.Currencies.Select(currency => (currency.Name, balances.Held(currency)))],
            GatheringLine());

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

    /// <param name="Currencies">Each spendable currency and how much of it you are holding.</param>
    private sealed record Wallet(long Gil, (string Name, long Held)[] Currencies, string? Gathering);
}
