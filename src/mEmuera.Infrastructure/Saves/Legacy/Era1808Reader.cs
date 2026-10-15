using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using mEmuera.Contracts;

namespace mEmuera.Infrastructure.Saves.Legacy;

public sealed record LegacyTable(string Schema, string Data);
public sealed record LegacySave(long GameCode, long GameVersion, string Description,
    ImmutableArray<SavedVariable> Variables, ImmutableArray<ImmutableArray<SavedVariable>> Characters,
    ImmutableDictionary<string, ImmutableDictionary<string, string>> Maps,
    ImmutableDictionary<string, string> XmlDocuments, ImmutableDictionary<string, LegacyTable> Tables);

/// <summary>只读解析旧引擎1808普通存档，不加载旧程序集。</summary>
public sealed class Era1808Reader
{
    private const int MaxBytes = 256 * 1024 * 1024;
    private static readonly Encoding Unicode = new UnicodeEncoding(false, false, true);
    private readonly BinaryReader reader;
    private long valuesRead;
    private Era1808Reader(Stream stream) => reader = new(stream, Unicode, true);
    private static ScriptException Invalid(string message) => new("LEGACY_SAVE", message);

    public static LegacySave Read(Stream source, CancellationToken cancellation = default)
    {
        try
        {
            using var header = new BinaryReader(source, Unicode, true);
            ulong magic = header.ReadUInt64();
            if (magic is not (0x0A1A0A0D41524589UL or 0x0A50495A41524589UL)) throw Invalid("不是1808二进制存档");
            if (header.ReadUInt32() != 1808 || header.ReadUInt32() != 0) throw Invalid("不支持的旧档版本或头扩展");
            using var payload = new MemoryStream();
            using var compressed = magic == 0x0A50495A41524589UL ? new GZipStream(source, CompressionMode.Decompress, true) : null;
            Stream input = compressed ?? source;
            byte[] buffer = new byte[65536];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (payload.Length + count > MaxBytes) throw Invalid("旧档解压后超过256 MiB");
                payload.Write(buffer, 0, count);
            }
            payload.Position = 0;
            return new Era1808Reader(payload).ReadPayload(cancellation);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or DecoderFallbackException or OverflowException or FormatException)
        { throw Invalid("旧档数据损坏: " + e.Message); }
    }

    private string Text()
    {
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > 16_000_000 || length % 2 != 0) throw Invalid("旧档字符串长度无效");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Unicode.GetString(bytes);
    }
    private long Integer(byte marker) => marker switch
    {
        <= 0xcf => marker, 0xd0 => reader.ReadInt16(), 0xd1 => reader.ReadInt32(), 0xd2 => reader.ReadInt64(),
        _ => throw Invalid("旧档整数标记无效")
    };
    private LegacySave ReadPayload(CancellationToken cancellation)
    {
        if (reader.ReadByte() != 0) throw Invalid("只支持普通游戏存档，不能导入全局或角色文件");
        long gameCode = reader.ReadInt64(), version = reader.ReadInt64();
        string description = Text();
        long count = reader.ReadInt64();
        if (count < 0 || count > 10000) throw Invalid("旧档角色数量无效");
        var characters = ImmutableArray.CreateBuilder<ImmutableArray<SavedVariable>>();
        for (int i = 0; i < count; i++) characters.Add(Variables(0xfe, cancellation));
        var variables = Variables(0xff, cancellation);
        var maps = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
        var xml = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var tables = ImmutableDictionary.CreateBuilder<string, LegacyTable>(StringComparer.Ordinal);
        if (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                byte type = reader.ReadByte();
                if (type == 0xff) break;
                string name = Text();
                if (type == 0x20)
                {
                    int entries = reader.ReadInt32();
                    if (entries < 0 || entries > 1_000_000 || maps.ContainsKey(name)) throw Invalid("旧档映射数量或名称无效");
                    var map = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < entries; i++)
                    {
                        string key = Text(), value = Text();
                        if (!map.TryAdd(key, value)) throw Invalid("旧档映射键重复");
                    }
                    maps.Add(name, map.ToImmutable());
                }
                else if (type == 0x21 && !xml.ContainsKey(name)) xml.Add(name, Text());
                else if (type == 0x22 && !tables.ContainsKey(name)) tables.Add(name, new(Text(), Text()));
                else throw Invalid("不支持的旧档扩展或扩展名称重复");
            }
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw Invalid("旧档末尾存在未知数据");
        return new(gameCode, version, description, variables, characters.ToImmutable(), maps.ToImmutable(), xml.ToImmutable(), tables.ToImmutable());
    }
    private ImmutableArray<SavedVariable> Variables(byte end, CancellationToken cancellation)
    {
        var result = ImmutableArray.CreateBuilder<SavedVariable>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            byte type = reader.ReadByte();
            if (type == end) return result.ToImmutable();
            if (type == 0xfd && end == 0xfe) continue;
            if (type > 7 && type is not (>= 0x10 and <= 0x13)) throw Invalid("旧档变量类型无效");
            string name = Text();
            if (name.Length == 0 || !names.Add(name)) throw Invalid("旧档变量名称为空或重复");
            ValueKind kind = type >= 0x10 ? ValueKind.String : type >= 4 ? ValueKind.Float : ValueKind.Integer;
            int rank = type & 3;
            int[] shape = rank == 0 ? [1] : Enumerable.Range(0, rank).Select(_ => reader.ReadInt32()).ToArray();
            long length = 1;
            foreach (int dimension in shape)
            {
                if (dimension < 0) throw Invalid("旧档数组尺寸为负");
                length = checked(length * dimension);
            }
            valuesRead = checked(valuesRead + length);
            if (valuesRead > 16_000_000) throw Invalid("旧档变量总元素数量超限");
            var values = Enumerable.Repeat(kind == ValueKind.String ? new EraValue("") : kind == ValueKind.Float ? new EraValue(0d) : new EraValue(0L), (int)length).ToArray();
            if (rank == 0) values[0] = kind == ValueKind.String ? new(Text()) : kind == ValueKind.Float ? new(reader.ReadDouble()) : new(Integer(reader.ReadByte()));
            else if (kind == ValueKind.Float)
                for (int i = 0; i < values.Length; i++) values[i] = new(reader.ReadDouble());
            else ReadArray(values, shape, kind, cancellation);
            result.Add(new(name, kind, values.Length, values.ToImmutableArray()) { Shape = shape.ToImmutableArray() });
        }
    }
    private void ReadArray(EraValue[] values, int[] shape, ValueKind kind, CancellationToken cancellation)
    {
        int[] position = new int[shape.Length];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            byte marker = reader.ReadByte();
            if (marker == 0xff) return;
            if (marker is 0xf0 or 0xf1 or 0xf2 or 0xe0 or 0xe1)
            {
                int level = marker switch { 0xf0 => 0, 0xf1 or 0xe0 => 1, _ => 2 };
                int axis = shape.Length - 1 - level;
                if (axis < 0) throw Invalid("旧档数组分隔符维数错误");
                long skip = marker is 0xe0 or 0xe1 ? 1 : Integer(reader.ReadByte());
                if (skip < 0 || skip > shape[axis] - position[axis]) throw Invalid("旧档数组零段越界");
                position[axis] += (int)skip;
                for (int i = axis + 1; i < position.Length; i++) position[i] = 0;
                continue;
            }
            int offset = 0;
            for (int i = 0; i < shape.Length; i++)
            {
                if (position[i] >= shape[i]) throw Invalid("旧档数组值越界");
                offset = checked(offset * shape[i] + position[i]);
            }
            values[offset] = kind == ValueKind.String
                ? marker == 0xd8 ? new(Text()) : throw Invalid("旧档字符串标记无效")
                : new(Integer(marker));
            position[^1]++;
        }
    }
}
