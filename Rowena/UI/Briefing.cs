using Dalamud.Plugin.Services;
using Rowena.Core.Conversions;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// One line in chat when you log in, and a word when something changes that you would
/// want to know about without having opened the window.
/// </summary>
/// <remarks>
/// The briefing is what makes the plugin the first thing read after logging in, by being
/// read without being opened: prices are refetched, the fetch is waited for, and one line
/// says what is near its cap, what the flips pay, and how old the furnishing sweep is.
///
/// The alerts are narrow on purpose. A currency entering the last tenth of its cap, a flip
/// whose return crosses the threshold, a sweep gone stale: each is said once when it
/// becomes true and not again until it has stopped being true, so a slow tick cannot turn
/// into a chat channel. Nothing here fetches; it reads what the cache has, on the same
/// few-second clock as the server bar. Undercuts are deliberately not here.
/// </remarks>
internal sealed class Briefing : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FetchPatience = TimeSpan.FromMinutes(3);

    /// <summary>How long a kicked refresh gets to start before it is assumed dropped.</summary>
    private static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(15);

    private readonly IClientState client;
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly MarketCache market;
    private readonly FurnishingSweep sweep;
    private readonly Headlines headlines;
    private readonly Configuration config;
    private readonly Func<bool> boardsKnown;
    private readonly Action refreshPrices;

    private DateTime nextAt;

    // The login briefing in flight: asked for, refresh kicked, waiting for the fetch.
    private bool wanted;
    private bool kicked;
    private bool sawFetching;
    private DateTime kickedAt;
    private DateTime giveUpAt;
    private DateTimeOffset? refreshBefore;

    // What has already been said, so it is not said again while still true.
    private readonly HashSet<uint> cappedSaid = [];
    private readonly HashSet<string> flipsSaid = [];
    private bool staleSaid;

    public Briefing(
        IClientState client,
        IFramework framework,
        IChatGui chat,
        MarketCache market,
        FurnishingSweep sweep,
        Headlines headlines,
        Configuration config,
        Func<bool> boardsKnown,
        Action refreshPrices)
    {
        this.client = client;
        this.framework = framework;
        this.chat = chat;
        this.market = market;
        this.sweep = sweep;
        this.headlines = headlines;
        this.config = config;
        this.boardsKnown = boardsKnown;
        this.refreshPrices = refreshPrices;

        client.Login += OnLogin;
        framework.Update += Tick;
    }

    public void Dispose()
    {
        client.Login -= OnLogin;
        framework.Update -= Tick;
    }

    /// <summary>Asks for the briefing now, as the login would: refresh first, then say.</summary>
    public void Now()
    {
        wanted = true;
        kicked = false;
        sawFetching = false;
        giveUpAt = DateTime.UtcNow + FetchPatience;
    }

    private void OnLogin()
    {
        if (config.BriefOnLogin)
            Now();
    }

    private void Tick(IFramework _)
    {
        if (DateTime.UtcNow < nextAt)
            return;

        nextAt = DateTime.UtcNow + Every;

        if (wanted)
            ContinueBriefing();

        if (client.IsLoggedIn && boardsKnown())
            Alerts();
    }

    private void ContinueBriefing()
    {
        // The world is not known for a moment after login, and a refresh asked before that
        // has no board to ask about.
        if (!boardsKnown())
            return;

        if (!kicked)
        {
            refreshBefore = market.LastRefresh;
            refreshPrices();
            kicked = true;
            kickedAt = DateTime.UtcNow;
            return;
        }

        sawFetching |= market.Busy;

        var fetched = !market.Busy && market.LastRefresh is { } at && at != refreshBefore;

        // A refresh asked while the cache was busy with something else is dropped, not
        // queued. If nothing has started fetching in a reasonable while, it is not going to,
        // and the briefing is better said from the cache than never.
        var neverStarted = !sawFetching && !fetched && DateTime.UtcNow > kickedAt + StartPatience;
        var gaveUp = DateTime.UtcNow > giveUpAt;

        if (!fetched && !neverStarted && !gaveUp)
            return;

        wanted = false;
        Say(Compose(fetched));
    }

    private string Compose(bool fresh)
    {
        var parts = new List<string>();

        var near = headlines.NearCap();
        if (near.Count > 0)
        {
            parts.Add(
                "near cap: "
                + string.Join(", ", near.Select(capped => $"{Phrases.UnitOf(capped.Currency)} {capped.Held:N0}/{capped.Cap:N0}")));
        }

        if (headlines.BestFlips() is { } flips)
        {
            parts.Add(
                $"flips pay {Phrases.CompactGil(flips.Total)} at best split, "
                + $"top {Describe(flips.Best.Conversion)} +{Phrases.CompactGil(flips.Best.Profit)}");
        }
        else if (fresh)
        {
            parts.Add("no flip pays right now");
        }

        parts.Add(sweep.ReadyAt is { } swept
            ? $"sweep {Phrases.Ago(DateTimeOffset.UtcNow - swept)} old"
            : "no furnishing sweep yet");

        var prefix = fresh ? "" : "prices not refetched, from the cache: ";
        return prefix + string.Join(". ", parts) + ".";
    }

    private void Alerts()
    {
        if (config.AlertNearCap)
        {
            var near = headlines.NearCap();
            var nearIds = near.Select(capped => capped.Currency.Id).ToHashSet();

            foreach (var capped in near)
            {
                if (cappedSaid.Add(capped.Currency.Id))
                    Say($"{capped.Currency.Name} is at {capped.Held:N0}/{capped.Cap:N0}. Past the cap, earning is lost.");
            }

            // Spent back under the line: the next time it climbs is news again.
            cappedSaid.RemoveWhere(id => !nearIds.Contains(id));
        }

        if (config.AlertFlipReturnPercent > 0 && !market.Busy && headlines.BestFlips() is { } flips)
        {
            var threshold = config.AlertFlipReturnPercent / 100d;
            var best = flips.Best;

            if (best.ReturnOnOutlay is { } roi && roi >= threshold)
            {
                if (flipsSaid.Add(best.Conversion.Id))
                    Say($"{Describe(best.Conversion)} returns {roi:P0} over {best.Runs} runs, +{Phrases.CompactGil(best.Profit)}.");
            }
            else
            {
                flipsSaid.Remove(best.Conversion.Id);
            }
        }

        if (config.AlertStaleSweep)
        {
            var stale = sweep.ReadyAt is { } at && DateTimeOffset.UtcNow - at > config.SweepAge() && !sweep.Running;

            if (stale && !staleSaid)
            {
                staleSaid = true;
                Say($"the furnishing sweep is {Phrases.Ago(DateTimeOffset.UtcNow - sweep.ReadyAt!.Value)} old. Re-sweep when convenient.");
            }
            else if (!stale)
            {
                staleSaid = false;
            }
        }
    }

    private static string Describe(Conversion conversion)
    {
        var inputs = string.Join(" + ", conversion.Inputs.Select(input => $"{input.Quantity:N0}x {input.Resource.Name}"));
        var outputs = string.Join(" + ", conversion.Outputs.Select(output => output.Resource.Name));
        return $"{inputs} -> {outputs}";
    }

    private void Say(string text) => chat.Print(text, "Rowena");
}
