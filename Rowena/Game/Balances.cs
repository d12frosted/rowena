using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Rowena.Core.Conversions;
using Rowena.IPC;

namespace Rowena.Game;

/// <summary>
/// What you are holding right now.
/// </summary>
/// <remarks>
/// The only thing in this plugin that genuinely needs a running client, and the reason the
/// plugin exists at all rather than a script. Whether the answer is "buy tokens" or "go
/// gather" depends on how many scrips you are already sitting on and how close that is to
/// the cap, and nothing outside the game knows that.
///
/// Kept deliberately small. Everything here is version-sensitive game memory, so when a
/// patch moves something this is the one file to look at.
///
/// Every read here has to happen on the framework thread. The object table throws outright
/// off it, and the raw pointers are only merely unsafe rather than loudly so, which is worse.
/// Anything wanting these values on a background task must be handed them, not the object.
/// </remarks>
internal sealed class Balances(IObjectTable objects, AllaganToolsIpc allaganTools, IPluginLog log)
{
    /// <summary>Gil is just an item, and it lives with the other currencies.</summary>
    private const uint GilItemId = 1;

    public long Gil => Currency(GilItemId);

    /// <summary>
    /// How many of a resource you hold, wherever it is kept.
    /// </summary>
    /// <remarks>
    /// Items go through AllaganTools when it is there, because the game's own count covers your
    /// bags, armoury and what you are wearing and stops at the retainer's door. A hundred Mount
    /// Tokens sitting in a retainer read as none, which is the kind of undercount that quietly
    /// tells you to go and buy what you already own.
    /// </remarks>
    public long Held(Resource resource) =>
        resource.Kind == ResourceKind.Currency
            ? Currency(resource.Id)
            : allaganTools.Owned(resource.Id) ?? InBags(resource.Id);

    /// <summary>
    /// A bound currency, asking both places the game keeps them.
    /// </summary>
    /// <remarks>
    /// Scrips, tomestones and the like are held by CurrencyManager and are not in any
    /// inventory container. Reading only the Currency container looks like it works, because
    /// gil is in there and answers correctly, while every scrip silently reads zero. Ask
    /// CurrencyManager first and fall back to the container, which is where gil actually is.
    /// </remarks>
    private long Currency(uint itemId) => InCurrencyManager(itemId) ?? InCurrencyContainer(itemId);

    private unsafe long? InCurrencyManager(uint itemId)
    {
        var manager = CurrencyManager.Instance();

        // HasItem is the guard that keeps this from reporting a confident zero for something
        // CurrencyManager simply does not track, which is what makes the fallback meaningful.
        if (manager is null || !manager->HasItem(itemId))
            return null;

        return manager->GetItemCount(itemId);
    }

    private unsafe long InCurrencyContainer(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return 0;

        var container = manager->GetInventoryContainer(InventoryType.Currency);
        if (container is null || !container->IsLoaded)
            return 0;

        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);
            if (slot is not null && slot->ItemId == itemId)
                return slot->Quantity;
        }

        return 0;
    }

    /// <summary>Ordinary items, counted across bags, armoury and what you are wearing.</summary>
    private unsafe long InBags(uint itemId)
    {
        var manager = InventoryManager.Instance();
        return manager is null ? 0 : manager->GetInventoryItemCount(itemId);
    }

    /// <summary>
    /// The data centre you are logged in to, for pricing against the board you can
    /// actually reach. Null when not logged in or when the sheets will not say.
    /// </summary>
    public string? DataCentre
    {
        get
        {
            try
            {
                var world = objects.LocalPlayer?.CurrentWorld.ValueNullable;
                var name = world?.DataCenter.ValueNullable?.Name.ExtractText();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch (Exception error)
            {
                // Worth surviving rather than throwing: the window falls back to asking.
                log.Warning(error, "Could not work out the current data centre.");
                return null;
            }
        }
    }
}
