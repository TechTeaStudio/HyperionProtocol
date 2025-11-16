using System.Buffers;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>interface for serialization.</summary>
public interface ISerializer
{
	/// <summary>Serialize object to byte array.</summary>
	byte[] Serialize<T>(T obj);

	/// <summary>Deserialize byte array to object.</summary>
	T Deserialize<T>(byte[] data);

	/// <summary>Serialize object to buffer writer (zero-copy for .NET 10+). Default implementation uses byte array method.</summary>
	void Serialize<T>(IBufferWriter<byte> writer, T obj)
	{
		var data = Serialize(obj);
		var span = writer.GetSpan(data.Length);
		data.CopyTo(span);
		writer.Advance(data.Length);
	}

	/// <summary>Deserialize from read-only span (zero-copy for .NET 10+). Default implementation converts to array.</summary>
	T Deserialize<T>(ReadOnlySpan<byte> data)
	{
		if(data.IsEmpty)
			return Deserialize<T>(Array.Empty<byte>());

		var array = data.ToArray();
		return Deserialize<T>(array);
	}
}
