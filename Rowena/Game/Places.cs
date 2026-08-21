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
/// The map flag is the game's own and always works. Going is a journey with state, because
/// none of its steps is instant: Lifestream teleports and a loading screen follows; the
/// zone arrives and vnavmesh needs a moment to build its mesh; the walk starts and takes
/// as long as it takes. Each is a hand-off, the way crafting is handed to Artisan, and the
/// journey is the bookkeeping between them: it waits for whatever is not ready yet, says
/// so, and gives up quietly when a step never completes, since a teleport can be cancelled
/// by walking off and a mesh can fail to build.
/// </remarks>
internal sealed class Places : IDisposable
{
    private static readonly TimeSpan ZonePatience = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MeshPatience = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How close counts as arrived, in yalms, measured to where the NPC stands.
    /// </summary>
    /// <remarks>
    /// Talking to an NPC works from a few yalms, and most vendors stand behind a counter the
    /// mesh does not cover, so walking to their exact position either finds no path or jams
    /// against the stall. The walk aims at the nearest point the mesh does cover and stops
    /// the moment it is within reach, wherever the path was heading.
    /// </remarks>
    private const float WithinReach = 3.5f;

    /// <summary>How far around the NPC to look for the mesh, when snapping the destination.</summary>
    private const float SnapExtent = 6f;

    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly IGameGui gui;
    private readonly IFramework framework;
    private readonly Aetherytes aetherytes;
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<bool> pathRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3> nearestOnMesh;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;

