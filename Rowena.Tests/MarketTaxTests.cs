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
        Assert.Equal(Fixtures.RecordedListingTax, MarketTax.Standard.On(Fixtures.RecordedListingTotal));
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
        Assert.Equal(Fixtures.RecordedHalfGilListingTax, MarketTax.Standard.On(Fixtures.RecordedHalfGilListingTotal));
    }

    [Fact]
    public void NoTaxLeavesTheSaleAlone()
    {
        Assert.Equal(1_000, MarketTax.None.NetProceeds(1_000));
    }
}
