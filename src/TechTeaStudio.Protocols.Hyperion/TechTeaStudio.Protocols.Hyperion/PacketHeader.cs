namespace TechTeaStudio.Protocols.Hyperion;

public sealed class PacketHeader
{
	public string Magic { get; set; } = "TTS"; // For future

	public Guid PacketId { get; set; }

	public int ChunkNumber { get; set; }

	public int TotalChunks { get; set; }

	public int DataLength { get; set; }

	public byte Flags { get; set; }
}
