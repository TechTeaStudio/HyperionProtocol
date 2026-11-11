<p align="center">
  <img src="https://raw.githubusercontent.com/TechTeaStudio/HyperionProtocol/main/src/TechTeaStudio.Protocols.Hyperion/icon2.png" alt="HyperionProtocol logo" width="160" />
</p>

<h1 align="center">HyperionProtocol</h1>

<p align="center">
  Chunked binary messaging for .NET over TCP, TLS or any <code>Stream</code>. Pluggable serialization, adaptive framing, streaming send and receive, hard limits, idle timeouts, per-chunk CRC-32C, error codes and OpenTelemetry-ready metrics - without the weight of a full RPC framework.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TechTeaStudio.Protocols.Hyperion"><img alt="NuGet" src="https://img.shields.io/nuget/v/TechTeaStudio.Protocols.Hyperion.svg?logo=nuget&label=NuGet" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Protocols.Hyperion"><img alt="Downloads" src="https://img.shields.io/nuget/dt/TechTeaStudio.Protocols.Hyperion.svg?logo=nuget&label=Downloads" /></a>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-6.0%20%7C%208.0%20%7C%209.0%20%7C%2010.0-512BD4?logo=dotnet&logoColor=white" />
  <a href="https://github.com/TechTeaStudio/HyperionProtocol/actions/workflows/dotnet.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/TechTeaStudio/HyperionProtocol/dotnet.yml?branch=main&logo=github&label=build" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
</p>

## Overview

HyperionProtocol solves the problems every long-lived TCP service in .NET eventually re-implements: message framing, chunking of payloads that don't fit in one socket write, a clean boundary between the wire format and your serializer of choice, and the operational hardening around them (size limits enforced before allocation, idle timeouts, integrity checks, classified errors, telemetry). It does this with a small public surface, four target frameworks, and no opinion about what your messages contain.

The package powers the Hyperion Omni Client but is independent: any .NET app that talks over a `NetworkStream`, an `SslStream`, a named pipe - anything that exposes a `Stream`, or a `PipeReader` / `PipeWriter` - can adopt it as a one-line replacement for hand-rolled framing.

## Documentation

| Document | What it answers |
|---|---|
| [docs/DESIGN.md](docs/DESIGN.md) | Why the protocol exists, what it is for and not for, how it compares to the alternatives, design principles, roadmap. |
| [docs/SPECIFICATION.md](docs/SPECIFICATION.md) | The wire format, byte by byte: frame layouts, header members, receiver state machine, checksum, handshake, versioning and deprecation policy, error codes, security considerations. Enough to write a second implementation. |
| [docs/DESIGN_CHECKLIST.md](docs/DESIGN_CHECKLIST.md) | A 50-item protocol design checklist (architecture, reliability, security, connection management, performance, compatibility, observability) answered item by item, with the recipe for everything deliberately left to TLS, TCP or the application. |
| [CHANGELOG.md](CHANGELOG.md) | Release history and migration notes. |

## When to reach for it

You want this library when you need a **point-to-point binary protocol over a reliable stream** and you'd rather not write the framing yourself. Concretely, that means:

- A client and a server you own on both ends.
- Messages that may be small (a few bytes) or large (multi-gigabyte file transfers, streamed from and to disk).
- A serializer you've already chosen - `System.Text.Json`, MessagePack, protobuf-net, MemoryPack, whatever - that you want to keep using as-is.
- TLS when the network is not trusted: wrap the socket in an `SslStream` and pass it in.
- An async/await codebase that benefits from `IAsyncEnumerable` and `System.IO.Pipelines`.

You probably **don't** want this library when you need browser interop (use WebSocket or SignalR), when you need full method-call RPC semantics with code generation (use gRPC), or when you need a message broker for pub/sub (use NATS or MQTT).

## How it compares

| Library / Approach | What it gives you | Schema / codegen | Native chunking | Streaming | Integrity | Browser | Dep footprint |
|---|---|---|---|---|---|---|---|
| **HyperionProtocol** | Framing + adaptive chunking + pluggable serializer + limits, timeouts, error codes, telemetry | None | Yes (default 1 MiB) | Both directions, chunk by chunk | Optional CRC-32C per chunk (+ TLS) | No | `System.IO.Pipelines` |
| **gRPC** (`Grpc.Net.Client`) | Full RPC framework with services, methods, streaming | `.proto` + codegen | Via HTTP/2 framing | Server/client streaming RPCs | TLS | Through grpc-web | Large (`Grpc.*`, HTTP/2 stack) |
| **SignalR client** | Real-time hub/RPC over WebSocket / SSE / long-poll | Attribute-based at API | No (transport-level only) | Streaming hub methods | TLS | First-class | Large (`Microsoft.AspNetCore.SignalR.Client` + transports) |
| **`System.Net.WebSockets`** | Frame-based bidirectional messaging | None | No (you split & reassemble) | Manual | TLS | First-class | In-box |
| **`StreamJsonRpc`** | JSON-RPC 2.0 over any `Stream` | None | No | No (response is one message) | None | Through duplex pipe wrappers | `StreamJsonRpc` |
| **Raw `TcpClient` + custom framing** | Everything is yours | None | Whatever you write | Whatever you write | Whatever you write | No | None |

