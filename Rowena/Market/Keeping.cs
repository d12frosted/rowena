namespace Rowena.Market;

/// <summary>
/// The things I have said are mine rather than stock.
/// </summary>
/// <remarks>
/// Every other reason to hold something can be worked out: the craft table knows what it wants,
/// and the game knows what I have not learned. This one cannot be. Chocobo greens and copper ore
/// are the same shape of row, and only I know that one of them is a material and the other is
/// what I feed a chocobo with.
///
/// Said once and kept, unlike the undercut ignores next door, which expire with the listing they
/// were about. This is a fact about the item rather than about a stack of it: greens do not stop
/// being greens when the last of them goes.
/// </remarks>
internal sealed class Keeping(Configuration config, Action save)
{
    /// <summary>Whether I have said this one is mine.</summary>
    public bool Mine(uint itemId) => config.Kept.Contains(itemId);

    /// <summary>Everything I have said that about, for the settings list that undoes it.</summary>
    public IReadOnlyList<uint> All => config.Kept;

    public void Keep(uint itemId, bool keep)
    {
        if (keep == Mine(itemId))
            return;

        if (keep)
            config.Kept.Add(itemId);
        else
            config.Kept.Remove(itemId);

        save();
    }
}
