using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wildlands.Formats.Models;

public enum BuildTableReferenceKind
{
    TableIdentity,
    Table,
    Handle,
    FileReference,
    ObjectPointer,
    ObjectId,
}

public sealed class BuildTableReference
{
    internal BuildTableReference(int offset, ulong value, BuildTableReferenceKind kind, string path,
        byte? referenceTag = null, byte? globalFlag = null)
    {
        Offset = offset;
        Value = value;
        Kind = kind;
        Path = path;
        ReferenceTag = referenceTag;
        GlobalFlag = globalFlag;
    }

    public int Offset { get; }
    public ulong Value { get; set; }
    public BuildTableReferenceKind Kind { get; }
    public string Path { get; }
    public int? ComponentIndex { get; internal set; }
    public byte? ReferenceTag { get; }
    public byte? GlobalFlag { get; }
    public bool IsContainerLocal => Kind == BuildTableReferenceKind.FileReference && GlobalFlag == 0;
}

public sealed record BuildTableObject(int Offset, ulong Id, uint ClassHash, string Path);

public sealed class BuildTableTagEntry
{
    internal BuildTableTagEntry(int headerOffset, int valueOffset, ulong objectId, uint value, string path)
    {
        HeaderOffset = headerOffset;
        ValueOffset = valueOffset;
        ObjectId = objectId;
        Value = value;
        Path = path;
    }

    public int HeaderOffset { get; }
    public int ValueOffset { get; }
    public ulong ObjectId { get; }
    public uint Value { get; internal set; }
    public string Path { get; }
}

public sealed class BuildTableTagList
{
    internal BuildTableTagList(int headerOffset, int countOffset, ulong objectId, IReadOnlyList<BuildTableTagEntry> entries, string path)
    {
        HeaderOffset = headerOffset;
        CountOffset = countOffset;
        ObjectId = objectId;
        Entries = entries;
        Path = path;
    }

    public int HeaderOffset { get; }
    public int CountOffset { get; }
    public ulong ObjectId { get; }
    public IReadOnlyList<BuildTableTagEntry> Entries { get; }
    public string Path { get; }
}

public sealed class BuildTableRow
{
    internal BuildTableRow(int index, int offset, int length, ulong id, IReadOnlyList<BuildTableReference> references, IReadOnlyList<BuildTableObject> objects, BuildTableTagEntry tag, BuildTableTagList possibleTags)
    {
        Index = index;
        Offset = offset;
        Length = length;
        Id = id;
        References = references;
        Objects = objects;
        TagEntry = tag;
        PossibleTagList = possibleTags;
    }

    public int Index { get; }
    public int Offset { get; }
    public int Length { get; }
    public ulong Id { get; }
    public IReadOnlyList<BuildTableReference> References { get; }
    public IReadOnlyList<BuildTableObject> Objects { get; }
    public BuildTableTagEntry TagEntry { get; }
    public BuildTableTagList PossibleTagList { get; }
    public int TagOffset => TagEntry.ValueOffset;
    public uint Tag => TagEntry.Value;
    public IReadOnlyList<BuildTableTagEntry> PossibleTagEntries => PossibleTagList.Entries;
    public IReadOnlyList<uint> PossibleTags => PossibleTagEntries.Select(entry => entry.Value).ToList();
}

public sealed class BuildTableAsset
{
    readonly byte[] _originalData;

    internal BuildTableAsset(byte[] originalData) => _originalData = originalData;

    public ulong Id { get; internal set; }
    public byte Prefix { get; internal set; }
    public int ColumnCount { get; internal set; }
    public int RowCount { get; internal set; }
    public int RowCountOffset { get; internal set; }
    public List<BuildTableReference> References { get; } = [];
    public List<BuildTableObject> Objects { get; } = [];
    public List<BuildTableRow> Rows { get; } = [];
    public List<BuildTableTagList> BuildTagLists { get; } = [];

