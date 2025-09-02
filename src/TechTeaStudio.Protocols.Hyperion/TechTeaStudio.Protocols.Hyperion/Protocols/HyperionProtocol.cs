using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>Main Hyperion Protocol class.</summary>
public class HyperionProtocol(ISerializer serializer)
{
	protected readonly ISerializer _serializer = serializer ?? new DefaultSerializer();
	private const int ChunkSize = 1024 * 1024;
	private const int MaxHeaderLength = 64 * 1024;
	private const string ProtocolMagic = "TTS";

	public virtual async Task SendAsync<T>(T message, NetworkStream stream, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if(!stream.CanWrite)
			throw new InvalidOperationException("Stream is not writable.");

		var data = _serializer.Serialize(message) ?? Array.Empty<byte>();
		int totalChunks = Math.Max(1, (int)Math.Ceiling((double)data.Length / ChunkSize));
		var packetId = Guid.NewGuid();

		try
		{
			for(int i = 0; i < totalChunks; i++)
			{
				ct.ThrowIfCancellationRequested();

				int offset = i * ChunkSize;
				int size = Math.Min(ChunkSize, data.Length - offset);

				var header = new PacketHeader
				{
					Magic = ProtocolMagic,
					PacketId = packetId,
					ChunkNumber = i,
					TotalChunks = totalChunks,
					DataLength = size,
					Flags = (byte)(i == totalChunks - 1 ? 1 : 0)
				};

				await SendChunkAsync(stream, header, data.AsMemory(offset, size), ct).ConfigureAwait(false);
			}

			await stream.FlushAsync(ct).ConfigureAwait(false);
		}
		catch(Exception ex) when(!(ex is OperationCanceledException))
		{
			throw new HyperionProtocolException("Failed to send message", ex);
		}
	}

	public virtual async Task SendChunkAsync(NetworkStream stream, PacketHeader header, ReadOnlyMemory<byte> data, CancellationToken ct)
	{
		var headerJson = JsonSerializer.SerializeToUtf8Bytes(header);

		if(headerJson.Length <= 0 || headerJson.Length > MaxHeaderLength)
			throw new HyperionProtocolException($"Header length out of range: {headerJson.Length}");

		var headerLengthBytes = new byte[4];
		BinaryPrimitives.WriteInt32BigEndian(headerLengthBytes, headerJson.Length);

		// Send header length
		await stream.WriteAsync(headerLengthBytes, ct).ConfigureAwait(false);

		// Send header
		await stream.WriteAsync(headerJson, ct).ConfigureAwait(false);

		// Send data if any
		if(data.Length > 0)
			await stream.WriteAsync(data, ct).ConfigureAwait(false);
	}

