using Dalamud.Plugin.Services;
using Rowena.Core.Market;

namespace Rowena.Game;

/// <summary>
/// How fast gathering actually goes here, watched rather than assumed.
/// </summary>
/// <remarks>
/// The session planner rests on a number nobody had measured, and every gil figure it prints
/// is scaled by it. Three hundred an hour was a placeholder that announced itself as one; this
/// replaces it with what actually happens.
///
/// Counted from the bags rather than from the game's gathering messages, because what is
/// wanted is items that arrived, and a bag is where they arrive. Only gatherable things are
/// counted and only while a node is open, so buying a stack of ore is not mistaken for mining
/// one.
///
/// The clock runs between gathers, not during them. An hour of gathering is mostly getting to
/// the next node, and a rate measured only while standing at one is a rate nobody sustains.
/// Gaps longer than a few minutes are not counted at all: standing somewhere for an hour and
/// then gathering does not make the rate one an hour.
/// </remarks>
internal sealed class GatherClock : IDisposable
{
    /// <summary>Longer than this between gathers and it was not one stretch of gathering.</summary>
    private const double GapSeconds = 300d;

    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

    private readonly IFramework framework;
    private readonly IGameGui gui;
    private readonly Balances balances;
    private readonly Gatherables gatherables;
    private readonly Configuration config;
    private readonly Action save;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private DateTime nextAt;
    private DateTime? lastGatherAt;
    private IReadOnlyDictionary<uint, int>? held;
    private HashSet<uint>? gatherable;

    public GatherClock(
        IFramework framework,
        IGameGui gui,
        Balances balances,
        Gatherables gatherables,
        Configuration config,
        Action save,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.framework = framework;
        this.gui = gui;
        this.balances = balances;
        this.gatherables = gatherables;
        this.config = config;
        this.save = save;
        this.diagnostics = diagnostics;
        this.log = log;

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    /// <summary>What has been watched so far.</summary>
    public GatherTally Tally => new(config.GatheredItems, config.GatheredSeconds);

    /// <summary>Items an hour, measured, or nothing when too little has been watched.</summary>
    public double? PerHour => Tally.PerHour;

    /// <summary>Forgets the measurement, for when it stops describing how you gather.</summary>
    public void Forget()
    {
        config.GatheredItems = 0;
        config.GatheredSeconds = 0;
        lastGatherAt = null;
        held = null;
        save();
    }

    private void Tick(IFramework _)
    {
        if (DateTime.UtcNow < nextAt)
            return;

        nextAt = DateTime.UtcNow + Every;

        try
        {
            Watch();
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not measure gathering.");
        }
    }

    /// <summary>
    /// Counts what arrived in the bags while a node was open.
    /// </summary>
    /// <remarks>
    /// The node being open is what makes this safe. Gatherable things arrive in bags for all
    /// sorts of reasons, and only one of them is gathering.
    /// </remarks>
    private void Watch()
    {
        if (gui.GetAddonByName("Gathering") == nint.Zero)
        {
            // Away from a node, so the next arrival is a fresh reading rather than a delta
            // against a bag that has been shopped from since.
            held = null;
            return;
        }

        var now = balances.Carrying();
        var before = held;

        held = now;

        if (before is null)
            return;

        gatherable ??= [.. gatherables.All().Select(one => one.ItemId)];

        var gained = 0;

        foreach (var (itemId, count) in now)
        {
            if (gatherable.Contains(itemId))
                gained += Math.Max(0, count - before.GetValueOrDefault(itemId));
        }

        if (gained == 0)
            return;

        var at = DateTime.UtcNow;

        if (lastGatherAt is { } last)
        {
            var tally = GatherPace.Add(Tally, gained, (at - last).TotalSeconds, GapSeconds);

            config.GatheredItems = tally.Items;
            config.GatheredSeconds = tally.Seconds;
            save();

            if (tally.PerHour is { } rate)
                diagnostics.Note("gathering", $"{gained} gathered, measuring {rate:F0} an hour");
        }

        lastGatherAt = at;
    }
}