    public byte[] Write()
    {
        byte[] result = (byte[])_originalData.Clone();
        foreach (var row in Rows)
        {
            WriteTag(result, row.TagEntry);
            foreach (BuildTableTagEntry entry in row.PossibleTagEntries)
                WriteTag(result, entry);
        }
        foreach (var reference in References)
        {
            if ((uint)reference.Offset > result.Length - sizeof(ulong))
                throw new InvalidDataException($"BuildTable reference offset 0x{reference.Offset:X} is outside the resource.");
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(reference.Offset, sizeof(ulong)), reference.Value);
        }
        return result;
    }

    public int ReplaceReference(ulong oldValue, ulong newValue)
    {
        int changed = 0;
        foreach (var reference in References)
        {
            if (reference.Kind == BuildTableReferenceKind.TableIdentity || reference.Value != oldValue)
                continue;
            reference.Value = newValue;
            changed++;
        }
        return changed;
    }

    public byte[] DuplicateRow(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));

        byte[] source = Write();
        var row = Rows[rowIndex];
        if (!CanDuplicateRow(rowIndex, out string reason))
            throw new InvalidOperationException(reason);
        byte[] clone = source.AsSpan(row.Offset, row.Length).ToArray();
        int insertAt = row.Offset + row.Length;
        var localObjects = Objects.Where(obj => IsLocalId(obj.Id)).OrderBy(obj => obj.Offset).ToList();
        if (localObjects.Zip(localObjects.Skip(1)).Any(pair => pair.First.Id >= pair.Second.Id))
            throw new InvalidOperationException("The BuildTable local object ids are not ordered, so a row cannot be inserted safely.");

        var rowIds = row.Objects.Where(obj => IsLocalId(obj.Id))
            .OrderBy(obj => obj.Offset).Select(obj => obj.Id).Distinct().ToList();
        if (rowIds.Count == 0)
            throw new InvalidOperationException("The selected row has no local object ids to clone.");
        ulong firstCloneId = localObjects.Where(obj => obj.Offset < insertAt).Max(obj => obj.Id) + 1;
        var remapped = rowIds.Select((id, index) => (id, NewId: firstCloneId + (ulong)index))
            .ToDictionary(pair => pair.id, pair => pair.NewId);

        ulong shift = (ulong)rowIds.Count;
        var shifted = localObjects.Where(obj => obj.Offset >= insertAt)
            .ToDictionary(obj => obj.Id, obj => checked(obj.Id + shift));
        if (shifted.Values.Any(id => !IsLocalId(id)))
            throw new InvalidOperationException("The BuildTable has no free local object ids.");
        byte[] shiftedSource = (byte[])source.Clone();
        foreach (BuildTableObject obj in Objects)
            if (shifted.TryGetValue(obj.Id, out ulong newId))
                BinaryPrimitives.WriteUInt64LittleEndian(shiftedSource.AsSpan(obj.Offset), newId);
        foreach (BuildTableReference reference in References)
            if (shifted.TryGetValue(reference.Value, out ulong newId))
                BinaryPrimitives.WriteUInt64LittleEndian(shiftedSource.AsSpan(reference.Offset), newId);

        foreach (var obj in row.Objects)
            if (remapped.TryGetValue(obj.Id, out ulong newId))
                BinaryPrimitives.WriteUInt64LittleEndian(clone.AsSpan(obj.Offset - row.Offset, sizeof(ulong)), newId);

        foreach (var reference in row.References)
            if (remapped.TryGetValue(reference.Value, out ulong newId))
                BinaryPrimitives.WriteUInt64LittleEndian(clone.AsSpan(reference.Offset - row.Offset, sizeof(ulong)), newId);

        byte[] result = new byte[checked(shiftedSource.Length + clone.Length)];
        shiftedSource.AsSpan(0, insertAt).CopyTo(result);
        clone.CopyTo(result.AsSpan(insertAt));
        shiftedSource.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + clone.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(RowCountOffset, sizeof(int)), RowCount + 1);

        var parsed = BuildTable.Read(result);
        if (parsed.RowCount != RowCount + 1 || parsed.Rows.Count != Rows.Count + 1)
            throw new InvalidDataException("The duplicated BuildTable row did not parse back correctly.");
        if (parsed.Rows[rowIndex + 1].Id == row.Id)
            throw new InvalidDataException("The duplicated BuildTable row did not receive a fresh local id.");
        var duplicateId = parsed.Objects.Where(obj => IsLocalId(obj.Id))
            .GroupBy(obj => obj.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
            throw new InvalidDataException($"The duplicated BuildTable contains local object id 0x{duplicateId.Key:X} more than once.");
        var parsedLocalObjects = parsed.Objects.Where(obj => IsLocalId(obj.Id))
            .OrderBy(obj => obj.Offset).ToList();
        if (parsedLocalObjects.Zip(parsedLocalObjects.Skip(1))
            .Any(pair => pair.First.Id >= pair.Second.Id))
            throw new InvalidDataException("The duplicated BuildTable does not retain ordered local object ids.");
        var defined = parsedLocalObjects.Select(obj => obj.Id).ToHashSet();
        if (parsed.References.Any(reference => IsLocalId(reference.Value) && !defined.Contains(reference.Value)))
            throw new InvalidDataException("The duplicated BuildTable contains a local reference without an object.");
        return result;
    }

    public void SetRowTag(int rowIndex, uint tag)
    {
        if ((uint)rowIndex >= (uint)Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        Rows[rowIndex].TagEntry.Value = tag;
    }

    public void SetPossibleTag(int rowIndex, int tagIndex, uint tag)
    {
        if ((uint)rowIndex >= (uint)Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        if ((uint)tagIndex >= (uint)Rows[rowIndex].PossibleTagEntries.Count)
            throw new ArgumentOutOfRangeException(nameof(tagIndex));
        Rows[rowIndex].PossibleTagEntries[tagIndex].Value = tag;
    }

    public void ReplacePossibleTag(int rowIndex, uint oldTag, uint newTag)
    {
        if ((uint)rowIndex >= (uint)Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        var matches = Rows[rowIndex].PossibleTagEntries.Where(entry => entry.Value == oldTag).ToList();
        if (matches.Count != 1)
            throw new InvalidDataException($"Row {rowIndex} contains {matches.Count} possible-tag entries with value 0x{oldTag:X8}; exactly one is required.");
        matches[0].Value = newTag;
    }

    public byte[] AddPossibleTag(int rowIndex, int templateTagIndex, uint newTag)
    {
        if ((uint)rowIndex >= (uint)Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        BuildTableTagList list = Rows[rowIndex].PossibleTagList;
        if ((uint)templateTagIndex >= (uint)list.Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(templateTagIndex));
        return AddBuildTag(list, list.Entries[templateTagIndex].Value, newTag);
    }

    public bool TryAddBuildTag(uint templateTag, uint newTag, out byte[] updated)
    {
        var lists = BuildTagLists.Where(list => list.Entries.Any(entry => entry.Value == templateTag)).ToList();
        if (lists.Count == 0)
        {
            updated = Write();
            return false;
        }
        if (lists.Count != 1)
            throw new InvalidDataException($"The BuildTable contains {lists.Count} BuildTags lists with template tag 0x{templateTag:X8}; exactly one is required.");
        updated = AddBuildTag(lists[0], templateTag, newTag);
        return true;
    }

    public bool TryAddBuildTagToAll(uint templateTag, uint newTag, out byte[] updated)
    {
        var listIndexes = BuildTagLists.Select((list, index) => (list, index))
            .Where(item => item.list.Entries.Any(entry => entry.Value == templateTag))
            .Select(item => item.index)
            .ToList();
        if (listIndexes.Count == 0)
        {
            updated = Write();
            return false;
        }
        if (listIndexes.Any(index => BuildTagLists[index].Entries.Any(entry => entry.Value == newTag)))
            throw new InvalidOperationException($"BuildTag 0x{newTag:X8} already exists in a matching BuildTags list.");

        updated = Write();
        foreach (int listIndex in listIndexes)
        {
            BuildTableAsset current = BuildTable.Read(updated);
            updated = current.AddBuildTag(current.BuildTagLists[listIndex], templateTag, newTag);
        }

        BuildTableAsset checkedTable = BuildTable.Read(updated);
        if (listIndexes.Any(index => checkedTable.BuildTagLists[index].Entries.Count(entry => entry.Value == newTag) != 1))
            throw new InvalidDataException("The new BuildTag was not retained in every matching BuildTags list.");
        return true;
    }

    public bool CanDuplicateRow(int rowIndex, out string reason)
    {
        if ((uint)rowIndex >= (uint)Rows.Count)
        {
            reason = "The option does not exist.";
            return false;
        }

        var fixedObject = Rows[rowIndex].Objects
            .FirstOrDefault(obj => obj.Id != 0 && !IsLocalId(obj.Id));
        if (fixedObject is not null)
        {
            reason = $"{fixedObject.Path} uses a fixed object id, so this option cannot be duplicated safely.";
            return false;
        }

        reason = "";
        return true;
    }

    public byte[] RemoveRow(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));

        var row = Rows[rowIndex];
        var localIds = row.Objects.Where(obj => IsLocalId(obj.Id)).Select(obj => obj.Id).ToHashSet();
        var outsideLink = References.FirstOrDefault(reference => (reference.Offset < row.Offset || reference.Offset >= row.Offset + row.Length) && localIds.Contains(reference.Value));
        if (outsideLink is not null)
            throw new InvalidOperationException($"This option is used by {outsideLink.Path} and cannot be removed safely.");

        byte[] source = Write();
        byte[] result = new byte[checked(source.Length - row.Length)];
        source.AsSpan(0, row.Offset).CopyTo(result);
        source.AsSpan(row.Offset + row.Length).CopyTo(result.AsSpan(row.Offset));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(RowCountOffset, sizeof(int)), RowCount - 1);

        var parsed = BuildTable.Read(result);
        if (parsed.RowCount != RowCount - 1 || parsed.Rows.Count != Rows.Count - 1)
            throw new InvalidDataException("The BuildTable did not parse correctly after removing the row.");
        return result;
    }

    byte[] AddBuildTag(BuildTableTagList list, uint templateTag, uint newTag)
    {
        int listIndex = BuildTagLists.IndexOf(list);
        if (listIndex < 0)
            throw new ArgumentException("The BuildTags list does not belong to this BuildTable.", nameof(list));
        if (list.Entries.Any(entry => entry.Value == newTag))
            throw new InvalidOperationException($"BuildTag 0x{newTag:X8} already exists in this BuildTags list.");

        var templates = list.Entries.Where(entry => entry.Value == templateTag).ToList();
        if (templates.Count != 1)
            throw new InvalidDataException($"The selected BuildTags list contains {templates.Count} entries with template tag 0x{templateTag:X8}; exactly one is required.");
        BuildTableTagEntry template = templates[0];
        int templateIndex = list.Entries.ToList().IndexOf(template);
        int insertAt = template.HeaderOffset + 16;
        ulong newObjectId = checked(template.ObjectId + 1);
        if (!IsLocalId(newObjectId))
            throw new InvalidOperationException("The BuildTags list has no free local object id.");

        var localObjects = Objects.Where(obj => IsLocalId(obj.Id)).OrderBy(obj => obj.Offset).ToList();
        if (localObjects.Zip(localObjects.Skip(1)).Any(pair => pair.First.Id >= pair.Second.Id))
            throw new InvalidDataException("The BuildTable local object ids are not strictly ordered.");
        if (localObjects.Any(obj => obj.Id >= newObjectId && obj.Id == uint.MaxValue)
            || References.Any(reference => reference.Value >= newObjectId && reference.Value == uint.MaxValue))
            throw new InvalidOperationException("The BuildTable has no free local object id.");

        byte[] source = Write();
        byte[] shifted = (byte[])source.Clone();
        foreach (BuildTableObject obj in Objects.Where(obj => obj.Id >= newObjectId && IsLocalId(obj.Id)))
            BinaryPrimitives.WriteUInt64LittleEndian(shifted.AsSpan(obj.Offset, sizeof(ulong)), checked(obj.Id + 1));
        foreach (BuildTableReference reference in References.Where(reference => reference.Value >= newObjectId && IsLocalId(reference.Value)))
            BinaryPrimitives.WriteUInt64LittleEndian(shifted.AsSpan(reference.Offset, sizeof(ulong)), checked(reference.Value + 1));

        byte[] result = new byte[checked(shifted.Length + 16)];
        shifted.AsSpan(0, insertAt).CopyTo(result);
        source.AsSpan(template.HeaderOffset, 16).CopyTo(result.AsSpan(insertAt));
        shifted.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + 16));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(list.CountOffset, sizeof(int)), list.Entries.Count + 1);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(insertAt, sizeof(ulong)), newObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(insertAt + 12, sizeof(uint)), newTag);

        BuildTableAsset parsed = BuildTable.Read(result);
        if (parsed.Id != Id || parsed.RowCount != RowCount || parsed.BuildTagLists.Count != BuildTagLists.Count)
            throw new InvalidDataException("The BuildTable structure changed while inserting a BuildTag.");
        BuildTableTagList checkedList = parsed.BuildTagLists[listIndex];
        if (checkedList.Path != list.Path || checkedList.Entries.Count != list.Entries.Count + 1 || checkedList.Entries[templateIndex + 1].Value != newTag)
            throw new InvalidDataException("The BuildTags list did not retain the inserted tag.");
        ValidateLocalIds(parsed);
        return result;
    }

    static void WriteTag(byte[] destination, BuildTableTagEntry entry)
    {
        if ((uint)entry.ValueOffset > destination.Length - sizeof(uint))
            throw new InvalidDataException($"BuildTable tag offset 0x{entry.ValueOffset:X} is outside the resource.");
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(entry.ValueOffset, sizeof(uint)), entry.Value);
    }

    static void ValidateLocalIds(BuildTableAsset table)
    {
        var objects = table.Objects.Where(obj => IsLocalId(obj.Id)).OrderBy(obj => obj.Offset).ToList();
        if (objects.Zip(objects.Skip(1)).Any(pair => pair.First.Id >= pair.Second.Id))
            throw new InvalidDataException("The BuildTable does not retain strictly ordered local object ids.");
        var defined = objects.Select(obj => obj.Id).ToHashSet();
        if (table.References.Any(reference => IsLocalId(reference.Value) && !defined.Contains(reference.Value)))
            throw new InvalidDataException("The BuildTable contains a local reference without an object.");
    }

    static bool IsLocalId(ulong id) => id is >= 0xF0000000UL and <= uint.MaxValue;
}

