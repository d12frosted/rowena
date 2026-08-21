using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class FetchQueueTests
{
    private const string Light = "Light";
    private const string Shiva = "Shiva";

    private static FetchQueue Queued(params (FetchPriority Priority, uint Id)[] entries)
    {
        var queue = new FetchQueue();

        foreach (var (priority, id) in entries)
            queue.Enqueue(Light, FetchKind.Book, [id], priority);

        return queue;
    }

    [Fact]
    public void TheMostUrgentWorkComesOutFirst()
    {
        var queue = Queued(
            (FetchPriority.Sweep, 1),
            (FetchPriority.Interactive, 2),
            (FetchPriority.Background, 3));

        Assert.Equal([2u], queue.Next(10)!.Value.Ids);
        Assert.Equal([3u], queue.Next(10)!.Value.Ids);
        Assert.Equal([1u], queue.Next(10)!.Value.Ids);
        Assert.Null(queue.Next(10));
    }

    [Fact]
    public void AskingTwiceForTheSameThingAsksOnce()
    {
        var queue = Queued((FetchPriority.Sweep, 1), (FetchPriority.Sweep, 1));

        Assert.Equal(1, queue.Pending);
        Assert.Equal([1u], queue.Next(10)!.Value.Ids);
        Assert.Null(queue.Next(10));
    }

    [Fact]
    public void AskingAgainMoreUrgentlyPromotesIt()
    {
        // Queued behind a sweep, then pressed by hand: it should not wait for the sweep.
        var queue = Queued((FetchPriority.Sweep, 1), (FetchPriority.Sweep, 2));
        queue.Enqueue(Light, FetchKind.Book, [2], FetchPriority.Interactive);

        var first = queue.Next(10)!.Value;

        Assert.Equal(FetchPriority.Interactive, first.Priority);
        Assert.Equal([2u], first.Ids);
    }

    [Fact]
    public void AskingAgainLessUrgentlyChangesNothing()
    {
        var queue = Queued((FetchPriority.Interactive, 1));
        queue.Enqueue(Light, FetchKind.Book, [1], FetchPriority.Sweep);

        Assert.Equal(FetchPriority.Interactive, queue.Next(10)!.Value.Priority);
    }

    [Fact]
    public void ABatchIsAllOneBoardAndOneKind()
    {
        var queue = new FetchQueue();
        queue.Enqueue(Light, FetchKind.Book, [1, 2], FetchPriority.Background);
        queue.Enqueue(Shiva, FetchKind.Book, [3], FetchPriority.Background);
        queue.Enqueue(Light, FetchKind.Summary, [4], FetchPriority.Background);

        var first = queue.Next(10)!.Value;

        Assert.Equal(Light, first.Scope);
        Assert.Equal(FetchKind.Book, first.Kind);
        Assert.Equal([1u, 2u], first.Ids);
    }

    [Fact]
    public void ABatchIsNoLargerThanAskedFor()
    {
        var queue = new FetchQueue();
        queue.Enqueue(Light, FetchKind.Book, [1, 2, 3, 4, 5], FetchPriority.Sweep);

        Assert.Equal([1u, 2u], queue.Next(2)!.Value.Ids);
        Assert.Equal([3u, 4u], queue.Next(2)!.Value.Ids);
        Assert.Equal([5u], queue.Next(2)!.Value.Ids);
    }

    [Fact]
    public void TheSameItemOnTwoBoardsIsTwoFetches()
    {
        // Buying and selling are different questions about the same item, priced apart.
        var queue = new FetchQueue();
        queue.Enqueue(Light, FetchKind.Book, [1], FetchPriority.Background);
        queue.Enqueue(Shiva, FetchKind.Book, [1], FetchPriority.Background);

        Assert.Equal(2, queue.Pending);
    }

    [Fact]
    public void NothingIsEverDropped()
    {
        var queue = new FetchQueue();
        queue.Enqueue(Light, FetchKind.Book, [.. Enumerable.Range(1, 500).Select(id => (uint)id)], FetchPriority.Sweep);
        queue.Enqueue(Light, FetchKind.Summary, [.. Enumerable.Range(1, 300).Select(id => (uint)id)], FetchPriority.Background);

        var seen = new List<uint>();
        var batches = 0;

        while (queue.Next(8) is { } batch)
        {
            seen.AddRange(batch.Ids);
            batches++;
        }

        Assert.Equal(800, seen.Count);
        Assert.Equal(0, queue.Pending);
        Assert.True(batches >= 100);
    }

    [Fact]
    public void UrgentWorkCutsInBetweenBatchesOfASweep()
    {
        // The point of the whole class: a sweep in flight must not make a press wait for it.
        var queue = new FetchQueue();
        queue.Enqueue(Light, FetchKind.Book, [.. Enumerable.Range(1, 100).Select(id => (uint)id)], FetchPriority.Sweep);

        queue.Next(8);
        queue.Enqueue(Light, FetchKind.Book, [999], FetchPriority.Interactive);

        Assert.Equal([999u], queue.Next(8)!.Value.Ids);
    }

    [Fact]
    public void AnEmptyQueueHasNothingToSay()
    {
        var queue = new FetchQueue();

        Assert.Null(queue.Next(10));
        Assert.Equal(0, queue.Pending);
        Assert.Equal(0, queue.PendingAt(FetchPriority.Sweep));
    }

    [Fact]
    public void PendingIsCountedByUrgency()
    {
        var queue = Queued(
            (FetchPriority.Sweep, 1),
            (FetchPriority.Sweep, 2),
            (FetchPriority.Interactive, 3));

        Assert.Equal(3, queue.Pending);
        Assert.Equal(2, queue.PendingAt(FetchPriority.Sweep));
        Assert.Equal(1, queue.PendingAt(FetchPriority.Interactive));
        Assert.Equal(0, queue.PendingAt(FetchPriority.Background));
    }
}

public class FetchQueueSizingTests
{
    [Fact]
    public void TheBatchIsSizedByWhatKindOfQuestionItTurnedOutToBe()
    {
        // A summary request carries a hundred ids comfortably and a book request eight, and
        // which of the two comes next is not known until the batch is chosen.
        var queue = new FetchQueue();
        queue.Enqueue("Light", FetchKind.Summary, [.. Enumerable.Range(1, 50).Select(id => (uint)id)], FetchPriority.Sweep);
        queue.Enqueue("Light", FetchKind.Book, [.. Enumerable.Range(100, 50).Select(id => (uint)id)], FetchPriority.Sweep);

        var sizes = new Dictionary<FetchKind, int> { [FetchKind.Book] = 8, [FetchKind.Summary] = 100 };

        var summaries = queue.Next(kind => sizes[kind])!.Value;
        Assert.Equal(FetchKind.Summary, summaries.Kind);
        Assert.Equal(50, summaries.Ids.Length);

        var books = queue.Next(kind => sizes[kind])!.Value;
        Assert.Equal(FetchKind.Book, books.Kind);
        Assert.Equal(8, books.Ids.Length);
    }
}
