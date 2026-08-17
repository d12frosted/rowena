using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Rowena.Core.Conversions;

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
/// </remarks>
internal sealed class Balances(IObjectTable objects, IPluginLog log)
{
    /// <summary>Gil is just an item, and it lives with the other currencies.</summary>
    private const uint GilItemId = 1;

    public long Gil => InCurrency(GilItemId);

    /// <summary>How many of a resource you hold, wherever the game keeps that kind.</summary>
    public long Held(Resource resource) =>
        resource.Kind == ResourceKind.Currency ? InCurrency(resource.Id) : InBags(resource.Id);

    /// <summary>
    /// Scrips, tomestones, seals and gil, which live in their own container rather than
    /// in your bags.
    /// </summary>
    private unsafe long InCurrency(uint itemId)
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
