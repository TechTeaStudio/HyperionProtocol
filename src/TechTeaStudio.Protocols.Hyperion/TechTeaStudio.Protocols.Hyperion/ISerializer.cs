namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>interface for serialization.</summary>
public interface ISerializer
{
	byte[] Serialize<T>(T obj);

	T Deserialize<T>(byte[] data);
}
