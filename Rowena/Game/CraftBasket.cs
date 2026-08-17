using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace Rowena.Game;

/// <summary>
/// Things you have decided to craft, held until there are enough of them to be worth a list.
/// </summary>
/// <remarks>
/// This exists because Artisan's importer always creates a new list and there is no way to append to
/// one. Exporting on every click would leave you with a list per furnishing; gathering them here
/// first means one list with everything in it.
///
/// Kept in the configuration rather than the price cache. A basket is something you meant, not
/// something that was fetched, so it should survive a reload for the same reason a setting does.
/// </remarks>
internal sealed class CraftBasket(Configuration config, Action save, IPluginLog log)
{
    public IReadOnlyList<Configuration.BasketItem> Items => config.ArtisanBasket;

    public int Count => config.ArtisanBasket.Count;

    public int TotalCrafts => config.ArtisanBasket.Sum(item => item.Quantity);

    /// <summary>
    /// Adds crafts, merging with anything already there for the same recipe.
    /// </summary>
    /// <remarks>
    /// Merging rather than appending a second row, because two entries for one recipe is something
    /// Artisan would faithfully reproduce as two entries, and nobody means that.
    /// </remarks>
    public void Add(uint recipeId, uint itemId, string name, int quantity)
    {
        if (recipeId == 0 || quantity <= 0)
            return;

        if (config.ArtisanBasket.FirstOrDefault(item => item.RecipeId == recipeId) is { } existing)
        {
            existing.Quantity += quantity;
        }
        else
        {
            config.ArtisanBasket.Add(new Configuration.BasketItem
            {
                RecipeId = recipeId,
                ItemId = itemId,
                Name = name,
                Quantity = quantity,
            });
        }

        save();
    }

    public void Remove(uint recipeId)
    {
        config.ArtisanBasket.RemoveAll(item => item.RecipeId == recipeId);
        save();
    }

    public void Clear()
    {
        config.ArtisanBasket.Clear();
        save();
    }

    /// <summary>
    /// Puts the basket on the clipboard in Artisan's import format.
    /// </summary>
    /// <returns>False when there is nothing to copy.</returns>
    public bool CopyForArtisan(string name)
    {
        if (config.ArtisanBasket.Count == 0)
            return false;

        try
        {
            var json = ArtisanList.Build(
                name,
                config.ArtisanBasket.Select(item => new ArtisanList.Entry(item.RecipeId, item.Quantity)));

            ImGui.SetClipboardText(json);
            log.Information($"Copied {config.ArtisanBasket.Count} recipes to the clipboard for Artisan.");
            return true;
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not build the Artisan list.");
            return false;
        }
    }
}
