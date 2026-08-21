using System.Text;
using Rowena.Core.Universalis;
using Xunit;

namespace Rowena.Tests;

public class BsonTests
{
    [Fact]
    public void ASubscribeMessageIsShapedTheWayTheFeedExpects()
    {
        var bytes = Bson.Document(("event", "subscribe"), ("channel", "listings/add{world=67}"));

        // Length prefix, and the whole thing terminated: what a reader will look for.
        Assert.Equal(bytes.Length, BitConverter.ToInt32(bytes));
        Assert.Equal(0, bytes[^1]);

        var read = Bson.Read(bytes);

        Assert.Equal("subscribe", read["event"]);
        Assert.Equal("listings/add{world=67}", read["channel"]);
    }

    [Fact]
    public void ARecordedListingsFrameReadsBack()
    {
        // Captured off the live feed, so this pins the shapes rather than my reading of them.
        var frame = Fixtures.Bytes("ws-listings-add.bson");

        var read = Bson.Read(frame);

        Assert.Equal("listings/add", read["event"]);
        Assert.True(Convert.ToUInt32(read["item"]) > 0);
        Assert.Equal(67u, Convert.ToUInt32(read["world"]));

        var listings = Assert.IsType<List<object?>>(read["listings"]);
        Assert.NotEmpty(listings);

        var first = Assert.IsType<Dictionary<string, object?>>(listings[0]);
        Assert.True(Convert.ToInt64(first["pricePerUnit"]) > 0);
        Assert.True(Convert.ToInt32(first["quantity"]) > 0);
        Assert.Contains("listingID", first.Keys);
    }

    [Fact]
    public void ARecordedSalesFrameReadsBack()
    {
        var read = Bson.Read(Fixtures.Bytes("ws-sales-add.bson"));

        Assert.Equal("sales/add", read["event"]);

        var sales = Assert.IsType<List<object?>>(read["sales"]);
        Assert.NotEmpty(sales);
    }

    [Fact]
    public void SomethingUnreadableIsLoudRatherThanQuiet()
    {
        // A frame carrying a type this does not know is a change upstream, and reading round
        // it would mean acting on half a message.
        var broken = new byte[] { 12, 0, 0, 0, 0x7F, (byte)'x', 0, 0, 0, 0, 0, 0 };

        Assert.Throws<InvalidDataException>(() => Bson.Read(broken));
    }

    [Fact]
    public void AChannelNamesTheEventAndTheWorld()
    {
        Assert.Equal("listings/add{world=67}", MarketFeed.Channel("listings/add", 67));
    }
}
