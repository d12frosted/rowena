using System.Text;
using Dalamud.Bindings.ImGui;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// What the plugin is doing where nothing is drawn.
/// </summary>
/// <remarks>
/// Two halves, and the first is the one that answers most questions. The state is read live
/// from the things themselves: whether the socket is up, what the queue is holding, whether
/// the game has handed over tax rates yet. "Nothing is happening" almost always turns out to
/// be one of those saying no, and none of them had anywhere to say it.
///
/// The second half is the last few minutes of events, for the questions state cannot answer:
/// what did it try, in what order, and what came back. Copying the lot to the clipboard is
/// there because the useful thing to do with this is hand it to somebody.
/// </remarks>
internal sealed class DiagnosticsPanel(
    Diagnostics diagnostics,
    MarketCache market,
    LiveMarket live,
    BoardWatcher board,
    CraftSweep furnishings,
    VendorSweep vendors,
    Places places,
    Configuration config)
{
    public void Draw()
    {
        if (!ImGui.CollapsingHeader("diagnostics"))
            return;

        var on = config.Diagnostics;

        if (ImGui.Checkbox("Keep an account of what is happening", ref on))
            config.Diagnostics = on;

        Style.MuffledWrapped(
            "The state below is always live. The events are only kept while this is ticked, and "
            + "go to the Dalamud log as well.");

        Style.Gap();
        DrawState();

        if (!config.Diagnostics)
            return;

        Style.Gap();

        if (Style.Row("copy all of it"))
            ImGui.SetClipboardText(Report());

        ImGui.SameLine();

        if (Style.Quiet("clear"))
            diagnostics.Clear();

        DrawEvents();
    }

    private void DrawState()
    {
        if (!ImGui.BeginTable("diagnostic-state", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableSetupColumn("What", ImGuiTableColumnFlags.WidthFixed, Style.Px(150));
        ImGui.TableSetupColumn("Doing", ImGuiTableColumnFlags.WidthStretch);

        foreach (var (what, doing, good) in State())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Style.Muted, what);
            ImGui.TableNextColumn();
            ImGui.TextColored(good ? Style.Plain : Style.Bad, doing);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// The live state, as lines. Shared by the table and the clipboard so they cannot drift.
    /// </summary>
    private IEnumerable<(string What, string Doing, bool Good)> State()
    {
        var held = market.Held;

        yield return (
            "fetching",
            market.Busy
                ? $"yes, {market.Progress?.Done ?? 0} of {market.Progress?.Total ?? 0}"
                : "idle",
            true);

        yield return (
            "queued",
            $"{market.PendingAt(FetchPriority.Interactive)} pressed, "
            + $"{market.PendingAt(FetchPriority.Background)} background, "
            + $"{market.PendingAt(FetchPriority.Sweep)} sweep",
            true);

        yield return (
            "held",
            $"{held.Books:N0} books, {held.Summaries:N0} summaries",
            held.Books > 0 || held.Summaries > 0);

        yield return (
            "last fetch",
            market.LastRefresh is { } at ? $"{Phrases.Ago(DateTimeOffset.UtcNow - at)} ago" : "never",
            market.LastRefresh is not null);

        yield return ("last error", market.LastError ?? "none", market.LastError is null);

        yield return (
            "live feed",
            config.LiveMarket
                ? live.Connected
                    ? $"connected, {live.Received:N0} changes seen, {live.Refetched:N0} refetched"
                    : "not connected"
                : "turned off",
            !config.LiveMarket || live.Connected);

        yield return (
            "tax rates",
            board.SellerRates is { Count: > 0 } rates
                ? string.Join(", ", rates.OrderBy(entry => entry.Key)
                    .Select(entry => $"{Cities.Name(entry.Key)} {entry.Value:P0}"))
                : board.RatesValidUntil is { } expired
                    ? $"expired at {expired:HH:mm}, assuming the worst until asked again"
                    : "not yet: ask a retainer vocate, or open a retainer's sell list",
            board.SellerRates is { Count: > 0 });

        yield return (
            "my retainers",
            board.RetainerTowns() is { Count: > 0 } towns
                ? $"in {string.Join(", ", towns.Select(Cities.Name))}, so selling costs {board.Tax().SellerRate:P0}"
                  + (board.RatesValidUntil is { } until ? $" until {until:HH:mm}" : "")
                : "none known yet",
            true);

        var listed = board.ListedItems();

        yield return (
            "my listings",
            listed.Count == 0
                ? "none seen: open a retainer, or the board for something you have listed"
                : string.Join(
                    "; ",
                    listed.Select(item =>
                    {
                        var mine = board.Listed(item);
                        var units = mine.Sum(listing => listing.Quantity);
                        var cheapest = mine.Min(listing => listing.UnitPrice);
                        return $"item {item}: {units} units, cheapest {cheapest:N0}";
                    })),
            true);

        var crafts = furnishings.Current;

        yield return (
            "craft sweep",
            crafts.HasResults
                ? $"{crafts.State}, {crafts.Shortlist.Count} shortlisted"
                : $"{crafts.State}, nothing yet",
            crafts.State != CraftSweep.Phase.Failed);

        var scan = vendors.Current;

        yield return (
            "vendor scan",
            scan.HasResults ? $"{scan.State}, {scan.Shortlist.Count} watched" : $"{scan.State}, nothing yet",
            scan.State != VendorSweep.Phase.Failed);

        yield return ("going somewhere", places.Status ?? "no", true);
    }

    private void DrawEvents()
    {
        var recent = diagnostics.Recent();

        if (recent.Count == 0)
        {
            Style.Nothing("nothing noted yet");
            return;
        }

        // Newest first: the thing that just happened is the thing being looked for.
        if (!ImGui.BeginChild("diagnostic-events", new(0, Style.Px(220)), true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var entry in recent.Reverse())
        {
            ImGui.TextColored(Style.Muted, $"{entry.At:HH:mm:ss} {entry.Area,-6}");
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Message);
        }

        ImGui.EndChild();
    }

    /// <summary>The whole thing as text, for handing to somebody who can read it.</summary>
    public string Report()
    {
        var report = new StringBuilder();
        report.AppendLine("Rowena diagnostics");

        foreach (var (what, doing, _) in State())
            report.AppendLine($"  {what}: {doing}");

        report.AppendLine();

        foreach (var entry in diagnostics.Recent())
            report.AppendLine($"{entry.At:HH:mm:ss} [{entry.Area}] {entry.Message}");

        return report.ToString();
    }
}
