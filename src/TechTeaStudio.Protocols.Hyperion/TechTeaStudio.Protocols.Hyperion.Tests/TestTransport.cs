using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

/// <summary>Shared transport fixtures: an in-memory duplex pair and a real loopback TCP pair.</summary>
internal static class TestTransport
{
    /// <summary>
    /// An in-memory, single-direction link: everything written to <see cref="Writer"/> can be read from
    /// <see cref="Reader"/>. Completing the writer simulates the peer closing the connection.
    /// </summary>
    internal sealed class Link : IDisposable
    {
        private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));

        /// <summary>Stream the "sender" writes to.</summary>
        public Stream Writer => _writerStream ??= _pipe.Writer.AsStream(leaveOpen: true);

        /// <summary>Stream the "receiver" reads from.</summary>
        public Stream Reader => _readerStream ??= _pipe.Reader.AsStream(leaveOpen: true);

        /// <summary>Raw pipe reader, for the Pipelines overloads.</summary>
        public PipeReader PipeReader => _pipe.Reader;

        /// <summary>Raw pipe writer, for the Pipelines overloads.</summary>
        public PipeWriter PipeWriter => _pipe.Writer;

        private Stream? _writerStream;
        private Stream? _readerStream;

        /// <summary>Writes raw bytes, as a misbehaving or legacy peer would.</summary>
        public async Task WriteRawAsync(params byte[][] parts)
        {
            foreach (var part in parts)
                await Writer.WriteAsync(part);
            await Writer.FlushAsync();
        }

        /// <summary>Simulates the peer closing its side of the connection.</summary>
        public Task CloseAsync() => _pipe.Writer.CompleteAsync().AsTask();

        public void Dispose()
        {
            _pipe.Writer.Complete();
            _pipe.Reader.Complete();
        }
    }

    /// <summary>Two connected TCP sockets over loopback.</summary>
    internal sealed class TcpPair : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly TcpClient _server;

        public NetworkStream ClientStream { get; }
        public NetworkStream ServerStream { get; }

        private TcpPair(TcpListener listener, TcpClient client, TcpClient server)
        {
            _listener = listener;
            _client = client;
            _server = server;
            ClientStream = client.GetStream();
            ServerStream = server.GetStream();
        }

        public static async Task<TcpPair> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var acceptTask = listener.AcceptTcpClientAsync();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var server = await acceptTask;

            return new TcpPair(listener, client, server);
        }

        public ValueTask DisposeAsync()
        {
            ClientStream.Dispose();
            ServerStream.Dispose();
            _client.Dispose();
            _server.Dispose();
            _listener.Stop();
            return default;
        }
    }

    public static byte[] RandomBytes(int size, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}
