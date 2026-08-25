using Dalamud.Bindings.ImGui;
using Rowena.Game;

namespace Rowena.UI;

/// <summary>
/// The "where" of a trade, as somewhere you can go.
/// </summary>
/// <remarks>
/// Shows the spot in the zone you are standing in when there is one, otherwise the first,
/// and says so: a counter in your own zone is an errand, one elsewhere is a trip. The rest
/// are on hover. Right-click goes there: the walk when you are in the zone and vnavmesh is
/// there to do it, a Lifestream teleport to the nearest aetheryte first when you are not.
/// Flagging on the map is always offered, since it always works.
/// </remarks>
internal sealed class VenueCell(Places places)
{
    public void Draw(string id, string fallback, IReadOnlyList<Spot> spots)
    {
        if (spots.Count == 0)
        {
            ImGui.TextColored(Style.Muted, fallback);
            return;
        }

        var here = spots.FirstOrDefault(places.IsHere);
        var nearby = here.TerritoryId != 0;

        // A city can exist as several territories, its instanced copies, all with one name.
        // Matching where you stand uses all of them; showing and flagging uses one per name.
        var distinct = spots.DistinctBy(spot => (spot.Npc, spot.Zone)).ToArray();
        var shown = nearby ? here : distinct[0];

        ImGui.PushID(id);

        ImGui.TextColored(
            nearby ? Style.Plain : Style.Muted,
            $"{shown.Npc}, {shown.Zone} ({shown.Map.X:F1}, {shown.Map.Y:F1})");

        var arrival = nearby ? null : places.ArrivalFor(shown);

        if (ImGui.IsItemHovered())
        {
            var lines = distinct.Select(spot => $"{spot.Npc}, {spot.Zone} ({spot.Map.X:F1}, {spot.Map.Y:F1})");
            ImGui.SetTooltip(
                (distinct.Length > 1 ? "Offered at:\n" + string.Join("\n", lines) + "\n\n" : "")
                + (nearby
                    ? "In this zone. Right-click to walk there with vnavmesh, or to flag it on the map."
                    : "Right-click to go there: Lifestream to "
                      + (arrival is { } a ? a.Name : "the zone")
                      + ", then vnavmesh to the counter. Or just flag it on the map."));
        }

        if (ImGui.BeginPopupContextItem("where"))
        {
            var going = places.Current is { } journey && journey.Target.TerritoryId == shown.TerritoryId
                && journey.Target.Npc == shown.Npc;

            // Going first, because it is the thing you came to the menu for; flagging is the
            // fallback that always works. "Not ready" is never a reason to refuse: the journey
            // waits for the mesh, and says so in the strip while it does.
            if (going)
            {
                ImGui.TextColored(Style.Muted, places.Status ?? "");

                if (ImGui.MenuItem("Stop going there"))
                    places.Cancel();
            }
            else if (nearby)
            {
                if (!places.HasNav)
                    ImGui.BeginDisabled();

                if (ImGui.MenuItem(places.NavReady ? "Walk there with vnavmesh" : "Walk there with vnavmesh, once its mesh is ready"))
                    places.Go(here);

                if (!places.HasNav)
                {
                    ImGui.EndDisabled();
                    ImGui.TextColored(Style.Muted, "   vnavmesh not found");
                }
            }
            else
            {
                var canGo = places.CanTeleport && arrival is not null;
                if (!canGo)
                    ImGui.BeginDisabled();

                if (ImGui.MenuItem(arrival is { } at ? $"Go there: teleport to {at.Name}, then walk" : "Go there"))
                    places.Go(shown);

                if (!canGo)
                {
                    ImGui.EndDisabled();
                    ImGui.TextColored(
                        Style.Muted,
                        arrival is null ? "   no aetheryte in that zone" : "   Lifestream not found");
                }
            }

            ImGui.Separator();

            foreach (var spot in distinct)
            {
                if (ImGui.MenuItem($"Flag on the map: {spot.Zone}"))
                    places.Flag(spot);
            }

            ImGui.EndPopup();
        }

        ImGui.PopID();
    }
}