public static class BuildTable
{
    public const uint ClassHash = 0x22ECBE63;

    const uint BuildColumnHash = 0x36839608;
    const uint BuildRowHash = 0x348B28D6;
    const uint PropertyPathHash = 0x3C00B2E0;
    const uint PropertyPathNodeHash = 0x1A8750BA;
    const uint EntityPositionSelectionHash = 0xD198A2B5;
    const uint BuildTagHash = 0xB332698E;
    const uint BuildTagsHash = 0x11BD5345;
    const uint RowSelectorHash = 0xF5BD7B8A;
    const uint DynamicPropertyHash = 0x2CECF817;
    const uint MaterialPropertyPathNodeSolverHash = 0xBC5B87AF;
    const uint Mask16Hash = 0x92B95F74;
    const uint UvTransformHash = 0xC52E2125;
    const uint TagSelectionHash = 0x4ACAEBB4;
    const uint TagSelectionValueHash = 0xB83D2231;
    const uint RowTagSelectionHash = 0x9CF7272E;
    const uint ColumnSelectionHash = 0xC246BB01;
    const uint RowTagSelectionAltHash = 0x3CFFB616;
    const uint ColumnSelectionAltHash = 0x7D08460D;

    const uint BoolType = 0;
    const uint CharType = 1 << 16;
    const uint Signed8Type = 2 << 16;
    const uint Unsigned8Type = 3 << 16;
    const uint Signed16Type = 4 << 16;
    const uint Unsigned16Type = 5 << 16;
    const uint Signed32Type = 6 << 16;
    const uint Unsigned32Type = 7 << 16;
    const uint Signed64Type = 8 << 16;
    const uint Unsigned64Type = 9 << 16;
    const uint FloatType = 10 << 16;
    const uint Vector2Type = 11 << 16;
    const uint Vector3Type = 12 << 16;
    const uint Vector4Type = 13 << 16;
    const uint QuaternionType = 14 << 16;
    const uint Matrix33Type = 15 << 16;
    const uint Matrix44Type = 16 << 16;
    const uint ObjectIdType = 17 << 16;
    const uint HandleType = 18 << 16;
    const uint ObjectType = 19 << 16;
    const uint ObjectPointerType = 20 << 16;
    const uint BaseObjectPointerType = 21 << 16;
    const uint BaseObjectType = 22 << 16;
    const uint EnumType = 25 << 16;
    const uint StringType = 26 << 16;
    const uint LocalizedStringType = 27 << 16;
    const uint ReferenceType = 28 << 16;

