using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>Something you can go and pick up, and what it takes to reach it.</summary>
/// <param name="JobId">The ClassJob that gathers it: miner or botanist.</param>
/// <param name="Timed">
/// True when the only nodes yielding it appear on a clock, which is a different errand
/// entirely from walking to a node that is always there.
/// </param>
internal readonly record struct Gatherable(uint ItemId, uint JobId, string Job, int Level, bool Timed);

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
        var transient = data.GetExcelSheet<GatheringPointTransient>();

        foreach (var point in data.GetExcelSheet<GatheringPoint>())
        {
            if (point.GatheringPointBase.RowId == 0)
                continue;

            var clock = transient.GetRowOrDefault(point.RowId) is { } times
                && (times.EphemeralStartTime != 65535 || times.GatheringRarePopTimeTable.RowId != 0);

            (clock ? timedBases : untimedBases).Add(point.GatheringPointBase.RowId);
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
                    timed);
            }
        }

        log.Information($"Found {found.Count} marketable gatherables.");
        return [.. found.Values];
    }

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
