using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace TechTeaStudio.Protocols.Hyperion.Protocols;

/// <summary>Small overhead protocol version.</summary>
/// <param name="serializer"></param>
public partial class SmartHyperionProtocol(ISerializer serializer) : HyperionProtocol(serializer)
{
	private const int LightweightThreshold = 1024;      // < 1KB = lightweight
	private const int DirectThreshold = 64 * 1024;      // < 64KB = direct
	private const byte LightweightMagic = 0xFF;
	private const byte DirectMagic = 0xFE;

	/// <summary>Smart send async.</summary>
	public override async Task SendAsync<T>(T message, NetworkStream stream, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if(!stream.CanWrite)
			throw new InvalidOperationException("Stream is not writable.");

		var data = _serializer.Serialize(message) ?? Array.Empty<byte>();

		try
		{
			if(data.Length < LightweightThreshold)
			{
				await SmartHyperionProtocol.SendLightweightAsync(data, stream, ct).ConfigureAwait(false);
			}
			else if(data.Length < DirectThreshold)
			{
				await SmartHyperionProtocol.SendDirectAsync(data, stream, ct).ConfigureAwait(false);
			}
			else
			{
				await base.SendAsync(message, stream, ct).ConfigureAwait(false);
			}
		}
		catch(Exception ex) when(!(ex is OperationCanceledException))
		{
			throw new HyperionProtocolException("Failed to send message via smart protocol", ex);
		}
	}

	/// <summary>Auto change state.</summary>
	public override async Task<T> ReceiveAsync<T>(NetworkStream stream, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if(!stream.CanRead)
			throw new InvalidOperationException("Stream is not readable.");

		try
		{
			var modeBuffer = new byte[1];
			if(!await ReadExactlyAsync(stream, modeBuffer, ct).ConfigureAwait(false))
				throw new EndOfStreamException("Stream ended while reading mode byte.");

			var mode = modeBuffer[0];

			if(mode == LightweightMagic)
			{
				return await ReceiveLightweightAsync<T>(stream, ct).ConfigureAwait(false);
			}
			else if(mode == DirectMagic)
			{
				return await ReceiveDirectAsync<T>(stream, ct).ConfigureAwait(false);
			}
			else
			{
				var headerLengthBuffer = new byte[4];
				headerLengthBuffer[0] = mode;

				if(!await ReadExactlyAsync(stream, headerLengthBuffer.AsMemory(1), ct).ConfigureAwait(false))
					throw new EndOfStreamException("Stream ended while reading header length.");

				return await ReceiveChunkedAsync<T>(stream, headerLengthBuffer, ct).ConfigureAwait(false);
			}
		}
		catch(Exception ex) when(!(ex is OperationCanceledException || ex is HyperionProtocolException))
		{
			throw new HyperionProtocolException("Failed to receive message via smart protocol", ex);
		}
	}

	#region Lightweight Protocol

	private static async Task SendLightweightAsync(byte[] data, NetworkStream stream, CancellationToken ct)
	{
		if(data.Length >= LightweightThreshold)
			throw new ArgumentException($"Data too large for lightweight mode: {data.Length}");

		//[magic:1][length:2][data:N]
		var buffer = new byte[3 + data.Length];

		buffer[0] = LightweightMagic;
		BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(1), (ushort)data.Length);
		data.CopyTo(buffer, 3);

		await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	private async Task<T> ReceiveLightweightAsync<T>(NetworkStream stream, CancellationToken ct)
	{
		var lengthBuffer = new byte[2];
		if(!await ReadExactlyAsync(stream, lengthBuffer, ct).ConfigureAwait(false))
			throw new EndOfStreamException("Stream ended while reading lightweight length.");

		var dataLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);

		var data = new byte[dataLength];
		if(dataLength > 0 && !await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
			throw new EndOfStreamException("Stream ended while reading lightweight data.");

		var result = _serializer.Deserialize<T>(data);
		return result ?? throw new HyperionProtocolException("Lightweight deserialization returned null.");
	}

	#endregion

	#region Direct Protocol

