using Dalamud.Plugin.Services;
using Rowena.Core.Conversions;

namespace Rowena;

/// <summary>
/// The editable catalogue on disk, and the two ways it is read.
/// </summary>
/// <remarks>
/// The shipped catalogue is written out on first run so there is a real file to edit rather
/// than a schema to read about. At startup a broken copy falls back to the embedded one with
/// a complaint in the log: a bad edit should cost the edit, not the plugin.
///
/// Mid-session the stakes are different. Falling back would replace trades you were looking
/// at with the shipped handful, so <see cref="TryLoad"/> keeps whatever is loaded and hands
/// the error back instead, to be shown beside the button that asked.
/// </remarks>
internal sealed class CatalogFile(string path, IPluginLog log)
{
    public string Path => path;

    /// <summary>The startup read: seeds the file when absent, falls back when broken.</summary>
    public ConversionCatalog LoadOrDefault()
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, ConversionCatalog.EmbeddedJson());
                log.Information($"Wrote a starting catalogue to {path}.");
            }

            return ConversionCatalog.Load(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            log.Error(error, $"Could not use {path}; falling back to the shipped catalogue.");
            return ConversionCatalog.Default;
        }
    }

    /// <summary>
    /// A mid-session read, reporting what happened rather than throwing or falling back.
    /// </summary>
    /// <remarks>
    /// The report is written for the settings tab: what loaded when it worked, and the
    /// parser's complaint when it did not, so a broken edit can be fixed without opening
    /// the log. The catalogue's own errors name the entry at fault, which is the part
    /// that matters.
    /// </remarks>
    public (ConversionCatalog? Catalog, string Report) TryLoad()
    {
        try
        {
            var catalog = ConversionCatalog.Load(File.ReadAllText(path));

            return (catalog, $"{catalog.Conversions.Count} trades over {catalog.Resources.Count} resources");
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not reload the catalogue.");
            return (null, error.Message);
        }
    }
}
