using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class LiquidationTests
{
    private static HoardVerdict Of(
        int quantity,
        long? floor,
        double velocity,
        long vendor,
        KeepWhy keep = KeepWhy.Surplus,
        int horizonDays = 7,
        long slotFloor = 0,
        int slotHolds = 999) =>
        Liquidation.Of(quantity, floor, velocity, vendor, MarketTax.None, horizonDays, slotFloor, slotHolds, keep);

    [Fact]
    public void SomethingWorthMoreOnTheBoardIsWorthListing()
    {
        var call = Of(20, 1000, 50, 100);

        Assert.Equal(HoardCall.List, call.Call);
        Assert.Equal(20_000, call.Worth);
    }

    [Fact]
    public void SomethingAVendorPaysMoreForGoesToTheVendor()
    {
        var call = Of(20, 80, 50, 100);

        Assert.Equal(HoardCall.Vendor, call.Call);
        Assert.Equal(2_000, call.Worth);
    }

    [Fact]
    public void TheVendorIsJudgedAgainstWhatTheBoardWouldLeaveMe()
    {
        // A hundred on the board is ninety-five after the city takes its cut, so a vendor
        // paying ninety-six wins on a sticker price that looks lower.
        var call = Liquidation.Of(10, 100, 50, 96, MarketTax.Standard, 7, 0, 999, KeepWhy.Surplus);

        Assert.Equal(HoardCall.Vendor, call.Call);
    }

    [Fact]
    public void NothingSellingMeansTheVendorIsTheOnlyBuyer()
    {
        var call = Of(20, 5000, 0, 10);

        Assert.Equal(HoardCall.Vendor, call.Call);
        Assert.Equal(200, call.Worth);
    }

    [Fact]
    public void UnlistedAndUnwantedIsJustClutter()
    {
        var call = Of(20, null, 0, 0);

        Assert.Equal(HoardCall.Worthless, call.Call);
        Assert.Equal(0, call.Worth);
    }

    [Fact]
    public void SomethingNeededForACraftIsNotForSaleAtAll()
    {
        // The pile is not all surplus, and telling somebody to vendor the materials for the
        // thing the craft table just told them to make would be the worst advice here.
        var call = Of(20, 1000, 50, 100, KeepWhy.Wanted);

        Assert.Equal(HoardCall.Keep, call.Call);
        Assert.Equal(KeepWhy.Wanted, call.Keep);
    }

    [Fact]
    public void SomethingIUseIsNotSurplusEither()
    {
        // Chocobo greens are not materials waiting to be sold, they are a thing I feed a
        // chocobo with, and no amount of arithmetic about the board knows that.
        var call = Of(400, 300, 90, 20, KeepWhy.Mine);

        Assert.Equal(HoardCall.Keep, call.Call);
        Assert.Equal(KeepWhy.Mine, call.Keep);
    }

    [Fact]
    public void AnUnlockIHaveNotLearnedIsNeverForSale()
    {
        // The one mistake here that cannot be undone with gil: an orchestrion roll I have not
        // played is worth the thing it teaches, whatever the board or the vendor says.
        var call = Of(1, 200, 4, 100, KeepWhy.Unlearned);

        Assert.Equal(HoardCall.Keep, call.Call);
        Assert.Equal(KeepWhy.Unlearned, call.Keep);
    }

    [Fact]
    public void AnUnlockNobodyWantsIsStillNotClutter()
    {
        // Keeping beats every other answer, including the one that says a thing is only a bag
        // slot. Nothing pays for this and it is still the last copy of something I cannot buy.
        var call = Of(1, null, 0, 0, KeepWhy.Unlearned);

        Assert.Equal(HoardCall.Keep, call.Call);
    }

    [Fact]
    public void SurplusIsTheDefaultAndSaysSo() =>
        Assert.Equal(KeepWhy.Surplus, Of(20, 1000, 50, 100).Keep);

    [Fact]
    public void AStackThatCannotEarnItsSlotGoesToTheVendor()
    {
        // Ten of something worth thirty is three hundred gil for a slot that could hold
        // anything else. The vendor pays now, takes all ten, and asks for no slot at all.
        var call = Of(10, 30, 50, 5, slotFloor: 500);

        Assert.Equal(HoardCall.Vendor, call.Call);
    }

    [Fact]
    public void TheFloorIsAboutTheStackAndNotTheUnitPrice()
    {
        // Four hundred chocobo greens at 295 are a cheap item and a hundred and eighteen
        // thousand gil. A floor read per unit would vendor the second largest pile I own.
        var call = Of(400, 295, 90, 20, slotFloor: 500);

        Assert.Equal(HoardCall.List, call.Call);
    }

    [Fact]
    public void AFortuneThatNeverSellsCannotEarnItsSlotEither()
    {
        // Ten thousand gil of stock at a hundredth of a sale a day realises seventy gil of it
        // in a week. What a slot earns is what sells while it is occupied, not what is in it.
        var call = Of(10, 1000, 0.01, 100, slotFloor: 500);

        Assert.Equal(HoardCall.Vendor, call.Call);
        Assert.Equal(70, call.Realised);
    }

    [Fact]
    public void TheFloorNeverSendsAnythingToAVendorThatPaysNothing()
    {
        // Below the floor is a statement about the slot, not about the item. Where the board is
        // the only counter there is, it is still the answer.
        var call = Of(10, 30, 50, 0, slotFloor: 500);

        Assert.Equal(HoardCall.List, call.Call);
    }

    [Fact]
    public void KeepingBeatsTheFloorToo()
    {
        var call = Of(10, 30, 50, 5, KeepWhy.Mine, slotFloor: 500);

        Assert.Equal(HoardCall.Keep, call.Call);
    }

    [Fact]
    public void AListingHoldsAStackAndTheStackDependsOnTheItem()
    {
        // Measured: 1,425 Hardsilver Sand, which stacks to 999, so one listing is 999 of them
        // and the rest is another listing.
        var call = Of(1_425, 683, 500, 2, slotHolds: 999);

        Assert.Equal(999, call.Listable);
        Assert.Equal(999 * 683, call.Realised);
        Assert.Equal(1_425 * 683, call.Worth);
    }

    [Fact]
    public void SomethingThatStacksToNinetyNineListsNinetyNine() =>
        Assert.Equal(99, Of(400, 300, 90, 20, slotHolds: 99).Listable);

    [Fact]
    public void SomethingUniqueIsOneListingOfOne() =>
        Assert.Equal(1, Of(4, 100_000, 50, 2, slotHolds: 1).Listable);

    [Fact]
    public void AStackSmallerThanTheListingIsJudgedWhole() =>
        Assert.Equal(40, Of(40, 100, 50, 2, slotHolds: 999).Listable);

    [Fact]
    public void OnlyWhatFitsInTheListingCountsTowardsTheFloor()
    {
        // Two hundred of something worth three that stacks to ninety-nine. The pile is six
        // hundred gil and a listing of ninety-nine earns a fraction of that.
        var call = Of(200, 3, 10, 1, slotFloor: 500, slotHolds: 99);

        Assert.Equal(HoardCall.Vendor, call.Call);
    }

    [Fact]
    public void WhatASlotWouldEarnIsReported()
    {
        // A hundred units at a thousand, twenty a day: five days to clear, so all of it lands
        // inside a seven day horizon and the slot earns the lot.
        var call = Of(100, 1000, 20, 10);

        Assert.Equal(100_000, call.Realised);
    }

    [Fact]
    public void HowLongTheBoardWouldTakeToEatTheWholePileIsReported()
    {
        var call = Of(100, 1000, 50, 10);

        Assert.Equal(2d, call.DaysToSell!.Value, 3);
    }

    [Fact]
    public void APileBiggerThanTheBoardsAppetiteSaysSo()
    {
        // Two hundred days of supply is not a listing, it is a storage problem, and the
        // vendor becomes the honest answer for most of it.
        var call = Of(1000, 1000, 5, 10);

        Assert.Equal(HoardCall.List, call.Call);
        Assert.True(call.Slow);
    }

    [Fact]
    public void APileTheBoardClearsInsideTheHorizonIsNotSlow() =>
        Assert.False(Of(10, 1000, 50, 10).Slow);
}
