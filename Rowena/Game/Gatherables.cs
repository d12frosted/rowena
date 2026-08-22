using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>Something you can go and pick up, and what it takes to reach it.</summary>
/// <param name="JobId">The ClassJob that gathers it: miner or botanist.</param>
/// <param name="Timed">
/// True when the only nodes yielding it appear on a clock, which is a different errand
/// entirely from walking to a node that is always there.
/// </param>
/// <param name="Windows">
/// When the clock lets you at it, in game minutes past its midnight. Empty for anything
/// standing there all day.
/// </param>
internal readonly record struct Gatherable(
    uint ItemId,
    uint JobId,
    string Job,
    int Level,
    bool Timed,
    IReadOnlyList<EorzeaWindow> Windows);

/// <summary>
/// Every marketable thing a miner or botanist can gather, with its level and job.
/// </summary>
/// <remarks>
/// Gathering is the one activity where this plugin already holds both halves of the answer
/// and was not using them: the board says what a thing fetches and how fast it moves, and
/// the sheets say whether you can go and get it. What was missing is only the list.
///
/// Two thousand items are gatherable and seven hundred and seventy of them are marketable,
/// which is small enough to survey in a handful of requests. The rest are quest and
/// currency items nobody can sell.
///
/// Fishing is deliberately absent. Two and a half thousand fish are gatherable in a sense
/// this plugin cannot model: bait, weather, time of day and a cast that may fail are not
/// things a market tool knows, and a table that ranked them beside a mining node would be
/// promising an hour it cannot deliver.
/// </remarks>
internal sealed class Gatherables(IDataManager data, IPluginLog log)
{
    /// <summary>Mining and quarrying are the miner's; logging and harvesting the botanist's.</summary>
    private static readonly Dictionary<uint, uint> JobByType = new()
    {
        [0] = 16,
        [1] = 16,
        [2] = 17,
        [3] = 17,
    };

    private IReadOnlyList<Gatherable>? cached;

    /// <summary>Built once. The sheets do not change while the game is running.</summary>
    public IReadOnlyList<Gatherable> All() => cached ??= Build();

    private IReadOnlyList<Gatherable> Build()
    {
        var items = data.GetExcelSheet<Item>();
        var gathering = data.GetExcelSheet<GatheringItem>();
        var jobs = data.GetExcelSheet<ClassJob>();

        // Which point bases only ever appear on a clock. An item found on any ordinary node
        // as well is not timed: the timed one is a bonus rather than the only way to it.
        var timedBases = new HashSet<uint>();
        var untimedBases = new HashSet<uint>();
        var windowsByBase = new Dictionary<uint, HashSet<EorzeaWindow>>();
        var transient = data.GetExcelSheet<GatheringPointTransient>();
        var popTimes = data.GetExcelSheet<GatheringRarePopTimeTable>();

        foreach (var point in data.GetExcelSheet<GatheringPoint>())
        {
            if (point.GatheringPointBase.RowId == 0)
                continue;

            var times = transient.GetRowOrDefault(point.RowId);
            var open = times is { } t ? Windows(t, popTimes) : [];

            (open.Count > 0 ? timedBases : untimedBases).Add(point.GatheringPointBase.RowId);

            if (open.Count == 0)
                continue;

            if (!windowsByBase.TryGetValue(point.GatheringPointBase.RowId, out var set))
                windowsByBase[point.GatheringPointBase.RowId] = set = [];

            foreach (var window in open)
                set.Add(window);
        }

        var found = new Dictionary<uint, Gatherable>();

        foreach (var pointBase in data.GetExcelSheet<GatheringPointBase>())
        {
            if (!JobByType.TryGetValue(pointBase.GatheringType.RowId, out var jobId))
                continue;

            var timed = timedBases.Contains(pointBase.RowId) && !untimedBases.Contains(pointBase.RowId);

            foreach (var slot in pointBase.Item)
            {
                if (slot.RowId == 0 || gathering.GetRowOrDefault((uint)slot.RowId) is not { } entry)
                    continue;

                var itemId = (uint)entry.Item.RowId;

                if (itemId == 0 || items.GetRowOrDefault(itemId) is not { } item)
                    continue;

                // Only what can be sold. The rest are quest pieces and currencies.
                if (item.ItemSearchCategory.RowId == 0)
                    continue;

                // The easiest way to it wins: the lowest level, and untimed over timed.
                if (found.TryGetValue(itemId, out var existing)
                    && (existing.Level <= pointBase.GatheringLevel || (!existing.Timed && timed)))
                {
                    continue;
                }

                found[itemId] = new Gatherable(
                    itemId,
                    jobId,
                    jobs.GetRowOrDefault(jobId)?.Abbreviation.ExtractText() ?? "",
                    pointBase.GatheringLevel,
                    timed,
                    timed && windowsByBase.TryGetValue(pointBase.RowId, out var open) ? [.. open] : []);
            }
        }

        log.Information(
            $"Found {found.Count} marketable gatherables, "
            + $"{found.Values.Count(g => g.Windows.Count > 0)} of them on a clock.");
        return [.. found.Values];
    }

