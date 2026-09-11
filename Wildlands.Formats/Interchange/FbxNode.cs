using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Models;

sealed class FbxNode(string name)
{
    readonly MemoryStream properties = new();
    readonly List<FbxNode> children = [];
    int propertyCount;

    public FbxNode Add(string name)
    {
        var child = new FbxNode(name);
        children.Add(child);
        return child;
    }

    public FbxNode Property(int value) => Property('I', BitConverter.GetBytes(value));
    public FbxNode Property(long value) => Property('L', BitConverter.GetBytes(value));
    public FbxNode Property(double value) => Property('D', BitConverter.GetBytes(value));

    public FbxNode Property(string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        var bytes = new byte[4 + text.Length];

        BitConverter.GetBytes(text.Length).CopyTo(bytes, 0);
        text.CopyTo(bytes, 4);

        return Property('S', bytes);
    }

    public FbxNode Array(double[] values) => Array('d', values.Length, 8, buffer => Buffer.BlockCopy(values, 0, buffer, 0, buffer.Length));
    public FbxNode Array(int[] values) => Array('i', values.Length, 4, buffer => Buffer.BlockCopy(values, 0, buffer, 0, buffer.Length));

    FbxNode Array(char type, int count, int itemSize, Action<byte[]> fill)
    {
        var contents = new byte[count * itemSize];
        fill(contents);

        var bytes = new byte[12 + contents.Length];
        BitConverter.GetBytes(count).CopyTo(bytes, 0);
        BitConverter.GetBytes(0).CopyTo(bytes, 4); // stored as is
        BitConverter.GetBytes(contents.Length).CopyTo(bytes, 8);
        contents.CopyTo(bytes, 12);

        return Property(type, bytes);
    }

    FbxNode Property(char type, byte[] bytes)
    {
        properties.WriteByte((byte)type);
        properties.Write(bytes);
        propertyCount++;
        return this;
    }

    public void Write(BinaryWriter writer)
    {
        var stream = writer.BaseStream;
        long start = stream.Position;
        var text = Encoding.UTF8.GetBytes(name);

        writer.Write(0u); // the end offset, filled in below
        writer.Write((uint)propertyCount);
        writer.Write((uint)properties.Length);
        writer.Write((byte)text.Length);
        writer.Write(text);
        properties.WriteTo(stream);

        foreach (var child in children)
            child.Write(writer);

        if (children.Count > 0)
            writer.Write(new byte[Terminator]);

        long end = stream.Position;
        stream.Position = start;
        writer.Write((uint)end);
        stream.Position = end;
    }

    // A list of records ends with a record whose header is all zeroes
    public const int Terminator = 13;
}
