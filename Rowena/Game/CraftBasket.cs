using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Rowena.Core.Lists;

namespace Rowena.Game;

/// <summary>
/// A plan of what to make, held until there is enough of it to be worth a list.
/// </summary>
/// <remarks>
/// Gathered here and sent as one Teamcraft link, rather than exported per click, which would leave a
/// list per furnishing.
///
/// Teamcraft rather than any one plugin's own format because it is the only thing they all read:
/// Artisan imports and exports it, GatherBuddyReborn's Vulcan window has a tab for it, and the site
/// itself resolves the sub-crafts, which no plugin-specific export here would have done.
///
/// Kept in the configuration rather than the price cache. A plan is something you meant, not
/// something that was fetched, so it should survive a reload for the same reason a setting does.
///
/// Nothing here is filtered by what you already own, and that is deliberate. A list says what you
/// intend to make; Artisan decides at craft time what actually needs doing, and it already skips
/// what is in stock. Subtracting inventory here would bake a snapshot of your bags into a document
/// that outlives it, and would be wrong by the time you ran it.
/// </remarks>
internal sealed class CraftBasket(Configuration config, Recipes recipes, Action save, IPluginLog log)
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

    /// <summary>
    /// Nudges a quantity, never below one.
    /// </summary>
    /// <remarks>
    /// Quantity is edited here rather than chosen when adding. One menu entry that adds a single
    /// craft, and the number adjusted afterwards where the list is visible, beats three entries
    /// offering amounts you cannot see the consequences of.
    /// </remarks>
    public void Adjust(uint recipeId, int delta)
    {
        if (config.ArtisanBasket.FirstOrDefault(item => item.RecipeId == recipeId) is not { } item)
            return;

        item.Quantity = Math.Max(1, item.Quantity + delta);
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

    /// <summary>The Teamcraft link for everything collected, or empty when there is nothing.</summary>
    public string Link() =>
        TeamcraftList.Url(config.ArtisanBasket.Select(item => new TeamcraftEntry(item.ItemId, item.Quantity)));

    /// <summary>Puts the Teamcraft link on the clipboard.</summary>
    public bool CopyLink()
    {
        var url = Link();
        if (url.Length == 0)
            return false;

        ImGui.SetClipboardText(url);
        log.Information($"Copied a Teamcraft link for {config.ArtisanBasket.Count} items.");
        return true;
    }

    /// <summary>
    /// Everything to craft, sub-crafts included, deepest first.
    /// </summary>
    /// <remarks>
    /// Artisan's list format holds what you asked for and nothing beneath it, so the tree is walked
    /// here. Teamcraft does its own, which is why only this side needs it.
    /// </remarks>
    public IReadOnlyList<CraftStep> Steps() =>
        RecipeTree.Expand(
            config.ArtisanBasket.Select(item => new CraftStep(item.RecipeId, item.ItemId, item.Quantity, 0)),
            recipes.ByItem);

    /// <summary>
    /// Puts the list on the clipboard in Artisan's own import format, sub-crafts and all.
    /// </summary>
    /// <remarks>
    /// One paste, against five steps for the Teamcraft route, which is why both exist.
    /// </remarks>
    public bool CopyForArtisan()
    {
        if (config.ArtisanBasket.Count == 0)
            return false;

        try
        {
            var steps = Steps();
            if (steps.Count == 0)
                return false;

            var json = ArtisanList.Build(
                config.ArtisanListName,
                steps.Select(step => new ArtisanEntry(step.RecipeId, step.Crafts)));

            ImGui.SetClipboardText(json);
            log.Information(
                $"Copied {steps.Count} recipes in Artisan's list format, expanded from "
                + $"{config.ArtisanBasket.Count}.");
            return true;
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not build the Artisan list.");
            return false;
        }
    }

    /// <summary>Opens the link in a browser.</summary>
    public bool Open()
    {
        var url = Link();
        if (url.Length == 0)
            return false;

        try
        {
            Util.OpenLink(url);
            return true;
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not open the Teamcraft link.");
            return false;
        }
    }
}
