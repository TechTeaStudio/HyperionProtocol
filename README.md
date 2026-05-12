<p align="center">
  <img src="https://raw.githubusercontent.com/TechTeaStudio/HyperionProtocol/main/src/TechTeaStudio.Protocols.Hyperion/icon2.png" alt="HyperionProtocol logo" width="160" />
</p>

<h1 align="center">HyperionProtocol</h1>

<p align="center">
  Chunked TCP messaging for .NET. Pluggable serialization, adaptive framing, streaming receive, and <code>System.IO.Pipelines</code> support &mdash; without the weight of a full RPC framework.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TechTeaStudio.Protocols.Hyperion"><img alt="NuGet" src="https://img.shields.io/nuget/v/TechTeaStudio.Protocols.Hyperion.svg?logo=nuget&label=NuGet" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Protocols.Hyperion"><img alt="Downloads" src="https://img.shields.io/nuget/dt/TechTeaStudio.Protocols.Hyperion.svg?logo=nuget&label=Downloads" /></a>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-6.0%20%7C%208.0%20%7C%209.0%20%7C%2010.0-512BD4?logo=dotnet&logoColor=white" />
  <a href="https://github.com/TechTeaStudio/HyperionProtocol/actions/workflows/dotnet.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/TechTeaStudio/HyperionProtocol/dotnet.yml?branch=main&logo=github&label=build" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
</p>

## Overview

HyperionProtocol solves three problems that every long-lived TCP service in .NET eventually re-implements: message framing, chunking of payloads that don't fit in one socket write, and a clean boundary between the wire format and your serializer of choice. It does this with a small, focused public surface, four target frameworks, and no opinion about what your messages contain.

The package powers the Hyperion Omni Client but is independent: any .NET app that talks over `NetworkStream` &mdash; or anything that exposes a `Stream` / `PipeReader` / `PipeWriter` &mdash; can adopt it as a one-line replacement for hand-rolled framing.

## When to reach for it

You want this library when you need a **point-to-point binary protocol over TCP** and you'd rather not write the framing yourself. Concretely, that means:

- A client and a server you own on both ends.
- Messages that may be small (a few bytes) or large (multi-gigabyte file transfers).
- A serializer you've already chosen &mdash; `System.Text.Json`, MessagePack, protobuf-net, MemoryPack, whatever &mdash; that you want to keep using as-is.
- An async/await codebase that benefits from `IAsyncEnumerable` and `System.IO.Pipelines`.

You probably **don't** want this library when you need browser interop (use WebSocket or SignalR), when you need full method-call RPC semantics with code generation (use gRPC), or when you need a message broker for pub/sub (use NATS or MQTT).

## How it compares

| Library / Approach | What it gives you | Schema / codegen | Native chunking | Streaming receive | Browser | Dep footprint |
|---|---|---|---|---|---|---|
| **HyperionProtocol** | Framing + adaptive chunking + pluggable serializer | None | Yes (default 1 MiB) | `IAsyncEnumerable<ReadOnlyMemory<byte>>` | No | `System.IO.Pipelines` |
| **gRPC** (`Grpc.Net.Client`) | Full RPC framework with services, methods, streaming | `.proto` + codegen | Via HTTP/2 framing | Yes (server-streaming RPCs) | Through grpc-web | Large (`Grpc.*`, HTTP/2 stack) |
| **SignalR client** | Real-time hub/RPC over WebSocket / SSE / long-poll | None at wire level, attribute-based at API | No (transport-level only) | Streaming hub methods | First-class | Large (`Microsoft.AspNetCore.SignalR.Client` + transports) |
| **`System.Net.WebSockets`** | Frame-based bidirectional messaging | None | No (you split & reassemble) | Manual | First-class | In-box |
| **`StreamJsonRpc`** | JSON-RPC 2.0 over any `Stream` | None | No | No (response is one message) | Through duplex pipe wrappers | `StreamJsonRpc` |
| **Raw `TcpClient` + custom framing** | Everything is yours | None | Whatever you write | Whatever you write | No | None |

