# Changelog

All notable changes to this package are documented here.
Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] — 2026-05-13

Feature release — streaming receive, first-class `System.IO.Pipelines` support, and protocol-version negotiation. No breaking changes; the wire format stays backward compatible with `0.2.x` (new `PacketHeader.Version` field is optional, missing == legacy v1).

### Added
- **`HyperionProtocol.ReceiveStreamingAsync(NetworkStream, CancellationToken)`** returning `IAsyncEnumerable<ReadOnlyMemory<byte>>` — yields each chunk payload as it arrives so a caller can stream straight to disk or another `Stream` without buffering the full message. Validation (magic, version, order, end-flag, packet-id continuity) is performed before every yield.
- **Pipelines API** — `SendAsync<T>(T, PipeWriter, CancellationToken)` and `Task<T> ReceiveAsync<T>(PipeReader, CancellationToken)`. Uses `PipeWriter.GetSpan`/`Advance` for zero-copy writes and `PipeReader.ReadAsync`+`AdvanceTo` for natural backpressure. Works transparently over `PipeReader.Create(networkStream)` / `PipeWriter.Create(networkStream)`.
- **Protocol versioning** — `PacketHeader.Version` field, `HyperionProtocol.ProtocolVersion` (currently `1`), `HyperionProtocol.MinSupportedProtocolVersion` (currently `0`, accepts legacy headers from 0.2.x senders that didn't emit the field).
- **`HyperionProtocol.HandshakeAsync(NetworkStream, int? localVersion, CancellationToken)`** — static helper that exchanges `"TTSH"` + version with the peer and returns the negotiated minimum. Optional; use it on connection if you want explicit version pinning.
- **`StreamingAndPipelinesTests`** — round-trip coverage for streaming, the Pipelines path (small + large payloads), handshake (matching + version-mismatch), and version validation (legacy zero, future-version rejection).
- **`PerformanceAndStressTests`** — `[Category("Performance")]` and `[Category("Stress")]` NUnit tests with loopback throughput measurements (50/100 MiB one-way via `HyperionProtocol`, Pipelines, and streaming receive), small-message ops/sec for `SmartHyperionProtocol`, plus concurrent-client stress at 50/100 connections and a sustained-load test (100 clients × 20 RTs).
- **`TechTeaStudio.Protocols.Hyperion.Benchmarks`** — separate BenchmarkDotNet console project with `OneWayThroughputBenchmarks`, `SmartProtocolBenchmarks`, and `ConcurrentClientsBenchmarks`. Targets `net8.0;net9.0;net10.0`, marked `IsPackable=false`. Run with `dotnet run -c Release --project ... --filter "*"`.

### Changed
- **`PacketHeader`** now emits a `Version` field. System.Text.Json on the receiving side silently ignores unknown fields, and the validator treats missing/zero version as legacy v1, so wire compatibility with 0.2.x is preserved both directions.
- **`System.IO.Pipelines 8.0.0`** added as a `PackageReference`. Brings the API onto all four target frameworks (net6/8/9/10) uniformly.

### Removed
- `IMPROVEMENTS_ANALYSIS.md` — outdated AI-style analysis doc that lived in repo root. The same suggestions now live in PR descriptions / future-work issues.

### Wire format
- Backward compatible. New senders write `"Version":1` into the header; old receivers ignore the unknown field. Old senders omit the field; new receivers see `Version=0` and treat as v1.

---

## [0.2.0] — 2026-05-12

Audit, cleanup, and multi-targeting pass. The wire format is **unchanged**; this is a source-level breaking change only (a handful of public methods were renamed or removed — see below). Most callers using `HyperionProtocol` / `SmartHyperionProtocol` with `SendAsync`/`ReceiveAsync` are unaffected.

### Added
- **`net6.0` target.** The library now multi-targets `net6.0;net8.0;net9.0;net10.0`. (Tests still target `net8.0;net9.0;net10.0`.)
- **`HyperionProtocolOptions`** — configurable `ChunkSize` (default 1 MiB) and `MaxHeaderLength` (default 64 KiB), validated in the constructor. Both ends of a connection must agree on these values.
- **Functional `ProtocolStats`.** `SmartHyperionProtocol.Stats` is now actually incremented (lightweight/direct/chunked counters and an approximate `TotalBytesSaved` estimate). `GetStatsSnapshot()` and `ResetStats()` are also exposed.
- **`SmartHyperionProtocolTests`** — round-trip tests for each framing mode (lightweight, direct, chunked) plus a stats-reset test.
- **XML documentation** is generated and packed into the `.nupkg` (`GenerateDocumentationFile=true`).
- **`CHANGELOG.md`** packed into the `.nupkg` alongside `README.md` and `LICENSE`.

### Changed
- **`SmartHyperionProtocol` chunked receive path** now reuses base validation (magic, total-chunks, chunk-number order, end-flag, packet-id continuity) instead of a weaker parallel implementation. Bad chunked traffic is rejected the same way for both protocols.
- **`HyperionProtocol` constructor** now requires a non-null `ISerializer`. The previous silent fallback to `new DefaultSerializer()` was dead defensive code and has been removed in favour of `ArgumentNullException`.
- **`ValidateHeader` / `ValidateHeaderLength`** now take the active `ChunkSize` / `MaxHeaderLength` explicitly. They used to read class-level constants and could not reflect the configured options.
- **`ProtocolStats` is no longer nested** inside `SmartHyperionProtocol` — it is a top-level `TechTeaStudio.Protocols.Hyperion.Protocols.ProtocolStats` and is `sealed`.
- **`SmartHyperionProtocol` is no longer `partial`** (no remaining reason for it).
- Test fixtures now bind to an ephemeral port (`IPAddress.Loopback, 0`) and use a single listener — the previous fixture started a second `TcpListener` on the same hard-coded port from inside the accept loop, which is why the tests were `[Ignore]`'d.

### Removed
- **Broken `ReadExactlyAsync(Stream, Span<byte>, ...)` overload.** It called `buffer.ToArray()` to escape `Span<byte>` across an `await`, which both allocated and silently corrupted the contract. The `byte[]` and `Memory<byte>` overloads remain.
- **Dead `DeserializeHeader(byte[])` overload.** `DeserializeHeader(ReadOnlySpan<byte>)` is the canonical entry point.
- **Dead `IsValidUtf8String` helper** from `DefaultSerializer`.
- **`HyperionProtocolTests` `[Ignore]` attribute.**

### Fixed
- **`HyperionProtocolTests` dual-listener bug.** `OneTimeSetUp` started a listener; the accept loop also started a listener on the same port (which silently failed). Fixed by passing the actual listener into the loop and binding to an ephemeral port.
- **Redundant header-data buffer copy in `ReceiveChunksAsync`.** The receive path rented a buffer from `ArrayPool`, copied it into a freshly-allocated `byte[]`, and immediately returned the rental — net zero benefit. The payload buffer is now allocated directly and read once.

### Wire format
- Unchanged. A `0.1.x` client and a `0.2.0` server (and vice versa) remain wire-compatible as long as both ends use the default `ChunkSize` (1 MiB) and `MaxHeaderLength` (64 KiB).

### Migration notes (from 0.1.x)

| 0.1.x                                        | 0.2.0                                                                                      |
| -------------------------------------------- | ------------------------------------------------------------------------------------------ |
| `new HyperionProtocol(null)` → silent default | `ArgumentNullException`. Pass `new DefaultSerializer()` explicitly.                       |
| `ValidateHeader(header, …)` (4 args)         | `ValidateHeader(header, expected, totalChunks, receivedCount, chunkSize)` (5 args)         |
| `ValidateHeaderLength(headerLength)`         | `ValidateHeaderLength(headerLength, maxHeaderLength)`                                      |
| `DeserializeHeader(byte[])`                  | `DeserializeHeader(ReadOnlySpan<byte>)`                                                    |
| `ReadExactlyAsync(Stream, Span<byte>, ct)`   | Removed — use the `byte[]` or `Memory<byte>` overload.                                     |
| `SmartHyperionProtocol.ProtocolStats` (nested) | `TechTeaStudio.Protocols.Hyperion.Protocols.ProtocolStats` (top-level)                   |

---

## [0.1.4] — 2026-04-28

- Switched CI to the shared TechTeaStudio NuGet publish workflow (`TechTeaStudio/.github/.github/workflows/nuget-publish-reusable.yml@main`).
- License refresh.

## [0.1.3] — `.NET 10` update

- Added `net10.0` target alongside `net8.0` and `net9.0`.
- Introduced `Span<T>` / `IBufferWriter<byte>` overloads on `ISerializer` and the default serializer for zero-copy paths.
- Added `ArrayPool<byte>` usage for header buffers in `ReceiveChunksAsync`.

## [0.1.x] — `Smart Protocol`

- Added `SmartHyperionProtocol` with adaptive framing (lightweight / direct / chunked).

## [0.1.0] — Initial release

- Chunked TCP messaging protocol with pluggable serialization, async/await API, and cancellation support.

[0.3.0]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.3.0
[0.2.0]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.2.0
[0.1.4]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.1.4
[0.1.3]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.1.3
[0.1.0]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.1.0
