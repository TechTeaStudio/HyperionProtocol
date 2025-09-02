using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using TechTeaStudio.Protocols.Hyperion;
using TechTeaStudio.Protocols.Hyperion.Protocols;


class Program
{
	static async Task Main()
	{
		Console.WriteLine("Starting Hyperion Protocol minimal test...");

		var sw = Stopwatch.StartNew();
		_ = RunServerAsync();
		await Task.Delay(500);
		sw.Stop();
		Console.WriteLine($"[Main] Server start + wait took {sw.ElapsedMilliseconds} ms");

		await RunSimpleTest();

		Console.WriteLine("Test completed. Press any key to exit...");
		Console.ReadKey();
	}

	static async Task RunServerAsync()
	{
		var listener = new TcpListener(IPAddress.Loopback, 6000);
		listener.Start();
		Console.WriteLine("Server listening on port 6000...");

		while(true)
		{
			try
			{
				var sw = Stopwatch.StartNew();
				var client = await listener.AcceptTcpClientAsync();
				sw.Stop();
				Console.WriteLine($"[Server] Accepted client in {sw.ElapsedMilliseconds} ms");

				_ = HandleClientAsync(client);
			}
			catch(Exception ex)
			{
				Console.WriteLine($"[Server] Error: {ex.Message}");
			}
		}
	}

	static async Task HandleClientAsync(TcpClient tcpClient)
	{
		try
		{
			using(tcpClient)
			using(var networkStream = tcpClient.GetStream())
			{
				var protocol = new SmartHyperionProtocol(new DefaultSerializer());

				var swReceive = Stopwatch.StartNew();
				var message = await protocol.ReceiveAsync<string>(networkStream);
				swReceive.Stop();
				Console.WriteLine($"[Server] Received: {message} (in {swReceive.ElapsedMilliseconds} ms)");

				var swSend = Stopwatch.StartNew();
				await protocol.SendAsync($"Echo: {message}", networkStream);
				await networkStream.FlushAsync();
				swSend.Stop();
				Console.WriteLine($"[Server] Sent echo in {swSend.ElapsedMilliseconds} ms");

				await Task.Delay(50);
			}
		}
		catch(Exception ex)
		{
			Console.WriteLine($"[Server] Error handling client: {ex.Message}");
		}
	}

	static async Task RunSimpleTest()
	{
		try
		{
			var swConnect = Stopwatch.StartNew();
			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Loopback, 6000);
			swConnect.Stop();
			Console.WriteLine($"[Client] Connected in {swConnect.ElapsedMilliseconds} ms");

			using var networkStream = client.GetStream();
			var protocol = new SmartHyperionProtocol(new DefaultSerializer());
			const string testMessage = "Hello HyperionProtocol!";

			Console.WriteLine($"[Client] Sending: {testMessage}");

			var swSend = Stopwatch.StartNew();
			await protocol.SendAsync(testMessage, networkStream);
			swSend.Stop();
			Console.WriteLine($"[Client] Sent in {swSend.ElapsedMilliseconds} ms");

			var swReceive = Stopwatch.StartNew();
			var response = await protocol.ReceiveAsync<string>(networkStream);
			swReceive.Stop();
			Console.WriteLine($"[Client] Received: {response} (in {swReceive.ElapsedMilliseconds} ms)");

			Console.WriteLine(response.StartsWith("Echo:") ? "✓ Test PASSED" : "✗ Test FAILED");
		}
		catch(Exception ex)
		{
			Console.WriteLine($"[Client] ✗ Test FAILED: {ex.Message}");
		}
	}
}