The honest pitch: HyperionProtocol sits in the gap between **"raw TCP with handmade length-prefixed framing"** and **"a full RPC framework"**. If you're about to write your fifth `[length:4][payload]` loop and then realise you also need to chunk multi-megabyte payloads and stream them straight to disk, this is the package that already did all of that.

## Install

```bash
dotnet add package TechTeaStudio.Protocols.Hyperion
```

Or pin a specific version in `.csproj`:

```xml
<PackageReference Include="TechTeaStudio.Protocols.Hyperion" Version="0.3.0" />
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
        var protocol = new SmartHyperionProtocol(new DefaultSerializer());

        var message = await protocol.ReceiveAsync<string>(stream);
        await protocol.SendAsync($"Echo: {message}", stream);
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

Anything bigger than 64 KiB falls onto the chunked path (default chunk size: 1 MiB). For receivers that don't want to buffer the full message in memory, use the streaming variant &mdash; it yields each chunk as it arrives.

```csharp
await using var file = File.Create("incoming.bin");
await foreach (var chunk in protocol.ReceiveStreamingAsync(stream))
{
    await file.WriteAsync(chunk);
}
```

Validation (magic, version, chunk order, end-flag, packet-id continuity) runs before every `yield`, so corrupt or out-of-order traffic surfaces as a `HyperionProtocolException` immediately, not after the whole payload has been read.

### System.IO.Pipelines

For high-throughput services, use the Pipelines overloads for zero-copy writes and natural backpressure:

```csharp
var writer = PipeWriter.Create(networkStream);
var reader = PipeReader.Create(networkStream);

await protocol.SendAsync(message, writer);
var response = await protocol.ReceiveAsync<MyType>(reader);
```

## Adaptive framing

`SmartHyperionProtocol` picks the cheapest layout per message. You don't tune anything &mdash; the sender decides based on payload size:

| Payload | Wire format | Overhead |
|---|---|---|
| `< 1 KiB` | `[magic:1 = 0xFF][length:2 BE][data]` | **3 bytes** |
| `< 64 KiB` | `[magic:1 = 0xFE][length:4 BE][data]` | **5 bytes** |
| anything else | chunked frames with a JSON `PacketHeader` (same as base `HyperionProtocol`) | **~120 bytes per chunk** |

The receiver reads one byte to disambiguate: `0xFF`/`0xFE` pick lightweight/direct, anything else is taken as the high byte of the chunked header-length prefix (a JSON header is always small enough that this byte is `0x00`).

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

## Protocol version negotiation

`PacketHeader.Version` is part of every chunked frame; the validator accepts versions in `[MinSupportedProtocolVersion .. ProtocolVersion]` (currently `0..1`, with `0` treated as legacy v1 from 0.2.x senders). An optional handshake helper exchanges a magic preamble plus version on connection and returns the minimum both sides agree to speak:

```csharp
int version = await HyperionProtocol.HandshakeAsync(stream);
if (version < HyperionProtocol.ProtocolVersion)
{
    // adapt: fall back to features the older peer understands
}
```

## Wire format

```
Chunked frame (HyperionProtocol & SmartHyperionProtocol fallback):

  ┌──────────────────┬────────────────────┬─────────────────┐
  │ Header Length    │ JSON Header        │ Chunk Payload   │
  │ 4 bytes BE       │ N bytes (≤ 64 KiB) │ ≤ 1 MiB         │
  └──────────────────┴────────────────────┴─────────────────┘

Header (JSON):
  { "Version":1, "Magic":"TTS", "PacketId":"<guid>",
    "ChunkNumber":0, "TotalChunks":5, "DataLength":1048576, "Flags":0 }

Smart-mode framing for small messages skips the JSON header entirely:

  Lightweight (<1 KiB):  [0xFF][len:2 BE][data]
  Direct      (<64 KiB): [0xFE][len:4 BE][data]
```

Both ends of a connection must agree on `ChunkSize` and `MaxHeaderLength`; the receiver rejects oversized chunks and headers. The defaults are 1 MiB and 64 KiB respectively, configurable via `HyperionProtocolOptions`.

## Public API

```csharp
public class HyperionProtocol
{
    public const int ProtocolVersion = 1;
    public const int MinSupportedProtocolVersion = 0;

    public HyperionProtocol(ISerializer serializer);
    public HyperionProtocol(ISerializer serializer, HyperionProtocolOptions options);

