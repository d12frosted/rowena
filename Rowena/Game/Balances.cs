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
    /// The most of a currency the game will let you hold, when it enforces a cap.
    /// </summary>
    /// <remarks>
    /// Null when there is none, or none the game will admit to: only CurrencyManager knows
    /// caps, so the bag-dwelling pseudo-currencies report nothing rather than a guess. The
    /// cap matters because a capped currency stops being earned without saying so; scrips
    /// at 3,900 of 4,000 are a warning, not a balance.
    /// </remarks>
    public unsafe long? CapOf(Resource resource)
    {
        if (resource.Kind != ResourceKind.Currency)
            return null;

        var manager = CurrencyManager.Instance();
        if (manager is null || !manager->HasItem(resource.Id))
            return null;

        var max = manager->GetItemMaxCount(resource.Id);
        return max == 0 ? null : max;
    }

    /// <summary>
    /// A bound currency, asking every place the game keeps them.
    /// </summary>
    /// <remarks>
    /// Scrips, tomestones and the like are held by CurrencyManager and are not in any
    /// inventory container. Reading only the Currency container looks like it works, because
    /// gil is in there and answers correctly, while every scrip silently reads zero. Ask
    /// CurrencyManager first and fall back to the container, which is where gil actually is.
    ///
    /// The bags are the last resort, for the currencies that are not really currencies: sky
    /// pirate spoils, cracked clusters, anything untradable a shop takes in trade. They sit
    /// in ordinary inventory, so the first two reads know nothing about them.
    /// </remarks>
    private long Currency(uint itemId) =>
        InCurrencyManager(itemId) ?? InCurrencyContainer(itemId) ?? InBags(itemId);

    private unsafe long? InCurrencyManager(uint itemId)
    {
        var manager = CurrencyManager.Instance();

        // HasItem is the guard that keeps this from reporting a confident zero for something
        // CurrencyManager simply does not track, which is what makes the fallback meaningful.
        if (manager is null || !manager->HasItem(itemId))
            return null;

        return manager->GetItemCount(itemId);
    }

    /// <summary>The Currency container's answer, or null when the item is not kept there.</summary>
    private unsafe long? InCurrencyContainer(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return null;

        var container = manager->GetInventoryContainer(InventoryType.Currency);
        if (container is null || !container->IsLoaded)
            return null;

        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);
            if (slot is not null && slot->ItemId == itemId)
                return slot->Quantity;
        }

        return null;
    }

    /// <summary>Ordinary items, counted across bags, armoury and what you are wearing.</summary>
    private unsafe long InBags(uint itemId)
    {
        var manager = InventoryManager.Instance();
        return manager is null ? 0 : manager->GetInventoryItemCount(itemId);
    }

    /// <summary>
    /// The world you are logged in to. Where your retainers stand, and so the only board you can
    /// actually sell on.
    /// </summary>
    public string? HomeWorld => World(world => world.Name.ExtractText());

    /// <summary>
    /// The data centre you are logged in to, for pricing against the board you can
    /// actually reach. Null when not logged in or when the sheets will not say.
    /// </summary>
    public string? DataCentre => World(world => world.DataCenter.ValueNullable?.Name.ExtractText());

    private string? World(Func<Lumina.Excel.Sheets.World, string?> read)
    {
        try
        {
            if (objects.LocalPlayer?.CurrentWorld.ValueNullable is not { } world)
                return null;

            var name = read(world);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception error)
        {
            // Worth surviving rather than throwing: the window falls back to asking.
            log.Warning(error, "Could not work out where you are logged in.");
            return null;
        }
    }
}
