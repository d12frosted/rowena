using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Rowena.Core.Lists;

/// <summary>
/// Builds the strings GatherBuddyReborn accepts.
/// </summary>
/// <remarks>
/// Rowena works out what is worth gathering and has no business gathering it, the same
/// division as the crafting list and Artisan. What was missing was a way to hand the list
/// over: GatherBuddyReborn's IPC offers a version, a name lookup and a switch for its
/// auto-gather, and nothing at all for managing what is in the list.
///
/// Its own import does, though, and there are two of them. A gather window preset is the list
/// the overlay shows; an auto-gather list is the one auto-gather actually works through. They
/// take different shapes behind different version bytes, and the second is the one worth
/// having: it carries a quantity per item, so a session plan arrives as the amounts it worked
/// out rather than as a list of names.
///
/// Both travel as gzipped JSON behind a version byte, base64 encoded, which is a format rather
/// than an interface: it can change under us without anybody's build breaking, so the version
/// is written out and the shape is pinned by tests that take the string apart again.
///
/// Everything here is a gatherable rather than a fish, which is the other thing these can
/// hold, because this plugin deliberately does not rank fish.
/// </remarks>
public static class GatherList
{
    /// <summary>The gather window preset format this was written against.</summary>
    private const byte WindowVersion = 1;

    /// <summary>The auto-gather list format this was written against.</summary>
    private const byte ListVersion = 5;

    /// <summary>GatherBuddyReborn's ObjectType: invalid, gatherable, fish.</summary>
    private const int Gatherable = 1;

    /// <summary>
    /// An auto-gather list, which is the one auto-gather works through.
    /// </summary>
    /// <param name="wanted">
    /// How many of each to gather. Nought means no particular number, which is what the far
    /// side reads a missing entry as.
    /// </param>
    public static string ForAutoGather(
        string name,
        string description,
        IReadOnlyDictionary<uint, int> wanted)
    {
        var ids = wanted.Keys.ToArray();

        return Encode(ListVersion, new AutoGatherList(
            ids,
            wanted.ToDictionary(entry => entry.Key, entry => (uint)Math.Max(0, entry.Value)),
            [],
            ids.ToDictionary(id => id, _ => true),
            name,
            description,
            "",
            0,
            true,
            false));
    }

    public static string Build(string name, string description, IEnumerable<uint> itemIds)
    {
        var ids = itemIds.Distinct().ToArray();

        var json = JsonSerializer.Serialize(new Preset(
            ids,
            [.. ids.Select(_ => Gatherable)],
            name,
            description,
            true));

        return Encode(WindowVersion, json);
    }

    /// <summary>Gzipped JSON behind a version byte, which is what both importers read.</summary>
    private static string Encode<T>(byte version, T payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload as string ?? JsonSerializer.Serialize(payload));

        using var compressed = new MemoryStream();

        using (var zip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            zip.WriteByte(version);
            zip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(compressed.ToArray());
    }

    /// <summary>
    /// The auto-gather list as the other plugin's reader expects to find it.
    /// </summary>
    /// <remarks>
    /// Names and casing mirror its own struct, including the misspelling of preferred, because
    /// they are what its reader matches on and a corrected spelling would simply not be found.
    /// </remarks>
    private sealed record AutoGatherList(
        uint[] ItemIds,
        Dictionary<uint, uint> Quantities,
        Dictionary<uint, uint> PrefferedLocations,
        Dictionary<uint, bool> EnabledItems,
        string Name,
        string Description,
        string FolderPath,
        int Order,
        bool Enabled,
        bool Fallback);

    /// <summary>
    /// The preset as the other plugin's serializer expects to find it.
    /// </summary>
    /// <remarks>
    /// Names and casing mirror its own class, since they are what its reader matches on.
    /// </remarks>
    /// <remarks>
    /// The types are written as numbers rather than bytes on purpose: a byte array serialises
    /// as a base64 string here and as an array of enums over there, so the obvious type is the
    /// wrong one and fails quietly at the far end.
    /// </remarks>
    private sealed record Preset(
        uint[] ItemIds,
        int[] ItemTypes,
        string Name,
        string Description,
        bool Enabled);
}
