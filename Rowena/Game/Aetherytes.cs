using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>An aetheryte you can teleport to, and where it stands on its map.</summary>
internal readonly record struct Aetheryte(uint Id, string Name, uint TerritoryId, Vector2 Map);

/// <summary>
/// The aetherytes of each zone, placed, so the nearest one to a counter can be chosen.
/// </summary>
/// <remarks>
/// The Aetheryte sheet says which zone each belongs to but not where it stands; that is on
/// the map marker sheet, in map pixels, keyed back to the aetheryte. Shards are left out:
/// they are not teleport destinations from another zone, which is the only question asked
/// here. Big zones have several aetherytes and arriving at the wrong one is a long walk,
/// so the distance is measured on the map rather than guessed.
/// </remarks>
internal sealed class Aetherytes(IDataManager data, IPluginLog log)
{
    /// <summary>Map markers of this type are aetherytes.</summary>
    private const byte AetheryteMarker = 3;

    private Dictionary<uint, List<Aetheryte>>? byTerritory;

    /// <summary>The aetheryte in the spot's zone closest to it, or null when the zone has none.</summary>
    public Aetheryte? NearestTo(Spot spot)
    {
        Build();

        if (!byTerritory!.TryGetValue(spot.TerritoryId, out var candidates) || candidates.Count == 0)
            return null;

        return candidates.MinBy(aetheryte => Vector2.DistanceSquared(aetheryte.Map, spot.Map));
    }

    private void Build()
    {
        if (byTerritory is not null)
            return;

        byTerritory = new Dictionary<uint, List<Aetheryte>>();

        // Marker positions by aetheryte id. A map's markers are one row of the subrow sheet,
        // named by the map's MapMarkerRange.
        var markerByAetheryte = new Dictionary<uint, (uint MapId, int X, int Y)>();
        var mapByRange = data.GetExcelSheet<Map>()
            .Where(map => map.MapMarkerRange != 0)
            .GroupBy(map => (uint)map.MapMarkerRange)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var marker in data.GetSubrowExcelSheet<MapMarker>().Flatten())
        {
            if (marker.DataType != AetheryteMarker || marker.DataKey.RowId == 0)
                continue;

            if (mapByRange.TryGetValue(marker.RowId, out var map))
                markerByAetheryte.TryAdd(marker.DataKey.RowId, (map.RowId, marker.X, marker.Y));
        }

        var maps = data.GetExcelSheet<Map>();

        foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>())
        {
            if (!row.IsAetheryte || row.Territory.RowId == 0)
                continue;

            if (!markerByAetheryte.TryGetValue(row.RowId, out var marker)
                || maps.GetRowOrDefault(marker.MapId) is not { } map)
                continue;

            var name = row.PlaceName.ValueNullable?.Name.ExtractText();

            var placed = new Aetheryte(
                row.RowId,
                string.IsNullOrWhiteSpace(name) ? $"aetheryte {row.RowId}" : name,
                row.Territory.RowId,
                new Vector2(ToMapCoordinate(marker.X, map.SizeFactor), ToMapCoordinate(marker.Y, map.SizeFactor)));

            if (!byTerritory.TryGetValue(placed.TerritoryId, out var list))
                byTerritory[placed.TerritoryId] = list = [];

            list.Add(placed);
        }

        log.Information($"Placed aetherytes in {byTerritory.Count} zones.");
    }

    /// <summary>
    /// A map pixel to the coordinate the map prints, the same scale the NPC spots use.
    /// </summary>
    /// <remarks>
    /// Markers are in the 2048-pixel space the map image is drawn in; the printed coordinate
    /// is that space scaled to 41 units at the map's size factor, plus one. The same formula
    /// Dalamud applies to world positions, minus the world offset, since pixels carry none.
    /// </remarks>
    private static float ToMapCoordinate(int pixel, ushort sizeFactor)
    {
        var c = sizeFactor / 100f;
        return 41f / c * (pixel / 2048f) + 1f;
    }
}