    const int MaximumItems = 1_000_000;

    public static BuildTableAsset Read(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var asset = new BuildTableAsset((byte[])resource.Clone());
        var parser = new Parser(resource, asset);
        parser.ReadRoot();
        return asset;
    }

    sealed class Parser
    {
        readonly byte[] _data;
        readonly BuildTableAsset _asset;
        readonly BinaryReader _reader;

        public Parser(byte[] data, BuildTableAsset asset)
        {
            _data = data;
            _asset = asset;
            _reader = new BinaryReader(new MemoryStream(data, writable: false));
        }

        public void ReadRoot()
        {
            var header = ReadHeader(ClassHash, "BuildTable");
            _asset.Id = header.Id;
            _asset.References.Add(new BuildTableReference(0, header.Id, BuildTableReferenceKind.TableIdentity, "BuildTable identity"));
            _asset.Prefix = ReadByte("BuildTable prefix");

            _asset.ColumnCount = ReadCount("BuildTable column");
            for (int i = 0; i < _asset.ColumnCount; i++)
            {
                ReadHeader(BuildColumnHash, $"column {i}");
                ReadColumn(i);
            }

            _asset.RowCountOffset = checked((int)_reader.BaseStream.Position);
            _asset.RowCount = ReadCount("BuildTable row");
            for (int i = 0; i < _asset.RowCount; i++)
            {
                int start = checked((int)_reader.BaseStream.Position);
                int referenceStart = _asset.References.Count;
                int objectStart = _asset.Objects.Count;
                var row = ReadHeader(BuildRowHash, $"row {i}");
                var rowData = ReadRow(i);
                int end = checked((int)_reader.BaseStream.Position);
                _asset.Rows.Add(new BuildTableRow(i, start, end - start, row.Id, _asset.References.Skip(referenceStart).ToList(), _asset.Objects.Skip(objectStart).ToList(), rowData.Tag, rowData.PossibleTags));
            }

            ReadByte("shuffle selection");
            ReadHeader(RowSelectorHash, "default row selector");
            ReadRowSelector();
            ReadBytes(4, "BuildTable flags");
            int subTables = ReadCount("sub-table");
            for (int i = 0; i < subTables; i++)
                ReadHandle($"sub-table {i}");

            if (_reader.BaseStream.Position != _reader.BaseStream.Length)
                throw new InvalidDataException($"BuildTable has {_reader.BaseStream.Length - _reader.BaseStream.Position} unexplained byte(s) at 0x{_reader.BaseStream.Position:X}.");
        }

