using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// One headline in the server info bar, whether or not the window is open.
/// </summary>
/// <remarks>
/// The window answers questions when asked; this is the one fact worth knowing without
/// asking. A currency running into its cap, because past the cap earning is simply lost
/// and nothing in the game says so; or a fetch in flight, so its progress can be read
/// without opening anything. When neither has anything to say the entry is the bare
/// icon: one character of launcher, and its very shortness is the all-clear.
///
/// The slot is introduced by a boxed R rather than the plugin's name: the bar is the
/// scarcest line of screen in the game, and the tooltip spells the name out for anyone
/// still learning the glyph.
///
/// Clicking a cap warning opens the window on the Sinks tab, where its working is;
/// otherwise the Overview. A fetch is readable from either, since the status strip
/// reporting it is on every tab.
///
/// Recomputed on its own clock, a few seconds rather than the window's half-second: it
/// runs whether or not anything is open. The books it reads are whatever the cache holds;
/// the bar inherits their age and adds no fetching of its own.
/// </remarks>
internal sealed class ServerBar : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    private static readonly string Icon = SeIconChar.BoxedLetterR.ToIconString();

    private readonly IDtrBarEntry entry;
    private readonly IFramework framework;
    private readonly MarketCache market;
    private readonly Headlines headlines;

    private DateTime nextAt;
    private bool showingCap;

    public ServerBar(
        IDtrBar bar,
        IFramework framework,
        MarketCache market,
        Headlines headlines,
        Action openSinks,
        Action openOverview)
    {
        this.framework = framework;
        this.market = market;
        this.headlines = headlines;

        entry = bar.Get("Rowena");
        entry.Shown = false;
        entry.OnClick = _ => (showingCap ? openSinks : openOverview)();

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

        if (market.Busy && market.Progress is { Total: > 0 } progress)
        {
            entry.Text = $"{Icon} fetching {100 * progress.Done / progress.Total}%";
            entry.Tooltip = $"Rowena: {progress.Done} of {progress.Total} items answered. Click to watch.";
            entry.Shown = true;
            showingCap = false;
            return;
        }

        if (NearCap() is { } warning)
        {
            entry.Text = $"{Icon} {warning.Line}";
            entry.Tooltip = warning.Detail;
            entry.Shown = true;
            showingCap = true;
            return;
        }

        entry.Text = Icon;
        entry.Tooltip = "Rowena. Nothing urgent; click to open.";
        entry.Shown = true;
        showingCap = false;
    }

    /// <summary>The currency closest to its cap, once it is into the last tenth.</summary>
    private (string Line, string Detail)? NearCap()
    {
        if (headlines.NearCap() is not [var near, ..])
            return null;

        return (
            $"{Phrases.UnitOf(near.Currency)} {near.Held:N0}/{near.Cap:N0}",
            $"Rowena: {near.Currency.Name} is nearly capped. Anything earned past the\n"
            + "cap is simply lost; the Sinks tab knows what spending it pays.");
    }
}
