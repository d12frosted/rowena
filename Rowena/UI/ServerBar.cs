using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;
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
/// Clicking opens the window on the Convert tab, where the headline's working is.
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
    private readonly Trades trades;
    private readonly Boards boards;
    private readonly Balances balances;
    private readonly Configuration config;

    private DateTime nextAt;

    public ServerBar(
        IDtrBar bar,
        IFramework framework,
        Trades trades,
        Boards boards,
        Balances balances,
        Configuration config,
        Action openConvert)
    {
        this.framework = framework;
        this.trades = trades;
        this.boards = boards;
        this.balances = balances;
        this.config = config;

        entry = bar.Get("Rowena");
        entry.Shown = false;
        entry.OnClick = _ => openConvert();

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

        nextAt = DateTime.UtcNow + Every;

        if (NearCap() is { } warning)
        {
            entry.Text = $"Rowena: {warning.Line}";
            entry.Tooltip = warning.Detail;
            entry.Shown = true;
            return;
        }

        if (BestFlips() is { } profit and > 0)
        {
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
        (Resource Currency, long Held, long Cap)? worst = null;

        foreach (var currency in trades.Currencies)
        {
            if (balances.CapOf(currency) is not { } cap)
                continue;

            var held = balances.Held(currency);
            if (held < cap - cap / 10)
                continue;

            if (worst is null || held * worst.Value.Cap > worst.Value.Held * cap)
                worst = (currency, held, cap);
        }

        if (worst is not { } near)
            return null;

        return (
            $"{Phrases.UnitOf(near.Currency)} {near.Held:N0}/{near.Cap:N0}",
            $"{near.Currency.Name} is nearly capped. Anything earned past the cap\n"
            + "is simply lost; the Convert tab knows what spending it pays.");
    }

    /// <summary>What the flips would pay, off the books the cache already holds.</summary>
    private long? BestFlips()
    {
        var tax = MarketTax.Standard;

        var candidates = trades.Flips
            .Where(conversion => ConversionEvaluator
                .Evaluate(conversion, 1, boards.Buying, boards.Selling, tax)
                is { IsExecutable: true, Profit: > 0 })
            .ToArray();

        if (candidates.Length == 0)
            return null;

        return ConversionAllocation
            .Allocate(candidates, boards.Buying, boards.Selling, tax, balances.Gil, config.SizingCap)
            .Sum(allocation => allocation.Profit);
    }
}
