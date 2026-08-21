using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace Rowena.Game;

/// <summary>
/// Getting to a spot: flagging it on the map, or going there.
/// </summary>
/// <remarks>
/// The map flag is the game's own and always works. Going is two hand-offs: Lifestream for
/// the teleport to the nearest aetheryte in the spot's zone, and vnavmesh for the walk from
/// there, each the way crafting is handed to Artisan: one click, that plugin's doing. The
/// walk is armed rather than chained, because arriving takes a loading screen and the mesh
/// a moment after that; it fires on the first tick both are true, and gives up quietly if
/// the zone never arrives, since a teleport can be cancelled by walking off.
/// </remarks>
internal sealed class Places : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private readonly IClientState client;
    private readonly IGameGui gui;
    private readonly IFramework framework;
    private readonly Aetherytes aetherytes;
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;

    private Spot? armed;
    private DateTime armedUntil;

    public Places(
        IDalamudPluginInterface plugins,
        IClientState client,
        IGameGui gui,
        IFramework framework,
        Aetherytes aetherytes,
        IPluginLog log)
    {
        this.client = client;
        this.gui = gui;
        this.framework = framework;
        this.aetherytes = aetherytes;
        this.log = log;

        navReady = plugins.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveTo = plugins.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        teleport = plugins.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    /// <summary>The zone you are standing in, or zero when not in one.</summary>
    public uint Here => client.TerritoryType;

    public bool IsHere(Spot spot) => spot.TerritoryId != 0 && spot.TerritoryId == Here;

    /// <summary>Whether vnavmesh is loaded and has a mesh for this zone.</summary>
    public bool CanWalk => Ask(navReady);

    /// <summary>Whether Lifestream is there to teleport with.</summary>
    public bool CanTeleport => teleport.HasFunction;

    /// <summary>The aetheryte a trip to the spot would arrive at, or null when its zone has none.</summary>
    public Aetheryte? ArrivalFor(Spot spot) => aetherytes.NearestTo(spot);

    /// <summary>Puts the flag on the map and opens it there.</summary>
    public void Flag(Spot spot) =>
        gui.OpenMapWithMapLink(new MapLinkPayload(spot.TerritoryId, spot.MapId, spot.Map.X, spot.Map.Y));

    /// <summary>
    /// Goes to the spot: walks if already in its zone, otherwise teleports and arms the walk.
    /// </summary>
    public bool Go(Spot spot)
    {
        if (IsHere(spot))
            return Walk(spot);

        if (ArrivalFor(spot) is not { } arrival)
            return false;

        try
        {
            if (!teleport.InvokeFunc(arrival.Id, 0))
                return false;
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception error)
        {
            log.Warning(error, "Lifestream refused the teleport.");
            return false;
        }

        armed = spot;
        armedUntil = DateTime.UtcNow + Patience;
        return true;
    }

    private void Tick(IFramework _)
    {
        if (armed is not { } spot)
            return;

        if (DateTime.UtcNow > armedUntil)
        {
            armed = null;
            return;
        }

        if (!IsHere(spot) || !CanWalk)
            return;

        armed = null;
        Walk(spot);
    }

    private bool Walk(Spot spot)
    {
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

    private static bool Ask(ICallGateSubscriber<bool> gate)
    {
        try
        {
            return gate.InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
    }
}