    /// <summary>
    /// When one node is standing there, from whichever of the two schedules it keeps.
    /// </summary>
    /// <remarks>
    /// Both are stored as hours and minutes packed into one number, so 900 is nine o'clock and
    /// a length of 160 is an hour and sixty minutes rather than a hundred and sixty of them.
    /// That reading is not a guess: taken this way every length in the game comes out as two,
    /// three or four hours, and the tables whose windows sit four hours apart are exactly the
    /// ones whose length is three. Read as plain minutes they would overlap each other.
    ///
    /// A start of nought paired with an end of nought is not a window at midnight, it is a
    /// field nobody filled in, and it belongs to points that no base ever refers to.
    /// </remarks>
    private static List<EorzeaWindow> Windows(
        GatheringPointTransient times,
        Lumina.Excel.ExcelSheet<GatheringRarePopTimeTable> popTimes)
    {
        var windows = new List<EorzeaWindow>();

        if (times.GatheringRarePopTimeTable.RowId != 0
            && popTimes.GetRowOrDefault(times.GatheringRarePopTimeTable.RowId) is { } table)
        {
            for (var slot = 0; slot < table.StartTime.Count; slot++)
            {
                int start = table.StartTime[slot], length = table.Duration[slot];

                if (start != 65535 && length != 0)
                    windows.Add(new EorzeaWindow(Clock(start), Clock(length)));
            }

            return windows;
        }

        if (times.EphemeralStartTime == 65535
            || (times.EphemeralStartTime == 0 && times.EphemeralEndTime == 0))
        {
            return windows;
        }

        var from = Clock(times.EphemeralStartTime);
        var to = Clock(times.EphemeralEndTime);

        windows.Add(new EorzeaWindow(
            from,
            ((to - from) % EorzeaClock.MinutesPerDay + EorzeaClock.MinutesPerDay) % EorzeaClock.MinutesPerDay));

        return windows;
    }

    /// <summary>Hours and minutes packed into one number, as plain minutes.</summary>
    private static int Clock(int packed) => packed / 100 * 60 + packed % 100;

    /// <summary>
    /// What level you are on a job, or zero when the game will not say.
    /// </summary>
    /// <remarks>
    /// Game memory, so this is read on the framework thread. It exists so the table can stop
    /// recommending a node you cannot stand at: a ranking full of things out of reach is a
    /// ranking somebody has to filter in their head.
    /// </remarks>
    public unsafe int LevelOf(uint classJobId)
    {
        var state = PlayerState.Instance();

        if (state is null)
            return 0;

        var index = data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.ExpArrayIndex ?? -1;

        return index < 0 ? 0 : state->ClassJobLevels[index];
    }
}
