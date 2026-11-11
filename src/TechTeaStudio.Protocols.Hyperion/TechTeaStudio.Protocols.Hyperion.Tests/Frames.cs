using System.Buffers.Binary;
using System.Text;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

/// <summary>Hand-built wire frames, so tests can speak to a receiver the way a legacy or hostile peer would.</summary>
internal static class Frames
{
    public static string HeaderJson(
        Guid packetId, int chunkNumber, int totalChunks, int dataLength, bool last,
        int? version = HyperionProtocol.ProtocolVersion, uint? crc = null,
        string magic = HyperionProtocol.ProtocolMagic, string? extraMembers = null,
        string? compression = null, int? rawLength = null)
    {
        var sb = new StringBuilder("{");
        if (version is not null)
            sb.Append("\"Version\":").Append(version.Value).Append(',');
        sb.Append("\"Magic\":\"").Append(magic).Append("\",")
          .Append("\"PacketId\":\"").Append(packetId).Append("\",")
          .Append("\"ChunkNumber\":").Append(chunkNumber).Append(',')
          .Append("\"TotalChunks\":").Append(totalChunks).Append(',')
          .Append("\"DataLength\":").Append(dataLength).Append(',')
          .Append("\"Flags\":").Append(last ? 1 : 0);
        if (crc is not null)
            sb.Append(",\"Crc32C\":").Append(crc.Value);
        if (compression is not null)
            sb.Append(",\"Compression\":\"").Append(compression).Append('"');
        if (rawLength is not null)
            sb.Append(",\"RawLength\":").Append(rawLength.Value);
        if (extraMembers is not null)
            sb.Append(',').Append(extraMembers);
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary><c>[headerLength:4 BE][headerJson][payload]</c>.</summary>
    public static byte[] Chunk(string headerJson, byte[] payload)
    {
        var json = Encoding.UTF8.GetBytes(headerJson);
        var frame = new byte[4 + json.Length + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        payload.CopyTo(frame, 4 + json.Length);
        return frame;
    }

    /// <summary>A well-formed chunk whose header matches <paramref name="payload"/>.</summary>
    public static byte[] Chunk(Guid packetId, int chunkNumber, int totalChunks, byte[] payload, uint? crc = null, int? version = HyperionProtocol.ProtocolVersion)
        => Chunk(HeaderJson(packetId, chunkNumber, totalChunks, payload.Length, last: chunkNumber == totalChunks - 1, version, crc), payload);

    /// <summary>
    /// A chunk announcing a compressed payload: <paramref name="wirePayload"/> goes on the wire as is and the header
    /// carries <c>Version: 2</c>, <c>Compression</c> and <c>RawLength</c>. Nothing is checked, so tests can lie.
    /// </summary>
    public static byte[] CompressedChunk(
        Guid packetId, int chunkNumber, int totalChunks, byte[] wirePayload, int? rawLength,
        uint? crc = null, string compression = PacketHeader.ZstandardCompression, int? dataLength = null)
        => Chunk(
            HeaderJson(packetId, chunkNumber, totalChunks, dataLength ?? wirePayload.Length, last: chunkNumber == totalChunks - 1,
                version: HyperionProtocol.MaxSupportedProtocolVersion, crc, compression: compression, rawLength: rawLength),
            wirePayload);

    /// <summary>
    /// A hand-written Zstandard frame (RFC 8878) that stores <paramref name="content"/> (1..255 bytes) in a single raw
    /// block, so tests can build valid compressed chunks on targets that have no encoder:
    /// <c>28 B5 2F FD</c> magic, <c>20</c> frame header descriptor (single segment, 1-byte content size, no checksum),
    /// content size, 3-byte little-endian block header (<c>size &lt;&lt; 3 | last</c>), content.
    /// </summary>
    public static byte[] ZstdRawFrame(byte[] content)
    {
        if (content.Length is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(content), "1..255 bytes.");

        var frame = new byte[9 + content.Length];
        frame[0] = 0x28; frame[1] = 0xB5; frame[2] = 0x2F; frame[3] = 0xFD;
        frame[4] = 0x20;
        frame[5] = (byte)content.Length;
        int blockHeader = (content.Length << 3) | 1;
        frame[6] = (byte)blockHeader;
        frame[7] = (byte)(blockHeader >> 8);
        frame[8] = (byte)(blockHeader >> 16);
        content.CopyTo(frame, 9);
        return frame;
    }

    /// <summary>
    /// Splits <paramref name="payload"/> into chunks of <paramref name="chunkSize"/> raw bytes, each sent as a
    /// compressed chunk holding a <see cref="ZstdRawFrame"/>; every third chunk stays raw (a mixed message).
    /// </summary>
    public static byte[][] CompressedChunks(byte[] payload, int chunkSize, bool withChecksum = false)
    {
        var id = Guid.NewGuid();
        int total = Math.Max(1, (payload.Length + chunkSize - 1) / chunkSize);
        var frames = new byte[total][];
        for (int i = 0; i < total; i++)
        {
            int offset = i * chunkSize;
            var slice = payload.AsSpan(offset, Math.Min(chunkSize, payload.Length - offset)).ToArray();
            if (slice.Length == 0 || i % 3 == 2)
            {
                frames[i] = Chunk(id, i, total, slice, withChecksum ? Crc32C.Compute(slice) : null, version: HyperionProtocol.MaxSupportedProtocolVersion);
                continue;
            }

            var wire = ZstdRawFrame(slice);
            frames[i] = CompressedChunk(id, i, total, wire, slice.Length, withChecksum ? Crc32C.Compute(wire) : null);
        }
        return frames;
    }

    /// <summary>Splits a captured wire image of chunked frames back into headers and on-wire payloads.</summary>
    public static List<(PacketHeader Header, string HeaderJson, byte[] Payload)> Parse(byte[] wire)
    {
        var frames = new List<(PacketHeader, string, byte[])>();
        int offset = 0;
        while (offset < wire.Length)
        {
            int headerLength = BinaryPrimitives.ReadInt32BigEndian(wire.AsSpan(offset));
            var headerBytes = wire.AsSpan(offset + 4, headerLength);
            var header = HyperionProtocol.DeserializeHeader(headerBytes);
            offset += 4 + headerLength;
            frames.Add((header, Encoding.UTF8.GetString(headerBytes), wire.AsSpan(offset, header.DataLength).ToArray()));
            offset += header.DataLength;
        }
        return frames;
    }

    /// <summary>Splits <paramref name="payload"/> into well-formed chunks of <paramref name="chunkSize"/> bytes, like the sender does.</summary>
    public static byte[][] Chunks(byte[] payload, int chunkSize, bool withChecksum = false)
    {
        var id = Guid.NewGuid();
        int total = Math.Max(1, (payload.Length + chunkSize - 1) / chunkSize);
        var frames = new byte[total][];
        for (int i = 0; i < total; i++)
        {
            int offset = i * chunkSize;
            var slice = payload.AsSpan(offset, Math.Min(chunkSize, payload.Length - offset)).ToArray();
            frames[i] = Chunk(id, i, total, slice, withChecksum ? Crc32C.Compute(slice) : null);
        }
        return frames;
    }

    /// <summary><c>[0xFF][length:2 BE][payload]</c>, with an optional explicit length to lie about.</summary>
    public static byte[] Lightweight(byte[] payload, int? declaredLength = null)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = SmartHyperionProtocol.LightweightMagic;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)(declaredLength ?? payload.Length));
        payload.CopyTo(frame, 3);
        return frame;
    }

    /// <summary><c>[0xFE][length:4 BE][payload]</c>, with an optional explicit length to lie about.</summary>
    public static byte[] Direct(byte[] payload, int? declaredLength = null)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = SmartHyperionProtocol.DirectMagic;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), declaredLength ?? payload.Length);
        payload.CopyTo(frame, 5);
        return frame;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    /// <summary>Everything a protocol writes for one message, captured in memory.</summary>
    public static async Task<byte[]> CaptureAsync<T>(HyperionProtocol protocol, T message)
    {
        using var ms = new MemoryStream();
        await protocol.SendAsync(message, ms);
        return ms.ToArray();
    }
}
