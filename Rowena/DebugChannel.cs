using Dalamud.Plugin.Services;

namespace Rowena;

/// <summary>
/// A way to drive the plugin from outside the game, for working on it.
/// </summary>
/// <remarks>
/// Everything interesting here is reached by pressing something in a window, which makes it
/// awkward to work on: the person who can read the log and change the code cannot press the
/// button, and the person who can press the button is me, one command at a time. So the
/// plugin watches a file and does what it says.
///
/// Only things a button already does, and nothing that touches the character: fetch, scan,
/// sweep, say the briefing, write out what is happening. Rowena reads and displays, and a
/// channel that could do more than the window can would be a worse thing than the convenience
/// is worth.
///
/// Alive only while diagnostics are on, which is also what makes it obvious: something that
/// can be driven from a file should not be quietly present in a plugin somebody installed to
/// look at prices.
/// </remarks>
internal sealed class DebugChannel : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private readonly string commands;
    private readonly string state;
    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;
    private readonly IReadOnlyDictionary<string, Action> actions;
    private readonly Func<string> report;

    private DateTime nextAt;

    public DebugChannel(
        string directory,
        IFramework framework,
        Configuration config,
        Diagnostics diagnostics,
        IPluginLog log,
        IReadOnlyDictionary<string, Action> actions,
        Func<string> report)
    {
        commands = Path.Combine(directory, "commands.txt");
        state = Path.Combine(directory, "state.txt");
        this.framework = framework;
        this.config = config;
        this.diagnostics = diagnostics;
        this.log = log;
        this.actions = actions;
        this.report = report;

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    private void Tick(IFramework _)
    {
        if (!config.Diagnostics || DateTime.UtcNow < nextAt)
            return;

        nextAt = DateTime.UtcNow + Every;

        try
        {
            if (!File.Exists(commands))
                return;

            var lines = File.ReadAllLines(commands);

            // Emptied before running anything, so a command that throws is not run again on
            // every tick forever.
            File.WriteAllText(commands, "");

            foreach (var line in lines.Select(line => line.Trim().ToLowerInvariant()).Where(line => line.Length > 0))
                Run(line);
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not read the debug channel.");
        }
    }

    private void Run(string command)
    {
        if (command == "state")
        {
            File.WriteAllText(state, report());
            diagnostics.Note("debug", "wrote state.txt");
            return;
        }

        if (!actions.TryGetValue(command, out var action))
        {
            diagnostics.Note("debug", $"no such command: {command}. Try {string.Join(", ", actions.Keys)}, state");
            return;
        }

        diagnostics.Note("debug", $"running {command}");

        try
        {
            action();
        }
        catch (Exception error)
        {
            diagnostics.Note("debug", $"{command} threw: {error.Message}");
            log.Warning(error, $"Debug command {command} failed.");
        }
    }
}
