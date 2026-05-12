using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// JSON-based default serializer. Fast paths exist for <see cref="string"/> and
/// <see cref="byte"/><c>[]</c> payloads to avoid round-tripping through JSON.
/// </summary>
public class DefaultSerializer : ISerializer
{
    /// <inheritdoc />
    public byte[] Serialize<T>(T obj)
    {
        if (obj is null) return Array.Empty<byte>();

        if (obj is string str)
            return Encoding.UTF8.GetBytes(str);

        if (obj is byte[] bytes)
            return bytes;

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(obj);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to serialize object of type {typeof(T)}", ex);
        }
    }

    /// <inheritdoc />
    public T Deserialize<T>(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            if (typeof(T) == typeof(string)) return (T)(object)string.Empty;
            if (typeof(T) == typeof(byte[]) || typeof(T) == typeof(object))
                return (T)(object)Array.Empty<byte>();
            return default!;
        }

        if (typeof(T) == typeof(string))
            return (T)(object)Encoding.UTF8.GetString(data);

        if (typeof(T) == typeof(byte[]) || typeof(T) == typeof(object))
            return (T)(object)data;

        try
        {
            return JsonSerializer.Deserialize<T>(data)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to deserialize data to type {typeof(T)}", ex);
        }
    }

    /// <inheritdoc />
    public void Serialize<T>(IBufferWriter<byte> writer, T obj)
    {
        if (obj is null) return;

        if (obj is string str)
        {
            var span = writer.GetSpan(Encoding.UTF8.GetByteCount(str));
            var written = Encoding.UTF8.GetBytes(str, span);
            writer.Advance(written);
            return;
        }

        if (obj is byte[] bytes)
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
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to serialize object of type {typeof(T)}", ex);
        }
    }

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            if (typeof(T) == typeof(string)) return (T)(object)string.Empty;
            if (typeof(T) == typeof(byte[]) || typeof(T) == typeof(object))
                return (T)(object)Array.Empty<byte>();
            return default!;
        }

        if (typeof(T) == typeof(string))
            return (T)(object)Encoding.UTF8.GetString(data);

        if (typeof(T) == typeof(byte[]) || typeof(T) == typeof(object))
            return (T)(object)data.ToArray();

        try
        {
            return JsonSerializer.Deserialize<T>(data)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to deserialize data to type {typeof(T)}", ex);
        }
    }
}
