using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
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
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;

    private readonly WindowSystem windows = new("Rowena");
    private readonly MainWindow mainWindow;
    private readonly ServerBar serverBar;
    private readonly Briefing briefing;
    private readonly Places places;
    private readonly BoardWatcher boardWatcher;
    private readonly LiveMarket live;
    private readonly HttpClient http;
    private readonly Configuration config;
    private readonly MarketCache market;
    private readonly PricingScope scope;
    private readonly Diagnostics diagnostics;
    private readonly DebugChannel debug;
    private readonly Warmup warmup;
    private readonly CraftSweep sweep;
    private readonly VendorSweep vendorSweep;
    private readonly GatherSweep gatherSweep;
    private readonly SalesLog sales;
    private readonly RetainerSales retainerSales;
    private readonly GatherClock gatherClock;
    private readonly Watch watch;
    private readonly RetainerStock retainerStock;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var catalogFile = new CatalogFile(
            Path.Combine(PluginInterface.ConfigDirectory.FullName, CatalogFileName), Log);
        var catalog = catalogFile.LoadOrDefault();
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

        diagnostics = new Diagnostics(config, Log);
        market = new MarketCache(source, store, diagnostics, Log)
        {
            Ttl = config.PriceTtl(),
            BookBatchSize = config.PriceBatchSize,
            SummaryBatchSize = config.SurveyBatchSize,
        };

        boardWatcher = new BoardWatcher(MarketBoard, config, Save, diagnostics, Log);
        live = new LiveMarket(
            new MarketFeed(message => diagnostics.Note("live", message)),
            market, Framework, scope, new Worlds(DataManager, Log), config, diagnostics, Log);
        var gatherBuddy = new GatherBuddyIpc(PluginInterface, Log);
        var craftables = new Craftables(DataManager, config, Log);
        sweep = new CraftSweep(craftables, market, config, Log);

        var basket = new CraftBasket(config, new Recipes(DataManager, Log), Save, Log);
        var actions = new ItemActions(
            new ArtisanIpc(PluginInterface, Log), allaganTools, basket, ChatGui, Log);
        var itemNames = new Items(DataManager);
        var cells = new ItemCells(itemNames, Textures, actions, market, scope, boardWatcher);
        var vendorPrices = new VendorPrices(DataManager);
        var boards = new Boards(market, scope, vendorPrices, boardWatcher);
        vendorSweep = new VendorSweep(vendorPrices, market, diagnostics, Log);
        var levels = new Levels(DataManager);
        var notices = new Notices();
        var gatherables = new Gatherables(DataManager, levels, Log);
        gatherSweep = new GatherSweep(gatherables, market, diagnostics, Log);
        var trades = new Trades(catalog, new SpecialShops(DataManager, new Vendors(DataManager, Log), Log));
        places = new Places(
            PluginInterface, ClientState, Objects, GameGui, Framework, new Aetherytes(DataManager, Log), Log);
        var venues = new VenueCell(places);
        // The refreshes are the window's, reached through lambdas because the window does not
        // exist yet: the tabs live inside it. Read at click time, when it long since does.
        var convertTab = new ConvertTab(
            trades, boards, balances, cells, config, market, venues, diagnostics,
            conversion => mainWindow!.RefreshTrade(conversion));
        var craftTab = new CraftTab(
            sweep, craftables, boards, cells, basket, config, levels, diagnostics,
            conversion => mainWindow!.RefreshTrade(conversion),
            () => RecheckCrafts());
        gatherClock = new GatherClock(
            Framework, GameGui, balances, gatherables, config, Save, diagnostics, Log);

        var gatherTab = new GatherTab(
            gatherSweep, gatherables, boards, cells, config, gatherClock, diagnostics);

        sales = new SalesLog(ChatGui, config, Save, diagnostics, Log);

        // Driven by prices moving rather than by a timer, so an undercut or a vendor listing
        // arrives while it still means something.
        watch = new Watch(
            Framework, market, boardWatcher, boards, scope, itemNames, config, notices, diagnostics, Log);

        // Chat only reports what sold while somebody was online to hear it. The rest is read
        // off the retainer itself, whenever one is open.
        retainerSales = new RetainerSales(
            Framework, config, sales, itemNames, notices, boardWatcher.TaxFor, Save, diagnostics, Log);

        var sellingTab = new SellingTab(
            boardWatcher, boards, cells, config, diagnostics, sales,
            ids => market.RefreshInBackground(scope.Selling, ids, true, FetchPriority.Interactive));
        var vendorTab = new VendorTab(
            vendorSweep, boards, cells, config, diagnostics,
            ids => market.RefreshInBackground(scope.Buying, [.. ids], true, FetchPriority.Interactive));
        var diagnosticsPanel = new DiagnosticsPanel(
            diagnostics, market, live, boardWatcher, sweep, vendorSweep, places, config);

        retainerStock = new RetainerStock(Framework, config, Save, diagnostics, Log);

        var hoardTab = new HoardTab(
            balances, retainerStock, boardWatcher, boards, market, cells, config, diagnostics,
            () => craftTab.Wants());

        var overviewTab = new OverviewTab(
            convertTab, craftTab, vendorTab, gatherTab, sellingTab, hoardTab, notices, sweep, config,
            tab => mainWindow!.Show(tab));

        var settingsTab = new SettingsTab(
            config, gatherClock, market, catalogFile, trades, boardWatcher, diagnosticsPanel,
            () => mainWindow!.RefreshPrices(), Save);

        mainWindow = new MainWindow(
            trades, market, balances, scope, gatherBuddy, cells, places, live, diagnostics, sweep, vendorSweep,
            gatherSweep, convertTab, craftTab, vendorTab, gatherTab, sellingTab, hoardTab, overviewTab, settingsTab, config, Save);
        windows.AddWindow(mainWindow);

        var headlines = new Headlines(trades, boards, balances, config);

        serverBar = new ServerBar(
            DtrBar, Framework, market, headlines,
            () => mainWindow.Show(MainWindow.Tab.Sinks),
            () => mainWindow.Show(MainWindow.Tab.Flips));

        briefing = new Briefing(
            ClientState, Framework, notices, market, sweep, headlines, config, diagnostics,
            () => gatherTab.OpenNow(),
            () => scope.Ready,
            () => mainWindow.RefreshPrices(FetchPriority.Background));

        // Driving the plugin from a file, so the half of the work that needs a button pressed
        // can be done by whoever is reading the log rather than only by whoever is at the game.
        debug = new DebugChannel(
            PluginInterface.ConfigDirectory.FullName, Framework, config, diagnostics, Log,
            new Dictionary<string, Action>
            {
                ["refresh"] = () => mainWindow.RefreshPrices(),
                ["sweep"] = () => sweep.Start(scope.Buying, scope.Selling, config.FurnishingShortlist, config.SweepAge()),
                ["recraft"] = RecheckCrafts,
                ["survey"] = () => gatherSweep.Start(scope.Selling, config.GatherShortlist, config.SweepAge()),
                ["gather"] = () => mainWindow.Show(MainWindow.Tab.Gather),
                ["selling"] = () => mainWindow.Show(MainWindow.Tab.Selling),
                ["overview"] = () => mainWindow.Show(MainWindow.Tab.Overview),
                ["hoard"] = () => mainWindow.Show(MainWindow.Tab.Hoard),
                ["recheck listings"] = () => market.RefreshInBackground(
                    scope.Selling, [.. boardWatcher.ListedItems()], true, FetchPriority.Interactive),
                ["watch"] = () => Log.Information($"Watch: {watch.Report}"),
                ["alert watch"] = () =>
                {
                    config.AlertUndercut = !config.AlertUndercut;
                    config.AlertVendorFind = config.AlertUndercut;
                    Save();
                    Log.Information($"Change alerts are now {(config.AlertUndercut ? "on" : "off")}.");
                },
                ["alert windows"] = () =>
                {
                    config.AlertWindows = !config.AlertWindows;
                    Save();
                    Log.Information($"Window alerts are now {(config.AlertWindows ? "on" : "off")}.");
                },
                ["sales"] = () => Log.Information(
                    $"Sales remembered: {sales.All().Count}. "
                    + string.Join("; ", sales.All().Take(8).Select(one => $"{one.Quantity}x {one.ItemId} for {one.Gil:N0}"))),
                ["plan 10"] = () => gatherTab.PlanFor(10),
                ["plan 30"] = () => gatherTab.PlanFor(30),
                ["plan 60"] = () => gatherTab.PlanFor(60),
                ["plan 120"] = () => gatherTab.PlanFor(120),
                ["plan off"] = () => gatherTab.PlanFor(0),
                ["timed on"] = () => gatherTab.IncludeTimed(true),
                ["timed off"] = () => gatherTab.IncludeTimed(false),
                ["aim gil"] = () => gatherTab.AimFor(GatherAim.MostGil),
                ["aim mixed"] = () => gatherTab.AimFor(GatherAim.MixedBag),
                ["aim soonest"] = () => gatherTab.AimFor(GatherAim.SellsSoonest),
                ["scan"] = () => vendorSweep.Start(scope.Buying, config.VendorCandidatesToCost, config.SweepAge()),
                ["brief"] = () => briefing.Now(),
                ["open"] = () => mainWindow.IsOpen = true,
                ["close"] = () => mainWindow.IsOpen = false,
                ["sinks"] = () => mainWindow.Show(MainWindow.Tab.Sinks),
                ["flips"] = () => mainWindow.Show(MainWindow.Tab.Flips),
                ["vendor"] = () => mainWindow.Show(MainWindow.Tab.Vendor),
                ["craft"] = () => mainWindow.Show(MainWindow.Tab.Craft),
                ["dump"] = () =>
                {
                    var into = PluginInterface.ConfigDirectory.FullName;
                    File.WriteAllText(Path.Combine(into, "dump.json"), convertTab.Dump());
                    File.WriteAllText(Path.Combine(into, "vendor.json"), vendorTab.Dump());
                    File.WriteAllText(Path.Combine(into, "craft.json"), craftTab.Dump());
                    File.WriteAllText(Path.Combine(into, "gather.json"), gatherTab.Dump());
                    File.WriteAllText(Path.Combine(into, "selling.json"), sellingTab.Dump());
                    File.WriteAllText(Path.Combine(into, "overview.json"), overviewTab.Dump());
                    File.WriteAllText(Path.Combine(into, "hoard.json"), hoardTab.Dump());
                },
            },
            diagnosticsPanel.Report);

        warmup = new Warmup(
            Framework,
            [
                mainWindow.RestoreAll,
                .. convertTab.Warmers,
                .. craftTab.Warmers,
                .. vendorTab.Warmers,
                .. gatherTab.Warmers,
                .. sellingTab.Warmers,
                .. hoardTab.Warmers,
            ],
            () => scope.Ready,
            diagnostics);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open Rowena. What you are holding, and what it is worth turning into. "
                + "Add sinks, flips, vendor, gather, craft or settings to open on that tab; brief says the login "
                + "line again.",
        });
    }

    /// <summary>
    /// Refetches what the swept crafts cost and fetch, without sweeping again.
    /// </summary>
    /// <remarks>
    /// The shortlist is hours of requests and stays useful; the prices under it are minutes
    /// old at best. Separating the two means the expensive half is not repeated to correct the
    /// cheap half.
    /// </remarks>
    private void RecheckCrafts()
    {
        var shortlist = sweep.Current.Shortlist;

        market.RefreshInBackground(
            scope.Buying,
            [
                .. shortlist
                    .SelectMany(conversion => conversion.Inputs)
                    .Where(amount => amount.Resource.Kind == ResourceKind.Item)
                    .Select(amount => amount.Resource.Id)
                    .Distinct(),
            ],
            true,
            FetchPriority.Background);

        market.RefreshInBackground(
            scope.Selling,
            [
                .. shortlist
                    .SelectMany(conversion => conversion.Outputs)
                    .Where(amount => amount.Resource.Kind == ResourceKind.Item)
                    .Select(amount => amount.Resource.Id)
                    .Distinct(),
            ],
            true,
            FetchPriority.Background);
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
            case "sinks" or "convert":
                mainWindow.Show(MainWindow.Tab.Sinks);
                break;

            case "flips":
                mainWindow.Show(MainWindow.Tab.Flips);
                break;

            case "vendor" or "vendors":
                mainWindow.Show(MainWindow.Tab.Vendor);
                break;

            case "gather" or "gathering":
                mainWindow.Show(MainWindow.Tab.Gather);
                break;

            case "craft" or "crafts":
                mainWindow.Show(MainWindow.Tab.Craft);
                break;

            case "settings" or "config":
                mainWindow.Show(MainWindow.Tab.Settings);
                break;

            case "brief" or "briefing":
                briefing.Now();
                break;

            default:
                // Opened rather than answered. Saying "no such tab" into the game would be this
                // plugin writing into the world to report a typo, which is a poor trade.
                mainWindow.Show(MainWindow.Tab.Overview);
                break;
        }
    }

    public void Dispose()
    {
        // A sweep is minutes of requests and a reload is constant in dev mode, so it is written out
        // on the way past rather than only when it finishes.
        market.Persist(sweep.Stored());

        warmup.Dispose();
        sales.Dispose();
        retainerSales.Dispose();
        gatherClock.Dispose();
        watch.Dispose();
        retainerStock.Dispose();
        debug.Dispose();
        briefing.Dispose();
        serverBar.Dispose();
        places.Dispose();
        boardWatcher.Dispose();
        live.Dispose();
        market.Dispose();
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        windows.RemoveAllWindows();
        http.Dispose();
    }
}