        void ReadColumn(int index)
        {
            ReadUInt32($"column {index} pass");
            ReadHeader(PropertyPathHash, $"column {index} target property");
            ReadPropertyPath(index);
            ReadInt32($"column {index} index");

            int selections = ReadCount($"column {index} entity-position selection");
            for (int i = 0; i < selections; i++)
            {
                ReadHeader(EntityPositionSelectionHash, $"column {index} entity-position selection {i}");
                ReadBytes(13, $"column {index} entity-position selection {i} data");
            }

            byte hasTable = ReadByte($"column {index} table flag");
            if (hasTable != 3)
                ReadReference(BuildTableReferenceKind.Table, $"column {index} table");

            int components = ReadCount($"column {index} component");
            for (int i = 0; i < components; i++)
            {
                uint marker = ReadUInt32($"column {index} component {i} marker");
                if (marker != DynamicPropertyHash)
                    throw new InvalidDataException($"Column {index} component {i} marker is 0x{marker:X8}, expected 0x{DynamicPropertyHash:X8}.");
                ReadDynamicProperty($"column {index} component {i}");
            }
        }

        void ReadPropertyPath(int column)
        {
            int nodes = ReadCount($"column {column} property-path node");
            for (int i = 0; i < nodes; i++)
            {
                ReadHeader(PropertyPathNodeHash, $"column {column} property-path node {i}");
                ReadPropertyPathNode(column, i);
            }
            ReadBytes(2, $"column {column} property-path flags");
        }

