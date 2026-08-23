using Rowena.Core.Market;

namespace Rowena.Market;

/// <summary>
/// The undercut price for any of my listings, with my own exceptions applied.
/// </summary>
/// <remarks>
/// One place, because two views ask the same question: the Selling tab when it draws a row,
/// and the retainer's price dialog when it opens on that row's item. They had better agree.
/// </remarks>
internal sealed class Undercutting(Boards boards, Configuration config, Action save)
{
    /// <summary>What I would have to ask to be cheapest, or null when I already am.</summary>
    public UndercutPlan? Plan(uint itemId, long mine) =>
        Undercut.Of(mine, boards.Selling(itemId), config.UndercutBy);

    public bool Ignored(uint itemId) => config.UndercutIgnored.Contains(itemId);

    public void Ignore(uint itemId, bool ignore)
    {
        if (ignore == Ignored(itemId))
            return;

        if (ignore)
            config.UndercutIgnored.Add(itemId);
        else
            config.UndercutIgnored.Remove(itemId);

        save();
    }

    /// <summary>
    /// Forgets the ignores for anything no longer listed.
    /// </summary>
    /// <remarks>
    /// An ignore was a decision about a stack that was out. Once it has gone, sold or taken
    /// back, the next stack of the same thing is a new question, and starts watched.
    /// </remarks>
    public void Prune(IReadOnlyCollection<uint> listed)
    {
        var removed = config.UndercutIgnored.RemoveAll(itemId => !listed.Contains(itemId));

        if (removed > 0)
            save();
    }

    /// <summary>The plan, unless I have said to leave this item alone.</summary>
    public UndercutPlan? Wanted(uint itemId, long mine) =>
        Ignored(itemId) ? null : Plan(itemId, mine);
}