    public int ChunkSize { get; }
    public int MaxHeaderLength { get; }

    // NetworkStream API
    public virtual Task SendAsync<T>(T message, NetworkStream stream, CancellationToken ct = default);
    public virtual Task<T> ReceiveAsync<T>(NetworkStream stream, CancellationToken ct = default);
    public virtual IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingAsync(NetworkStream stream, CancellationToken ct = default);

    // Pipelines API
    public virtual Task SendAsync<T>(T message, PipeWriter writer, CancellationToken ct = default);
    public virtual Task<T> ReceiveAsync<T>(PipeReader reader, CancellationToken ct = default);

    // Optional handshake
    public static Task<int> HandshakeAsync(NetworkStream stream, int? localVersion = null, CancellationToken ct = default);
}

public class SmartHyperionProtocol : HyperionProtocol
{
    public ProtocolStats Stats { get; }
    public ProtocolStats GetStatsSnapshot();
    public void ResetStats();
}

public interface ISerializer
{
    byte[] Serialize<T>(T obj);
    T Deserialize<T>(byte[] data);
    void Serialize<T>(IBufferWriter<byte> writer, T obj);
    T Deserialize<T>(ReadOnlySpan<byte> data);
}

public sealed class HyperionProtocolOptions
{
    public const int DefaultChunkSize = 1024 * 1024;
    public const int DefaultMaxHeaderLength = 64 * 1024;

    public int ChunkSize { get; init; } = DefaultChunkSize;
    public int MaxHeaderLength { get; init; } = DefaultMaxHeaderLength;
}
```

Exceptions that escape the receive/send paths are wrapped in `HyperionProtocolException` &mdash; bad magic, bad chunk order, header out of range, mismatched packet id, premature EOF.

## Performance

Numbers from a typical dev box over loopback (Ryzen-class CPU, single TCP pair):

| Test | Result |
|---|---|
| One-way 100 MiB &mdash; chunked `HyperionProtocol` | **~290&ndash;340 MiB/sec** |
| One-way 100 MiB &mdash; Pipelines path | ~250&ndash;280 MiB/sec |
| One-way 100 MiB &mdash; `ReceiveStreamingAsync` | **~430&ndash;500 MiB/sec** (no buffering of the full payload) |
| `SmartHyperionProtocol` lightweight round-trip | ~13&thinsp;000&ndash;14&thinsp;000 ops/sec, ~0.07 ms/op |
| 100 concurrent clients, single round-trip each | ~40 ms total |

These come straight from the `[Category("Performance")]` NUnit tests in this repo. Run them yourself:

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

It includes `OneWayThroughputBenchmarks` (NetworkStream vs Pipelines vs streaming receive across 64 KiB&ndash;64 MiB payloads with `[MemoryDiagnoser]`), `SmartProtocolBenchmarks` (round-trip latency across the lightweight / direct / chunked thresholds), and `ConcurrentClientsBenchmarks` (aggregate throughput at 10 / 50 / 100 clients).

## Project layout

```
HyperionProtocol/
├── src/TechTeaStudio.Protocols.Hyperion/
│   ├── TechTeaStudio.Protocols.Hyperion.sln
│   ├── TechTeaStudio.Protocols.Hyperion/             <- NuGet package source
│   │   ├── Protocols/HyperionProtocol.cs             <- chunked protocol (base)
│   │   ├── Protocols/SmartHyperionProtocol.cs        <- adaptive framing
│   │   ├── Protocols/ChunkData.cs
│   │   ├── ISerializer.cs / DefaultSerializer.cs
│   │   ├── PacketHeader.cs / ProtocolStats.cs
│   │   ├── HyperionProtocolOptions.cs
│   │   └── HyperionProtocolException.cs
│   ├── TechTeaStudio.Protocols.Hyperion.Tests/       <- NUnit (correctness + perf + stress)
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

The library multi-targets `net6.0;net8.0;net9.0;net10.0`. `net7.0` is intentionally skipped (EOL). The test and benchmark projects target `net8.0;net9.0;net10.0` only &mdash; the NUnit3 test platform is unstable on `net6.0`.

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
