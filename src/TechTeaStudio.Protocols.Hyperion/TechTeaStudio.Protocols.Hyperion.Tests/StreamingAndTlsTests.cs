using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class StreamingAndTlsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    // --- SendStreamingAsync ---------------------------------------------------------------------

    [Test]
    public async Task SendStreaming_ToReceiveStreaming_RoundTripsWithoutBuffering()
    {
        var payload = TestTransport.RandomBytes((int)(2.5 * 1024 * 1024));
        var protocol = new HyperionProtocol(new DefaultSerializer());
        using var link = new TestTransport.Link();

        var sendTask = protocol.SendStreamingAsync(new MemoryStream(payload), payload.Length, link.Writer);

        var collected = new List<byte>();
        int chunks = 0;
        await foreach (var chunk in protocol.ReceiveStreamingAsync(link.Reader))
        {
            chunks++;
            collected.AddRange(chunk.ToArray());
        }
        await sendTask;

        Assert.That(chunks, Is.EqualTo(3));
        Assert.That(collected.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public async Task SendStreaming_ToBufferedReceive_OnEveryPath()
    {
        var payload = TestTransport.RandomBytes(1024 * 1024 + 1);
        var protocol = new HyperionProtocol(new DefaultSerializer());

        using var wire = new MemoryStream();
        await protocol.SendStreamingAsync(new MemoryStream(payload), payload.Length, wire);
        var bytes = wire.ToArray();

        Assert.That(await protocol.ReceiveAsync<byte[]>(new MemoryStream(bytes)), Is.EqualTo(payload));
        Assert.That(await new SmartHyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(bytes)), Is.EqualTo(payload));
        Assert.That(await protocol.ReceiveAsync<byte[]>(PipeReader.Create(new MemoryStream(bytes))), Is.EqualTo(payload));
    }

    [Test]
    public async Task SendStreaming_EmptySource_ProducesEmptyMessage()
    {
        var protocol = new HyperionProtocol(new DefaultSerializer());
        using var wire = new MemoryStream();
        await protocol.SendStreamingAsync(new MemoryStream(), 0, wire);
        wire.Position = 0;

        Assert.That(await protocol.ReceiveAsync<byte[]>(wire), Is.Empty);
    }

    [Test]
    public async Task SendStreaming_SourceShorterThanAnnounced_ThrowsSendFailed()
    {
        var protocol = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkSize = 16 });
        using var wire = new MemoryStream();

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => protocol.SendStreamingAsync(new MemoryStream(new byte[40]), 100, wire));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.SendFailed));
        await Task.CompletedTask;
    }

    [Test]
    public async Task SendStreaming_FromFile_ToFile()
    {
        var payload = TestTransport.RandomBytes(3 * 1024 * 1024 + 77);
        var source = Path.GetTempFileName();
        var target = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(source, payload);
            var protocol = new HyperionProtocol(new DefaultSerializer());
            using var link = new TestTransport.Link();

            var sendTask = Task.Run(async () =>
            {
                await using var file = File.OpenRead(source);
                await protocol.SendStreamingAsync(file, file.Length, link.Writer);
            });

            await using (var output = File.Create(target))
            {
                await foreach (var chunk in protocol.ReceiveStreamingAsync(link.Reader))
                    await output.WriteAsync(chunk);
            }
            await sendTask;

            Assert.That(await File.ReadAllBytesAsync(target), Is.EqualTo(payload));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }

    // --- Streaming receive variants ---------------------------------------------------------------

    [Test]
    public async Task Smart_ReceiveStreaming_UnderstandsAllThreeFramings()
    {
        var smart = new SmartHyperionProtocol(new DefaultSerializer());
        var small = Encoding.UTF8.GetBytes("tiny");
        var medium = TestTransport.RandomBytes(5000);
        var large = TestTransport.RandomBytes(2 * 1024 * 1024 + 1);

        using var wire = new MemoryStream();
        await smart.SendAsync(small, wire);
        await smart.SendAsync(medium, wire);
        await smart.SendAsync(large, wire);
        wire.Position = 0;

        Assert.That(await Collect(smart.ReceiveStreamingAsync(wire)), Is.EqualTo((small, 1)));
        Assert.That(await Collect(smart.ReceiveStreamingAsync(wire)), Is.EqualTo((medium, 1)));
        Assert.That(await Collect(smart.ReceiveStreamingAsync(wire)), Is.EqualTo((large, 3)));
    }

    [Test]
    public async Task ReceiveStreaming_PipeReader_YieldsEveryChunk()
    {
        var payload = TestTransport.RandomBytes(2 * 1024 * 1024 + 5);
        var protocol = new HyperionProtocol(new DefaultSerializer());
        var bytes = await Frames.CaptureAsync(protocol, payload);

        var (collected, chunks) = await Collect(protocol.ReceiveStreamingAsync(PipeReader.Create(new MemoryStream(bytes))));
        Assert.That(chunks, Is.EqualTo(3));
        Assert.That(collected, Is.EqualTo(payload));
    }

    [Test]
    public async Task ReceiveStreaming_ConsecutiveMessagesOnOneStream()
    {
        var protocol = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkSize = 1000 });
        var first = TestTransport.RandomBytes(2500, seed: 1);
        var second = TestTransport.RandomBytes(10, seed: 2);

        using var wire = new MemoryStream();
        await protocol.SendAsync(first, wire);
        await protocol.SendAsync(second, wire);
        wire.Position = 0;

        Assert.That(await Collect(protocol.ReceiveStreamingAsync(wire)), Is.EqualTo((first, 3)));
        Assert.That(await Collect(protocol.ReceiveStreamingAsync(wire)), Is.EqualTo((second, 1)));
    }

    // --- TLS -----------------------------------------------------------------------------------

    [Test]
    public async Task SslStream_CarriesTheProtocol_IncludingHandshakeAndAllFramings()
    {
        using var certificate = CreateSelfSignedCertificate();
        await using var pair = await TestTransport.TcpPair.CreateAsync();

        await using var serverTls = new SslStream(pair.ServerStream, leaveInnerStreamOpen: false);
        await using var clientTls = new SslStream(pair.ClientStream, leaveInnerStreamOpen: false, (_, _, _, _) => true);

        var serverAuth = serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.None,
        });
        var clientAuth = clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" });
        await Task.WhenAll(serverAuth, clientAuth).WaitAsync(Deadline);

        var versions = await Task.WhenAll(HyperionProtocol.HandshakeAsync(clientTls), HyperionProtocol.HandshakeAsync(serverTls)).WaitAsync(Deadline);
        Assert.That(versions, Is.All.EqualTo(HyperionProtocol.ProtocolVersion));

        var client = new SmartHyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkChecksum = ChunkChecksumMode.Emit });
        var server = new SmartHyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkChecksum = ChunkChecksumMode.Required });
        var large = TestTransport.RandomBytes(1024 * 1024 + 3);

        foreach (var payload in new[] { Encoding.UTF8.GetBytes("secret"), TestTransport.RandomBytes(5000), large })
        {
            var send = client.SendAsync(payload, clientTls);
            var received = await server.ReceiveAsync<byte[]>(serverTls).WaitAsync(Deadline);
            await send;
            Assert.That(received, Is.EqualTo(payload));

            send = server.SendAsync("ack", serverTls);
            Assert.That(await client.ReceiveAsync<string>(clientTls).WaitAsync(Deadline), Is.EqualTo("ack"));
            await send;
        }

        Assert.That(client.GetStatsSnapshot().TotalMessagesSent, Is.EqualTo(3));
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        var builder = new SubjectAlternativeNameBuilder();
        builder.AddDnsName("localhost");
        request.CertificateExtensions.Add(builder.Build());

        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#endif
    }

    private static async Task<(byte[] Payload, int Chunks)> Collect(IAsyncEnumerable<ReadOnlyMemory<byte>> source)
    {
        var collected = new List<byte>();
        int chunks = 0;
        await foreach (var chunk in source)
        {
            chunks++;
            collected.AddRange(chunk.ToArray());
        }
        return (collected.ToArray(), chunks);
    }
}
