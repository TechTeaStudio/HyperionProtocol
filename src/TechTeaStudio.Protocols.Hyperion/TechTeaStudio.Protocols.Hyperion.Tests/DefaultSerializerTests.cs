namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class DefaultSerializerTests
{
	private DefaultSerializer _serializer = null!;

	[SetUp]
	public void SetUp()
	{
		_serializer = new DefaultSerializer();
	}

	[Test]
	public void Serialize_String_ReturnsUtf8Bytes()
	{
		// Arrange
		const string input = "Hello World";

		// Act
		var result = _serializer.Serialize(input);

		// Assert
		var expected = System.Text.Encoding.UTF8.GetBytes(input);
		Assert.That(result, Is.EqualTo(expected));
	}

	[Test]
	public void Deserialize_Utf8Bytes_ReturnsString()
	{
		// Arrange
		const string expected = "Hello World";
		var input = System.Text.Encoding.UTF8.GetBytes(expected);

		// Act
		var result = _serializer.Deserialize<string>(input);

		// Assert
		Assert.That(result, Is.EqualTo(expected));
	}

	[Test]
	public void SerializeDeserialize_ByteArray_Roundtrip()
	{
		// Arrange
		var input = new byte[] { 1, 2, 3, 4, 5 };

		// Act
		var serialized = _serializer.Serialize(input);
		var result = _serializer.Deserialize<byte[]>(serialized);

		// Assert
		Assert.That(result, Is.EqualTo(input));
	}

	[Test]
	public void Serialize_Null_ReturnsEmptyArray()
	{
		// Act
		var result = _serializer.Serialize<string>(null!);

		// Assert
		Assert.That(result, Is.EqualTo(Array.Empty<byte>()));
	}
}