	private static async Task SendDirectAsync(byte[] data, NetworkStream stream, CancellationToken ct)
	{
		if(data.Length >= DirectThreshold)
			throw new ArgumentException($"Data too large for direct mode: {data.Length}");

		//[magic:1][length:4][data:N]
		var headerBuffer = new byte[5];
		headerBuffer[0] = DirectMagic;
		BinaryPrimitives.WriteInt32BigEndian(headerBuffer.AsSpan(1), data.Length);

		await stream.WriteAsync(headerBuffer, ct).ConfigureAwait(false);

		if(data.Length > 0)
			await stream.WriteAsync(data, ct).ConfigureAwait(false);

		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	private async Task<T> ReceiveDirectAsync<T>(NetworkStream stream, CancellationToken ct)
	{
		var lengthBuffer = new byte[4];
		if(!await ReadExactlyAsync(stream, lengthBuffer, ct).ConfigureAwait(false))
			throw new EndOfStreamException("Stream ended while reading direct length.");

		var dataLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

		if(dataLength < 0 || dataLength >= DirectThreshold)
			throw new HyperionProtocolException($"Invalid direct data length: {dataLength}");

		var data = new byte[dataLength];
		if(dataLength > 0 && !await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
			throw new EndOfStreamException("Stream ended while reading direct data.");

		var result = _serializer.Deserialize<T>(data);
		return result ?? throw new HyperionProtocolException("Direct deserialization returned null.");
	}

	#endregion

	#region Chunked Protocol

	private async Task<T> ReceiveChunkedAsync<T>(NetworkStream stream, byte[] headerLengthBuffer, CancellationToken ct)
	{
		int headerLength = BinaryPrimitives.ReadInt32BigEndian(headerLengthBuffer);

		var chunks = new List<ChunkData>();
		int totalChunks = int.MaxValue;
		Guid? expectedPacketId = null;

		var headerBytes = new byte[headerLength];
		if(!await ReadExactlyAsync(stream, headerBytes, ct).ConfigureAwait(false))
			throw new EndOfStreamException($"Stream ended while reading {headerLength}-byte header.");

		var firstHeader = JsonSerializer.Deserialize<PacketHeader>(headerBytes)
			?? throw new HyperionProtocolException("First header deserialized to null.");

		expectedPacketId = firstHeader.PacketId;
		totalChunks = firstHeader.TotalChunks;

		var firstData = new byte[firstHeader.DataLength];
		if(firstHeader.DataLength > 0 && !await ReadExactlyAsync(stream, firstData, ct).ConfigureAwait(false))
			throw new EndOfStreamException("Stream ended while reading first chunk data.");

		chunks.Add(new ChunkData(firstHeader.ChunkNumber, firstData));

		for(int i = 1; i < totalChunks; i++)
		{
			ct.ThrowIfCancellationRequested();

			var nextHeaderLengthBuf = new byte[4];
			if(!await ReadExactlyAsync(stream, nextHeaderLengthBuf, ct).ConfigureAwait(false))
				throw new EndOfStreamException("Stream ended while reading next header length.");

			int nextHeaderLength = BinaryPrimitives.ReadInt32BigEndian(nextHeaderLengthBuf);

			var nextHeaderBytes = new byte[nextHeaderLength];
			if(!await ReadExactlyAsync(stream, nextHeaderBytes, ct).ConfigureAwait(false))
				throw new EndOfStreamException("Stream ended while reading next header.");

			var header = JsonSerializer.Deserialize<PacketHeader>(nextHeaderBytes)
				?? throw new HyperionProtocolException("Header deserialized to null.");

			var data = new byte[header.DataLength];
			if(header.DataLength > 0 && !await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
				throw new EndOfStreamException("Stream ended while reading chunk data.");

			chunks.Add(new ChunkData(header.ChunkNumber, data));
		}

		var completeData = CombineChunks(chunks);
		var result = _serializer.Deserialize<T>(completeData);

		return result ?? throw new HyperionProtocolException("Chunked deserialization returned null.");
	}

	private static byte[] CombineChunks(List<ChunkData> chunks)
	{
		int totalLength = chunks.Sum(c => c.Data.Length);
		var result = new byte[totalLength];
		int offset = 0;

		foreach(var chunk in chunks.OrderBy(c => c.ChunkNumber))
		{
			chunk.Data.CopyTo(result, offset);
			offset += chunk.Data.Length;
		}

		return result;
	}

	#endregion

	#region Helper Methods

	/// <summary>ReadExactly.</summary>
	private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
	{
		int totalRead = 0;
		while(totalRead < buffer.Length)
		{
			int bytesRead = await stream.ReadAsync(buffer.Slice(totalRead), ct).ConfigureAwait(false);
			if(bytesRead == 0)
				return false; // EOF reached

			totalRead += bytesRead;
		}
		return true;
	}

	#endregion

	#region Statistics 

	/// <summary>UsingStatistics.</summary>
	public ProtocolStats Stats { get; } = new();

	#endregion
}