The honest pitch: HyperionProtocol sits in the gap between **"raw TCP with handmade length-prefixed framing"** and **"a full RPC framework"**. If you're about to write your fifth `[length:4][payload]` loop and then realise you also need to chunk multi-megabyte payloads, stream them straight to disk, reject a hostile length before allocating, and time out a silent peer, this is the package that already did all of that.

## Install

```bash
dotnet add package TechTeaStudio.Protocols.Hyperion
```

Or pin a specific version in `.csproj`:

```xml
<PackageReference Include="TechTeaStudio.Protocols.Hyperion" Version="0.5.0" />
```

## Quick start

### Server

```csharp
using System.Net;
using System.Net.Sockets;
using TechTeaStudio.Protocols.Hyperion;
using TechTeaStudio.Protocols.Hyperion.Protocols;

var listener = new TcpListener(IPAddress.Any, 8080);
listener.Start();

// One protocol instance serves every connection; it holds configuration only.
var protocol = new SmartHyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions
{
    MaxMessageSize = 64 * 1024 * 1024,           // reject anything above 64 MiB before reading it
    ReceiveTimeout = TimeSpan.FromSeconds(30),   // drop peers that go silent mid-message
});

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleClientAsync(client);
}

async Task HandleClientAsync(TcpClient client)
{
    using (client)
    using (var stream = client.GetStream())
    {
        try
        {
            while (true)
            {
                var message = await protocol.ReceiveAsync<string>(stream);
                await protocol.SendAsync($"Echo: {message}", stream);
            }
        }
        catch (HyperionProtocolException ex) when (ex.ErrorCode == HyperionErrorCode.PeerClosed)
        {
            // Normal end of session: the client closed the connection between messages.
        }
    }
}
```

### Client

```csharp
using var client = new TcpClient();
await client.ConnectAsync("localhost", 8080);
using var stream = client.GetStream();

var protocol = new SmartHyperionProtocol(new DefaultSerializer());

await protocol.SendAsync("Hello HyperionProtocol!", stream);
var response = await protocol.ReceiveAsync<string>(stream);
```

### Custom serializer

Implement `ISerializer` to plug in MessagePack, protobuf, MemoryPack, or anything else:

```csharp
public sealed class MessagePackSerializer : ISerializer
{
    public byte[] Serialize<T>(T obj)   => MessagePack.MessagePackSerializer.Serialize(obj);
    public T Deserialize<T>(byte[] data) => MessagePack.MessagePackSerializer.Deserialize<T>(data);
}

var protocol = new SmartHyperionProtocol(new MessagePackSerializer());
```

The default `DefaultSerializer` already short-circuits `string` and `byte[]` to avoid round-tripping them through JSON.

## Working with large payloads

Anything bigger than 64 KiB falls onto the chunked path (default chunk size: 1 MiB). Neither side has to hold the whole message in memory:

```csharp
// Sender: stream a file of any size straight from disk.
await using var file = File.OpenRead("backup.tar");
await protocol.SendStreamingAsync(file, file.Length, stream);

// Receiver: write each chunk as it arrives.
await using var output = File.Create("incoming.bin");
await foreach (var chunk in protocol.ReceiveStreamingAsync(stream))
{
    await output.WriteAsync(chunk);
}
```

Validation (magic, version, chunk order, end flag, packet-id continuity, size limits, checksum) runs before every `yield`, so corrupt or out-of-order traffic surfaces as a `HyperionProtocolException` immediately, not after the whole payload has been read. A buffered `ReceiveAsync<byte[]>` reads the same frames when you do want the whole thing in memory.

Messages are capped by `MaxMessageSize` (default 1 GiB) on both sides. For bigger streamed transfers raise it on the sender and the receiver, up to `long.MaxValue`. To resume an interrupted transfer, send from an offset (`file.Position = resumeAt`, `length - resumeAt`) and append on the receiver; the protocol has no notion of a transfer beyond one message, by design.

### System.IO.Pipelines

For high-throughput services, use the Pipelines overloads for zero-copy writes and natural backpressure:

```csharp
var writer = PipeWriter.Create(networkStream);
var reader = PipeReader.Create(networkStream);

await protocol.SendAsync(message, writer);
var response = await protocol.ReceiveAsync<MyType>(reader);

await foreach (var chunk in protocol.ReceiveStreamingAsync(reader)) { /* ... */ }
```

The Pipelines overloads speak chunked framing only (also on `SmartHyperionProtocol`).

## Adaptive framing

`SmartHyperionProtocol` picks the cheapest layout per message. You don't tune anything - the sender decides based on payload size:

