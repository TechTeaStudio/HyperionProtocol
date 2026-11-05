using System.Text.Json.Serialization;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Source-generated System.Text.Json metadata for <see cref="PacketHeader"/>: no reflection on the hot path,
/// no warm-up cost on the first frame, and trimming/AOT friendly.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(PacketHeader))]
internal sealed partial class HyperionJsonContext : JsonSerializerContext
{
}
