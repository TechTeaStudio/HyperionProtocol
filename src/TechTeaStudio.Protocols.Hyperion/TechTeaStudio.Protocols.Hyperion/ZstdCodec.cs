#if NET11_0_OR_GREATER
using System.IO.Compression;
#endif

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// The only place that touches Zstandard, and the only file with a target-framework guard: the codec ships with
/// .NET 11 (<c>System.IO.Compression.ZstandardEncoder</c> / <c>ZstandardDecoder</c>). Older targets get bodies that
/// throw; they are unreachable because <see cref="HyperionProtocolOptions.Validate"/> refuses every mode but
/// <see cref="ChunkCompressionMode.Off"/> when <see cref="IsSupported"/> is <c>false</c>.
/// </summary>
/// <remarks>
/// Both operations are one-shot static calls: each chunk is a self-contained frame, so there is no encoder or
/// decoder state to keep and protocol instances stay stateless and shareable across threads.
/// </remarks>
internal static class ZstdCodec
{
    private const string Unsupported =
        "Zstandard chunk compression requires .NET 11 or later (not available in the browser or on WASI).";

#if NET11_0_OR_GREATER

    /// <summary>Whether chunks can be compressed and decompressed in this process.</summary>
    internal static bool IsSupported { get; } = !OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi();

    /// <summary>
    /// Compresses <paramref name="raw"/> into <paramref name="destination"/> as one Zstandard frame (default level,
    /// no dictionary, no frame checksum). Returns <c>false</c>, with <paramref name="written"/> = 0, when the frame
    /// does not fit or is not strictly smaller than the input; the caller then sends the chunk raw. Pass a destination
    /// of <c>raw.Length - 1</c> bytes and "does not fit" is the same thing as "did not shrink".
    /// </summary>
    internal static bool TryCompress(ReadOnlySpan<byte> raw, Span<byte> destination, out int written)
    {
        written = 0;
        if (raw.IsEmpty || destination.IsEmpty)
            return false;

        bool ok;
        int produced;
        try
        {
            ok = ZstandardEncoder.TryCompress(raw, destination, out produced);
        }
        catch (Exception ex)
        {
            // The encoder reports "does not fit" by returning false. Anything thrown means the codec itself is
            // unusable (for example the native library failed to load).
            throw new HyperionProtocolException(HyperionErrorCode.CompressionNotSupported,
                "The Zstandard encoder is not usable in this process.", ex);
        }

        if (!ok || produced <= 0 || produced >= raw.Length)
            return false;

        written = produced;
        return true;
    }

    /// <summary>
    /// Decompresses one chunk into a new array of exactly <paramref name="rawLength"/> bytes. The output can never
    /// exceed that array, whatever the frame claims; any other outcome (invalid frame, truncated frame, trailing
    /// bytes, fewer or more bytes than announced, a decoder exception) is
    /// <see cref="HyperionErrorCode.DecompressionFailed"/>.
    /// </summary>
    internal static byte[] Decompress(ReadOnlySpan<byte> wire, int rawLength, int chunkNumber)
    {
        var raw = new byte[rawLength];

        bool ok;
        int written;
        try
        {
            ok = ZstandardDecoder.TryDecompress(wire, raw, out written);
        }
        catch (Exception ex)
        {
            throw new HyperionProtocolException(HyperionErrorCode.DecompressionFailed,
                $"Chunk {chunkNumber}: the Zstandard decoder failed on a {wire.Length}-byte payload.", ex);
        }

        if (!ok)
        {
            throw new HyperionProtocolException(HyperionErrorCode.DecompressionFailed,
                $"Chunk {chunkNumber}: the {wire.Length}-byte payload is not a Zstandard frame that expands to RawLength ({rawLength}) bytes.");
        }
        if (written != rawLength)
        {
            throw new HyperionProtocolException(HyperionErrorCode.DecompressionFailed,
                $"Chunk {chunkNumber}: the payload expands to {written} bytes but the header announces RawLength {rawLength}.");
        }
        return raw;
    }

#else

    /// <summary>Whether chunks can be compressed and decompressed in this process. Always <c>false</c> below .NET 11.</summary>
    internal static bool IsSupported => false;

    internal static bool TryCompress(ReadOnlySpan<byte> raw, Span<byte> destination, out int written)
        => throw new PlatformNotSupportedException(Unsupported);

    internal static byte[] Decompress(ReadOnlySpan<byte> wire, int rawLength, int chunkNumber)
        => throw new PlatformNotSupportedException(Unsupported);

#endif

    /// <summary>Throws <see cref="PlatformNotSupportedException"/> when <paramref name="mode"/> needs a codec this process lacks.</summary>
    internal static void EnsureSupported(ChunkCompressionMode mode)
    {
        if (mode != ChunkCompressionMode.Off && !IsSupported)
            throw new PlatformNotSupportedException($"ChunkCompression = {mode} is not supported here. {Unsupported}");
    }
}