	public virtual async Task<T> ReceiveAsync<T>(NetworkStream stream, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if(!stream.CanRead)
			throw new InvalidOperationException("Stream is not readable.");

		try
		{
			var chunks = await ReceiveChunksAsync(stream, ct).ConfigureAwait(false);
			var completeData = CombineChunks(chunks);

			var result = _serializer.Deserialize<T>(completeData);

			if(result == null)
			{
				if(typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
					throw new HyperionProtocolException("Deserialized result is null but target type is non-nullable value type.");

				if(!typeof(T).IsValueType && typeof(T) != typeof(string))
					throw new HyperionProtocolException("Deserialized result is null for reference type.");
			}

			return result!;
		}
		catch(Exception ex) when(!(ex is OperationCanceledException || ex is HyperionProtocolException))
		{
			throw new HyperionProtocolException("Failed to receive message", ex);
		}
	}

	public virtual async Task<List<ChunkData>> ReceiveChunksAsync(NetworkStream stream, CancellationToken ct)
	{
		var chunks = new List<ChunkData>();
		var headerLengthBuf = new byte[4];

		int totalChunks = int.MaxValue;
		Guid? expectedPacketId = null;

		while(chunks.Count < totalChunks)
		{
			ct.ThrowIfCancellationRequested();

			// Read header length
			if(!await ReadExactlyAsync(stream, headerLengthBuf, ct).ConfigureAwait(false))
				throw new EndOfStreamException("Stream ended while reading header length.");

			int headerLength = BinaryPrimitives.ReadInt32BigEndian(headerLengthBuf);
			ValidateHeaderLength(headerLength);

			// Read header
			var headerBytes = new byte[headerLength];
			if(!await ReadExactlyAsync(stream, headerBytes, ct).ConfigureAwait(false))
				throw new EndOfStreamException($"Stream ended while reading {headerLength}-byte header.");

			var header = DeserializeHeader(headerBytes);
			ValidateHeader(header, expectedPacketId, totalChunks, chunks.Count);

			// Update state on first chunk
			if(expectedPacketId == null)
			{
				expectedPacketId = header.PacketId;
				totalChunks = header.TotalChunks;
			}

			// Read data
			var data = new byte[header.DataLength];
			if(header.DataLength > 0)
			{
				if(!await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
					throw new EndOfStreamException($"Stream ended while reading {header.DataLength}-byte payload.");
			}

			chunks.Add(new ChunkData(header.ChunkNumber, data));
		}

		return chunks;
	}

	public static void ValidateHeaderLength(int headerLength)
	{
		if(headerLength <= 0 || headerLength > MaxHeaderLength)
			throw new HyperionProtocolException($"Invalid header length: {headerLength}");
	}

	public static PacketHeader DeserializeHeader(byte[] headerBytes)
	{
		try
		{
			return JsonSerializer.Deserialize<PacketHeader>(headerBytes)
				?? throw new HyperionProtocolException("Header deserialized to null.");
		}
		catch(JsonException ex)
		{
			throw new HyperionProtocolException("Failed to deserialize packet header.", ex);
		}
	}

	public static void ValidateHeader(PacketHeader header, Guid? expectedPacketId, int expectedTotalChunks, int receivedChunkCount)
	{
		if(header.Magic != ProtocolMagic)
			throw new HyperionProtocolException($"Invalid protocol magic. Expected '{ProtocolMagic}', got '{header.Magic}'.");

		if(header.ChunkNumber < 0 || header.ChunkNumber >= header.TotalChunks)
			throw new HyperionProtocolException($"Invalid chunk number {header.ChunkNumber} for {header.TotalChunks} total chunks.");

		if(header.TotalChunks <= 0)
			throw new HyperionProtocolException($"Invalid total chunks: {header.TotalChunks}");

		if(header.DataLength < 0 || header.DataLength > ChunkSize)
			throw new HyperionProtocolException($"Invalid data length: {header.DataLength}. Max: {ChunkSize}");

		bool isLastChunk = header.ChunkNumber == header.TotalChunks - 1;
		bool hasEndFlag = (header.Flags & 1) != 0;
		if(hasEndFlag != isLastChunk)
			throw new HyperionProtocolException("End flag mismatch with chunk position.");

		if(expectedPacketId.HasValue)
		{
			if(header.PacketId != expectedPacketId)
				throw new HyperionProtocolException("Packet ID mismatch between chunks.");

			if(header.TotalChunks != expectedTotalChunks)
				throw new HyperionProtocolException("Total chunks mismatch between chunks.");
		}

		if(header.ChunkNumber != receivedChunkCount)
			throw new HyperionProtocolException($"Chunk received out of order. Expected {receivedChunkCount}, got {header.ChunkNumber}.");
	}

	public static byte[] CombineChunks(List<ChunkData> chunks)
	{
		int totalLength = 0;
		foreach(var chunk in chunks)
			totalLength += chunk.Data.Length;

		var result = new byte[totalLength];
		int offset = 0;

		foreach(var chunk in chunks)
		{
			chunk.Data.CopyTo(result, offset);
			offset += chunk.Data.Length;
		}

		return result;
	}

	/// <summary>
	/// Reads exactly buffer.Length bytes into buffer.
	/// Returns false if EOF is encountered before the buffer is completely filled.
	/// </summary>
	public static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
	{
		int totalRead = 0;
		while(totalRead < buffer.Length)
		{
			int bytesRead = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, ct).ConfigureAwait(false);
			if(bytesRead == 0)
				return false; // EOF reached

			totalRead += bytesRead;
		}
		return true;
	}

}


