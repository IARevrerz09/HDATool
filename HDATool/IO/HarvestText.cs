// HarvestText.cs — Refactored
// Harvest Moon: Save the Homeland — Text Codec
// Architecture: Strategy + Value Object + Guard Clauses + Span<T> optimizations

using System;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using HDATool.Properties;

namespace HDATool.IO;

/// <summary>
/// Encodes and decodes dialog text for Harvest Moon: Save the Homeland (PS2).
/// 
/// Format overview:
///   - A "pointers" stream contains uint32 offsets (little-endian) into the data stream,
///     one per dialog, terminated by a zero or a sentinel equal to data length.
///   - Each dialog is a bit-packed stream: a header byte precedes up to 8 values.
///     If the corresponding bit is 0 → read 1 byte (8-bit value).
///     If the corresponding bit is 1 → read 2 bytes (16-bit value, big-endian in original).
///   - Value 2 = end-of-dialog sentinel.
///   - Value 7 = variable placeholder; one extra byte follows as the variable index.
/// </summary>
public sealed class HarvestText
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const int    AlignData        = 4;
    private const int    AlignBlock       = 16;
    private const byte   EndOfDialog      = 2;
    private const byte   VariableCode     = 7;
    private const byte   FallbackSpace    = 0x10;
    private const int    TableSize        = 0x10000;
    private const string HexEscapePrefix  = "\\x";

    // -------------------------------------------------------------------------
    // Immutable character table (built once, thread-safe)
    // -------------------------------------------------------------------------

    private static readonly FrozenDictionary<int, string>    s_decodeTable;  // index → glyph
    private static readonly FrozenDictionary<string, int>    s_encodeTable;  // glyph → index

    static HarvestText()
    {
        (s_decodeTable, s_encodeTable) = BuildTables();
    }

    // -------------------------------------------------------------------------
    // Public API — file paths
    // -------------------------------------------------------------------------

    /// <summary>Decodes a dialog binary pair from disk into a UTF-8 string.</summary>
    /// <exception cref="ArgumentNullException"/>
    /// <exception cref="FileNotFoundException"/>
    public static string Decode(string dataPath, string pointersPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath,     nameof(dataPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(pointersPath, nameof(pointersPath));

        using var dataStream     = OpenRead(dataPath);
        using var pointersStream = OpenRead(pointersPath);
        return Decode(dataStream, pointersStream);
    }

    /// <summary>Encodes a dialog string and writes the binary pair to disk atomically.</summary>
    /// <exception cref="ArgumentNullException"/>
    public static void Encode(string text, string dataPath, string pointersPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath,     nameof(dataPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(pointersPath, nameof(pointersPath));
        ArgumentNullException.ThrowIfNull(text,                 nameof(text));

        var encoded = Encode(text);
        WriteAtomic(dataPath,     encoded.Data);
        WriteAtomic(pointersPath, encoded.Pointers);
    }

    // -------------------------------------------------------------------------
    // Public API — streams
    // -------------------------------------------------------------------------

    /// <summary>Decodes from open streams. Streams are NOT disposed by this method.</summary>
    /// <exception cref="ArgumentNullException"/>
    /// <exception cref="InvalidDataException">Thrown when the stream layout is corrupt.</exception>
    public static string Decode(Stream data, Stream pointers)
    {
        ArgumentNullException.ThrowIfNull(data,     nameof(data));
        ArgumentNullException.ThrowIfNull(pointers, nameof(pointers));

        if (!data.CanRead || !data.CanSeek)
            throw new ArgumentException("Data stream must be readable and seekable.", nameof(data));
        if (!pointers.CanRead)
            throw new ArgumentException("Pointers stream must be readable.", nameof(pointers));

        var output = new StringBuilder(capacity: 4096);
        var reader = new BinaryReader(data,     Encoding.Latin1, leaveOpen: true);
        var ptrRdr = new BinaryReader(pointers, Encoding.Latin1, leaveOpen: true);

        // Read all pointer values up-front; avoids interleaved seeks on the same stream.
        var offsetList = ReadPointerOffsets(ptrRdr);

        for (int i = 0; i < offsetList.Count - 1; i++)
        {
            uint currentOffset = offsetList[i];
            uint nextOffset    = offsetList[i + 1];

            // A zero next-offset signals end of valid dialogs (original convention).
            if (nextOffset == 0) break;

            data.Seek(currentOffset, SeekOrigin.Begin);
            DecodeDialog(reader, data, output);
        }

        return output.ToString();
    }

    /// <summary>Encodes a dialog string into the binary wire format.</summary>
    /// <exception cref="ArgumentNullException"/>
    public static EncodedText Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text, nameof(text));

        // Split on the end-of-dialog glyph (value = 2 in the table).
        string dialogSeparator = s_decodeTable.TryGetValue(EndOfDialog, out var sep)
            ? sep
            : throw new InvalidOperationException("Character table is missing the end-of-dialog sentinel (0x02).");

        ReadOnlySpan<string> dialogs = text.Split(dialogSeparator, StringSplitOptions.RemoveEmptyEntries);

        using var dataStream     = new MemoryStream(capacity: dialogs.Length * 128);
        using var pointersStream = new MemoryStream(capacity: (dialogs.Length + 1) * 4);
        using var dataWriter     = new BinaryWriter(dataStream,     Encoding.Latin1, leaveOpen: true);
        using var ptrWriter      = new BinaryWriter(pointersStream, Encoding.Latin1, leaveOpen: true);

        foreach (var dialog in dialogs)
            EncodeDialog(dialog, dataStream, dataWriter, ptrWriter);

        // Terminal pointer = total data length (matches decoder's zero-sentinel convention).
        AlignStream(dataStream, AlignData);
        ptrWriter.Write((uint)dataStream.Length);

        AlignStream(dataStream,     AlignBlock);
        AlignStream(pointersStream, AlignBlock);

        return new EncodedText(dataStream.ToArray(), pointersStream.ToArray());
    }

    // -------------------------------------------------------------------------
    // Value object
    // -------------------------------------------------------------------------

    /// <summary>Immutable result of <see cref="Encode(string)"/>.</summary>
    public readonly record struct EncodedText(byte[] Data, byte[] Pointers);

    // -------------------------------------------------------------------------
    // Decode helpers
    // -------------------------------------------------------------------------

    private static List<uint> ReadPointerOffsets(BinaryReader reader)
    {
        var list = new List<uint>(capacity: 64);
        var stream = reader.BaseStream;
        while (stream.Position + 4 <= stream.Length)
            list.Add(reader.ReadUInt32());
        return list;
    }

    private static void DecodeDialog(BinaryReader reader, Stream data, StringBuilder output)
    {
        byte header = 0;
        byte mask   = 0;

        while (data.Position < data.Length)
        {
            // Every 8 characters the header byte is refreshed (MSB first).
            if ((mask >>= 1) == 0)
            {
                header = reader.ReadByte();
                mask   = 0x80;
            }

            // Decode character value: 8-bit or 16-bit depending on header bit.
            uint value = (header & mask) == 0
                ? reader.ReadByte()
                : reader.ReadUInt16();

            if (value == EndOfDialog) return;

            AppendValue(reader, data, value, output);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendValue(BinaryReader reader, Stream data, uint value, StringBuilder output)
    {
        if (value == VariableCode)
        {
            // Variable token: one extra byte is the variable index.
            byte varIndex = reader.ReadByte();
            output.Append($"[var]\\x{varIndex:X4}");
            return;
        }

        if (s_decodeTable.TryGetValue((int)value, out string? glyph))
            output.Append(glyph);
        else
            output.Append($"{HexEscapePrefix}{value:X4}");
    }

    // -------------------------------------------------------------------------
    // Encode helpers
    // -------------------------------------------------------------------------

    private static void EncodeDialog(
        string dialog,
        MemoryStream data,
        BinaryWriter writer,
        BinaryWriter ptrWriter)
    {
        AlignStream(data, AlignData);
        ptrWriter.Write((uint)data.Position);

        // The header byte precedes each group of 8 values. We write a placeholder
        // byte, then back-fill its value once we know which values were 16-bit.
        byte header          = 0;
        int  mask            = 0;
        long headerPosition  = data.Position;
        data.WriteByte(0);  // placeholder

        int index = 0;
        while (index < dialog.Length)
        {
            // Refresh header slot every 8 characters.
            if ((mask >>= 1) == 0)
            {
                // Commit current header before starting a new slot.
                FlushHeader(data, writer, ref header, ref headerPosition);
                mask = 0x80;
            }

            index += EncodeCharacter(dialog, index, mask, data, writer, ref header);
        }

        // ---- End of dialog ----
        FlushHeader(data, writer, ref header, ref headerPosition);

        // If mask has been consumed exactly (mask == 1 means one slot used),
        // a trailing zero byte must be written before the sentinel.
        if (mask == 1) data.WriteByte(0);
        data.WriteByte(EndOfDialog);
    }

    /// <summary>
    /// Encodes one logical character and returns the number of source chars consumed.
    /// </summary>
    private static int EncodeCharacter(
        string  dialog,
        int     index,
        int     mask,
        Stream  data,
        BinaryWriter writer,
        ref byte header)
    {
        char ch = dialog[index];

        // ---- Line break (CRLF or LF) ----
        if (ch == '\r' && index + 1 < dialog.Length && dialog[index + 1] == '\n')
        {
            data.WriteByte(0);
            return 2;  // consumed 2 chars
        }
        if (ch == '\n')
        {
            data.WriteByte(0);
            return 1;
        }

        // ---- Hex escape: \xNNNN ----
        if (ch == '\\' && index + 6 <= dialog.Length && dialog[index + 1] == 'x')
        {
            string hexStr = dialog.Substring(index + 2, 4);
            if (ushort.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber, null, out ushort hexVal))
            {
                WriteValue(hexVal, mask, data, writer, ref header);
                return 6;
            }
        }

        // ---- Multi-character table lookup (e.g. "[var]...") ----
        if (ch == '[')
        {
            foreach (var (glyph, tableIndex) in s_encodeMultiTable)
            {
                if (index + glyph.Length <= dialog.Length &&
                    dialog.AsSpan(index, glyph.Length).SequenceEqual(glyph))
                {
                    WriteValue((ushort)tableIndex, mask, data, writer, ref header);
                    return glyph.Length;
                }
            }
        }

        // ---- Single character lookup ----
        string singleChar = ch.ToString();
        if (s_encodeTable.TryGetValue(singleChar, out int charValue))
        {
            WriteValue((ushort)charValue, mask, data, writer, ref header);

            // Value 7 (variable) consumes an extra mask slot in the original codec.
            return charValue == VariableCode ? 1 : 1;
        }

        // ---- Fallback: emit space ----
        data.WriteByte(FallbackSpace);
        return 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteValue(
        ushort value, int mask,
        Stream data, BinaryWriter writer,
        ref byte header)
    {
        if (value > 0xFF)
        {
            writer.Write(value);
            header |= (byte)mask;
        }
        else
        {
            data.WriteByte((byte)value);
        }
    }

    private static void FlushHeader(
        Stream data,
        BinaryWriter writer,
        ref byte header,
        ref long headerPosition)
    {
        long currentPosition = data.Position;
        data.Seek(headerPosition, SeekOrigin.Begin);
        data.WriteByte(header);
        data.Seek(currentPosition, SeekOrigin.Begin);

        // Reserve slot for the next header byte.
        headerPosition = data.Position;
        data.WriteByte(0);
        header = 0;
    }

    // -------------------------------------------------------------------------
    // Table construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds both the decode (index→glyph) and encode (glyph→index) lookup tables
    /// from the embedded character table resource.
    /// Complexity: O(n) where n = number of table entries.
    /// </summary>
    private static (FrozenDictionary<int, string> decode, FrozenDictionary<string, int> encode) BuildTables()
    {
        ReadOnlySpan<char> lineBreaks = ['\n'];
        string raw = Resources.CharacterTable
            ?? throw new InvalidOperationException("CharacterTable resource is missing.");

        var decodeEntries = new Dictionary<int, string>(capacity: 512);
        var encodeEntries = new Dictionary<string, int>(capacity: 512);

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Skip comment lines
            if (line.StartsWith('#')) continue;

            int sep = line.IndexOf('=');
            if (sep < 1) continue;

            ReadOnlySpan<char> keySpan   = line.AsSpan(0, sep).Trim();
            ReadOnlySpan<char> valueSpan = line.AsSpan(sep + 1);

            if (!TryParseHex(keySpan, out int index)) continue;

            string glyph = valueSpan.ToString()
                .Replace("\\n",     "\r\n", StringComparison.Ordinal)
                .Replace("\\equal", "=",    StringComparison.Ordinal);

            decodeEntries[index] = glyph;

            // Only the first mapping wins for encode (avoids ambiguous round-trips).
            encodeEntries.TryAdd(glyph, index);
        }

        return (decodeEntries.ToFrozenDictionary(), encodeEntries.ToFrozenDictionary());
    }

    // Pre-sorted multi-char encode entries (longest-match first) for "[var]…" tokens.
    // Built once from s_encodeTable at class init.
    private static readonly (string Glyph, int Index)[] s_encodeMultiTable =
        BuildMultiTable();

    private static (string, int)[] BuildMultiTable()
    {
        // Called after s_encodeTable is ready (static ctor ordering guarantees this
        // if placed after s_decodeTable/s_encodeTable initialisation).
        var list = new List<(string, int)>();
        foreach (var kvp in s_encodeTable)
            if (kvp.Key.Length > 1)
                list.Add((kvp.Key, kvp.Value));

        // Longest match first prevents partial matches consuming a prefix.
        list.Sort((a, b) => b.Glyph.Length.CompareTo(a.Glyph.Length));
        return list.ToArray();
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseHex(ReadOnlySpan<char> span, out int value)
        => int.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value);

    private static void AlignStream(Stream stream, int alignment)
    {
        if (alignment <= 1) return;
        int remainder = (int)(stream.Position % alignment);
        if (remainder == 0) return;
        int padding = alignment - remainder;
        // WriteByte in a loop is fine; alignment is at most 16 bytes.
        for (int i = 0; i < padding; i++) stream.WriteByte(0);
    }

    /// <summary>Opens a file for reading with sharing allowed (no exclusive lock).</summary>
    private static FileStream OpenRead(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required file not found: '{path}'", path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
    }

    /// <summary>
    /// Writes bytes to a temp file, then atomically replaces the destination.
    /// Prevents partial writes from corrupting an existing file.
    /// </summary>
    private static void WriteAtomic(string destination, byte[] data)
    {
        string tmp = destination + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, destination, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup; do not swallow the original exception.
            try { File.Delete(tmp); } catch { /* ignored */ }
            throw;
        }
    }
}