        void ReadPropertyPathNode(int column, int node)
        {
            ReadBytes(41, $"column {column} property-path node {node} data");
            byte solverTag = ReadByte($"column {column} property-path node {node} solver tag");
            if (solverTag == 3)
                return;
            if (solverTag != 0)
                throw new InvalidDataException($"Property-path node solver uses unsupported tag {solverTag}.");

            var solver = ReadHeader(MaterialPropertyPathNodeSolverHash, $"column {column} property-path node {node} material solver");
            ReadByte($"column {column} property-path node {node} material solver blend-mode filter");
            ReadInt32($"column {column} property-path node {node} material solver blend mode");
            ReadBytes(2, $"column {column} property-path node {node} material solver alpha-test flags");
            ReadByte($"column {column} property-path node {node} material solver overrider-match switch");
            ReadInt32($"column {column} property-path node {node} material solver mode");
            ReadByte($"column {column} property-path node {node} material solver flag-mask switch");
            ReadHeader(Mask16Hash, $"column {column} property-path node {node} material solver mask");
            ReadBytes(16, $"column {column} property-path node {node} material solver mask data");
            ReadHandle($"column {column} property-path node {node} target material");
            ReadHandle($"column {column} property-path node {node} target material template");
        }

        (BuildTableTagEntry Tag, BuildTableTagList PossibleTags) ReadRow(int index)
        {
            ReadSingle($"row {index} weight");
            ReadObjectPointer($"row {index} table");
            var tag = ReadBuildTag($"row {index} tag");
            var possibleTags = ReadBuildTags($"row {index} possible tags");

            int components = ReadCount($"row {index} component");
            for (int i = 0; i < components; i++)
            {
                int componentIndex = ReadInt32($"row {index} component {i} index");
                int referenceStart = _asset.References.Count;
                ReadDynamicProperty($"row {index} component {i}");
                foreach (var reference in _asset.References.Skip(referenceStart))
                    reference.ComponentIndex = componentIndex;
            }
            return (tag, possibleTags);
        }

