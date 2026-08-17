using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Rowena.Game;

/// <summary>Opening the game's own windows on an item.</summary>
/// <remarks>
/// A table of numbers about things you cannot look at is a worse tool than it needs to be. These
/// hand off to the windows the game already has rather than reimplementing any of it.
///
/// Framework thread only, like everything else that touches the client.
/// </remarks>
internal static class GameActions
{
    /// <summary>Opens the crafting log at a recipe.</summary>
    public static unsafe bool OpenCraftingLog(uint recipeId, IPluginLog log)
    {
        try
        {
            var agent = AgentRecipeNote.Instance();
            if (agent is null)
                return false;

            agent->OpenRecipeByRecipeId(recipeId);
            return true;
        }
        catch (Exception error)
        {
            log.Warning(error, $"Could not open the crafting log at recipe {recipeId}.");
            return false;
        }
    }

    /// <summary>Opens the market board search for an item, the way ctrl-clicking a link does.</summary>
    public static unsafe bool SearchMarketBoard(uint itemId, IPluginLog log)
    {
        try
        {
            var finder = ItemFinderModule.Instance();
            if (finder is null)
                return false;

            // The flag asks it to open the search window rather than only setting the term.
            finder->SearchForItem(itemId, true);
            return true;
        }
        catch (Exception error)
        {
            log.Warning(error, $"Could not search the market board for item {itemId}.");
            return false;
        }
    }
}
