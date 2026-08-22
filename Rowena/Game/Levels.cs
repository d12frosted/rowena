using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>
/// What level you are on a job, or zero when the game will not say.
/// </summary>
/// <remarks>
/// Game memory, so this is read on the framework thread. It exists so a table can stop
/// recommending something you cannot do: a ranking full of things out of reach is a ranking
/// somebody has to filter in their head, which is the work this is supposed to be doing.
///
/// Shared between the two tables that need it. Gathering asked first and crafting asks the
/// same question of the same array, and two copies of it would be two chances to index it
/// differently.
/// </remarks>
internal sealed class Levels(IDataManager data)
{
    /// <summary>
    /// The level of one class or job.
    /// </summary>
    /// <remarks>
    /// The levels are kept in an array indexed by something of the game's own choosing rather
    /// than by job id, and the sheet is the only thing that knows the mapping.
    /// </remarks>
    public unsafe int Of(uint classJobId)
    {
        var state = PlayerState.Instance();

        if (state is null)
            return 0;

        var index = data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.ExpArrayIndex ?? -1;

        return index < 0 ? 0 : state->ClassJobLevels[index];
    }
}
