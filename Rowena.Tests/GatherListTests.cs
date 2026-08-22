using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rowena.Core.Lists;
using Xunit;

namespace Rowena.Tests;

public class GatherListTests
{
    /// <summary>Takes the string apart the way the other plugin does: base64, gunzip, version, JSON.</summary>
    private static JsonElement Decode(string encoded)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
        using var zip = new GZipStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zip.CopyTo(plain);

        var bytes = plain.ToArray();

        Assert.Equal(1, bytes[0]);

        return JsonDocument.Parse(Encoding.UTF8.GetString(bytes.AsSpan()[1..])).RootElement;
    }

    [Fact]
    public void AListTravelsAsAVersionedGzippedPreset()
    {
        var preset = Decode(GatherList.Build("Rowena", "worth gathering", [7767, 12871]));

        Assert.Equal("Rowena", preset.GetProperty("Name").GetString());
        Assert.Equal("worth gathering", preset.GetProperty("Description").GetString());
        Assert.True(preset.GetProperty("Enabled").GetBoolean());
        Assert.Equal([7767, 12871], preset.GetProperty("ItemIds").EnumerateArray().Select(id => id.GetUInt32()));
    }

    [Fact]
    public void EverythingIsAGatherableRatherThanAFish()
    {
        // The preset can hold fish, and this plugin deliberately does not rank them, so the
        // type beside every id says gatherable.
        var preset = Decode(GatherList.Build("Rowena", "", [7767, 12871, 5116]));

        Assert.Equal([1, 1, 1], preset.GetProperty("ItemTypes").EnumerateArray().Select(t => t.GetInt32()));
    }

    [Fact]
    public void TheSameThingTwiceIsListedOnce()
    {
        var preset = Decode(GatherList.Build("Rowena", "", [7767, 7767, 12871]));

        Assert.Equal(2, preset.GetProperty("ItemIds").GetArrayLength());
        Assert.Equal(2, preset.GetProperty("ItemTypes").GetArrayLength());
    }

    [Fact]
    public void AnEmptyListIsStillAValidPreset()
    {
        var preset = Decode(GatherList.Build("Rowena", "", []));

        Assert.Equal(0, preset.GetProperty("ItemIds").GetArrayLength());
    }
}
