using Dalamud.Bindings.ImGui;
using Rowena.Game;

namespace Rowena.UI;

/// <summary>
/// The "where" of a trade, as somewhere you can go.
/// </summary>
/// <remarks>
/// Shows the spot in the zone you are standing in when there is one, otherwise the first,
/// and says so: a counter in your own zone is an errand, one elsewhere is a trip. The rest
/// are on hover. Right-click flags any of them on the map, and walks to the one you are in
/// the zone of when vnavmesh is there to do the walking.
/// </remarks>
internal sealed class VenueCell(Places places)
{
    public void Draw(string id, string fallback, IReadOnlyList<Spot> spots)
    {
        if (spots.Count == 0)
        {
            ImGui.TextColored(Palette.Dim, fallback);
            return;
        }

        var here = spots.FirstOrDefault(places.IsHere);
        var shown = here.TerritoryId != 0 ? here : spots[0];
        var nearby = here.TerritoryId != 0;

        ImGui.PushID(id);

        ImGui.TextColored(
            nearby ? Palette.Plain : Palette.Dim,
            $"{shown.Npc}, {shown.Zone} ({shown.Map.X:F1}, {shown.Map.Y:F1})");

        if (ImGui.IsItemHovered())
        {
            var lines = spots.Select(spot => $"{spot.Npc}, {spot.Zone} ({spot.Map.X:F1}, {spot.Map.Y:F1})");
            ImGui.SetTooltip(
                (spots.Count > 1 ? "Offered at:\n" + string.Join("\n", lines) + "\n\n" : "")
                + (nearby ? "In this zone. " : "")
                + "Right-click to flag on the map"
                + (nearby ? " or walk there with vnavmesh." : "."));
        }

        if (ImGui.BeginPopupContextItem("where"))
        {
            foreach (var spot in spots)
            {
                if (ImGui.MenuItem($"Flag on the map: {spot.Zone}"))
                    places.Flag(spot);
            }

            if (nearby)
            {
                ImGui.Separator();

                var canWalk = places.CanWalk;
                if (!canWalk)
                    ImGui.BeginDisabled();

                if (ImGui.MenuItem("Walk there with vnavmesh"))
                    places.Walk(here);

                if (!canWalk)
                {
                    ImGui.EndDisabled();
                    ImGui.TextColored(Palette.Dim, "   vnavmesh not found, or no mesh for this zone yet");
                }
            }

            ImGui.EndPopup();
        }

        ImGui.PopID();
    }
}
