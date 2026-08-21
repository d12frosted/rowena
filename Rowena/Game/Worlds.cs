using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>
/// Which worlds a board name covers.
/// </summary>
/// <remarks>
/// A scope in this plugin is a name typed or read from the game: a world, a data centre, or a
/// region. The websocket subscribes per world, so it needs that name turned back into the
/// worlds behind it. Read once from the sheets, which is also the only place that knows which
/// worlds a data centre currently holds, and that changes with every new one.
/// </remarks>
internal sealed class Worlds(IDataManager data, IPluginLog log)
{
    private IReadOnlyDictionary<string, uint[]>? byScope;

    /// <summary>The worlds a scope covers, or empty when the name is not one.</summary>
    public IReadOnlyList<uint> In(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return [];

        Build();
        return byScope!.TryGetValue(scope, out var worlds) ? worlds : [];
    }

    private void Build()
    {
        if (byScope is not null)
            return;

        var scopes = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
        var byCentre = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var world in data.GetExcelSheet<World>())
        {
            if (!world.IsPublic)
                continue;

            var name = world.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            scopes[name] = [world.RowId];

            if (world.DataCenter.ValueNullable?.Name.ExtractText() is { } centre && !string.IsNullOrWhiteSpace(centre))
            {
                if (!byCentre.TryGetValue(centre, out var list))
                    byCentre[centre] = list = [];

                list.Add(world.RowId);
            }
        }

        foreach (var (centre, worlds) in byCentre)
            scopes[centre] = [.. worlds];

        byScope = scopes;
        log.Verbose($"Mapped {scopes.Count} board names to worlds.");
    }
}
