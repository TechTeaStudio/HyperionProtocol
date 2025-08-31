using System.Net;
using System.Net.Sockets;

using TechTeaStudio.Protocols.Hyperion;

class Program
{
	static async Task Main()
	{
		Console.WriteLine("Starting Hyperion Protocol minimal test...");

		_ = RunServerAsync();
		await Task.Delay(500); 
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
				var client = await listener.AcceptTcpClientAsync();
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
				var protocol = new HyperionProtocol(new DefaultSerializer());
				var message = await protocol.ReceiveAsync<string>(networkStream);

				Console.WriteLine($"[Server] Received: {message}");

				await protocol.SendAsync($"Echo: {message}", networkStream);
				await networkStream.FlushAsync();
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
			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Loopback, 6000);
			using var networkStream = client.GetStream();

			var protocol = new HyperionProtocol(new DefaultSerializer());
			const string testMessage = "Hello HyperionProtocol!";

			Console.WriteLine($"[Client] Sending: {testMessage}");

			await protocol.SendAsync(testMessage, networkStream);
			var response = await protocol.ReceiveAsync<string>(networkStream);

			Console.WriteLine($"[Client] Received: {response}");
			Console.WriteLine(response.StartsWith("Echo:") ? "✓ Test PASSED" : "✗ Test FAILED");
		}
		catch(Exception ex)
		{
			Console.WriteLine($"[Client] ✗ Test FAILED: {ex.Message}");
		}
	}
}
