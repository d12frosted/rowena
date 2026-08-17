using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Rowena.Core.Conversions;
using Rowena.Core.Universalis;
using Rowena.Game;
using Rowena.IPC;
using Rowena.Market;
using Rowena.UI;

namespace Rowena;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/rowena";
    private const string CatalogFileName = "conversions.json";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windows = new("Rowena");
    private readonly MainWindow mainWindow;
    private readonly HttpClient http;
    private readonly Configuration config;
    private readonly MarketCache market;
    private readonly PricingScope scope;
    private readonly FurnishingSweep sweep;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var catalog = LoadCatalog();
        var allaganTools = new AllaganToolsIpc(PluginInterface, Log);
        var balances = new Balances(Objects, allaganTools, Log);
        scope = new PricingScope(config, balances);

        // Universalis asks callers to identify themselves, which costs nothing and makes
        // it possible for them to tell a badly behaved client from a busy one.
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Rowena/0.0.1 (+https://github.com/d12frosted/rowena)");

        // The scope is not the client's business. It is resolved by the window, on the thread
        // where the game will answer, and passed down with each fetch.
        var source = new UniversalisClient(http, config.ListingDepth);

        var store = new PriceStore(
            Path.Combine(PluginInterface.ConfigDirectory.FullName, "prices.json.gz"), Log);

        market = new MarketCache(source, store, Log) { Ttl = config.PriceTtl() };

        var gatherBuddy = new GatherBuddyIpc(PluginInterface, Log);
        var furnishings = new Furnishings(DataManager, Log);
        sweep = new FurnishingSweep(furnishings, market, Log);

        var basket = new CraftBasket(config, new Recipes(DataManager, Log), Save, Log);
        var actions = new ItemActions(
            new ArtisanIpc(PluginInterface, Log), allaganTools, basket, config, ChatGui, Log);
        var cells = new ItemCells(new Items(DataManager), Textures, actions, market, scope);
        var boards = new Boards(market, scope);
        var trades = new Trades(catalog);
        var convertTab = new ConvertTab(trades, boards, balances, cells, config);
        var craftTab = new CraftTab(sweep, furnishings, boards, cells, basket, config);
        var settingsTab = new SettingsTab(config, market, Save);

        mainWindow = new MainWindow(
            trades, market, balances, scope, gatherBuddy, sweep,
            convertTab, craftTab, settingsTab, config, Save);
        windows.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open Rowena. What you are holding, and what it is worth turning into. "
                + "Add convert, craft or settings to open on that tab.",
        });
    }

    /// <summary>
    /// Loads the catalogue, preferring an editable copy beside the configuration.
    /// </summary>
    /// <remarks>
    /// The shipped catalogue is written out on first run so there is a real file to edit
    /// rather than a schema to read about. A copy the user has broken falls back to the
    /// embedded one with a complaint in the log: a bad edit should cost them their edit,
    /// not the plugin.
    /// </remarks>
    private static ConversionCatalog LoadCatalog()
    {
        var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, CatalogFileName);

        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
                File.WriteAllText(path, ConversionCatalog.EmbeddedJson());
                Log.Information($"Wrote a starting catalogue to {path}.");
            }

            return ConversionCatalog.Load(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            Log.Error(error, $"Could not use {path}; falling back to the shipped catalogue.");
            return ConversionCatalog.Default;
        }
    }

    private void Save() => PluginInterface.SavePluginConfig(config);

    private void OpenMainUi() => mainWindow.IsOpen = true;

    /// <summary>
    /// The gear in Dalamud's plugin list, which used to open the market screen and call it settings.
    /// </summary>
    private void OpenSettings() => mainWindow.Show(MainWindow.Tab.Settings);

    /// <summary>
    /// Opens the window, on a named tab when one is named.
    /// </summary>
    /// <remarks>
    /// Naming a tab shows it rather than toggling the window, because "/rowena craft" is a request to
    /// look at something and closing the window is never what it meant. Bare stays a toggle, which is
    /// what a keybind wants. An argument that names nothing says so instead of quietly toggling.
    /// </remarks>
    private void OnCommand(string command, string arguments)
    {
        var wanted = arguments.Trim().ToLowerInvariant();

        if (wanted.Length == 0)
        {
            mainWindow.Toggle();
            return;
        }

        switch (wanted)
        {
            case "convert" or "sinks" or "flips":
                mainWindow.Show(MainWindow.Tab.Convert);
                break;

            case "craft" or "crafts":
                mainWindow.Show(MainWindow.Tab.Craft);
                break;

            case "settings" or "config":
                mainWindow.Show(MainWindow.Tab.Settings);
                break;

            default:
                ChatGui.Print($"Rowena has no \"{wanted}\" tab. Try convert, craft or settings.");
                break;
        }
    }

    public void Dispose()
    {
        // A sweep is minutes of requests and a reload is constant in dev mode, so it is written out
        // on the way past rather than only when it finishes.
        market.Persist(sweep.Snapshot());

        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        windows.RemoveAllWindows();
        http.Dispose();
    }
}
