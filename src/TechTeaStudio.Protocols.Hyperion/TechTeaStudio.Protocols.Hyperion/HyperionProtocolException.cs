namespace TechTeaStudio.Protocols.Hyperion;

public class HyperionProtocolException : Exception
{
	public HyperionProtocolException(string message) : base(message) { }

	public HyperionProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

