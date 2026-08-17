namespace Rowena.Core.Lists;

/// <param name="Yield">How many the recipe makes per craft, which is not always one.</param>
public sealed record RecipeNode(
    uint RecipeId,
    uint ItemId,
    int Yield,
    IReadOnlyList<(uint ItemId, int Amount)> Ingredients);

/// <param name="Crafts">Times to run the recipe, not the number of items produced.</param>
/// <param name="Depth">How far below the things you asked for this sits. Zero is what you asked for.</param>
public readonly record struct CraftStep(uint RecipeId, uint ItemId, int Crafts, int Depth);

/// <summary>
/// Expands wanted crafts into everything that has to be made first.
/// </summary>
/// <remarks>
/// Asking for five wardrobes and being handed a list saying "five wardrobes" is not a list, it is the
/// thing you already knew. What makes it useful is the ten steel hinges and the ten iron plates
/// underneath, ordered so the list can be worked from the top.
///
/// Teamcraft does this itself, which is why its link needs none of it. Artisan's own list format does
/// not: it lists what you asked for and nothing beneath it, so the expansion has to happen before the
/// list is handed over.
///
/// Resolved in depth order rather than by walking outward, and that matters. Walking outward
/// computes a craft count per requirement as it arrives, so two separate calls for one steel hinge
/// round up to two crafts where one craft of three would have covered both. Settling every consumer
/// of an item before the item itself makes the arithmetic exact.
/// </remarks>
public static class RecipeTree
{
    /// <summary>Deep enough for any real chain, and a backstop against a cycle in the data.</summary>
    private const int MaxDepth = 10;

    /// <summary>
    /// Everything to craft, deepest first, so nothing is made before what it is made of.
    /// </summary>
    /// <param name="byItem">
    /// The recipe making an item, or null when nothing does. Ingredients with no recipe are bought or
    /// gathered, so they are not steps and do not appear.
    /// </param>
    public static IReadOnlyList<CraftStep> Expand(
        IEnumerable<CraftStep> wanted,
        Func<uint, RecipeNode?> byItem)
    {
        var recipes = new Dictionary<uint, RecipeNode>();
        var depth = new Dictionary<uint, int>();
        var needed = new Dictionary<uint, long>();

        foreach (var step in wanted)
        {
            if (step.Crafts <= 0 || byItem(step.ItemId) is not { } recipe)
                continue;

            Discover(step.ItemId, 0);
            needed[step.ItemId] =
                needed.GetValueOrDefault(step.ItemId) + (long)step.Crafts * Math.Max(1, recipe.Yield);
        }

        // Shallowest first, so every consumer of an item has added its requirement before the item is
        // turned into a craft count.
        foreach (var itemId in depth.OrderBy(entry => entry.Value).Select(entry => entry.Key))
        {
            var recipe = recipes[itemId];
            var crafts = Crafts(itemId, recipe);

            if (crafts <= 0)
                continue;

            foreach (var (ingredientId, amount) in recipe.Ingredients)
            {
                if (amount > 0 && recipes.ContainsKey(ingredientId))
                    needed[ingredientId] = needed.GetValueOrDefault(ingredientId) + crafts * amount;
            }
        }

        return
        [
            .. recipes
                .Select(entry => new CraftStep(
                    entry.Value.RecipeId,
                    entry.Key,
                    (int)Math.Min(int.MaxValue, Crafts(entry.Key, entry.Value)),
                    depth[entry.Key]))
                .Where(step => step.Crafts > 0)
                .OrderByDescending(step => step.Depth)
                .ThenBy(step => step.ItemId),
        ];

        long Crafts(uint itemId, RecipeNode recipe)
        {
            var yield = Math.Max(1, recipe.Yield);
            var quantity = needed.GetValueOrDefault(itemId);
            return (quantity + yield - 1) / yield;
        }

        // Records how deep an item can sit, taking the deepest route to it. A steel hinge needed both
        // directly and through something else belongs below both, or it would be crafted too late.
        void Discover(uint itemId, int at)
        {
            if (at > MaxDepth || byItem(itemId) is not { } recipe)
                return;

            if (depth.TryGetValue(itemId, out var known) && known >= at)
                return;

            recipes[itemId] = recipe;
            depth[itemId] = at;

            foreach (var (ingredientId, amount) in recipe.Ingredients)
            {
                if (amount > 0)
                    Discover(ingredientId, at + 1);
            }
        }
    }
}
