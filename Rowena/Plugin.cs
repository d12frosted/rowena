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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windows = new("Rowena");
    private readonly MainWindow mainWindow;
    private readonly HttpClient http;
    private readonly Configuration config;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var catalog = LoadCatalog();
        var balances = new Balances(Objects, Log);
        var scope = new PricingScope(config, balances);

        // Universalis asks callers to identify themselves, which costs nothing and makes
        // it possible for them to tell a badly behaved client from a busy one.
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Rowena/0.0.1 (+https://github.com/d12frosted/rowena)");

        // The scope is not the client's business. It is resolved by the window, on the thread
        // where the game will answer, and passed down with each fetch.
        var source = new UniversalisClient(http, config.ListingDepth);

        var market = new MarketCache(source, Log)
        {
            Ttl = TimeSpan.FromMinutes(Math.Max(1, config.PriceTtlMinutes)),
        };

        var gatherBuddy = new GatherBuddyIpc(PluginInterface, Log);

        mainWindow = new MainWindow(catalog, market, balances, scope, gatherBuddy, config, Save);
        windows.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Rowena. What you are holding, and what it is worth turning into.",
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

    private void OnCommand(string command, string arguments) => mainWindow.Toggle();

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        windows.RemoveAllWindows();
        http.Dispose();
    }
}