| Payload | Wire format | Overhead |
|---|---|---|
| `< 1 KiB` | `[magic:1 = 0xFF][length:2 BE][data]` | **3 bytes** |
| `< 64 KiB` | `[magic:1 = 0xFE][length:4 BE][data]` | **5 bytes** |
| anything else | chunked frames with a JSON `PacketHeader` (same as base `HyperionProtocol`) | **~140 bytes per chunk** (~160 with checksum) |

The receiver reads one byte to disambiguate: `0xFF`/`0xFE` pick lightweight/direct, `0x00..0x7F` is the high byte of a chunked header-length prefix, and `0x80..0xFD` is reserved for future frame types and rejected. `SmartHyperionProtocol` understands all three framings in `ReceiveAsync` and `ReceiveStreamingAsync`; the base `HyperionProtocol` speaks chunked only, and the two produce identical chunked frames.

You can observe what the picker chose:

```csharp
var stats = protocol.GetStatsSnapshot();
Console.WriteLine(stats);
// Protocol Stats:
// Lightweight: 42 (84.0%)
// Direct:       6 (12.0%)
// Chunked:      2  (4.0%)
// Bytes saved: 6,612
```

## Limits and timeouts

Everything a peer can make you do is bounded by `HyperionProtocolOptions`:

| Option | Default | What it bounds |
|---|---|---|
| `ChunkSize` | 1 MiB | Payload bytes per chunked frame. Both ends should agree; a sender with a smaller chunk size is fine. |
| `MaxHeaderLength` | 64 KiB | Bytes of JSON header per chunk. |
| `MaxMessageSize` | 1 GiB | Bytes per logical message. Enforced on send before anything is written, and on receive from the first header (before any payload byte) and as chunks accumulate. Applies to streamed receives too. |
| `ReceiveTimeout` | off | Idle timeout: how long to wait for the peer to deliver *any* bytes. Restarts whenever data arrives; pauses while your code processes a streamed chunk. |
| `SendTimeout` | off | How long one chunk write (or the final flush) may take. Protects against a peer that stops reading. |
| `ChunkChecksum` | `Off` | Per-chunk CRC-32C policy, see below. |
| `ChunkCompression` | `Off` | Per-chunk Zstandard policy (`Off` / `Accept` / `Compress`), .NET 11 only, see below. |
| `CompressionThreshold` | 1024 | With `Compress`: messages smaller than this many bytes are sent exactly as with compression off. |

Limits are checked before anything is allocated: a hostile header announcing 2 billion chunks costs the receiver one exception, not 2 GiB. The receiver also enforces the *uniform-chunk rule* (every non-final chunk carries exactly the size of chunk 0), which every sender since 0.1.0 has satisfied, and which is what makes the early size check possible.

A timeout surfaces as `HyperionErrorCode.Timeout` with an inner `TimeoutException`; the connection must then be closed. Your own `CancellationToken` always wins over the timer and surfaces as `OperationCanceledException`.

## Integrity checks (CRC-32C)

Chunked frames can carry a CRC-32C of their payload in the header (`"Crc32C"`, hardware accelerated on x64 and ARM64):

```csharp
var options = new HyperionProtocolOptions { ChunkChecksum = ChunkChecksumMode.Emit };     // sender adds a checksum to every chunk
var strict  = new HyperionProtocolOptions { ChunkChecksum = ChunkChecksumMode.Required }; // receiver also rejects chunks without one
```

A receiver verifies any checksum that is present, whatever its own mode, before the chunk reaches your code. `Off` (default) keeps the header byte-identical to 0.3.x. The checksum catches corruption that TCP's 16-bit checksum can miss on long transfers and in storage; it is not a MAC, so use TLS against an active attacker. Lightweight and direct frames rely on the transport.

## Compression (Zstandard, .NET 11)

Chunked frames can carry their payload Zstandard-compressed, chunk by chunk. It is opt-in, and it needs the Zstandard codec that ships with .NET 11, so it is available when the library runs on `net11.0` (not in the browser or on WASI). `HyperionProtocol.IsCompressionSupported` tells you at run time.

```csharp
// Both ends on .NET 11. The sender compresses, the receiver accepts.
var sender   = new SmartHyperionProtocol(serializer, new HyperionProtocolOptions { ChunkCompression = ChunkCompressionMode.Compress });
var receiver = new SmartHyperionProtocol(serializer, new HyperionProtocolOptions { ChunkCompression = ChunkCompressionMode.Accept });
```

| Mode | Emits compressed chunks | Incoming compressed chunk |
|---|---|---|
| `Off` (default) | never | rejected with `CompressionNotSupported`, from the header, before any payload is read |
| `Accept` | never | decompressed |
| `Compress` | for messages of `CompressionThreshold` bytes or more | decompressed |

How it behaves:

