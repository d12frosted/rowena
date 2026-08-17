using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Rowena.IPC;

/// <summary>Gathering, delegated to GatherBuddyReborn.</summary>
/// <remarks>
/// Its IPC is narrow on purpose: a version, an item identifier, and the auto-gather switch
/// with its status. There is nothing for handing it a shopping list, so queueing work goes
/// through its chat commands instead. That is a looser contract than IPC and it is called out
/// where it happens, because a renamed command fails silently in a way a missing IPC gate
/// does not.
///
/// Every call is guarded. GatherBuddyReborn may not be installed, may be an older build, or
/// may be mid-teardown, and none of those should throw into a draw call.
/// </remarks>
internal sealed class GatherBuddyIpc(IDalamudPluginInterface plugin, ICommandManager commands, IPluginLog log)
{
    /// <summary>The IPC prefix is GatherBuddyReborn's internal name, not "GatherBuddy".</summary>
    private const string Prefix = "GatherBuddyReborn";

    /// <summary>What this was written against. Higher is assumed compatible, lower is not.</summary>
    private const int KnownVersion = 2;

    private readonly ICallGateSubscriber<int> version =
        plugin.GetIpcSubscriber<int>($"{Prefix}.Version");

    private readonly ICallGateSubscriber<string, uint> identify =
        plugin.GetIpcSubscriber<string, uint>($"{Prefix}.Identify");

    private readonly ICallGateSubscriber<bool> autoGatherEnabled =
        plugin.GetIpcSubscriber<bool>($"{Prefix}.IsAutoGatherEnabled");

    private readonly ICallGateSubscriber<bool, object> setAutoGatherEnabled =
        plugin.GetIpcSubscriber<bool, object>($"{Prefix}.SetAutoGatherEnabled");

    private readonly ICallGateSubscriber<bool> autoGatherWaiting =
        plugin.GetIpcSubscriber<bool>($"{Prefix}.IsAutoGatherWaiting");

    private readonly ICallGateSubscriber<string> statusText =
        plugin.GetIpcSubscriber<string>($"{Prefix}.GetAutoGatherStatusText");

    // Identify is a round trip through another plugin, and the window would otherwise ask
    // the same question of the same item every frame. Gatherability does not change.
    private readonly Dictionary<string, bool> gatherable = new(StringComparer.OrdinalIgnoreCase);

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

    public bool AutoGathering => Try(() => autoGatherEnabled.InvokeFunc(), false);

    /// <summary>Enabled, but parked: no node is up, or it is between windows.</summary>
    public bool Waiting => Try(() => autoGatherWaiting.InvokeFunc(), false);

    public string Status => Try(() => statusText.InvokeFunc(), "") ?? "";

    public void SetAutoGathering(bool enabled) =>
        Try(() =>
        {
            setAutoGatherEnabled.InvokeAction(enabled);
            return true;
        }, false);

    /// <summary>
    /// Whether GatherBuddyReborn recognises this as something it can go and get.
    /// </summary>
    /// <remarks>
    /// Its identifier answers 0 for anything that is not a gatherable or a fish, which is
    /// exactly the check needed before offering to gather something. Without it the window
    /// would cheerfully offer to go and gather a Mount Token.
    /// </remarks>
    public bool IsGatherable(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return false;

        if (gatherable.TryGetValue(itemName, out var known))
            return known;

        var answer = Try(() => identify.InvokeFunc(itemName), 0u) != 0u;

        // Only remembered once it has actually answered. Caching a false from a plugin that
        // was not loaded yet would keep the button hidden for the rest of the session.
        if (Responding)
            gatherable[itemName] = answer;

        return answer;
    }

    /// <summary>Sends it after an item by name.</summary>
    public void Gather(string itemName) => Command($"/gather {itemName}");

    /// <summary>
    /// Starts its collectable routine, which is how scrips are actually earned.
    /// </summary>
    public void StartCollectables() => Command("/gatherbuddy collect");

    public void StopCollectables() => Command("/gatherbuddy collectstop");

    /// <summary>
    /// Runs one of its chat commands.
    /// </summary>
    /// <remarks>
    /// Not IPC, so nothing reports back and a renamed command simply does nothing. Logged at
    /// information level so it is possible to tell "I asked and it ignored me" apart from
    /// "I never asked".
    /// </remarks>
    private void Command(string command)
    {
        if (!Responding)
        {
            log.Warning($"Not sending '{command}': GatherBuddyReborn is not responding.");
            return;
        }

        log.Information($"Handing off to GatherBuddyReborn: {command}");
        commands.ProcessCommand(command);
    }

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
