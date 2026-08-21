namespace Rowena.UI;

/// <summary>
/// The cities a retainer can stand in, by the id the board reports.
/// </summary>
/// <remarks>
/// Written out rather than read from a sheet: the ids come off a network packet as a small
/// fixed set, and the sheet that names them keys them differently. Eight entries that change
/// once an expansion are not worth a lookup that could silently return the wrong name.
/// </remarks>
internal static class Cities
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [1] = "Limsa Lominsa",
        [2] = "Gridania",
        [3] = "Ul'dah",
        [4] = "Ishgard",
        [7] = "Kugane",
        [10] = "The Crystarium",
        [12] = "Old Sharlayan",
        [14] = "Tuliyollal",
    };

    public static string Name(uint cityId) => Names.TryGetValue(cityId, out var name) ? name : $"city {cityId}";
}
