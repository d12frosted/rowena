using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class RetainerSlotsTests
{
    [Fact]
    public void AStackThatClearsInsideTheHorizonIsWorthAllOfIt()
    {
        var picks = RetainerSlots.Fill([new SlotCandidate(1, 5, 10_000, 2)], slots: 1, horizonDays: 7);

        Assert.Equal(10_000, picks[0].Realised);
    }

    [Fact]
    public void AStackThatTakesMonthsIsWorthTheSliceThatSells()
    {
        // The whole point. A slot is a rate rather than a lump: four hundred greens are worth a
        // hundred and twenty thousand and take a hundred and sixteen days, so a week of that
        // slot is worth a sixteenth of the number on the stack.
        var picks = RetainerSlots.Fill([new SlotCandidate(1, 415, 120_765, 116)], slots: 1, horizonDays: 7);

        // Truncated rather than rounded: a forecast that rounds up is a forecast that flatters.
        Assert.Equal(7_287, picks[0].Realised);
        Assert.Equal(120_765, picks[0].Worth);
    }

    [Fact]
    public void TheSlowFortuneLosesTheSlotToTheQuickerSmallerOne()
    {
        // Ranked on what the stack is worth, the greens win and hold a slot for four months.
        // Ranked on what the slot earns, they do not.
        var picks = RetainerSlots.Fill(
            [new SlotCandidate(1, 415, 120_765, 116), new SlotCandidate(2, 147, 46_452, 0.3)],
            slots: 1,
            horizonDays: 7);

        Assert.Single(picks);
        Assert.Equal(2u, picks[0].ItemId);
    }

    [Fact]
    public void SomethingThatNeverSellsNeverEarnsASlot()
    {
        var picks = RetainerSlots.Fill(
            [new SlotCandidate(1, 10, 999_999, null), new SlotCandidate(2, 1, 10, 1)],
            slots: 4,
            horizonDays: 7);

        Assert.Single(picks);
        Assert.Equal(2u, picks[0].ItemId);
    }

    [Fact]
    public void OnlyAsManySlotsAsThereAre()
    {
        var picks = RetainerSlots.Fill(
            [new SlotCandidate(1, 1, 500, 1), new SlotCandidate(2, 1, 400, 1), new SlotCandidate(3, 1, 300, 1)],
            slots: 2,
            horizonDays: 7);

        Assert.Equal(2, picks.Count);
        Assert.Equal([1u, 2u], picks.Select(pick => pick.ItemId));
    }

    [Fact]
    public void NoSlotsIsNoPlan() =>
        Assert.Empty(RetainerSlots.Fill([new SlotCandidate(1, 1, 500, 1)], slots: 0, horizonDays: 7));

    [Fact]
    public void AWorthlessStackIsNotWorthASlotEither() =>
        Assert.Empty(RetainerSlots.Fill([new SlotCandidate(1, 10, 0, 1)], slots: 5, horizonDays: 7));

    [Fact]
    public void ThePlanIsWorthWhatItsSlotsEarnRatherThanWhatTheyHold()
    {
        var picks = RetainerSlots.Fill(
            [new SlotCandidate(1, 415, 120_765, 116), new SlotCandidate(2, 147, 46_452, 0.3)],
            slots: 2,
            horizonDays: 7);

        Assert.Equal(53_739, RetainerSlots.Earns(picks));
    }
}
