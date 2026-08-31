using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>Names and icons for item ids.</summary>
/// <remarks>
/// The core deals in ids and the names it was handed when a catalogue or a recipe was read. A
/// window wants the icon too, and wants it for materials that no conversion ever named. One
/// cached lookup rather than threading display details through the arithmetic.
/// </remarks>
internal sealed class Items(IDataManager data)
{
    private readonly Dictionary<uint, Entry> cache = [];

    /// <param name="StackSize">The most of it that fits in one inventory slot, and so in one market slot.</param>
    public readonly record struct Entry(string Name, ushort Icon, int StackSize)
    {
        public bool HasIcon => Icon != 0;
    }

    public Entry Get(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var known))
            return known;

        var entry = Read(itemId);
        cache[itemId] = entry;
        return entry;
    }

    public string Name(uint itemId) => Get(itemId).Name;

    /// <summary>How many of it one slot holds, floored at one so nothing is unlistable by arithmetic.</summary>
    public int StackSize(uint itemId) => Math.Max(1, Get(itemId).StackSize);

    private Entry Read(uint itemId)
    {
        if (data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } item)
            return new Entry($"item {itemId}", 0, 1);

        var name = item.Name.ExtractText();

        return new Entry(
            string.IsNullOrWhiteSpace(name) ? $"item {itemId}" : name,
            (ushort)item.Icon,
            (int)item.StackSize);
    }
}
