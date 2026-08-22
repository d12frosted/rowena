using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class EorzeaClockTests
{
    private static DateTimeOffset At(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

    [Fact]
    public void ADayThereIsSeventyMinutesHere()
    {
        // The whole reason this class exists: a four hour window sounds generous and is under
        // twelve minutes of anybody's evening.
        Assert.Equal(4200, EorzeaClock.ToReal(1440).TotalSeconds);
        Assert.Equal(700, EorzeaClock.ToReal(240).TotalSeconds);
        Assert.Equal(350, EorzeaClock.ToReal(120).TotalSeconds);
    }

    [Fact]
    public void TheClockWrapsAtMidnight()
    {
        Assert.Equal(0, EorzeaClock.MinuteOfDay(At(0)));
        Assert.Equal(0, EorzeaClock.MinuteOfDay(At(4200)));
        Assert.Equal(720, EorzeaClock.MinuteOfDay(At(2100)));
    }

    [Fact]
    public void TheClockAdvancesFasterThanOurs()
    {
        // Seventy real minutes is a whole day there, so one real minute is a little over
        // twenty of theirs.
        Assert.Equal(20, EorzeaClock.MinuteOfDay(At(60)));
    }

    [Fact]
    public void AWindowIsOpenFromItsStartUntilItsLengthIsUp()
    {
        var window = new EorzeaWindow(400, 240);

        Assert.False(window.IsOpenAt(399));
        Assert.True(window.IsOpenAt(400));
        Assert.True(window.IsOpenAt(639));
        Assert.False(window.IsOpenAt(640));
    }

    [Fact]
    public void AWindowCanRunPastMidnight()
    {
        // Twenty-two hundred for four hours runs to two in the morning, stored as a start and
        // a length, so the wrap has to be handled rather than assumed away.
        var window = new EorzeaWindow(1320, 240);

        Assert.True(window.IsOpenAt(1400));
        Assert.True(window.IsOpenAt(60));
        Assert.False(window.IsOpenAt(300));
    }

    [Fact]
    public void AnOpenWindowIsNoWaitAtAll()
    {
        Assert.Equal(0, new EorzeaWindow(400, 240).MinutesUntilOpen(500));
    }

    [Fact]
    public void AClosedWindowSaysHowLongUntilItComesRound()
    {
        Assert.Equal(100, new EorzeaWindow(400, 240).MinutesUntilOpen(300));

        // Just after it shuts is the longest wait there is: the whole day round again.
        Assert.Equal(1200, new EorzeaWindow(400, 240).MinutesUntilOpen(640));
    }

    [Fact]
    public void AnOpenWindowSaysHowLongIsLeftOfIt()
    {
        Assert.Equal(140, new EorzeaWindow(400, 240).MinutesLeftAt(500));
        Assert.Equal(0, new EorzeaWindow(400, 240).MinutesLeftAt(300));
    }

    [Fact]
    public void TheSoonestOfSeveralWindowsIsTheOneThatMatters()
    {
        EorzeaWindow[] windows = [new(1200, 120), new(400, 120), new(800, 120)];

        Assert.Equal(100, EorzeaWindow.NextOpen(windows, 300));
        Assert.Equal(0, EorzeaWindow.NextOpen(windows, 450));
    }

    [Fact]
    public void NoWindowsMeansItNeverOpens() => Assert.Null(EorzeaWindow.NextOpen([], 300));
}
