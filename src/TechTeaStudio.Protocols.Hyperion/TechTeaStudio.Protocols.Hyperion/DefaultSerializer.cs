using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>Default realization for HyperionProtocol.</summary>
public class DefaultSerializer : ISerializer
{
	public byte[] Serialize<T>(T obj)
	{
		if(obj == null) return Array.Empty<byte>();

		if(obj is string str)
		{
			return Encoding.UTF8.GetBytes(str);
		}

		if(obj is byte[] bytes)
		{
			return bytes;
		}

		try
		{
			return JsonSerializer.SerializeToUtf8Bytes(obj);
		}
		catch(Exception ex)
		{
			throw new InvalidOperationException($"Failed to serialize object of type {typeof(T)}", ex);
		}
	}

	public T Deserialize<T>(byte[] data)
	{
		if(data == null || data.Length == 0)
		{
			return default(T)!;
		}

		if(typeof(T) == typeof(string))
		{
			return (T)(object)Encoding.UTF8.GetString(data);
		}

		if(typeof(T) == typeof(byte[]))
		{
			return (T)(object)data;
		}

		if(typeof(T) == typeof(object))
		{
			return (T)(object)data;
		}

		try
		{
			return JsonSerializer.Deserialize<T>(data)!;
		}
		catch(Exception ex)
		{
			throw new InvalidOperationException($"Failed to deserialize data to type {typeof(T)}", ex);
		}
	}

	/// <summary>Serialize to buffer writer (zero-copy optimization).</summary>
	public void Serialize<T>(IBufferWriter<byte> writer, T obj)
	{
		if(obj == null)
		{
			return;
		}

		if(obj is string str)
		{
			var span = writer.GetSpan(Encoding.UTF8.GetByteCount(str));
			var written = Encoding.UTF8.GetBytes(str, span);
			writer.Advance(written);
			return;
		}

		if(obj is byte[] bytes)
		{
			var span = writer.GetSpan(bytes.Length);
			bytes.CopyTo(span);
			writer.Advance(bytes.Length);
			return;
		}

		try
		{
			using var jsonWriter = new Utf8JsonWriter(writer);
			JsonSerializer.Serialize(jsonWriter, obj);
		}
		catch(Exception ex)
		{
			throw new InvalidOperationException($"Failed to serialize object of type {typeof(T)}", ex);
		}
	}

	/// <summary>Deserialize from span (zero-copy optimization).</summary>
	public T Deserialize<T>(ReadOnlySpan<byte> data)
	{
		if(data.IsEmpty)
		{
			return default(T)!;
		}

		if(typeof(T) == typeof(string))
		{
			return (T)(object)Encoding.UTF8.GetString(data);
		}

		if(typeof(T) == typeof(byte[]))
		{
			var result = new byte[data.Length];
			data.CopyTo(result);
			return (T)(object)result;
		}

		if(typeof(T) == typeof(object))
		{
			var result = new byte[data.Length];
			data.CopyTo(result);
			return (T)(object)result;
		}

		try
		{
			return JsonSerializer.Deserialize<T>(data)!;
		}
		catch(Exception ex)
		{
			throw new InvalidOperationException($"Failed to deserialize data to type {typeof(T)}", ex);
		}
	}

	private static bool IsValidUtf8String(string str)
	{
		if(string.IsNullOrEmpty(str)) return true;

		return str.All(c =>
			char.IsLetterOrDigit(c) ||
			char.IsPunctuation(c) ||
			char.IsWhiteSpace(c) ||
			char.IsSymbol(c));
	}
}
