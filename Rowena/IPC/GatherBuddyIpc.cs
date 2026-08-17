using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Rowena.IPC;

/// <summary>Whether GatherBuddyReborn is currently working, and what it says it is doing.</summary>
/// <remarks>
/// A sensor, not a remote control. There were buttons here to start its auto-gather and its
/// collectable routine, and they were not worth having: the list and its settings live in
/// GatherBuddyReborn, so anyone about to gather is already in that window and will start it
/// there. Duplicating a control with strictly less capability is worse than not offering it.
///
/// What is worth having is the state, because an earning rate is only meaningful when it is
/// measured over time that was actually spent gathering. This is what tells the difference
/// between a quiet hour and an hour at a menu.
///
/// Every call is guarded. GatherBuddyReborn may not be installed, may be an older build, or
/// may be mid-teardown, and none of those should throw into a draw call.
/// </remarks>
internal sealed class GatherBuddyIpc(IDalamudPluginInterface plugin, IPluginLog log)
{
    /// <summary>The IPC prefix is GatherBuddyReborn's internal name, not "GatherBuddy".</summary>
    private const string Prefix = "GatherBuddyReborn";

    /// <summary>What this was written against. Higher is assumed compatible, lower is not.</summary>
    private const int KnownVersion = 2;

    private readonly ICallGateSubscriber<int> version =
        plugin.GetIpcSubscriber<int>($"{Prefix}.Version");

    private readonly ICallGateSubscriber<bool> autoGatherEnabled =
        plugin.GetIpcSubscriber<bool>($"{Prefix}.IsAutoGatherEnabled");

    private readonly ICallGateSubscriber<bool> autoGatherWaiting =
        plugin.GetIpcSubscriber<bool>($"{Prefix}.IsAutoGatherWaiting");

    private readonly ICallGateSubscriber<string> statusText =
        plugin.GetIpcSubscriber<string>($"{Prefix}.GetAutoGatherStatusText");

    private bool complainedAboutVersion;

    /// <summary>Whether GatherBuddyReborn answers at all.</summary>
    public bool Responding
    {
        get
        {
            try
            {
                version.InvokeFunc();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public int? Version => Try<int?>(() => version.InvokeFunc(), null);

    /// <summary>
    /// True when it is installed but older than this was written against, so the gates we
    /// want may not all be there.
    /// </summary>
    public bool TooOld
    {
        get
        {
            if (Version is not { } found || found >= KnownVersion)
                return false;

            if (!complainedAboutVersion)
            {
                complainedAboutVersion = true;
                log.Warning($"GatherBuddyReborn reports IPC version {found}, expected at least {KnownVersion}.");
            }

            return true;
        }
    }

    /// <summary>
    /// Auto-gather is switched on. The clock for any measured rate should run while this holds
    /// and stop when it does not.
    /// </summary>
    public bool AutoGathering => Try(() => autoGatherEnabled.InvokeFunc(), false);

    /// <summary>Enabled, but parked: no node is up, or it is between windows.</summary>
    public bool Waiting => Try(() => autoGatherWaiting.InvokeFunc(), false);

    public string Status => Try(() => statusText.InvokeFunc(), "") ?? "";

    private T Try<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch (Exception error)
        {
            log.Verbose(error, "GatherBuddyReborn IPC call failed.");
            return fallback;
        }
    }
}
