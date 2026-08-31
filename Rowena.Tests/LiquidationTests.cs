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
        int horizonDays = 7) =>
        Liquidation.Of(quantity, floor, velocity, vendor, MarketTax.None, horizonDays, keep);

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
        var call = Liquidation.Of(10, 100, 50, 96, MarketTax.Standard, 7, KeepWhy.Surplus);

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
