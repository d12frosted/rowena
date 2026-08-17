using Rowena.Core.Universalis;
using Xunit;

namespace Rowena.Tests;

public class UniversalisJsonTests
{
    [Fact]
    public void ParsesARecordedSingleItemResponse()
    {
        var book = Fixtures.Book(Fixtures.MountToken);

        Assert.Equal(41807u, book.ItemId);
        Assert.Equal(48_795, book.Floor);
        Assert.True(book.SaleVelocityPerDay > 0);
        Assert.NotEmpty(book.Listings);
        Assert.All(book.Listings, listing => Assert.True(listing.Quantity > 0));
    }

    [Fact]
    public void ListingsComeBackInPriceOrder()
    {
        var prices = Fixtures.Book(Fixtures.MountToken).Listings.Select(listing => listing.UnitPrice).ToArray();

        Assert.Equal(prices.OrderBy(price => price), prices);
    }

    [Fact]
    public void UploadTimeIsUsedAsTheSnapshotTime()
    {
        // How stale the data is matters, and the honest answer is when a player last saw
        // that board, not when we parsed the file.
        var book = Fixtures.Book(Fixtures.MountToken);

        Assert.NotEqual(default, book.Retrieved);
        Assert.True(book.Retrieved.Year > 2020);
    }

    [Fact]
    public void ParseItemsAcceptsTheSingleItemShapeToo()
    {
        // A one-id request to the comma-separated endpoint answers in the single-item
        // shape, so callers should not have to know which they are going to get.
        var books = UniversalisJson.ParseItems(Fixtures.Read(Fixtures.MountToken));

        Assert.Single(books);
        Assert.Equal(48_795, books[41807u].Floor);
    }

    [Fact]
    public void ParsesASurveyOfSeveralItems()
    {
        var survey = UniversalisJson.ParseSurvey(Fixtures.Read(Fixtures.Aggregated));

        Assert.Equal(3, survey.Count);
        Assert.Equal(18_500, survey[6524u].Floor);
        Assert.Equal(25_000, survey[6549u].Floor);
        Assert.Equal(48_795, survey[41807u].Floor);
        Assert.All(survey.Values, summary => Assert.True(summary.SaleVelocityPerDay > 0));
    }

    [Fact]
    public void ASurveyKnowsWhatTurnsOverAndWhatDoesNot()
    {
        var survey = UniversalisJson.ParseSurvey(Fixtures.Read(Fixtures.Aggregated));

        // Revenue potential is the whole point of a survey: it is the ceiling on what anyone can
        // earn from an item in a day, so it decides what is worth the cost of a full book.
        Assert.All(survey.Values, summary => Assert.True(summary.Trades));
        Assert.True(survey[41807u].DailyRevenue > survey[6524u].DailyRevenue);
    }

    [Fact]
    public void ASurveyOmitsWhatItWasNotAsked()
    {
        Assert.False(UniversalisJson.ParseSurvey(Fixtures.Read(Fixtures.Aggregated)).ContainsKey(1u));
    }

    [Fact]
    public void ASurveyOfNothingIsEmptyRatherThanAFailure()
    {
        Assert.Empty(UniversalisJson.ParseSurvey("{ \"results\": [], \"failedItems\": [] }"));
        Assert.Empty(UniversalisJson.ParseSurvey("{}"));
    }

    [Fact]
    public void AWorldScopedSurveyReportsTheWorldAndNotItsDataCentre()
    {
        // The bug this pins: a world request carries world, dc and region branches, and reading dc
        // unconditionally made a Shiva survey report Light's numbers. Recorded on the same day, the
        // world had 56,997 and about 10 sales where the data centre had 49,900 and 128.
        var world = UniversalisJson.ParseSurvey(Fixtures.Read(Fixtures.AggregatedWorld))[41807u];
        var dataCentre = UniversalisJson.ParseSurvey(Fixtures.Read(Fixtures.Aggregated))[41807u];

        Assert.Equal(56_997, world.Floor);
        Assert.NotEqual(dataCentre.Floor, world.Floor);
        Assert.True(
            world.SaleVelocityPerDay < dataCentre.SaleVelocityPerDay,
            "one world cannot sell faster than the data centre containing it");
    }

    [Fact]
    public void ADataCentreScopedSurveyStillWorksWithNoWorldBranch()
    {
        // The narrowest-branch rule has to degrade to the data centre when that is all there is,
        // or scoping to a data centre would come back empty.
        var survey = UniversalisJson.ParseSurvey(Fixtures.Read(Fixtures.Aggregated));

        Assert.All(survey.Values, summary => Assert.NotNull(summary.Floor));
    }

    [Fact]
    public void TheTwoEndpointsDisagreeAboutHowFastThingsSell()
    {
        // Recorded, not hypothetical. Whichever is right, mixing them would mean shortlisting an
        // item on one number and ranking it on another, so one has to be imposed on both.
        var fromListings = Fixtures.Book(Fixtures.MountToken).SaleVelocityPerDay;
        var fromSurvey = UniversalisJson.ParseSurvey(Fixtures.Read(Fixtures.Aggregated))[41807u].SaleVelocityPerDay;

        Assert.True(Math.Abs(fromListings - fromSurvey) > 1d, "the two sources are expected to differ");
    }

    [Fact]
    public void ParsesTheMountAsWell()
    {
        var book = Fixtures.Book(Fixtures.RroneekHorn);

        Assert.Equal(43598u, book.ItemId);
        Assert.Equal(6_199_934, book.Floor);
    }
}
