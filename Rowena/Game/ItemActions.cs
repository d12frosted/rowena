using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Rowena.IPC;

namespace Rowena.Game;

/// <summary>
/// The things a row can do once you click it.
/// </summary>
/// <remarks>
/// Gathered behind one type so the window takes one dependency rather than five, and so the
/// question of which plugin does what is answered here instead of in a draw call.
///
/// The division is not arbitrary. Artisan crafts, because it has a gate that takes a recipe and a
/// quantity. AllaganTools makes lists, because it has a gate for that and already knows what you
/// hold, so it can work out what is still to be acquired. The game opens its own windows.
/// </remarks>
internal sealed class ItemActions(
    ArtisanIpc artisan,
    AllaganToolsIpc allaganTools,
    CraftBasket basket,
    IPluginLog log)
{
    /// <summary>Crafts waiting to be handed to Artisan as a list.</summary>
    public CraftBasket Basket => basket;

    /// <summary>Craft lists AllaganTools holds, which can be added to.</summary>
    public IReadOnlyDictionary<string, string> CraftLists() => allaganTools.CraftLists();

    public bool AddToExistingList(string listKey, uint itemId, uint quantity) =>
        allaganTools.AddToExistingList(listKey, itemId, quantity);

    public bool CanCraft => artisan.Available;

    public bool CraftingBusy => artisan.Busy;

    public bool CanMakeLists => allaganTools.Available;

    public void OpenCraftingLog(uint recipeId) => GameActions.OpenCraftingLog(recipeId, log);

    public void SearchMarketBoard(uint itemId) => GameActions.SearchMarketBoard(itemId, log);

    public void Craft(uint recipeId, int quantity) => artisan.Craft(recipeId, quantity);

    /// <summary>
    /// Asks AllaganTools for a craft list of one product.
    /// </summary>
    /// <remarks>
    /// The product, not its materials. AllaganTools resolves what a craft needs and subtracts what
    /// you already have, across retainers, which is the part actually worth not doing by hand.
    /// </remarks>
    public bool AddToCraftList(string itemName, uint itemId, uint quantity) =>
        allaganTools.AddCraftList($"Rowena: {itemName}", new Dictionary<uint, uint> { [itemId] = quantity })
            is not null;
}
