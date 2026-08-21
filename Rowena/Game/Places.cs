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

    private readonly IClientState client;
    private readonly IGameGui gui;
    private readonly IFramework framework;
    private readonly Aetherytes aetherytes;
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<bool> pathRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;

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
        pathRunning = plugins.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = plugins.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
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

        Current = null;
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

            case Phase.WaitingForMesh when NavReady:
                Current = Walk(journey.Target)
                    ? journey with { Phase = Phase.Walking, Deadline = DateTime.MaxValue }
                    : null;
                break;

            case Phase.Walking when !Ask(pathRunning):
                // Arrived, or vnavmesh stopped for its own reasons. Either way it is over.
                Current = null;
                break;
        }
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
