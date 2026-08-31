using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>
/// Whether an item teaches something, and whether I have learned it already.
/// </summary>
/// <remarks>
/// The pile has one kind of stack in it that arithmetic gets dangerously wrong. An orchestrion
/// roll I have already played is a duplicate, and a duplicate is surplus like any other; the
/// same roll before I have played it is worth the music, whatever a vendor offers for it. The
/// two look identical to every number the market can supply.
///
/// The game knows, and it is the only thing that does. One call covers the whole family, since
/// what an unlock item does is an item action and the client answers for all of them: rolls,
/// minions, mounts, cards, bardings, hairstyles, fashion accessories, glasses, framer's kits,
/// and the master and folklore tomes that teach a recipe or a node.
///
/// Answered live rather than remembered. Learning something is exactly the event that makes a
/// roll surplus, and a cache would keep calling it precious until the next login. Only the
/// question of whether an item teaches at all is cached, because that one is in a sheet and
/// sheets do not change while the game is running.
/// </remarks>
internal sealed class Unlocks(IDataManager data, IPluginLog log)
{
    /// <summary>
    /// The item actions that teach rather than do.
    /// </summary>
    /// <remarks>
    /// A list rather than "anything with an action", because a potion has an action too and
    /// asking the client whether I have learned a potion is a question with no good answer.
    /// These are the ones I am sure of, read off the sheets; anything else falls through to
    /// being priced normally, which is what happened before any of this existed.
    /// </remarks>
    private static readonly HashSet<uint> Teaches =
    [
        853,    // minions
        1013,   // chocobo bardings
        1322,   // mounts
        2136,   // master tomes, which teach a recipe
        2633,   // unlock links: hairstyles, dances, the aesthetics booklets
        3357,   // triple triad cards
        4107,   // folklore tomes, which teach a node
        19743,  // field notes
        20086,  // fashion accessories
        25183,  // orchestrion rolls
        29459,  // framer's kits
        37312,  // glasses
    ];

    private readonly Dictionary<uint, bool> teaching = [];

    /// <summary>
    /// Whether I have learned what this teaches, or null when it teaches nothing.
    /// </summary>
    /// <remarks>
    /// Three answers rather than two, and the third is the important one: most of a bag is
    /// ordinary stock, and "not an unlock" must not read as "not learned yet" or every stack in
    /// the pile would be held back forever.
    /// </remarks>
    public unsafe bool? Learned(uint itemId)
    {
        if (!Teaching(itemId))
            return null;

        try
        {
            var row = ExdModule.GetItemRowById(itemId);
            var state = UIState.Instance();

            if (row is null || state is null)
                return null;

            // One means learned. Anything else is read as not learned, which errs towards
            // keeping something I could have sold rather than selling something I cannot buy
            // back, and that is the right way round for this question.
            return state->IsItemActionUnlocked(row) == 1;
        }
        catch (Exception error)
        {
            log.Warning(error, $"Could not read the unlock state of item {itemId}.");
            return null;
        }
    }

    /// <summary>Whether the item teaches anything at all, off the sheet and remembered.</summary>
    private bool Teaching(uint itemId)
    {
        if (teaching.TryGetValue(itemId, out var known))
            return known;

        var teaches = Read(itemId);
        teaching[itemId] = teaches;

        return teaches;
    }

    /// <summary>
    /// The item action's type, which is what says a thing teaches.
    /// </summary>
    /// <remarks>
    /// Read through the row reference's own id rather than a named column: the sheet's second
    /// field is the action type and Lumina's schema calls it Action, so the id is the type. The
    /// numbers above are what that field actually holds for each family.
    /// </remarks>
    private bool Read(uint itemId)
    {
        if (data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } item)
            return false;

        return item.ItemAction.RowId != 0 && Teaches.Contains(item.ItemAction.Value.Action.RowId);
    }
}
