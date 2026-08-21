using System.Text;

namespace Rowena.Core.Universalis;

/// <summary>
/// Just enough BSON to talk to Universalis, and no more.
/// </summary>
/// <remarks>
/// The websocket speaks BSON in both directions, which normally means taking a dependency on
/// a Mongo driver to read four field types. What is actually needed is a document of two
/// strings going out and a document of numbers, strings and arrays coming back, which is a
/// page of code and no dependency at all. Anything this does not understand throws rather
/// than being skipped, since a silently misread frame is worse than a loud one.
///
/// Deliberately untyped on the way in: the feed's shapes are somebody else's and change
/// without notice, so they are read as a dictionary and interrogated by whoever cares.
/// </remarks>
public static class Bson
{
    public static byte[] Document(params (string Name, string Value)[] fields)
    {
        using var body = new MemoryStream();
        foreach (var (name, value) in fields)
        {
            body.WriteByte(0x02);
            body.Write(Encoding.UTF8.GetBytes(name));
            body.WriteByte(0);
            var bytes = Encoding.UTF8.GetBytes(value);
            body.Write(BitConverter.GetBytes(bytes.Length + 1));
            body.Write(bytes);
            body.WriteByte(0);
        }

        var payload = body.ToArray();
        using var whole = new MemoryStream();
        whole.Write(BitConverter.GetBytes(payload.Length + 5));
        whole.Write(payload);
        whole.WriteByte(0);
        return whole.ToArray();
    }

    public static Dictionary<string, object?> Read(ReadOnlySpan<byte> data)
    {
        var at = 0;
        return ReadDocument(data, ref at);
    }

    private static Dictionary<string, object?> ReadDocument(ReadOnlySpan<byte> data, ref int at)
    {
        var length = BitConverter.ToInt32(data[at..]);
        var end = at + length;
        at += 4;

        var result = new Dictionary<string, object?>();

        while (at < end - 1)
        {
            var type = data[at++];
            var name = ReadCString(data, ref at);
            result[name] = ReadValue(data, ref at, type);
        }

        at = end;
        return result;
    }

    private static object? ReadValue(ReadOnlySpan<byte> data, ref int at, byte type)
    {
        switch (type)
        {
            case 0x01: { var v = BitConverter.ToDouble(data[at..]); at += 8; return v; }
            case 0x02:
            {
                var len = BitConverter.ToInt32(data[at..]); at += 4;
                var s = Encoding.UTF8.GetString(data.Slice(at, len - 1)); at += len; return s;
            }
            case 0x03: return ReadDocument(data, ref at);
            case 0x04:
            {
                var doc = ReadDocument(data, ref at);
                return doc.Values.ToList();
            }
            case 0x05:
            {
                var len = BitConverter.ToInt32(data[at..]); at += 4;
                at += 1;
                var bytes = data.Slice(at, len).ToArray(); at += len; return bytes;
            }
            case 0x08: return data[at++] != 0;
            case 0x09: { var v = BitConverter.ToInt64(data[at..]); at += 8; return v; }
            case 0x0A: return null;
            case 0x10: { var v = BitConverter.ToInt32(data[at..]); at += 4; return v; }
            case 0x11:
            case 0x12: { var v = BitConverter.ToInt64(data[at..]); at += 8; return v; }
            default: throw new InvalidDataException($"BSON type 0x{type:X2} not handled");
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int at)
    {
        var start = at;
        while (data[at] != 0) at++;
        var s = Encoding.UTF8.GetString(data[start..at]);
        at++;
        return s;
    }
}
