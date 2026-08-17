using System.Text;
using Rowena.Core.Lists;
using Xunit;

namespace Rowena.Tests;

public class TeamcraftListTests
{
    private static string Decode(string encoded) => Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

    [Fact]
    public void EncodesItemIdQuantityPairs()
    {
        // The shape Artisan's own exporter produces, so anything that reads its links reads these.
        var encoded = TeamcraftList.Encode([new TeamcraftEntry(6524, 5), new TeamcraftEntry(6549, 2)]);

        Assert.Equal("6524,null,5;6549,null,2", Decode(encoded));
    }

    [Fact]
    public void TheLinkIsTheImportUrlPlusThePayload()
    {
        var url = TeamcraftList.Url([new TeamcraftEntry(6524, 1)]);

        Assert.StartsWith(TeamcraftList.ImportBase, url);
        Assert.Equal("6524,null,1", Decode(url[TeamcraftList.ImportBase.Length..]));
    }

    [Fact]
    public void NothingToSendMeansNoLink()
    {
        Assert.Equal("", TeamcraftList.Url([]));
        Assert.Equal("", TeamcraftList.Encode([]));
    }

    [Fact]
    public void EntriesWithoutAnItemOrAQuantityAreDropped()
    {
        // A zero would encode as a request for nothing, which Teamcraft would either reject or
        // silently include as an empty row. Neither is worth passing on.
        var encoded = TeamcraftList.Encode(
        [
            new TeamcraftEntry(0, 5),
            new TeamcraftEntry(6524, 0),
            new TeamcraftEntry(6549, 3),
        ]);

        Assert.Equal("6549,null,3", Decode(encoded));
    }

    [Fact]
    public void ItemIdsAreSentRatherThanRecipeIds()
    {
        // Worth pinning: Artisan's list format is keyed by recipe and Teamcraft's is keyed by item,
        // and sending one where the other is expected produces a plausible link to the wrong things.
        var encoded = TeamcraftList.Encode([new TeamcraftEntry(6524, 1)]);

        Assert.StartsWith("6524,", Decode(encoded));
    }
}
