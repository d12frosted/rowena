using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace Rowena.Game;

/// <summary>
/// Getting to a spot: flagging it on the map, or handing it to vnavmesh.
/// </summary>
/// <remarks>
/// The map flag is the game's own and always works; it is also what makes the nearest
/// aetheryte obvious, which is the part of the journey this does not do. Walking is a
/// hand-off to vnavmesh, the way crafting is a hand-off to Artisan: it happens on a click,
/// it is that plugin's pathing, and it is only offered from inside the spot's zone, since
/// a path to another zone does not exist and a teleport is a decision with a price on it.
/// </remarks>
internal sealed class Places(IDalamudPluginInterface plugins, IClientState client, IGameGui gui, IPluginLog log)
{
    private readonly ICallGateSubscriber<bool> navReady = plugins.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");

    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo =
        plugins.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");

    /// <summary>The zone you are standing in, or zero when not in one.</summary>
    public uint Here => client.TerritoryType;

    public bool IsHere(Spot spot) => spot.TerritoryId != 0 && spot.TerritoryId == Here;

    /// <summary>Whether vnavmesh is loaded and has a mesh for this zone.</summary>
    public bool CanWalk
    {
        get
        {
            try
            {
                return navReady.InvokeFunc();
            }
            catch (IpcNotReadyError)
            {
                return false;
            }
        }
    }

    /// <summary>Puts the flag on the map and opens it there.</summary>
    public void Flag(Spot spot) =>
        gui.OpenMapWithMapLink(new MapLinkPayload(spot.TerritoryId, spot.MapId, spot.Map.X, spot.Map.Y));

    /// <summary>Asks vnavmesh to walk there. False when it would not or could not.</summary>
    public bool Walk(Spot spot)
    {
        if (!IsHere(spot))
            return false;

        try
        {
            return moveTo.InvokeFunc(spot.World, false);
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception error)
        {
            log.Warning(error, "vnavmesh refused the walk.");
            return false;
        }
    }
}