        BuildTableTagEntry ReadBuildTag(string field)
        {
            int headerOffset = checked((int)_reader.BaseStream.Position);
            ScimitarHeader header = ReadHeader(BuildTagHash, field);
            int valueOffset = checked((int)_reader.BaseStream.Position);
            return new BuildTableTagEntry(headerOffset, valueOffset, header.Id, ReadUInt32(field + " value"), field);
        }

        BuildTableTagList ReadBuildTags(string field)
        {
            int headerOffset = checked((int)_reader.BaseStream.Position);
            ScimitarHeader header = ReadHeader(BuildTagsHash, field);
            int countOffset = checked((int)_reader.BaseStream.Position);
            int tags = ReadCount(field + " tag");
            var entries = new List<BuildTableTagEntry>(tags);
            for (int i = 0; i < tags; i++)
                entries.Add(ReadBuildTag($"{field} tag {i}"));
            var result = new BuildTableTagList(headerOffset, countOffset, header.Id, entries, field);
            _asset.BuildTagLists.Add(result);
            return result;
        }

        void ReadRowSelector()
        {
            _ = ReadBuildTags("default selector tags");
            ReadUInt32("default selector column mask");
            ReadUInt32("default selector random seed");

            int selections = ReadCount("default selector selection");
            for (int i = 0; i < selections; i++)
                ReadObjectPointer($"default selector selection {i}");

            ReadHandle("associated entity builder");
            int additional = ReadCount("additional table");
            for (int i = 0; i < additional; i++)
                ReadHandle($"additional table {i}");
        }

        void ReadDynamicProperty(string field)
        {
            uint dataType = ReadUInt32(field + " data type");
            uint type = ReadUInt32(field + " type");
            ReadUInt32(field + " unknown field");
            uint storage = IsStorageType(type) ? type : dataType;

            switch (storage)
            {
                case BoolType:
                case CharType:
                case Signed8Type:
                case Unsigned8Type:
                    ReadByte(field + " value");
                    break;
                case Signed16Type:
                case Unsigned16Type:
                    ReadBytes(2, field + " value");
                    break;
                case Signed32Type:
                case Unsigned32Type:
                case FloatType:
                case EnumType:
                    ReadBytes(4, field + " value");
                    break;
                case Signed64Type:
                case Unsigned64Type:
                    ReadBytes(8, field + " value");
                    break;
                case ObjectIdType:
                    ReadReference(BuildTableReferenceKind.ObjectId, field);
                    break;
                case Vector2Type:
                    ReadBytes(8, field + " value");
                    break;
                case Vector3Type:
                    ReadBytes(12, field + " value");
                    break;
                case Vector4Type:
                case QuaternionType:
                    ReadBytes(16, field + " value");
                    break;
                case Matrix33Type:
                    ReadBytes(36, field + " value");
                    break;
                case Matrix44Type:
                    ReadBytes(64, field + " value");
                    break;
                case HandleType:
                    ReadHandle(field);
                    break;
                case ReferenceType:
                    ReadFileReference(field);
                    break;
                case ObjectPointerType:
                case BaseObjectPointerType:
                    ReadObjectPointer(field);
                    break;
                case StringType:
                    ReadString32(field);
                    break;
                case LocalizedStringType:
                    ReadBytes(8, field + " localized-string id");
                    break;
                case ObjectType:
                case BaseObjectType:
                    ReadEmbeddedObject(field);
                    break;
                default:
                    throw new NotSupportedException($"{field} has unknown dynamic-property storage 0x{storage:X8} (data type 0x{dataType:X8}, type 0x{type:X8}).");
            }
        }

        static bool IsStorageType(uint value) => value is >= CharType and <= ReferenceType || value == BoolType;

        void ReadString32(string field)
        {
            int length = ReadCount(field + " string byte");
            ReadBytes(length, field + " string");
            if (length > 0)
                ReadByte(field + " string terminator");
        }

        void ReadObjectPointer(string field)
        {
            byte tag = ReadByte(field + " pointer tag");
            switch (tag)
            {
                case 1:
                case 2:
                    ReadReference(BuildTableReferenceKind.ObjectPointer, field);
                    break;
                case 3:
                    break;
                case 0:
                    ReadEmbeddedObject(field);
                    break;
                default:
                    throw new InvalidDataException($"{field} uses unsupported object-pointer tag {tag}.");
            }
        }

