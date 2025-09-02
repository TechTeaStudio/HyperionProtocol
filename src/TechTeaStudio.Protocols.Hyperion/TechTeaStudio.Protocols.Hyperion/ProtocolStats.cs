namespace TechTeaStudio.Protocols.Hyperion.Protocols;

public partial class SmartHyperionProtocol
{
	public class ProtocolStats
	{
		public long LightweightMessagesSent { get; internal set; }

		public long DirectMessagesSent { get; internal set; }

		public long ChunkedMessagesSent { get; internal set; }

		public long TotalBytesSaved { get; internal set; }

		public void Reset()
		{
			LightweightMessagesSent = 0;
			DirectMessagesSent = 0;
			ChunkedMessagesSent = 0;
			TotalBytesSaved = 0;
		}

		public override string ToString()
		{
			var total = LightweightMessagesSent + DirectMessagesSent + ChunkedMessagesSent;
			return $"Protocol Stats:\n" +
				   $"Lightweight: {LightweightMessagesSent} ({100.0 * LightweightMessagesSent / Math.Max(1, total):F1}%)\n" +
				   $"Direct: {DirectMessagesSent} ({100.0 * DirectMessagesSent / Math.Max(1, total):F1}%)\n" +
				   $"Chunked: {ChunkedMessagesSent} ({100.0 * ChunkedMessagesSent / Math.Max(1, total):F1}%)\n" +
				   $"Bytes saved: {TotalBytesSaved:N0}";
		}
	}

}
