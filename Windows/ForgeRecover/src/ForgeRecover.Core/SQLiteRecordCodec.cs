using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ForgeRecover.Core;

public enum SQLiteDecodedStorageClass
{
    Null,
    Integer,
    Real,
    Text,
    Blob
}

public sealed record SQLiteDecodedValue(
    int ColumnIndex,
    ulong SerialType,
    SQLiteDecodedStorageClass StorageClass,
    long? IntegerValue,
    double? RealValue,
    string? TextValue,
    string? BlobPreviewHex,
    string? BlobSha256,
    int ByteLength,
    bool TextTruncated);

public sealed record SQLiteDecodedRecord(
    long RowId,
    long PayloadLength,
    int LocalPayloadLength,
    int OverflowPagesRead,
    IReadOnlyList<SQLiteDecodedValue> Values,
    string CanonicalSha256);

internal enum SQLiteTextEncoding
{
    Utf8 = 1,
    Utf16LittleEndian = 2,
    Utf16BigEndian = 3
}

internal sealed class SQLiteRecordCodec
{
    private readonly int _usablePageSize;
    private readonly SQLiteTextEncoding _textEncoding;
    private readonly int _maximumOverflowPages;
    private readonly int _maximumTextCharacters;

    public SQLiteRecordCodec(
        int usablePageSize,
        SQLiteTextEncoding textEncoding,
        int maximumOverflowPages,
        int maximumTextCharacters)
    {
        if (usablePageSize < 480) throw new ArgumentOutOfRangeException(nameof(usablePageSize));
        if (maximumOverflowPages < 1) throw new ArgumentOutOfRangeException(nameof(maximumOverflowPages));
        if (maximumTextCharacters < 16) throw new ArgumentOutOfRangeException(nameof(maximumTextCharacters));
        _usablePageSize = usablePageSize;
        _textEncoding = textEncoding;
        _maximumOverflowPages = maximumOverflowPages;
        _maximumTextCharacters = maximumTextCharacters;
    }

    public bool TryDecodeTableLeafCell(
        ReadOnlyMemory<byte> page,
        int cellOffset,
        Func<uint, ReadOnlyMemory<byte>?> pageResolver,
        out SQLiteDecodedRecord? record,
        out int cellBytesConsumed,
        out string? warning)
    {
        record = null;
        cellBytesConsumed = 0;
        warning = null;
        var span = page.Span;
        if (cellOffset < 0 || cellOffset >= _usablePageSize || cellOffset >= span.Length) return false;

        if (!TryReadVarint(span, cellOffset, out var payloadLengthUnsigned, out var payloadVarintBytes)) return false;
        if (payloadLengthUnsigned > int.MaxValue) return false;
        var payloadLength = checked((int)payloadLengthUnsigned);
        if (payloadLength < 1) return false;

        var rowIdOffset = cellOffset + payloadVarintBytes;
        if (!TryReadVarint(span, rowIdOffset, out var rowIdUnsigned, out var rowIdVarintBytes)) return false;
        var rowId = unchecked((long)rowIdUnsigned);
        if (rowId < 0) return false;

        var payloadOffset = rowIdOffset + rowIdVarintBytes;
        var localPayload = LocalPayloadBytes(payloadLength, _usablePageSize);
        if (payloadOffset < 0 || localPayload < 0 || payloadOffset + localPayload > _usablePageSize || payloadOffset + localPayload > span.Length)
            return false;

        byte[] payload;
        var overflowPagesRead = 0;
        if (localPayload == payloadLength)
        {
            payload = span.Slice(payloadOffset, payloadLength).ToArray();
            cellBytesConsumed = payloadVarintBytes + rowIdVarintBytes + payloadLength;
        }
        else
        {
            var overflowPointerOffset = payloadOffset + localPayload;
            if (overflowPointerOffset + 4 > _usablePageSize || overflowPointerOffset + 4 > span.Length) return false;
            var overflowPage = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(overflowPointerOffset, 4));
            if (overflowPage == 0) return false;

            payload = new byte[payloadLength];
            span.Slice(payloadOffset, localPayload).CopyTo(payload);
            var written = localPayload;
            var visited = new HashSet<uint>();
            while (written < payloadLength)
            {
                if (!visited.Add(overflowPage))
                {
                    warning = "Overflow chain contains a cycle.";
                    return false;
                }
                if (++overflowPagesRead > _maximumOverflowPages)
                {
                    warning = "Overflow chain exceeded configured safety limit.";
                    return false;
                }

                var overflow = pageResolver(overflowPage);
                if (overflow is null || overflow.Value.Length < _usablePageSize)
                {
                    warning = $"Overflow page {overflowPage} is unavailable.";
                    return false;
                }

                var overflowSpan = overflow.Value.Span;
                var next = BinaryPrimitives.ReadUInt32BigEndian(overflowSpan.Slice(0, 4));
                var take = Math.Min(payloadLength - written, _usablePageSize - 4);
                overflowSpan.Slice(4, take).CopyTo(payload.AsSpan(written, take));
                written += take;
                overflowPage = next;
                if (written < payloadLength && overflowPage == 0)
                {
                    warning = "Overflow chain ended before the declared payload length.";
                    return false;
                }
            }

            cellBytesConsumed = payloadVarintBytes + rowIdVarintBytes + localPayload + 4;
        }

