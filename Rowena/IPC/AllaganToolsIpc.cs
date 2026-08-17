using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Rowena.IPC;

/// <summary>What you own everywhere, including inside retainers.</summary>
/// <remarks>
/// This is data the game genuinely does not have. A retainer's inventory is only loaded while
/// you have that retainer open, so nothing reading client memory can tell you what is sitting
/// in one. AllaganTools persists them, which makes it the only source for "how many of these do
/// I actually own".
///
/// Deliberately not using AllaganTools.ItemCountOwned, despite the name being exactly right.
/// It filters on a list of container types drawn from AllaganTools' own sorted-container enum
/// rather than the game's, which would mean tracking a second plugin's internal numbering, and
/// passing an empty list returns a confident zero rather than everything. ItemCount takes -1
/// for "any container", so summing that across the owned inventories needs no such knowledge.
/// </remarks>
internal sealed class AllaganToolsIpc(IDalamudPluginInterface plugin, IPluginLog log)
{
    private const string Prefix = "AllaganTools";

    /// <summary>ItemCount's "any container" sentinel.</summary>
    private const int AnyContainer = -1;

    /// <summary>
    /// The owner list barely changes, and it costs an IPC call plus a call per owner to use.
    /// </summary>
    private static readonly TimeSpan OwnerListLifetime = TimeSpan.FromSeconds(30);

    private readonly ICallGateSubscriber<bool> initialized =
        plugin.GetIpcSubscriber<bool>($"{Prefix}.IsInitialized");

    private readonly ICallGateSubscriber<bool, HashSet<ulong>> owners =
        plugin.GetIpcSubscriber<bool, HashSet<ulong>>($"{Prefix}.GetCharactersOwnedByActive");

    private readonly ICallGateSubscriber<uint, ulong, int, uint> itemCount =
        plugin.GetIpcSubscriber<uint, ulong, int, uint>($"{Prefix}.ItemCount");

    private readonly ICallGateSubscriber<string, Dictionary<uint, uint>, string> addCraftList =
        plugin.GetIpcSubscriber<string, Dictionary<uint, uint>, string>($"{Prefix}.AddNewCraftList");

    private readonly ICallGateSubscriber<Dictionary<string, string>> craftLists =
        plugin.GetIpcSubscriber<Dictionary<string, string>>($"{Prefix}.GetCraftLists");

    private readonly ICallGateSubscriber<string, uint, uint, bool> addToCraftList =
        plugin.GetIpcSubscriber<string, uint, uint, bool>($"{Prefix}.AddItemToCraftList");

    private ulong[] cachedOwners = [];
    private DateTime ownersCachedAt = DateTime.MinValue;

    /// <summary>
    /// Whether AllaganTools is there and has finished loading.
    /// </summary>
    /// <remarks>
    /// Both halves matter. It answers its gates before its inventory monitor has caught up, and
    /// during that window every count is a truthful-looking zero.
    /// </remarks>
    public bool Available
    {
        get
        {
            try
            {
                return initialized.InvokeFunc();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// How many of an item you own across your character, your saddlebag and every retainer.
    /// Null when AllaganTools cannot answer, which is different from owning none.
    /// </summary>
    public long? Owned(uint itemId)
    {
        var inventories = Owners();
        if (inventories.Length == 0)
            return null;

        long total = 0;
        var answered = false;

        foreach (var owner in inventories)
        {
            if (Try<uint?>(() => itemCount.InvokeFunc(itemId, owner, AnyContainer), null) is not { } count)
                continue;

            total += count;
            answered = true;
        }

        return answered ? total : null;
    }

    /// <summary>
    /// Creates a craft list. Returns its name or id, or null if it could not be made.
    /// </summary>
    /// <remarks>
    /// This is the handoff GatherBuddyReborn had no gate for. AllaganTools owns lists and already
    /// knows what you hold, so it can work out what is still to be bought or gathered, which is
    /// exactly the part worth not doing by hand.
    /// </remarks>
    public string? AddCraftList(string name, IReadOnlyDictionary<uint, uint> items)
    {
        if (!Available || items.Count == 0)
            return null;

        var payload = items.ToDictionary(entry => entry.Key, entry => entry.Value);
        var result = Try<string?>(() => addCraftList.InvokeFunc(name, payload), null);

        if (result is not null)
            log.Information($"Created AllaganTools craft list '{name}' with {items.Count} items.");

        return result;
    }

    /// <summary>The craft lists AllaganTools already holds, by key.</summary>
    public IReadOnlyDictionary<string, string> CraftLists() =>
        Available
            ? Try<IReadOnlyDictionary<string, string>>(() => craftLists.InvokeFunc(), new Dictionary<string, string>())
            : new Dictionary<string, string>();

    /// <summary>
    /// Adds to a list that already exists.
    /// </summary>
    /// <remarks>
    /// The one thing Artisan cannot do at all, which is why the crafting-list features here run
    /// through AllaganTools rather than through the plugin that does the crafting.
    /// </remarks>
    public bool AddToExistingList(string listKey, uint itemId, uint quantity)
    {
        if (!Available)
            return false;

        // Its own return value reports the write rather than the outcome, so success is inferred
        // from not throwing.
        Try(() => addToCraftList.InvokeFunc(listKey, itemId, quantity), false);
        log.Information($"Added {quantity}x {itemId} to AllaganTools list {listKey}.");
        return true;
    }

    private ulong[] Owners()
    {
        if (cachedOwners.Length > 0 && DateTime.UtcNow - ownersCachedAt < OwnerListLifetime)
            return cachedOwners;

        if (!Available)
            return [];

        // Includes the active character, not just its retainers, so the sum is everything.
        var found = Try<HashSet<ulong>?>(() => owners.InvokeFunc(true), null);
        if (found is null)
            return [];

        cachedOwners = [.. found];
        ownersCachedAt = DateTime.UtcNow;
        return cachedOwners;
    }

    private T Try<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch (Exception error)
        {
            log.Verbose(error, "AllaganTools IPC call failed.");
            return fallback;
        }
    }
}
