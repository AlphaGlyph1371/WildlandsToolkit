using System;
using System.Collections.Generic;
using System.IO;

namespace Wildlands.Formats.Weather;

public sealed class TimeCycle
{
    public const uint ClassHash = 0xF0C85018;         // TimeOfDayPropertyControllerData
    public const uint WeatherClassHash = 0x887004DC;  // WeatherPropertyControllerData

    // A key at 2880 is midnight again; 2880 / 24 = 120 units per hour
    public const int DayLength = 2880;

    // A weather curve uses the same 120 units per step, but runs over 100 steps instead of 24
    // hours: every one of its keys ends at 12000, never at 2880.
    public const int WeatherLength = 12000;

    public ulong Id { get; private set; }
    public uint Type { get; private set; }
    public List<TimeCycleEntry> Entries { get; } = [];

    public static bool IsTimeCycle(uint classHash) => classHash == ClassHash || classHash == WeatherClassHash;

    public bool IsWeather => Type == WeatherClassHash;

    public int Length => IsWeather ? WeatherLength : DayLength;

    public static TimeCycle Read(byte[] resource)
    {
        using var reader = new BinaryReader(new MemoryStream(resource));

        var cycle = new TimeCycle { Id = reader.ReadUInt64(), Type = reader.ReadUInt32() };
        if (!IsTimeCycle(cycle.Type))
            throw new InvalidDataException($"Not a time cycle (class 0x{cycle.Type:X8}).");

        reader.ReadByte();

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            cycle.Entries.Add(TimeCycleEntry.Read(reader));

        if (reader.BaseStream.Position != resource.Length)
            throw new InvalidDataException($"{resource.Length - reader.BaseStream.Position} bytes left over.");

        return cycle;
    }

    public byte[] Write()
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Id);
            writer.Write(Type);
            writer.Write((byte)1);
            writer.Write(Entries.Count);

            uint next = 0xF8000000;
            foreach (var entry in Entries)
                entry.Write(writer, ref next);
        }
        return stream.ToArray();
    }
}

public enum CurveKind
{
    Float,    // 0xCFD66AAD
    Color,    // 0x8BE9C203, four floats, RGBA
    Vector3,  // 0x158D57A0
    Byte,     // 0x5841BD2B, values 0, 1 and 2 all occur
}

public readonly record struct PropertyStep(uint NameHash, ushort ArrayIndex)
{
    public const ushort NotAnArray = 0xFFFF;

    public bool IsArrayElement => ArrayIndex != NotAnArray;
}

public sealed class TimeCycleEntry
{
    const uint EntryClass = 0x050A3620;  // PropertyControllerEntry
    const uint PathClass = 0x3C00B2E0;   // PropertyPath
    const uint StepClass = 0x1A8750BA;   // PropertyPathNode

    const ulong ExternalId = 0x0000010000000000;

    public List<PropertyStep> Path { get; } = [];
    public CurveKind Kind { get; private set; }

    public ulong Handle { get; private set; }

    public List<ushort> Times { get; } = [];

    public List<float[]> Values { get; } = [];

    public int Components => Kind == CurveKind.Color ? 4 : Kind == CurveKind.Vector3 ? 3 : 1;

    internal static TimeCycleEntry Read(BinaryReader reader)
    {
        var entry = new TimeCycleEntry();

        ReadHeader(reader, EntryClass);
        ReadHeader(reader, PathClass);

        int depth = reader.ReadInt32();
        for (int i = 0; i < depth; i++)
        {
            ReadHeader(reader, StepClass);

            ushort index = reader.ReadUInt16();
            reader.ReadBytes(3);
            reader.ReadUInt64();                 // ExternalId
            entry.Path.Add(new PropertyStep(reader.ReadUInt32(), index));
            reader.ReadBytes(24);
            reader.ReadByte();                   // tag 3, an empty reference

            if (i == depth - 1)
            {
                reader.ReadUInt16();
                entry.Handle = reader.ReadUInt64();
                reader.ReadByte();
            }
        }

        entry.Kind = KindOf(ReadHeader(reader, 0));

        int keys = reader.ReadInt32();
        entry.Times.Add(0);
        for (int i = 1; i < keys; i++)
            entry.Times.Add(reader.ReadUInt16());

        int components = entry.Components;
        for (int i = 0; i < keys; i++)
        {
            var value = new float[components];
            for (int c = 0; c < components; c++)
                value[c] = entry.Kind == CurveKind.Byte ? reader.ReadByte() : reader.ReadSingle();
            entry.Values.Add(value);
        }

        reader.ReadByte();
        reader.ReadInt32();

        return entry;
    }

    internal void Write(BinaryWriter writer, ref uint nextId)
    {
        WriteHeader(writer, ref nextId, EntryClass);
        WriteHeader(writer, ref nextId, PathClass);
        writer.Write(Path.Count);

        for (int i = 0; i < Path.Count; i++)
        {
            WriteHeader(writer, ref nextId, StepClass);

            writer.Write(Path[i].ArrayIndex);
            writer.Write(new byte[3]);
            writer.Write(ExternalId);
            writer.Write(Path[i].NameHash);
            writer.Write(new byte[24]);
            writer.Write((byte)3);

            if (i == Path.Count - 1)
            {
                writer.Write((ushort)0);
                writer.Write(Handle);
                writer.Write((byte)0);
            }
        }

        WriteHeader(writer, ref nextId, ClassOf(Kind));
        writer.Write(Values.Count);

        for (int i = 1; i < Times.Count; i++)
            writer.Write(Times[i]);

        foreach (var value in Values)
            foreach (float component in value)
            {
                if (Kind == CurveKind.Byte)
                    writer.Write((byte)component);
                else
                    writer.Write(component);
            }

        writer.Write((byte)1);
        writer.Write(0);
    }

    static uint ReadHeader(BinaryReader reader, uint expected)
    {
        reader.ReadUInt64();
        uint hash = reader.ReadUInt32();

        if (expected != 0 && hash != expected)
            throw new InvalidDataException($"Expected class 0x{expected:X8}, found 0x{hash:X8}.");

        return hash;
    }

    static void WriteHeader(BinaryWriter writer, ref uint nextId, uint classHash)
    {
        writer.Write((ulong)nextId++);
        writer.Write(classHash);
    }

    static CurveKind KindOf(uint classHash) => classHash switch
    {
        0xCFD66AAD => CurveKind.Float,
        0x8BE9C203 => CurveKind.Color,
        0x158D57A0 => CurveKind.Vector3,
        0x5841BD2B => CurveKind.Byte,
        _ => throw new InvalidDataException($"Unknown curve class 0x{classHash:X8}."),
    };

    static uint ClassOf(CurveKind kind) => kind switch
    {
        CurveKind.Float => 0xCFD66AAD,
        CurveKind.Color => 0x8BE9C203,
        CurveKind.Vector3 => 0x158D57A0,
        _ => 0x5841BD2B,
    };

    public string PathText()
    {
        var text = new System.Text.StringBuilder();
        foreach (var step in Path)
        {
            if (text.Length > 0)
                text.Append('.');
            text.Append(PropertyNames.Name(step.NameHash));
            if (step.IsArrayElement)
                text.Append('[').Append(step.ArrayIndex).Append(']');
        }
        return text.ToString();
    }
}