    public Places(
        IDalamudPluginInterface plugins,
        IClientState client,
        IObjectTable objects,
        IGameGui gui,
        IFramework framework,
        Aetherytes aetherytes,
        IPluginLog log)
    {
        this.client = client;
        this.objects = objects;
        this.gui = gui;
        this.framework = framework;
        this.aetherytes = aetherytes;
        this.log = log;

        navReady = plugins.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveTo = plugins.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathRunning = plugins.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = plugins.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        nearestOnMesh = plugins.GetIpcSubscriber<Vector3, float, float, Vector3>("vnavmesh.Query.Mesh.NearestPoint");
        teleport = plugins.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    public enum Phase
    {
        /// <summary>Teleported; waiting for the zone to load.</summary>
        Travelling,

        /// <summary>In the zone; waiting for vnavmesh to have a mesh.</summary>
        WaitingForMesh,

        /// <summary>Handed to vnavmesh; it is walking.</summary>
        Walking,
    }

    /// <param name="Deadline">When the current phase is given up on.</param>
    public sealed record Journey(Spot Target, Phase Phase, DateTime Deadline);

    /// <summary>The journey under way, or null.</summary>
    public Journey? Current { get; private set; }

    /// <summary>What the journey is doing, for showing.</summary>
    public string? Status => Current switch
    {
        null => null,
        { Phase: Phase.Travelling } journey => $"Going to {journey.Target.Npc}: waiting for {journey.Target.Zone} to load",
        { Phase: Phase.WaitingForMesh } journey => $"Going to {journey.Target.Npc}: waiting for vnavmesh's mesh",
        { } journey => $"Walking to {journey.Target.Npc}",
    };

    /// <summary>The zone you are standing in, or zero when not in one.</summary>
    public uint Here => client.TerritoryType;

    public bool IsHere(Spot spot) => spot.TerritoryId != 0 && spot.TerritoryId == Here;

    /// <summary>Whether vnavmesh is loaded at all. Whether it is ready is a moment, not a fact.</summary>
    public bool HasNav => navReady.HasFunction;

    /// <summary>Whether vnavmesh has a mesh for this zone right now.</summary>
    public bool NavReady => Ask(navReady);

    /// <summary>Whether Lifestream is there to teleport with.</summary>
    public bool CanTeleport => teleport.HasFunction;

    /// <summary>The aetheryte a trip to the spot would arrive at, or null when its zone has none.</summary>
    public Aetheryte? ArrivalFor(Spot spot) => aetherytes.NearestTo(spot);

    /// <summary>Puts the flag on the map and opens it there.</summary>
    public void Flag(Spot spot) =>
        gui.OpenMapWithMapLink(new MapLinkPayload(spot.TerritoryId, spot.MapId, spot.Map.X, spot.Map.Y));

    /// <summary>
    /// Starts going to the spot: waits for the mesh if already in its zone, teleports first
    /// if not. A journey already under way is replaced.
    /// </summary>
    public bool Go(Spot spot)
    {
        Cancel();

        if (IsHere(spot))
        {
            Current = new Journey(spot, Phase.WaitingForMesh, DateTime.UtcNow + MeshPatience);
            return true;
        }

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

        Current = new Journey(spot, Phase.Travelling, DateTime.UtcNow + ZonePatience);
        return true;
    }

    /// <summary>Forgets the journey, and stops vnavmesh if it was walking for it.</summary>
    public void Cancel()
    {
        if (Current is { Phase: Phase.Walking })
            Stop();

        Current = null;
    }

    private void Stop()
    {
        try
        {
            pathStop.InvokeAction();
        }
        catch (IpcNotReadyError)
        {
            // Nothing to stop.
        }
    }

    /// <summary>Whether you stand close enough to the spot to talk to whoever is there.</summary>
    private bool WithinReachOf(Spot spot)
    {
        if (objects.LocalPlayer?.Position is not { } at)
            return false;

        // Flat distance: a counter is level with the floor it is on, and the NPC's recorded
        // height can sit a little off the walkable surface.
        var dx = at.X - spot.World.X;
        var dz = at.Z - spot.World.Z;

        return dx * dx + dz * dz <= WithinReach * WithinReach;
    }

    private void Tick(IFramework _)
    {
        if (Current is not { } journey)
            return;

        if (DateTime.UtcNow > journey.Deadline)
        {
            log.Information($"Gave up going to {journey.Target.Npc}: {journey.Phase} took too long.");
            Current = null;
            return;
        }

        switch (journey.Phase)
        {
            case Phase.Travelling when IsHere(journey.Target):
                Current = journey with { Phase = Phase.WaitingForMesh, Deadline = DateTime.UtcNow + MeshPatience };
                break;

            case Phase.WaitingForMesh when !IsHere(journey.Target):
                // Left the zone again before the mesh came; the walk would go nowhere.
                Current = null;
                break;

            case Phase.WaitingForMesh when WithinReachOf(journey.Target):
                // Already standing at the counter; nothing to walk.
                Current = null;
                break;

            case Phase.WaitingForMesh when NavReady:
                Current = Walk(journey.Target)
                    ? journey with { Phase = Phase.Walking, Deadline = DateTime.MaxValue }
                    : null;
                break;

            case Phase.Walking when WithinReachOf(journey.Target):
                // Close enough to talk. Stop here rather than push on to the point the path
                // was aiming at, which may be a stall away from where the NPC stands.
                Stop();
                Current = null;
                break;

            case Phase.Walking when !Ask(pathRunning):
                // Stopped short, or vnavmesh gave up. Either way it is over.
                Current = null;
                break;
        }
    }

    private bool Walk(Spot spot)
    {
        try
        {
            return moveTo.InvokeFunc(Reachable(spot.World), false);
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

    /// <summary>
    /// The nearest point the mesh covers, for a position that may be behind a counter.
    /// </summary>
    /// <remarks>
    /// Asked of vnavmesh rather than guessed, since it knows its mesh. When it cannot say,
    /// the original position is used and the walk takes its chances, which is no worse than
    /// before.
    /// </remarks>
    private Vector3 Reachable(Vector3 position)
    {
        try
        {
            var snapped = nearestOnMesh.InvokeFunc(position, SnapExtent, SnapExtent);
            return snapped == default ? position : snapped;
        }
        catch (Exception)
        {
            return position;
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