        if (!TryDecodeRecordPayload(payload, out var values)) return false;
        record = new SQLiteDecodedRecord(
            rowId,
            payloadLength,
            localPayload,
            overflowPagesRead,
            values,
            CanonicalHash(rowId, values));
        return true;
    }

    public bool TryDecodeRecordPayload(ReadOnlySpan<byte> payload, out IReadOnlyList<SQLiteDecodedValue> values)
    {
        values = Array.Empty<SQLiteDecodedValue>();
        if (!TryReadVarint(payload, 0, out var headerLengthUnsigned, out var headerVarintBytes)) return false;
        if (headerLengthUnsigned > int.MaxValue) return false;
        var headerLength = checked((int)headerLengthUnsigned);
        if (headerLength < headerVarintBytes || headerLength > payload.Length) return false;

        var serialTypes = new List<ulong>();
        var headerOffset = headerVarintBytes;
        while (headerOffset < headerLength)
        {
            if (!TryReadVarint(payload, headerOffset, out var serialType, out var bytes)) return false;
            serialTypes.Add(serialType);
            headerOffset += bytes;
        }
        if (headerOffset != headerLength || serialTypes.Count == 0 || serialTypes.Count > 4096) return false;

        var decoded = new List<SQLiteDecodedValue>(serialTypes.Count);
        var dataOffset = headerLength;
        for (var index = 0; index < serialTypes.Count; index++)
        {
            var serialType = serialTypes[index];
            if (!TrySerialLength(serialType, out var byteLength)) return false;
            if (dataOffset < 0 || dataOffset + byteLength > payload.Length) return false;
            var data = payload.Slice(dataOffset, byteLength);
            if (!TryDecodeValue(index, serialType, data, out var value)) return false;
            decoded.Add(value!);
            dataOffset += byteLength;
        }

        if (dataOffset > payload.Length) return false;
        values = decoded;
        return true;
    }

    public static bool TryReadVarint(ReadOnlySpan<byte> data, int offset, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;
        if (offset < 0 || offset >= data.Length) return false;

        for (var index = 0; index < 8; index++)
        {
            if (offset + index >= data.Length) return false;
            var current = data[offset + index];
            if ((current & 0x80) == 0)
            {
                value = (value << 7) | current;
                bytesRead = index + 1;
                return true;
            }
            value = (value << 7) | (ulong)(current & 0x7f);
        }

        if (offset + 8 >= data.Length) return false;
        value = (value << 8) | data[offset + 8];
        bytesRead = 9;
        return true;
    }

    public static int LocalPayloadBytes(int payloadLength, int usablePageSize)
    {
        if (payloadLength < 0) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        if (usablePageSize < 480) throw new ArgumentOutOfRangeException(nameof(usablePageSize));
        var maxLocal = usablePageSize - 35;
        if (payloadLength <= maxLocal) return payloadLength;

        var minLocal = ((usablePageSize - 12) * 32 / 255) - 23;
        var k = minLocal + ((payloadLength - minLocal) % (usablePageSize - 4));
        return k <= maxLocal ? k : minLocal;
    }

    private bool TryDecodeValue(int columnIndex, ulong serialType, ReadOnlySpan<byte> data, out SQLiteDecodedValue? value)
    {
        value = null;
        switch (serialType)
        {
            case 0:
                value = new SQLiteDecodedValue(columnIndex, serialType, SQLiteDecodedStorageClass.Null, null, null, null, null, null, 0, false);
                return true;
            case 1:
            case 2:
            case 3:
            case 4:
            case 5:
            case 6:
                var integer = DecodeSignedBigEndian(data);
                value = new SQLiteDecodedValue(columnIndex, serialType, SQLiteDecodedStorageClass.Integer, integer, null, null, null, null, data.Length, false);
                return true;
            case 7:
                if (data.Length != 8) return false;
                var bits = BinaryPrimitives.ReadInt64BigEndian(data);
                value = new SQLiteDecodedValue(columnIndex, serialType, SQLiteDecodedStorageClass.Real, null, BitConverter.Int64BitsToDouble(bits), null, null, null, 8, false);
                return true;
            case 8:
                value = new SQLiteDecodedValue(columnIndex, serialType, SQLiteDecodedStorageClass.Integer, 0, null, null, null, null, 0, false);
                return true;
            case 9:
                value = new SQLiteDecodedValue(columnIndex, serialType, SQLiteDecodedStorageClass.Integer, 1, null, null, null, null, 0, false);
                return true;
            case 10:
            case 11:
                return false;
            default:
                if (serialType < 12) return false;
                if ((serialType & 1) == 0)
                {
                    var previewBytes = data[..Math.Min(data.Length, 64)];
                    value = new SQLiteDecodedValue(
                        columnIndex,
                        serialType,
                        SQLiteDecodedStorageClass.Blob,
                        null,
                        null,
                        null,
                        Convert.ToHexString(previewBytes).ToLowerInvariant(),
                        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                        data.Length,
                        false);
                    return true;
                }

                var text = DecodeText(data);
                var truncated = text.Length > _maximumTextCharacters;
                if (truncated) text = text[.._maximumTextCharacters];
                value = new SQLiteDecodedValue(
                    columnIndex,
                    serialType,
                    SQLiteDecodedStorageClass.Text,
                    null,
                    null,
                    text,
                    null,
                    null,
                    data.Length,
                    truncated);
                return true;
        }
    }

    private string DecodeText(ReadOnlySpan<byte> data)
    {
        return _textEncoding switch
        {
            SQLiteTextEncoding.Utf8 => Encoding.UTF8.GetString(data),
            SQLiteTextEncoding.Utf16LittleEndian => Encoding.Unicode.GetString(data),
            SQLiteTextEncoding.Utf16BigEndian => Encoding.BigEndianUnicode.GetString(data),
            _ => Encoding.UTF8.GetString(data)
        };
    }

    private static bool TrySerialLength(ulong serialType, out int byteLength)
    {
        byteLength = serialType switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 6,
            6 => 8,
            7 => 8,
            8 => 0,
            9 => 0,
            10 => -1,
            11 => -1,
            _ when (serialType & 1) == 0 => checked((int)((serialType - 12) / 2)),
            _ => checked((int)((serialType - 13) / 2))
        };
        return byteLength >= 0;
    }

    private static long DecodeSignedBigEndian(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(data));
        ulong value = 0;
        foreach (var current in data) value = (value << 8) | current;
        if (data.Length == 8) return unchecked((long)value);
        if ((data[0] & 0x80) != 0)
        {
            var bits = data.Length * 8;
            value |= ulong.MaxValue << bits;
        }
        return unchecked((long)value);
    }

    private static string CanonicalHash(long rowId, IReadOnlyList<SQLiteDecodedValue> values)
    {
        var builder = new StringBuilder();
        builder.Append(rowId).Append('|');
        foreach (var value in values)
        {
            builder.Append(value.ColumnIndex).Append(':').Append(value.StorageClass).Append(':');
            switch (value.StorageClass)
            {
                case SQLiteDecodedStorageClass.Null:
                    builder.Append("null");
                    break;
                case SQLiteDecodedStorageClass.Integer:
                    builder.Append(value.IntegerValue);
                    break;
                case SQLiteDecodedStorageClass.Real:
                    builder.Append(value.RealValue?.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case SQLiteDecodedStorageClass.Text:
                    builder.Append(value.TextValue);
                    break;
                case SQLiteDecodedStorageClass.Blob:
                    builder.Append(value.BlobSha256);
                    break;
            }
            builder.Append('|');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
