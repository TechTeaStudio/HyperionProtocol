using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class HyperionProtocolTests
{
	private TcpListener? _listener;
	private CancellationTokenSource? _serverCts;
	private const int TestPort = 6071; 

	[OneTimeSetUp]
	public async Task OneTimeSetUp()
	{
		_serverCts = new CancellationTokenSource();
		_listener = new TcpListener(IPAddress.Loopback, TestPort);
		_listener.Start();

		_ = RunTestServerAsync(_serverCts.Token);

		await Task.Delay(200);
	}

	[OneTimeTearDown]
	public void OneTimeTearDown()
	{
		_serverCts?.Cancel();
		_listener?.Stop();
		_serverCts?.Dispose();
		_listener?.Dispose();
	}

	[Test]
	public async Task SendReceive_SimpleString_Success()
	{
		// Arrange
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, TestPort);
		using var networkStream = client.GetStream();

		var protocol = new HyperionProtocol(new DefaultSerializer());
		const string testMessage = "Hello HyperionProtocol!";

		// Act
		await protocol.SendAsync(testMessage, networkStream);
		var response = await protocol.ReceiveAsync<string>(networkStream);

		// Assert
		Assert.That(response, Is.EqualTo($"Echo: {testMessage}"));
	}

	[Test]
	public async Task SendReceive_EmptyString_Success()
	{
		// Arrange
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, TestPort);
		using var networkStream = client.GetStream();

		var protocol = new HyperionProtocol(new DefaultSerializer());
		const string testMessage = "";

		// Act
		await protocol.SendAsync(testMessage, networkStream);
		var response = await protocol.ReceiveAsync<string>(networkStream);

		// Assert
		Assert.That(response, Is.EqualTo("Echo: "));
	}

	[Test]
	public async Task SendReceive_LargeString_Success()
	{
		// Arrange
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, TestPort);
		using var networkStream = client.GetStream();

		var protocol = new HyperionProtocol(new DefaultSerializer());
		var largeString = new string('A', 10000); // 10KB строка

		// Act
		var sw = Stopwatch.StartNew();
		await protocol.SendAsync(largeString, networkStream);
		var response = await protocol.ReceiveAsync<string>(networkStream);
		sw.Stop();

		// Assert
		Assert.That(response, Is.EqualTo($"Echo: {largeString}"));
		Console.WriteLine($"Large string test completed in {sw.ElapsedMilliseconds} ms");
	}

	[Test]
	public async Task SendReceive_ByteArray_Success()
	{
		// Arrange
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, TestPort);
		using var networkStream = client.GetStream();

		var protocol = new HyperionProtocol(new DefaultSerializer());
		var testData = GenerateTestData(1024);

		// Act
		await protocol.SendAsync(testData, networkStream);
		var response = await protocol.ReceiveAsync<string>(networkStream);

		// Assert
		Assert.That(response, Is.EqualTo($"Received {testData.Length} bytes"));
	}

	[Test]
	public async Task SendReceive_LargeByteArray_Success()
	{
		// Arrange
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, TestPort);
		using var networkStream = client.GetStream();

		var protocol = new HyperionProtocol(new DefaultSerializer());
		var largeData = GenerateTestData(5 * 1024 * 1024); // 5MB данных

		// Act
		var sw = Stopwatch.StartNew();
		await protocol.SendAsync(largeData, networkStream);
		var response = await protocol.ReceiveAsync<string>(networkStream);
		sw.Stop();

		// Assert
		Assert.That(response, Is.EqualTo($"Received {largeData.Length} bytes"));
		Console.WriteLine($"Large data test (5MB) completed in {sw.ElapsedMilliseconds} ms");
	}

	[Test]
	public async Task SendReceive_MultipleChunks_Success()
	{
		// Arrange
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, TestPort);
		using var networkStream = client.GetStream();

		var protocol = new HyperionProtocol(new DefaultSerializer());
		var largeData = GenerateTestData(2 * 1024 * 1024);

		// Act
		await protocol.SendAsync(largeData, networkStream);
		var response = await protocol.ReceiveAsync<string>(networkStream);

		// Assert
		Assert.That(response, Is.EqualTo($"Received {largeData.Length} bytes"));
	}

	[Test]
	public async Task SendReceive_ConcurrentClients_Success()
	{
		// Arrange
		const int clientCount = 10;
		var tasks = new List<Task>();
		var semaphore = new SemaphoreSlim(5, 5);

		// Act
		for(int i = 0; i < clientCount; i++)
		{
			int clientId = i;
			tasks.Add(Task.Run(async () =>
			{
				await semaphore.WaitAsync();
				try
				{
					using var client = new TcpClient();
					await client.ConnectAsync(IPAddress.Loopback, TestPort);
					using var networkStream = client.GetStream();

					var protocol = new HyperionProtocol(new DefaultSerializer());
					var message = $"Client {clientId} message";

					await protocol.SendAsync(message, networkStream);
					var response = await protocol.ReceiveAsync<string>(networkStream);

					Assert.That(response, Is.EqualTo($"Echo: {message}"));
				}
				finally
				{
					semaphore.Release();
				}
			}));
		}

		// Assert
		await Task.WhenAll(tasks);
		Assert.Pass($"All {clientCount} concurrent clients completed successfully");
	}

	[Test]
	public void SendAsync_NullStream_ThrowsArgumentNullException()
	{
		// Arrange
		var protocol = new HyperionProtocol(new DefaultSerializer());

		// Act & Assert
		Assert.ThrowsAsync<ArgumentNullException>(async () =>
			await protocol.SendAsync("test", null!));
	}

	[Test]
	public void ReceiveAsync_NullStream_ThrowsArgumentNullException()
	{
		// Arrange
		var protocol = new HyperionProtocol(new DefaultSerializer());

		// Act & Assert
		Assert.ThrowsAsync<ArgumentNullException>(async () =>
			await protocol.ReceiveAsync<string>(null!));
	}

	[Test]
	public async Task SendReceive_WithCancellation_ThrowsOperationCanceledException()
	{
		// Arrange
		using var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, TestPort);
		using var networkStream = client.GetStream();

		var protocol = new HyperionProtocol(new DefaultSerializer());
		using var cts = new CancellationTokenSource();

		// Act & Assert
		cts.Cancel();

		Assert.ThrowsAsync<OperationCanceledException>(async () =>
			await protocol.SendAsync("test", networkStream, cts.Token));
	}

	private static async Task RunTestServerAsync(CancellationToken cancellationToken)
	{
		var listener = new TcpListener(IPAddress.Loopback, TestPort);
		listener.Start();

		try
		{
			while(!cancellationToken.IsCancellationRequested)
			{
				try
				{
					var tcpClient = await listener.AcceptTcpClientAsync();
					_ = HandleTestClientAsync(tcpClient, cancellationToken);
				}
				catch(ObjectDisposedException)
				{
					break;
				}
				catch(Exception ex)
				{
					if(!cancellationToken.IsCancellationRequested)
					{
						Console.WriteLine($"[TestServer] Error accepting client: {ex.Message}");
					}
				}
			}
		}
		finally
		{
			listener.Stop();
		}
	}

	private static async Task HandleTestClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
	{
		try
		{
			using(tcpClient)
			using(var networkStream = tcpClient.GetStream())
			{
				var protocol = new HyperionProtocol(new DefaultSerializer());

				var rawData = await protocol.ReceiveAsync<byte[]>(networkStream, cancellationToken);

				string response;

				if(IsLikelyTextData(rawData))
				{
					var str = System.Text.Encoding.UTF8.GetString(rawData);
					response = $"Echo: {str}";
				}
				else
				{
					response = $"Received {rawData.Length} bytes";
				}

				await protocol.SendAsync(response, networkStream, cancellationToken);
				await networkStream.FlushAsync(cancellationToken);
				await Task.Delay(50, cancellationToken);
			}
		}
		catch(OperationCanceledException)
		{
		}
		catch(Exception ex)
		{
			Console.WriteLine($"[TestServer] Error handling client: {ex.Message}");
		}
	}

	private static bool IsLikelyTextData(byte[] data)
	{
		if(data.Length == 0) return true;

		try
		{
			var str = System.Text.Encoding.UTF8.GetString(data);

			if(str.Contains('\0')) return false;

			int printableCount = str.Count(c => char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c));
			return printableCount > data.Length * 0.8;
		}
		catch
		{
			return false;
		}
	}

	private static byte[] GenerateTestData(int size)
	{
		var data = new byte[size];
		var random = new Random(42);
		random.NextBytes(data);
		return data;
	}
}
