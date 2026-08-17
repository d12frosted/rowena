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

    public readonly record struct Entry(string Name, ushort Icon)
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

    private Entry Read(uint itemId)
    {
        if (data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } item)
            return new Entry($"item {itemId}", 0);

        var name = item.Name.ExtractText();
        return new Entry(string.IsNullOrWhiteSpace(name) ? $"item {itemId}" : name, (ushort)item.Icon);
    }
}
