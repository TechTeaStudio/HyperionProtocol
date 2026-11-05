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
        string magic = HyperionProtocol.ProtocolMagic, string? extraMembers = null)
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
