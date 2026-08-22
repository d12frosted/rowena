using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Rowena.Core.Lists;

/// <summary>
/// Builds the string GatherBuddyReborn's gather window accepts.
/// </summary>
/// <remarks>
/// Rowena works out what is worth gathering and has no business gathering it, the same
/// division as the crafting list and Artisan. What was missing was a way to hand the list
/// over: GatherBuddyReborn's IPC offers a version, a name lookup and a switch for its
/// auto-gather, and nothing at all for managing what is in the list.
///
/// Its own import does, though. A gather window preset travels as gzipped JSON behind a
/// version byte, base64 encoded, which is a format rather than an interface: it can change
/// under us without anybody's build breaking, so the version byte is written out and the
/// shape is pinned by a test that takes the string apart again.
///
/// Everything here is a gatherable rather than a fish, which is the other thing the preset
/// can hold, because this plugin deliberately does not rank fish.
/// </remarks>
public static class GatherList
{
    /// <summary>The preset format this was written against.</summary>
    private const byte CurrentVersion = 1;

    /// <summary>GatherBuddyReborn's ObjectType: invalid, gatherable, fish.</summary>
    private const int Gatherable = 1;

    public static string Build(string name, string description, IEnumerable<uint> itemIds)
    {
        var ids = itemIds.Distinct().ToArray();

        var json = JsonSerializer.Serialize(new Preset(
            ids,
            [.. ids.Select(_ => Gatherable)],
            name,
            description,
            true));

        var bytes = Encoding.UTF8.GetBytes(json);

        using var compressed = new MemoryStream();

        using (var zip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            zip.WriteByte(CurrentVersion);
            zip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(compressed.ToArray());
    }

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
