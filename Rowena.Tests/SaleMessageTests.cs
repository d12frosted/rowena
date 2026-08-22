using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class SaleMessageTests
{
    [Fact]
    public void ASingleItemSoldReadsAsOneOfThem()
    {
        var sale = SaleMessage.Read(
            "The Mythrite Ore you put up for sale in the Limsa Lominsa markets has sold for 1,894 gil (after fees).");

        Assert.Equal(1, sale!.Value.Quantity);
        Assert.Equal(1894, sale.Value.Gil);
    }

    [Fact]
    public void AStackSoldReadsAsTheWholeStack()
    {
        var sale = SaleMessage.Read(
            "The 5 Mythrite Ore you put up for sale in the New Gridania markets has sold for 9,470 gil (after fees).");

        Assert.Equal(5, sale!.Value.Quantity);
        Assert.Equal(9470, sale.Value.Gil);
    }

    [Fact]
    public void TheGilIsTheNumberBesideTheWordAndNotTheBiggestOne()
    {
        // A big stack of something cheap has a quantity that would win a "largest number"
        // guess, which is exactly the case worth getting right.
        var sale = SaleMessage.Read(
            "The 99 Water Crystal you put up for sale in the Ul'dah markets has sold for 47 gil (after fees).");

        Assert.Equal(99, sale!.Value.Quantity);
        Assert.Equal(47, sale.Value.Gil);
    }

    [Fact]
    public void AnItemWhoseNameStartsWithADigitIsStillOneItem()
    {
        // "The 2nd Ward..." is not a quantity, and neither is a grade in a name.
        var sale = SaleMessage.Read(
            "The Grade 3 Thanalan Topsoil you put up for sale in the Kugane markets has sold for 2,133 gil (after fees).");

        Assert.Equal(1, sale!.Value.Quantity);
        Assert.Equal(2133, sale.Value.Gil);
    }

    [Fact]
    public void AMessageThatIsNotASaleIsNotRead() =>
        Assert.Null(SaleMessage.Read("Your retainer has returned from their venture."));

    [Fact]
    public void NothingIsReadFromNothing() => Assert.Null(SaleMessage.Read(""));
}
