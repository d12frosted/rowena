using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Rowena.Core.Conversions;

namespace Rowena.Game;

/// <summary>
/// Every craftable, tradable furnishing, as conversions.
/// </summary>
/// <remarks>
/// A craft is materials in and a product out at a fixed ratio, which is precisely a
/// <see cref="Conversion"/>. Saying so means the existing machinery prices it with no new
/// arithmetic: ingredients costed by walking the book, the product valued net of tax, absorption
/// from its sale velocity, and unpriceable rows reported rather than guessed at.
///
/// Furnishings are identified from HousingFurniture and HousingYardObject rather than by item
/// category. Those two sheets are the game's own answer to "is this a furnishing", where the
/// category route means hardcoding a list of category ids and hoping none are added.
///
/// Only direct ingredients. Following the tree down would find cheaper routes and rescue
/// recipes whose intermediates are not traded, but pricing the ingredients as listed is a real
/// executable route on its own: buy the lot. Whether the tree is worth building is a question
/// the discard count answers, so it is measured rather than assumed.
/// </remarks>
internal sealed class Furnishings(IDataManager data, IPluginLog log)
{
    private IReadOnlyList<Conversion>? cached;
    private readonly Dictionary<string, Made> made = new(StringComparer.Ordinal);

    /// <summary>What a conversion actually is in the game's terms.</summary>
    /// <remarks>
    /// Kept beside the conversions rather than inside them. A recipe id is what the crafting log
    /// and Artisan want, and neither is any business of a project that does not reference the
    /// game.
    /// </remarks>
    /// <param name="JobId">The ClassJob row, for its icon and abbreviation.</param>
    internal readonly record struct Made(uint RecipeId, uint ItemId, uint JobId, string Job);

    /// <summary>
    /// CraftType 0 is Carpenter, which is ClassJob 8, and the eight run in step from there.
    /// </summary>
    /// <remarks>
    /// Added rather than looked up because CraftType carries no reference to ClassJob. The offset is
    /// the whole of the relationship.
    /// </remarks>
    private const uint FirstCrafterClassJob = 8;

    /// <summary>Built once. The sheets do not change while the game is running.</summary>
    public IReadOnlyList<Conversion> Craftable() => cached ??= Build();

    /// <summary>The recipe and product behind a conversion, if it came from here.</summary>
    public Made? Behind(string conversionId) =>
        made.TryGetValue(conversionId, out var found) ? found : null;

    private IReadOnlyList<Conversion> Build()
    {
        var housing = HousingItemIds();
        if (housing.Count == 0)
        {
            log.Warning("No housing furnishings found in the sheets; the craft sweep will be empty.");
            return [];
        }

        var recipes = data.GetExcelSheet<Recipe>();
        var conversions = new List<Conversion>();

        // Several jobs can make the same furnishing. The materials are the same either way, so
        // a second recipe for one item is a duplicate row rather than a second opportunity.
        var seen = new HashSet<uint>();

        foreach (var recipe in recipes)
        {
            var resultId = recipe.ItemResult.RowId;

            if (resultId == 0 || !housing.Contains(resultId) || !seen.Add(resultId))
                continue;

            if (recipe.ItemResult.ValueNullable is not { } product || product.IsUntradable)
                continue;

            var yield = Math.Max(1, (int)recipe.AmountResult);
            var ingredients = Ingredients(recipe);

            // A recipe with nothing to buy is not one this can price. It happens for rows that
            // exist in the sheet without being real recipes.
            if (ingredients.Count == 0)
                continue;

            var name = product.Name.ExtractText();
            var jobId = recipe.CraftType.RowId + FirstCrafterClassJob;

            made[$"craft-{recipe.RowId}"] = new Made(
                recipe.RowId,
                resultId,
                jobId,
                data.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId)?.Abbreviation.ExtractText() ?? "");

            conversions.Add(new Conversion(
                $"craft-{recipe.RowId}",
                string.IsNullOrWhiteSpace(name) ? $"item {resultId}" : name,
                ingredients,
                [new ResourceAmount(Resource.Item(resultId, name), yield)],
                recipe.CraftType.ValueNullable?.Name.ExtractText() ?? "craft"));
        }

        log.Information($"Found {conversions.Count} craftable tradable furnishings.");
        return conversions;
    }

    private static List<ResourceAmount> Ingredients(Recipe recipe)
    {
        var ingredients = new List<ResourceAmount>();
        var slots = Math.Min(recipe.Ingredient.Count, recipe.AmountIngredient.Count);

        for (var slot = 0; slot < slots; slot++)
        {
            var ingredient = recipe.Ingredient[slot];
            var amount = recipe.AmountIngredient[slot];

            if (ingredient.RowId == 0 || amount == 0)
                continue;

            if (ingredient.ValueNullable is not { } item)
                continue;

            var name = item.Name.ExtractText();
            ingredients.Add(new ResourceAmount(
                Resource.Item(ingredient.RowId, string.IsNullOrWhiteSpace(name) ? $"item {ingredient.RowId}" : name),
                amount));
        }

        return ingredients;
    }

    private HashSet<uint> HousingItemIds()
    {
        var ids = new HashSet<uint>();

        foreach (var row in data.GetExcelSheet<HousingFurniture>())
            ids.Add(row.Item.RowId);

        foreach (var row in data.GetExcelSheet<HousingYardObject>())
            ids.Add(row.Item.RowId);

        ids.Remove(0);
        return ids;
    }
}
