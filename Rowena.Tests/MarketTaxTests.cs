using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class MarketTaxTests
{
    [Fact]
    public void TaxOnARecordedListingMatchesWhatTheBoardCharged()
    {
        // 146,385 x 0.05 is 7,319.25, and the board charged 7,319, so the fraction is
        // dropped rather than rounded. Cheap to get wrong, and it compounds over a
        // multi-million gil sale.
        Assert.Equal(Fixtures.RecordedListingTax, MarketTax.Standard.OnPurchase(Fixtures.RecordedListingTotal));
    }

    [Fact]
    public void NetProceedsIsTheSaleLessTheCut()
    {
        Assert.Equal(
            Fixtures.RecordedListingTotal - Fixtures.RecordedListingTax,
            MarketTax.Standard.NetProceeds(Fixtures.RecordedListingTotal));
    }

    [Fact]
    public void TheHalfGilIsDroppedToo()
    {
        // A recorded Barreltender listing of 7,499,990 carries a tax of 374,999, and 5%
        // of it is exactly 374,999.5. So the cut is floored, not rounded half up.
        Assert.Equal(Fixtures.RecordedHalfGilListingTax, MarketTax.Standard.OnPurchase(Fixtures.RecordedHalfGilListingTotal));
    }

    [Fact]
    public void TheTwoSidesAreChargedSeparately()
    {
        // The buyer always pays five percent. The seller pays what the retainer's city
        // charges, which is nought to five and moves daily.
        var cheapCity = new MarketTax(0.05d, 0.03d);

        Assert.Equal(50, cheapCity.OnPurchase(1_000));
        Assert.Equal(970, cheapCity.NetProceeds(1_000));
    }

    [Fact]
    public void ACityThatChargesNothingLeavesTheWholeSale()
    {
        var free = new MarketTax(0.05d, 0d);

        Assert.Equal(1_000, free.NetProceeds(1_000));
        Assert.Equal(50, free.OnPurchase(1_000));
    }

    [Fact]
    public void TheStandardRateAssumesTheWorstCity()
    {
        // Until the game says otherwise, the seller side is the maximum the game charges,
        // which is what the three original city states always charge. Assuming a cheaper one
        // would flatter every sale.
        Assert.Equal(MarketTax.Standard.SellerRate, 0.05d);
    }

    [Fact]
    public void NoTaxLeavesTheSaleAlone()
    {
        Assert.Equal(1_000, MarketTax.None.NetProceeds(1_000));
    }
}
