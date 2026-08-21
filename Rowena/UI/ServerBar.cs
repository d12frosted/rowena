using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// One headline in the server info bar, whether or not the window is open.
/// </summary>
/// <remarks>
/// The window answers questions when asked; this is the one fact worth knowing without
/// asking. A currency running into its cap comes first, because past the cap earning is
/// simply lost and nothing in the game says so. Otherwise, what the best split of your gil
/// across the flips would pay. When neither has anything to say the entry hides, since a
/// bar slot that always says something trains you to read none of it.
///
/// Clicking opens the window on the tab the headline came from, where its working is.
///
/// Recomputed on its own clock, a few seconds rather than the window's half-second: it
/// runs whether or not anything is open, and the allocation behind the flip figure is not
/// frame-cheap. The books it reads are whatever the cache holds; the bar inherits their
/// age and adds no fetching of its own.
/// </remarks>
internal sealed class ServerBar : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    private readonly IDtrBarEntry entry;
    private readonly IFramework framework;
    private readonly MarketCache market;
    private readonly Headlines headlines;

    private DateTime nextAt;
    private bool showingFlips;

    public ServerBar(
        IDtrBar bar,
        IFramework framework,
        MarketCache market,
        Headlines headlines,
        Action openSinks,
        Action openFlips)
    {
        this.framework = framework;
        this.market = market;
        this.headlines = headlines;

        entry = bar.Get("Rowena");
        entry.Shown = false;

        // Where the click lands follows the headline: a cap warning is answered by sinks, a
        // profit figure by flips. Whatever was last shown is where the working is.
        entry.OnClick = _ => (showingFlips ? openFlips : openSinks)();

        framework.Update += Tick;
    }

    public void Dispose()
    {
        framework.Update -= Tick;
        entry.Remove();
    }

    private void Tick(IFramework _)
    {
        if (DateTime.UtcNow < nextAt)
            return;

        // A second while fetching, so the percentage moves; the usual few seconds otherwise.
        nextAt = DateTime.UtcNow + (market.Busy ? TimeSpan.FromSeconds(1) : Every);

        // A fetch in flight outranks everything: the other headlines are read off the books it
        // is about to replace, and a bar that says "flips pay 6M" while the numbers behind it
        // are being refetched is confidently stale.
        if (market.Busy && market.Progress is { Total: > 0 } progress)
        {
            entry.Text = $"Rowena: fetching {100 * progress.Done / progress.Total}%";
            entry.Tooltip = $"{progress.Done} of {progress.Total} items answered. Click to watch.";
            entry.Shown = true;
            return;
        }

        if (NearCap() is { } warning)
        {
            entry.Text = $"Rowena: {warning.Line}";
            entry.Tooltip = warning.Detail;
            entry.Shown = true;
            showingFlips = false;
            return;
        }

        if (BestFlips() is { } profit and > 0)
        {
            showingFlips = true;
            entry.Text = $"Rowena: flips pay {Phrases.CompactGil(profit)}";
            entry.Tooltip =
                $"The best split of your gil across the flips pays {profit:N0} gil\n"
                + "at current depth. Click for the working.";
            entry.Shown = true;
            return;
        }

        entry.Shown = false;
    }

    /// <summary>The currency closest to its cap, once it is into the last tenth.</summary>
    private (string Line, string Detail)? NearCap()
    {
        if (headlines.NearCap() is not [var near, ..])
            return null;

        return (
            $"{Phrases.UnitOf(near.Currency)} {near.Held:N0}/{near.Cap:N0}",
            $"{near.Currency.Name} is nearly capped. Anything earned past the cap\n"
            + "is simply lost; the Sinks tab knows what spending it pays.");
    }

    private long? BestFlips() => headlines.BestFlips()?.Total;
}
