using Rowena.Core.Lists;
using Xunit;

namespace Rowena.Tests;

public class RecipeTreeTests
{
    private const uint Wardrobe = 100;
    private const uint Hinge = 200;
    private const uint Plate = 300;
    private const uint Ingot = 400;
    private const uint Ore = 500;
    private const uint Bench = 600;

    /// <summary>Ore has no recipe: it is bought or gathered, so it is never a step.</summary>
    private static readonly RecipeNode[] Recipes =
    [
        new(1, Wardrobe, 1, [(Hinge, 2), (Plate, 2), (Ore, 4)]),
        new(2, Hinge, 1, [(Ingot, 1)]),
        new(3, Plate, 1, [(Ingot, 2)]),
        new(4, Ingot, 3, [(Ore, 5)]),
        new(5, Bench, 1, [(Hinge, 1)]),
    ];

    private static RecipeNode? ByItem(uint itemId) => Recipes.FirstOrDefault(r => r.ItemId == itemId);

    private static IReadOnlyList<CraftStep> Expand(params CraftStep[] wanted) =>
        RecipeTree.Expand(wanted, ByItem);

    private static CraftStep Want(uint itemId, int crafts) => new(0, itemId, crafts, 0);

    private static int CraftsOf(IReadOnlyList<CraftStep> steps, uint itemId) =>
        steps.SingleOrDefault(step => step.ItemId == itemId).Crafts;

    [Fact]
    public void WhatYouAskedForSurvives()
    {
        Assert.Equal(5, CraftsOf(Expand(Want(Wardrobe, 5)), Wardrobe));
    }

    [Fact]
    public void IntermediatesAppearWithTheirQuantitiesMultipliedThrough()
    {
        // Five wardrobes want ten hinges and ten plates.
        var steps = Expand(Want(Wardrobe, 5));

        Assert.Equal(10, CraftsOf(steps, Hinge));
        Assert.Equal(10, CraftsOf(steps, Plate));
    }

    [Fact]
    public void RawMaterialsAreNotSteps()
    {
        // Ore is bought. A list telling you to craft ore would be nonsense.
        Assert.DoesNotContain(Expand(Want(Wardrobe, 1)), step => step.ItemId == Ore);
    }

    [Fact]
    public void YieldIsAccountedFor()
    {
        // One wardrobe needs 2 hinges and 2 plates, so 2 ingots and 4 ingots: 6 ingots. The ingot
        // recipe makes three at a time, so two crafts and not six.
        Assert.Equal(2, CraftsOf(Expand(Want(Wardrobe, 1)), Ingot));
    }

    [Fact]
    public void SharedIntermediatesAreOneEntryWithTheAmountsAdded()
    {
        // A wardrobe wants two hinges and a bench wants one. Three hinges, in one row, not two rows
        // that each look sufficient on their own.
        var steps = Expand(Want(Wardrobe, 1), Want(Bench, 1));

        Assert.Single(steps, step => step.ItemId == Hinge);
        Assert.Equal(3, CraftsOf(steps, Hinge));
    }

    [Fact]
    public void RoundingHappensOnceOnTheTotal()
    {
        // The reason this resolves in depth order. Two consumers each needing one ingot's worth
        // would round to a craft apiece if settled as they arrived; together they need one craft.
        // A hinge wants one ingot and a plate wants two. Settled as they arrive that is a craft
        // apiece, two in total; settled together it is three ingots, which one craft of three covers.
        var steps = Expand(Want(Hinge, 1), Want(Plate, 1));

        Assert.Equal(1, CraftsOf(steps, Hinge));
        Assert.Equal(1, CraftsOf(steps, Plate));
        Assert.Equal(1, CraftsOf(steps, Ingot));
    }

    [Fact]
    public void NothingIsCraftedBeforeWhatItIsMadeOf()
    {
        var steps = Expand(Want(Wardrobe, 1));
        var order = steps.Select(step => step.ItemId).ToList();

        Assert.True(order.IndexOf(Ingot) < order.IndexOf(Hinge), "ingots come before hinges");
        Assert.True(order.IndexOf(Hinge) < order.IndexOf(Wardrobe), "hinges come before the wardrobe");
        Assert.True(order.IndexOf(Plate) < order.IndexOf(Wardrobe), "plates come before the wardrobe");
    }

    [Fact]
    public void AnItemNeededAtTwoDepthsSinksToTheDeeper()
    {
        // Hinges are wanted directly and also under the wardrobe. They must still come first.
        var steps = Expand(Want(Wardrobe, 1), Want(Hinge, 2));
        var order = steps.Select(step => step.ItemId).ToList();

        Assert.True(order.IndexOf(Hinge) < order.IndexOf(Wardrobe));
        Assert.Equal(4, CraftsOf(steps, Hinge));
    }

    [Fact]
    public void SomethingWithNoRecipeIsNotExpanded()
    {
        Assert.Empty(Expand(Want(Ore, 5)));
    }

    [Fact]
    public void NothingWantedIsNothingToDo()
    {
        Assert.Empty(Expand());
        Assert.Empty(Expand(Want(Wardrobe, 0)));
    }

    [Fact]
    public void ACycleInTheDataDoesNotHang()
    {
        // Not a thing the game's sheets contain, but a list that never returns would be a far worse
        // failure than a wrong one.
        RecipeNode[] loop =
        [
            new(1, 10, 1, [(20, 1)]),
            new(2, 20, 1, [(10, 1)]),
        ];

        var steps = RecipeTree.Expand(
            [Want(10, 1)],
            itemId => loop.FirstOrDefault(recipe => recipe.ItemId == itemId));

        Assert.NotEmpty(steps);
    }
}