        void ReadEmbeddedObject(string field)
        {
            int headerOffset = checked((int)_reader.BaseStream.Position);
            var header = ReadHeader(0, field + " embedded object");

            switch (header.ClassHash)
            {
                case BuildRowHash:
                    _ = ReadRow(-1);
                    break;
                case BuildTagsHash:
                    ReadBuildTagsPayload(headerOffset, header, field);
                    break;
                case BuildTagHash:
                    ReadUInt32(field + " tag value");
                    break;
                case UvTransformHash:
                    ReadBytes(32, field + " uv transform");
                    ReadByte(field + " animate translation");
                    ReadByte(field + " animate rotation");
                    break;
                case RowTagSelectionHash:
                case RowTagSelectionAltHash:
                    ReadBytes(8, field + " row tag selection");
                    break;
                case ColumnSelectionAltHash:
                    ReadBytes(41, field + " column selection");
                    break;
                case ColumnSelectionHash:
                    ReadBytes(60, field + " column selection");
                    break;
                case TagSelectionHash:
                    ReadUInt32(field + " tag");
                    ReadHeader(TagSelectionValueHash, field + " tag selection value");
                    ReadBytes(40, field + " tag selection value data");
                    break;
                default:
                    throw new NotSupportedException($"{field} embeds unsupported class 0x{header.ClassHash:X8} at 0x{_reader.BaseStream.Position - 4:X}.");
            }
        }

        void ReadBuildTagsPayload(int headerOffset, ScimitarHeader header, string field)
        {
            int countOffset = checked((int)_reader.BaseStream.Position);
            int tags = ReadCount(field + " tag");
            var entries = new List<BuildTableTagEntry>(tags);
            for (int i = 0; i < tags; i++)
                entries.Add(ReadBuildTag($"{field} tag {i}"));
            _asset.BuildTagLists.Add(new BuildTableTagList(headerOffset, countOffset, header.Id, entries, field));
        }

        void ReadHandle(string field)
        {
            byte referenceTag = ReadByte(field + " handle tag");
            ReadReference(BuildTableReferenceKind.Handle, field, referenceTag);
        }

        void ReadFileReference(string field)
        {
            byte referenceTag = ReadByte(field + " reference tag");
            byte globalFlag = ReadByte(field + " global flag");
            ReadReference(BuildTableReferenceKind.FileReference, field, referenceTag, globalFlag);
        }

        ulong ReadReference(BuildTableReferenceKind kind, string field,
            byte? referenceTag = null, byte? globalFlag = null)
        {
            int offset = checked((int)_reader.BaseStream.Position);
            ulong value = ReadUInt64(field + " id");
            _asset.References.Add(new BuildTableReference(offset, value, kind, field,
                referenceTag, globalFlag));
            return value;
        }

        ScimitarHeader ReadHeader(uint expected, string field)
        {
            Ensure(12, field);
            int offset = checked((int)_reader.BaseStream.Position);
            var header = ScimitarHeader.Read(_reader, expected);
            _asset.Objects.Add(new BuildTableObject(offset, header.Id, header.ClassHash, field));
            return header;
        }

        int ReadCount(string field)
        {
            int count = ReadInt32(field + " count");
            if (count < 0 || count > MaximumItems)
                throw new InvalidDataException($"Invalid {field} count {count} at 0x{_reader.BaseStream.Position - 4:X}.");
            return count;
        }

        byte ReadByte(string field)
        {
            Ensure(1, field);
            return _reader.ReadByte();
        }

        int ReadInt32(string field)
        {
            Ensure(4, field);
            return _reader.ReadInt32();
        }

        uint ReadUInt32(string field)
        {
            Ensure(4, field);
            return _reader.ReadUInt32();
        }

        ulong ReadUInt64(string field)
        {
            Ensure(8, field);
            return _reader.ReadUInt64();
        }

        float ReadSingle(string field)
        {
            Ensure(4, field);
            return _reader.ReadSingle();
        }

        byte[] ReadBytes(int count, string field)
        {
            Ensure(count, field);
            return _reader.ReadBytes(count);
        }

        void Ensure(int count, string field)
        {
            if (count < 0 || _reader.BaseStream.Position > _reader.BaseStream.Length - count)
                throw new EndOfStreamException($"Unexpected end of BuildTable while reading {field} at 0x{_reader.BaseStream.Position:X}.");
        }
    }
}
