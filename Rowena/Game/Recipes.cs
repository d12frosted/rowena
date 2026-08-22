using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Rowena.Core.Lists;

namespace Rowena.Game;

/// <summary>
/// Which recipe makes an item, for every craftable item in the game.
/// </summary>
/// <remarks>
/// Wider than <see cref="Craftables"/>, which only cares about things you can put in a house. A
/// wardrobe's steel hinges are not furnishings and still have to be found, so expanding a craft
/// tree needs the whole sheet.
///
/// Built once on first use. It is ten thousand rows, so it is a visible pause if that first use
/// happens in a frame, which is why the caller should reach for it off the framework thread.
/// </remarks>
internal sealed class Recipes(IDataManager data, IPluginLog log)
{
    private Dictionary<uint, RecipeNode>? byItem;

    /// <summary>The recipe making an item, or null when nothing does.</summary>
    public RecipeNode? ByItem(uint itemId) => Index().GetValueOrDefault(itemId);

    private Dictionary<uint, RecipeNode> Index()
    {
        if (byItem is not null)
            return byItem;

        var index = new Dictionary<uint, RecipeNode>();

        foreach (var recipe in data.GetExcelSheet<Recipe>())
        {
            var itemId = recipe.ItemResult.RowId;

            // Several jobs can make the same thing. Whichever comes first will do: the ingredients
            // are the same, and picking between them is a question about your jobs, not this one.
            if (itemId == 0 || index.ContainsKey(itemId))
                continue;

            var ingredients = new List<(uint ItemId, int Amount)>();
            var slots = Math.Min(recipe.Ingredient.Count, recipe.AmountIngredient.Count);

            for (var slot = 0; slot < slots; slot++)
            {
                var ingredient = recipe.Ingredient[slot];
                var amount = recipe.AmountIngredient[slot];

                if (ingredient.RowId != 0 && amount > 0)
                    ingredients.Add((ingredient.RowId, amount));
            }

            if (ingredients.Count == 0)
                continue;

            index[itemId] = new RecipeNode(
                recipe.RowId,
                itemId,
                Math.Max(1, (int)recipe.AmountResult),
                ingredients);
        }

        log.Information($"Indexed {index.Count} recipes.");
        return byItem = index;
    }
}
