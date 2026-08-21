using Dalamud.Plugin.Services;

namespace Rowena.UI;

/// <summary>
/// Runs each model once, early, so no tab pays for it on the frame it is opened.
/// </summary>
/// <remarks>
/// The first build of a model costs three times what every build after it costs, and the
/// difference is the runtime compiling the code rather than anything this plugin can remove:
/// it stayed at fifty to sixty milliseconds while the work inside it was cut repeatedly. So
/// it is paid up front instead, where a dropped frame is a loading screen rather than
/// somebody opening a window.
///
/// One per tick rather than all at once, since a framework update long enough to fix the
/// problem would be a framework update long enough to be the problem.
/// </remarks>
internal sealed class Warmup : IDisposable
{
    private readonly IFramework framework;
    private readonly Queue<Action> waiting;
    private readonly Func<bool> ready;
    private readonly Diagnostics diagnostics;

    public Warmup(IFramework framework, IEnumerable<Action> warmers, Func<bool> ready, Diagnostics diagnostics)
    {
        this.framework = framework;
        this.ready = ready;
        this.diagnostics = diagnostics;
        waiting = new Queue<Action>(warmers);

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    private void Tick(IFramework _)
    {
        // Nothing to warm against until there is a board to price on, and a model built
        // before then is one that has to be built again anyway.
        if (!ready())
            return;

        if (!waiting.TryDequeue(out var warm))
        {
            framework.Update -= Tick;
            diagnostics.Note("draw", "models warmed");
            return;
        }

        try
        {
            warm();
        }
        catch (Exception error)
        {
            // A model that cannot be built yet will be built again when it is drawn, so this
            // is survivable. It is not silent: swallowing it hid a restore that never ran.
            diagnostics.Note("draw", $"warming threw: {error.Message}");
        }
    }
}
