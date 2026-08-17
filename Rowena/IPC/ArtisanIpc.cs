using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Rowena.IPC;

/// <summary>Crafting, delegated to Artisan.</summary>
/// <remarks>
/// Artisan has no gate for building a list, but it has something better for this purpose:
/// CraftItem takes a recipe and a quantity and gets on with it. So "make five of these" is one
/// call, and the list plugin can own lists.
///
/// Every call is guarded. Artisan may not be installed, and none of that should throw into a
/// draw call.
/// </remarks>
internal sealed class ArtisanIpc(IDalamudPluginInterface plugin, IPluginLog log)
{
    private const string Prefix = "Artisan";

    private readonly ICallGateSubscriber<bool> isBusy =
        plugin.GetIpcSubscriber<bool>($"{Prefix}.IsBusy");

    private readonly ICallGateSubscriber<bool> listRunning =
        plugin.GetIpcSubscriber<bool>($"{Prefix}.IsListRunning");

    private readonly ICallGateSubscriber<ushort, int, object> craftItem =
        plugin.GetIpcSubscriber<ushort, int, object>($"{Prefix}.CraftItem");

    private readonly ICallGateSubscriber<Dictionary<int, string>> getLists =
        plugin.GetIpcSubscriber<Dictionary<int, string>>($"{Prefix}.GetLists");

    private readonly ICallGateSubscriber<int, object> startList =
        plugin.GetIpcSubscriber<int, object>($"{Prefix}.StartListById");

    /// <summary>Whether Artisan answers at all.</summary>
    public bool Available
    {
        get
        {
            try
            {
                isBusy.InvokeFunc();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Already crafting, so a new request would interrupt something.</summary>
    public bool Busy => Try(() => isBusy.InvokeFunc(), false) || Try(() => listRunning.InvokeFunc(), false);

    /// <summary>
    /// Queues a number of crafts of one recipe.
    /// </summary>
    /// <remarks>
    /// The recipe id is a ushort here, which is Artisan's choice rather than the game's. Recipe
    /// ids fit comfortably, but the cast is checked so a future sheet cannot silently wrap.
    /// </remarks>
    public bool Craft(uint recipeId, int quantity)
    {
        if (recipeId > ushort.MaxValue)
        {
            log.Warning($"Recipe {recipeId} does not fit the id size Artisan expects.");
            return false;
        }

        if (quantity <= 0)
            return false;

        return Try(
            () =>
            {
                craftItem.InvokeAction((ushort)recipeId, quantity);
                log.Information($"Asked Artisan for {quantity} of recipe {recipeId}.");
                return true;
            },
            false);
    }

    /// <summary>
    /// The lists Artisan already has, by id.
    /// </summary>
    /// <remarks>
    /// Readable and startable, but not extendable: Artisan exposes no way to add to one. That is why
    /// new items accumulate in a basket here and go over as a fresh list.
    /// </remarks>
    public IReadOnlyDictionary<int, string> Lists() =>
        Try<IReadOnlyDictionary<int, string>>(() => getLists.InvokeFunc(), new Dictionary<int, string>());

    public bool StartList(int id) =>
        Try(
            () =>
            {
                startList.InvokeAction(id);
                log.Information($"Asked Artisan to start list {id}.");
                return true;
            },
            false);

    private T Try<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch (Exception error)
        {
            log.Verbose(error, "Artisan IPC call failed.");
            return fallback;
        }
    }
}
