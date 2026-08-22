using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class SaleReconciliationTests
{
    private static MarketSlot Slot(uint itemId, int quantity, long price) => new(itemId, quantity, price);

    private static readonly MarketSlot Empty = default;

    [Fact]
    public void AnEmptiedSlotThatBroughtGilIsASale()
    {
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000)],
            [Empty],
            gilGained: 10_000,
            MarketTax.None);

        Assert.Single(sales);
        Assert.Equal(10, sales[0].Quantity);
        Assert.Equal(10_000, sales[0].Gross);
    }

    [Fact]
    public void AnEmptiedSlotThatBroughtNoGilWasTakenOffTheMarket()
    {
        // The whole reason the retainer's purse is read at all: a listing that vanished and a
        // listing that sold look identical from the slots alone, and calling a cancellation a
        // sale would quietly invent income.
        var sales = SaleReconciliation.Between([Slot(1, 10, 1000)], [Empty], gilGained: 0, MarketTax.None);

        Assert.Empty(sales);
    }

    [Fact]
    public void TheGilIsCountedNetOfTheCitysCut()
    {
        // Ten at a thousand is ten thousand listed and 9,500 in the purse, so 9,500 is what
        // has to turn up for this to have been a sale.
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000)],
            [Empty],
            gilGained: 9_500,
            MarketTax.Standard);

        Assert.Single(sales);
        Assert.Equal(9_500, sales[0].Net);
        Assert.Equal(10_000, sales[0].Gross);
    }

    [Fact]
    public void PartOfAStackSellingIsPartOfASale()
    {
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000)],
            [Slot(1, 4, 1000)],
            gilGained: 6_000,
            MarketTax.None);

        Assert.Single(sales);
        Assert.Equal(6, sales[0].Quantity);
    }

    [Fact]
    public void RepricingIsNotASale()
    {
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000)],
            [Slot(1, 10, 1200)],
            gilGained: 0,
            MarketTax.None);

        Assert.Empty(sales);
    }

    [Fact]
    public void TwoSlotsEmptyWithGilForOnlyOneGivesOneSale()
    {
        // AllaganMarket checks each vanished slot against the same purse without spending it,
        // so two things going at once can both be called sales on one thing's worth of gil.
        // The budget is spent down as it is attributed.
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000), Slot(2, 10, 1000)],
            [Empty, Empty],
            gilGained: 10_000,
            MarketTax.None);

        Assert.Single(sales);
    }

    [Fact]
    public void TwoSlotsEmptyWithGilForBothGivesTwoSales()
    {
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000), Slot(2, 10, 1000)],
            [Empty, Empty],
            gilGained: 20_000,
            MarketTax.None);

        Assert.Equal(2, sales.Count);
    }

    [Fact]
    public void GilLeavingThePurseExplainsNothing()
    {
        // Withdrawing from the retainer makes the purse smaller. Read as an unsigned
        // difference that becomes an enormous number and every empty slot becomes a sale.
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000)],
            [Empty],
            gilGained: -500_000,
            MarketTax.None);

        Assert.Empty(sales);
    }

    [Fact]
    public void ADifferentItemInTheSlotIsNotReadAsASale()
    {
        // Slots get reused. Something else standing where mine was says nothing about what
        // happened to mine, so it is left alone rather than guessed at.
        var sales = SaleReconciliation.Between(
            [Slot(1, 10, 1000)],
            [Slot(2, 5, 800)],
            gilGained: 10_000,
            MarketTax.None);

        Assert.Empty(sales);
    }

    [Fact]
    public void NothingChangingIsNoSale() =>
        Assert.Empty(SaleReconciliation.Between([Slot(1, 10, 1000)], [Slot(1, 10, 1000)], 0, MarketTax.None));

    [Fact]
    public void NoPreviousLookIsNoOpinion() =>
        Assert.Empty(SaleReconciliation.Between([], [Slot(1, 10, 1000)], 50_000, MarketTax.None));
}