- **Per chunk, self-contained.** Every chunk is one complete Zstandard frame with no dictionary and no state shared with other chunks. Streaming keeps its `O(ChunkSize)` memory on both sides, and `ReceiveStreamingAsync` yields the uncompressed chunks.
- **Only when it helps.** A chunk is sent compressed only if the result is strictly smaller; otherwise it goes out raw. Already-compressed data (JPEG, video, archives) costs one attempt per chunk and travels as before. Messages below `CompressionThreshold` are not touched at all.
- **Chunked frames only.** Lightweight and direct frames are never compressed, so with `SmartHyperionProtocol` over a `Stream` compression starts at 64 KiB.
- **Checksums cover what is on the wire.** With `ChunkChecksum` on, the CRC-32C is computed over the compressed bytes and verified before decompression.
- **Limits are about your data.** `MaxMessageSize`, the uniform-chunk rule and the metrics all count uncompressed bytes. The timeouts still measure the transport only.

**It is not negotiated, so you must know your peer.** Turn `Compress` on only towards a peer that runs 0.5.0 or later on .NET 11 with `Accept` or `Compress`. Anything else fails loudly on the first chunk of the first compressed message, and never hands compressed bytes to your serializer:

| Receiver | Result of a compressed message |
|---|---|
| 0.5.0+, .NET 11, `Accept` or `Compress` | received |
| 0.5.0+, `Off` (the only mode below .NET 11) | `CompressionNotSupported` |
| 0.3.0 to 0.4.1 | `UnsupportedVersion` (compressed messages carry protocol version 2) |
| 0.2.x and older | not safe: these versions do not check the version; never compress towards them |

Everything else is unaffected: with `Off` or `Accept` the library emits exactly the bytes 0.4.1 emits, and a 0.5.0 receiver in any mode reads everything older senders produce. On `net6.0` through `net10.0` the library behaves as in 0.4.1, and asking for `Accept` or `Compress` there throws `PlatformNotSupportedException` when the protocol is constructed.

Decompression is bounded: a chunk expands into a buffer of exactly the `RawLength` its header announces, which may not exceed `ChunkSize`, and a payload that would expand to anything else is rejected as `DecompressionFailed`. What compression does change is that a peer can fill a buffered receive up to `MaxMessageSize` while sending far fewer bytes, so on a server that accepts compression from untrusted clients set `MaxMessageSize` to what you are willing to hold per connection, or receive with `ReceiveStreamingAsync`.

## Error handling

Every failure is a `HyperionProtocolException` with a stable `ErrorCode`:

| Code | Meaning | Connection afterwards |
|---|---|---|
| `PeerClosed` | The peer closed the connection between messages (or completed the pipe before a send). | Already closed; the normal end of a session. |
| `UnexpectedEndOfStream` | The connection ended in the middle of a frame. | Close. |
| `Timeout` | No progress within `ReceiveTimeout` / `SendTimeout`. | Close. |
| `MessageTooLarge` | Message exceeds `MaxMessageSize`. | Receive: close. Send: still usable, nothing was written. |
| `SerializationFailed` | The serializer threw or returned null for a non-nullable type. | Send: still usable. Receive: close. |
| `InvalidMagic`, `UnsupportedVersion`, `InvalidHeaderLength`, `MalformedHeader`, `InvalidChunkCount`, `InvalidChunkNumber`, `InvalidDataLength`, `EndFlagMismatch`, `PacketIdMismatch`, `ChunkOutOfOrder`, `ChecksumMismatch`, `ChecksumMissing` | The peer sent a frame this receiver does not accept. | Close. |
| `CompressionNotSupported` | The peer sent a compressed chunk but `ChunkCompression` is `Off` here (always so below .NET 11), or the algorithm is unknown. Detected from the header. | Close, and fix the configuration: the sender must not compress towards this peer. |
| `DecompressionFailed` | A compressed chunk is not a valid Zstandard frame or does not expand to exactly its announced `RawLength`. | Close. |
| `HandshakeFailed` | Bad magic, unsupported version, or connection lost during `HandshakeAsync`. | Close. |
| `SendFailed`, `ReceiveFailed` | Transport I/O error; see `InnerException`. | Close. |

The rule is simple: after any exception other than the two send-side validation errors, the stream position is undefined and the connection must be closed. Fuzz tests guarantee that malformed input can only ever produce a `HyperionProtocolException`, never another exception type and never a hang.

## Security

The API takes any `Stream`, so TLS is an `SslStream` away:

```csharp
// Server
var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
{
    ServerCertificate = certificate,
    ClientCertificateRequired = true,                       // mutual TLS = authentication
    RemoteCertificateValidationCallback = ValidateClient,
});
var message = await protocol.ReceiveAsync<MyType>(tls);

// Client
var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
{
    TargetHost = "api.example.com",
    ClientCertificates = new X509CertificateCollection { clientCertificate },
});
await protocol.SendAsync(message, tls);
```

