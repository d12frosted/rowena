using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rowena.Core.Lists;
using Xunit;

namespace Rowena.Tests;

public class GatherListTests
{
    /// <summary>Takes the string apart the way the other plugin does: base64, gunzip, version, JSON.</summary>
    private static JsonElement Decode(string encoded, byte version = 1)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
        using var zip = new GZipStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zip.CopyTo(plain);

        var bytes = plain.ToArray();

        Assert.Equal(version, bytes[0]);

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
    public void AnAutoGatherListCarriesHowManyOfEachToGather()
    {
        // The reason this format is the one worth having: the plan works out amounts and the
        // gather window preset has nowhere to put them.
        var list = Decode(
            GatherList.ForAutoGather("Rowena", "an hour", new Dictionary<uint, int> { [7767] = 40, [12871] = 25 }),
            version: 5);

        Assert.Equal(40u, list.GetProperty("Quantities").GetProperty("7767").GetUInt32());
        Assert.Equal(25u, list.GetProperty("Quantities").GetProperty("12871").GetUInt32());
        Assert.Equal([7767, 12871], list.GetProperty("ItemIds").EnumerateArray().Select(id => id.GetUInt32()));
    }

    [Fact]
    public void EveryItemOnAnAutoGatherListArrivesSwitchedOn()
    {
        // A list whose items are all off gathers nothing, and the far side only fills them in
        // for itself when the field is missing entirely.
        var list = Decode(
            GatherList.ForAutoGather("Rowena", "", new Dictionary<uint, int> { [7767] = 1 }),
            version: 5);

        Assert.True(list.GetProperty("EnabledItems").GetProperty("7767").GetBoolean());
        Assert.True(list.GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void TheAutoGatherListCarriesEveryFieldItsReaderRefusesToBeWithout()
    {
        // Its reader returns false outright when any of these is missing, and a misspelling it
        // shipped has to be matched rather than corrected.
        var list = Decode(GatherList.ForAutoGather("Rowena", "", new Dictionary<uint, int> { [1] = 1 }), version: 5);

        foreach (var field in new[] { "ItemIds", "Quantities", "PrefferedLocations", "Name", "Description" })
            Assert.True(list.TryGetProperty(field, out _), $"{field} is missing");
    }

    [Fact]
    public void AnEmptyListIsStillAValidPreset()
    {
        var preset = Decode(GatherList.Build("Rowena", "", []));

        Assert.Equal(0, preset.GetProperty("ItemIds").GetArrayLength());
    }
}