TLS provides confidentiality, cryptographic integrity, replay protection within a connection, and (with client certificates) authentication. If you prefer token authentication, make the first message of every connection a credential and verify it before processing anything else. Authorization is entirely yours. For servers reachable by untrusted clients, also set `MaxMessageSize` to what you really need, set `ReceiveTimeout`, and configure your serializer's own limits (`MaxDepth` in System.Text.Json, the untrusted-data mode in MessagePack): after framing, the payload is the serializer's problem. The full threat model is in [docs/SPECIFICATION.md](docs/SPECIFICATION.md#10-security-considerations).

## Observability

The library reports through `System.Diagnostics.Metrics` and `System.Diagnostics.ActivitySource`, both named `TechTeaStudio.Protocols.Hyperion`. Nothing is emitted unless something listens.

```csharp
// OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(HyperionDiagnostics.MeterName))
    .WithTracing(t => t.AddSource(HyperionDiagnostics.ActivitySourceName));
```

```bash
dotnet-counters monitor --process-id <pid> --counters TechTeaStudio.Protocols.Hyperion
```

| Instrument | Tags | Meaning |
|---|---|---|
| `hyperion.messages.sent` / `hyperion.messages.received` | `hyperion.mode` = `lightweight` \| `direct` \| `chunked` | Messages completed. |
| `hyperion.bytes.sent` / `hyperion.bytes.received` | `hyperion.mode` | Payload bytes (framing overhead excluded). |
| `hyperion.chunks.sent` / `hyperion.chunks.received` | `hyperion.mode` | Frames. |
| `hyperion.errors` | `hyperion.error.code`, `hyperion.direction` | Protocol exceptions raised. |

Activities: one `Hyperion.Send` (Producer) or `Hyperion.Receive` (Consumer) span per message, tagged with mode, payload size and frame count, with error status and code on failure. The library has no logging dependency: log the exception (its `ToString()` starts with `[ErrorCode]`) at your call site, and let the exporters handle the rest.

## Operational recipes

The things a framing layer should *not* do itself, and how to do them above or below it. The [design checklist](docs/DESIGN_CHECKLIST.md) has the reasoning for each.

### Message types and correlation

The framing layer carries bytes, not types; a type tag on every 3-byte frame would cost more than it gives, and your serializer already has a way to express it. Use an envelope:

```csharp
public sealed record Envelope(string Type, Guid CorrelationId, byte[] Body);

await protocol.SendAsync(new Envelope("GetUser", Guid.NewGuid(), body), stream);
var reply = await protocol.ReceiveAsync<Envelope>(stream);   // match on reply.CorrelationId
```

With MessagePack unions or System.Text.Json polymorphism the discriminator lives inside the serializer and the envelope disappears.

### Keep-alive and heartbeats

Dead-peer detection without protocol traffic is TCP keep-alive:

```csharp
socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
```

Combine it with `ReceiveTimeout` for liveness *within* a message, and with an application-level ping message if you need round-trip measurements. Protocol-level ping/pong frames are planned for protocol version 2 (see the roadmap in [docs/DESIGN.md](docs/DESIGN.md#7-roadmap)).

### Reconnection

Protocol objects are stateless, so reconnection is: open a new socket, optionally re-run `HandshakeAsync`, keep using the same `protocol` instance. Retry policy (exponential backoff with jitter, a cap on attempts) and the decision whether a failed send may be repeated are yours: the library never retries, because it cannot know whether a message is idempotent. Give retried messages an idempotency key in the envelope.

### Graceful shutdown

Finish your last send, then `socket.Shutdown(SocketShutdown.Send)`, then keep receiving until `PeerClosed` arrives. The other side sees `PeerClosed` at its next message boundary and knows nothing was lost; a disconnect in the middle of a frame is reported as `UnexpectedEndOfStream` instead.

### Concurrency

One instance for any number of connections and threads. Per connection: at most one send and one receive in flight at a time (a send and a receive may overlap; two sends would interleave frames). If several producers share a connection, serialize them with a `SemaphoreSlim` or a `Channel<T>` drained by one writer. Bound the number of connections in your accept loop.

### Batching

Send a `List<T>` as one message and let the serializer do the work, or write several messages through the `PipeWriter` overload and let the pipe coalesce. The 3-byte lightweight framing removes most of the reason to batch in the first place.

### Compression

On .NET 11, with both ends under your control, use the built-in per-chunk Zstandard: see [Compression (Zstandard, .NET 11)](#compression-zstandard-net-11). On older targets, for another algorithm, or when only some message types should be compressed, compress between serializer and framing, where you know the data: wrap your `ISerializer` with `BrotliStream` or a zstd binding, and cap the decompressed size on the receiver. With `ChunkCompression` at its default `Off` the framing layer never decompresses, so it cannot be the source of a decompression bomb.

### Resource limits

Per connection the receiver holds at most `MaxMessageSize` (buffered) or `ChunkSize` (streamed) of payload plus bytes actually received; `ReceiveTimeout` bounds how long a stalled peer can hold that. With compression accepted, a buffered receive can reach `MaxMessageSize` on far fewer transmitted bytes, so that limit is the one to size. Per-process limits (connection count, per-peer message rate) belong to the host's accept loop.

## Protocol version negotiation

`PacketHeader.Version` is part of every chunked frame; the validator accepts versions in `[MinSupportedProtocolVersion .. MaxSupportedProtocolVersion]` (currently `0..2`, with `0` treated as legacy v1 from 0.2.x senders). `ProtocolVersion` (`1`) is what the library emits for every uncompressed message and what the handshake advertises by default; version 2 appears only on messages sent with `ChunkCompression = Compress`. An optional handshake helper exchanges a magic preamble plus version on connection and returns the minimum both sides agree to speak:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
int version = await HyperionProtocol.HandshakeAsync(stream, ct: cts.Token);
if (version < HyperionProtocol.ProtocolVersion)
{
    // adapt: fall back to features the older peer understands
}
```

New optional header members that are harmless to ignore (such as `Crc32C`) never bump the version: receivers ignore what they don't know. The version moves only when a frame would become unreadable to an older peer, which is why compressed messages are version 2 while everything else stays version 1. The handshake does not negotiate compression; it is reserved to grow a feature-negotiation phase for version 3. The full policy, including deprecation rules, is in [docs/SPECIFICATION.md](docs/SPECIFICATION.md#7-versioning-and-compatibility-policy).

## Wire format

```
Chunked frame (HyperionProtocol & SmartHyperionProtocol fallback):

  ┌──────────────────┬────────────────────┬─────────────────┐
  │ Header Length    │ JSON Header        │ Chunk Payload   │
  │ 4 bytes BE       │ N bytes (≤ 64 KiB) │ ≤ 1 MiB         │
  └──────────────────┴────────────────────┴─────────────────┘

Header (JSON; "Crc32C" only when checksums are on, unknown members ignored):
  { "Version":1, "Magic":"TTS", "PacketId":"<guid>",
    "ChunkNumber":0, "TotalChunks":5, "DataLength":1048576, "Flags":0, "Crc32C":910901175 }

Header of a compressed chunk (version 2; the payload is one Zstandard frame of DataLength bytes
that expands to RawLength bytes, and "Crc32C" covers the compressed bytes):
  { "Version":2, "Magic":"TTS", "PacketId":"<guid>",
    "ChunkNumber":0, "TotalChunks":5, "DataLength":18734, "Flags":0, "Crc32C":2868463187,
    "Compression":"zstd", "RawLength":1048576 }

Smart-mode framing for small messages skips the JSON header entirely:

  Lightweight (<1 KiB):  [0xFF][len:2 BE][data]
  Direct      (<64 KiB): [0xFE][len:4 BE][data]

Handshake (optional, both peers write then read):

  [0x54 0x54 0x53 0x48 "TTSH"][version:4 BE]
```

The complete, normative description is in [docs/SPECIFICATION.md](docs/SPECIFICATION.md).

## Public API

```csharp
public class HyperionProtocol
{
    public const string ProtocolMagic = "TTS";
    public const int ProtocolVersion = 1;              // emitted for uncompressed messages, advertised by the handshake
    public const int MaxSupportedProtocolVersion = 2;  // accepted; 2 = compressed chunks
    public const int MinSupportedProtocolVersion = 0;
    public static bool IsCompressionSupported { get; } // true on .NET 11+ outside browser/WASI

    public HyperionProtocol(ISerializer serializer);
    public HyperionProtocol(ISerializer serializer, HyperionProtocolOptions options);

    public int ChunkSize { get; }
    public int MaxHeaderLength { get; }
    public long MaxMessageSize { get; }
    public HyperionProtocolOptions Options { get; }

    // Stream API: NetworkStream, SslStream, named pipes, MemoryStream, ...
    public virtual Task SendAsync<T>(T message, Stream stream, CancellationToken ct = default);
    public virtual Task SendStreamingAsync(Stream source, long length, Stream destination, CancellationToken ct = default);
    public virtual Task SendChunkAsync(Stream stream, PacketHeader header, ReadOnlyMemory<byte> data, CancellationToken ct);
    public virtual Task<T> ReceiveAsync<T>(Stream stream, CancellationToken ct = default);
    public virtual IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingAsync(Stream stream, CancellationToken ct = default);

    // Pipelines API
    public virtual Task SendAsync<T>(T message, PipeWriter writer, CancellationToken ct = default);
    public virtual Task<T> ReceiveAsync<T>(PipeReader reader, CancellationToken ct = default);
    public virtual IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingAsync(PipeReader reader, CancellationToken ct = default);

    // Optional handshake
    public static Task<int> HandshakeAsync(Stream stream, int? localVersion = null, CancellationToken ct = default);

    // Building blocks for custom receivers
    public static void ValidateHeaderLength(int headerLength, int maxHeaderLength);
    public static PacketHeader DeserializeHeader(ReadOnlySpan<byte> headerBytes);
    public static void ValidateHeader(PacketHeader header, Guid? expectedPacketId, int expectedTotalChunks, int receivedChunkCount, int chunkSize);
    public static byte[] CombineChunks(List<ChunkData> chunks);
    public static Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct);
    public static Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct);
}

public class SmartHyperionProtocol : HyperionProtocol
{
    public const int LightweightThreshold = 1024;
    public const int DirectThreshold = 64 * 1024;
    public const byte LightweightMagic = 0xFF;
    public const byte DirectMagic = 0xFE;

    public ProtocolStats Stats { get; }
    public ProtocolStats GetStatsSnapshot();
    public void ResetStats();
    // SendAsync / ReceiveAsync / ReceiveStreamingAsync over Stream are overridden to speak all three framings.
}

public sealed class HyperionProtocolOptions
{
    public const int DefaultChunkSize = 1024 * 1024;
    public const int DefaultMaxHeaderLength = 64 * 1024;
    public const long DefaultMaxMessageSize = 1024L * 1024 * 1024;
    public const int DefaultCompressionThreshold = 1024;

    public int ChunkSize { get; init; } = DefaultChunkSize;
    public int MaxHeaderLength { get; init; } = DefaultMaxHeaderLength;
    public long MaxMessageSize { get; init; } = DefaultMaxMessageSize;
    public TimeSpan? ReceiveTimeout { get; init; }
    public TimeSpan? SendTimeout { get; init; }
    public ChunkChecksumMode ChunkChecksum { get; init; } = ChunkChecksumMode.Off;
    public ChunkCompressionMode ChunkCompression { get; init; } = ChunkCompressionMode.Off;
    public int CompressionThreshold { get; init; } = DefaultCompressionThreshold;
}

public enum ChunkChecksumMode { Off, Emit, Required }
public enum ChunkCompressionMode { Off, Accept, Compress }

public class HyperionProtocolException : Exception
{
    public HyperionProtocolException(string message);
    public HyperionProtocolException(string message, Exception innerException);
    public HyperionProtocolException(HyperionErrorCode errorCode, string message);
    public HyperionProtocolException(HyperionErrorCode errorCode, string message, Exception? innerException);
    public HyperionErrorCode ErrorCode { get; }
}

public enum HyperionErrorCode
{
    Unspecified, InvalidMagic, UnsupportedVersion, InvalidHeaderLength, MalformedHeader, InvalidChunkCount,
    InvalidChunkNumber, InvalidDataLength, EndFlagMismatch, PacketIdMismatch, ChunkOutOfOrder, MessageTooLarge,
    ChecksumMismatch, ChecksumMissing, CompressionNotSupported, DecompressionFailed,
    PeerClosed, UnexpectedEndOfStream, Timeout, HandshakeFailed,
    SerializationFailed, SendFailed, ReceiveFailed,
}

public sealed class PacketHeader
{
    public const byte EndFlag = 0x01;
    public const string ZstandardCompression = "zstd";
    public int Version { get; set; }
    public string Magic { get; set; }
    public Guid PacketId { get; set; }
    public int ChunkNumber { get; set; }
    public int TotalChunks { get; set; }
    public int DataLength { get; set; }
    public byte Flags { get; set; }
    public uint? Checksum { get; set; }   // serialized as "Crc32C", omitted when null
    public string? Compression { get; set; }  // "zstd" on a compressed chunk, omitted when null
    public int? RawLength { get; set; }       // uncompressed length of a compressed chunk, omitted when null
    public bool IsLastChunk { get; }
}

public static class Crc32C
{
    public static uint Compute(ReadOnlySpan<byte> data);
    public static uint Append(uint crc, ReadOnlySpan<byte> data);
}

public static class HyperionDiagnostics
{
    public const string MeterName = "TechTeaStudio.Protocols.Hyperion";
    public const string ActivitySourceName = "TechTeaStudio.Protocols.Hyperion";
}

public interface ISerializer
{
    byte[] Serialize<T>(T obj);
    T Deserialize<T>(byte[] data);
    void Serialize<T>(IBufferWriter<byte> writer, T obj);
    T Deserialize<T>(ReadOnlySpan<byte> data);
}
```

## Performance

Numbers from a typical dev box over loopback (Ryzen-class CPU, single TCP pair):

| Test | Result |
|---|---|
| One-way 100 MiB - chunked `HyperionProtocol` | **~290&ndash;340 MiB/sec** |
| One-way 100 MiB - Pipelines path | ~250&ndash;280 MiB/sec |
| One-way 100 MiB - `ReceiveStreamingAsync` | **~430&ndash;500 MiB/sec** (no buffering of the full payload) |
| `SmartHyperionProtocol` lightweight round-trip | ~13&thinsp;000&ndash;14&thinsp;000 ops/sec, ~0.07 ms/op |
| 100 concurrent clients, single round-trip each | ~40 ms total |

These come straight from the `[Category("Performance")]` NUnit tests in this repo. The 0.4.0 hardening does not touch the hot path when its features are off: the idle timer is a no-op without a timeout, telemetry is a null check without listeners, and the header is byte-identical without checksums. With checksums on, CRC-32C runs at several GB/s on hardware with SSE4.2 or ARMv8 CRC instructions. Run the tests yourself:

```bash
dotnet test src/TechTeaStudio.Protocols.Hyperion/TechTeaStudio.Protocols.Hyperion.sln -c Release \
    --filter "TestCategory=Performance|TestCategory=Stress" \
    --logger "console;verbosity=normal"
```

Each performance test asserts a generous lower throughput floor (30 MiB/sec) to catch regressions without flaking on slow CI.

For accurate, statistically-meaningful measurements there's a separate BenchmarkDotNet project:

```bash
dotnet run -c Release \
  --project src/TechTeaStudio.Protocols.Hyperion/TechTeaStudio.Protocols.Hyperion.Benchmarks \
  --framework net9.0 -- --filter "*"
```

It includes `OneWayThroughputBenchmarks` (NetworkStream vs Pipelines vs streaming receive across 64 KiB&ndash;64 MiB payloads with `[MemoryDiagnoser]`), `SmartProtocolBenchmarks` (round-trip latency across the lightweight / direct / chunked thresholds), `ConcurrentClientsBenchmarks` (aggregate throughput at 10 / 50 / 100 clients), and `ChecksumBenchmarks` (CRC-32C hardware vs software, and a chunked send with checksums on vs off).

## Project layout

```
HyperionProtocol/
├── docs/
│   ├── DESIGN.md                                      <- purpose, scope, comparison, principles, roadmap
│   ├── SPECIFICATION.md                               <- normative wire format
│   └── DESIGN_CHECKLIST.md                            <- 50-item protocol design review
├── src/TechTeaStudio.Protocols.Hyperion/
│   ├── TechTeaStudio.Protocols.Hyperion.sln
│   ├── TechTeaStudio.Protocols.Hyperion/             <- NuGet package source
│   │   ├── Protocols/HyperionProtocol.cs             <- chunked protocol (base), streaming, handshake
│   │   ├── Protocols/SmartHyperionProtocol.cs        <- adaptive framing
│   │   ├── Protocols/ChunkData.cs
│   │   ├── ISerializer.cs / DefaultSerializer.cs
│   │   ├── PacketHeader.cs / HyperionJsonContext.cs  <- header + source-generated JSON
│   │   ├── HyperionProtocolOptions.cs / ChunkChecksumMode.cs / ChunkCompressionMode.cs
│   │   ├── ZstdCodec.cs                              <- Zstandard per chunk; the only file with a .NET 11 guard
│   │   ├── HyperionProtocolException.cs / HyperionErrorCode.cs
│   │   ├── Crc32C.cs / OperationGuard.cs / HyperionDiagnostics.cs
│   │   └── ProtocolStats.cs
│   ├── TechTeaStudio.Protocols.Hyperion.Tests/       <- NUnit (correctness, limits, checksums, compression, timeouts, fuzz, TLS, telemetry, perf, stress)
│   ├── TechTeaStudio.Protocols.Hyperion.Benchmarks/  <- BenchmarkDotNet console app
│   └── ConsoleTestApp/                                <- smoke test
├── .github/workflows/dotnet.yml                       <- CI publish (shared TTS workflow)
├── CHANGELOG.md
├── LICENSE
└── README.md
```

## Build &amp; test

```bash
dotnet build src/TechTeaStudio.Protocols.Hyperion/TechTeaStudio.Protocols.Hyperion.sln
dotnet test  src/TechTeaStudio.Protocols.Hyperion/TechTeaStudio.Protocols.Hyperion.sln
```

The library multi-targets `net6.0;net8.0;net9.0;net10.0;net11.0`. `net7.0` is intentionally skipped (EOL). The test and benchmark projects target `net8.0;net9.0;net10.0;net11.0` only - the NUnit3 test platform is unstable on `net6.0`. Test categories: `Performance`, `Stress`, `Fuzz` (filter with `--filter "TestCategory!=Performance&TestCategory!=Stress"` for a quick run).

## Versioning &amp; release

Version lives in `TechTeaStudio.Protocols.Hyperion.csproj` as a 3-part `<Version>X.Y.Z</Version>`. Bump rules:

- Bug fix &rarr; `Z + 1`
- New feature, source-compatible &rarr; `Y + 1`, reset `Z = 0`
- Breaking change in public API or wire format &rarr; `X + 1` (after `1.0`), reset `Y = Z = 0`

Commit format is `vX.Y.Z <short description>`. Push to `main` triggers the shared TechTeaStudio NuGet publish workflow, which packs and pushes to nuget.org with `--skip-duplicate`. **Never push to nuget.org manually.**

See [CHANGELOG.md](CHANGELOG.md) for the full release history.

## License

Licensed under the [MIT License](LICENSE). Copyright &copy; Tech Tea Studio.

<p align="center">
  Built as part of the Hyperion Ecosystem by <a href="https://techteastudio.cc">TechTeaStudio</a>.
</p>
