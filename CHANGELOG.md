# Changelog

All notable changes to this package are documented here.
Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-11-05

Protocol design review release. The library was walked through a 50-item protocol design checklist
(architecture, message specification, reliability, security, connection management, performance,
compatibility, observability and testing); the result is documented in `docs/DESIGN_CHECKLIST.md`,
the wire format is now formally specified in `docs/SPECIFICATION.md`, and `docs/DESIGN.md` explains
purpose, scope and positioning. The gaps the review found are closed in this release **without
changing a byte of the existing wire format**: every frame emitted since 0.1.0 is still accepted,
and with checksums off a 0.4.0 sender emits headers byte-identical to 0.3.0.

### Added
- **`HyperionProtocolOptions.MaxMessageSize`** (default 1 GiB): upper bound on one logical message, enforced on send before anything is written and on receive from the *first* header (before a single payload byte is read, using the chunk count it announces) and again as chunks accumulate. Applies to every receive path including streaming; raise it (up to `long.MaxValue`) for larger streamed transfers.
- **`HyperionProtocolOptions.ReceiveTimeout`** (idle timeout: time waiting for the peer to deliver any bytes, paused while your code processes a streamed chunk) and **`SendTimeout`** (bounds each chunk write and the final flush). Both default to off. Expiry surfaces as `HyperionErrorCode.Timeout` with an inner `TimeoutException`; your own `CancellationToken` always takes precedence and surfaces as `OperationCanceledException`.
- **`HyperionProtocolOptions.ChunkChecksum`** (`ChunkChecksumMode.Off` / `Emit` / `Required`): optional per-chunk **CRC-32C** carried in the new optional `"Crc32C"` header member and verified before a chunk reaches your code. Receivers verify any checksum that is present regardless of their own mode. Public `Crc32C` helper (`Compute`, `Append`) with SSE4.2 / ARMv8 intrinsics and a table fallback.
- **`HyperionErrorCode`** and **`HyperionProtocolException.ErrorCode`**: 21 stable codes (`InvalidMagic`, `UnsupportedVersion`, `InvalidHeaderLength`, `MalformedHeader`, `InvalidChunkCount`, `InvalidChunkNumber`, `InvalidDataLength`, `EndFlagMismatch`, `PacketIdMismatch`, `ChunkOutOfOrder`, `MessageTooLarge`, `ChecksumMismatch`, `ChecksumMissing`, `PeerClosed`, `UnexpectedEndOfStream`, `Timeout`, `HandshakeFailed`, `SerializationFailed`, `SendFailed`, `ReceiveFailed`, `Unspecified`). Each documents whether the connection is still usable. New constructors `(HyperionErrorCode, string)` and `(HyperionErrorCode, string, Exception?)`; the old constructors keep working with `Unspecified`.
- **`HyperionProtocol.SendStreamingAsync(Stream source, long length, Stream destination, CancellationToken)`**: send a payload of any size (a file, a network stream) as one chunked message without buffering it; pairs with `ReceiveStreamingAsync` on the other side for disk-to-disk transfers with `O(ChunkSize)` memory on both ends.
- **`HyperionProtocol.ReceiveStreamingAsync(PipeReader, CancellationToken)`** for the Pipelines path.
- **`SmartHyperionProtocol.ReceiveStreamingAsync(Stream, ...)`** override that understands all three framings (lightweight and direct frames are yielded as a single item).
- **`HyperionDiagnostics`**: a `System.Diagnostics.Metrics` meter (`hyperion.messages.sent/received`, `hyperion.bytes.sent/received`, `hyperion.chunks.sent/received`, tagged by framing mode, and `hyperion.errors` tagged by code and direction) and an `ActivitySource` (one `Hyperion.Send` / `Hyperion.Receive` span per message), both named `TechTeaStudio.Protocols.Hyperion`. Zero cost without listeners.
- **`PacketHeader.Checksum`** (JSON `"Crc32C"`, omitted when null), **`PacketHeader.IsLastChunk`**, **`PacketHeader.EndFlag`**; public constants `SmartHyperionProtocol.LightweightThreshold`, `DirectThreshold`, `LightweightMagic`, `DirectMagic`; `HyperionProtocol.MaxMessageSize` and `Options` properties.
- **Documentation**: `docs/DESIGN.md`, `docs/SPECIFICATION.md`, `docs/DESIGN_CHECKLIST.md`; README sections on limits and timeouts, integrity, error handling, security (TLS), observability and operational recipes (message types and correlation, keep-alive, reconnection, graceful shutdown, concurrency, batching, compression, resource limits).
- **Tests**: `Crc32CTests` (RFC 3720 vectors, hardware vs software, incremental), `LimitsAndValidationTests` (size limits before payload, uniform-chunk rule, error codes, byte-level 0.2.x/0.3.x compatibility, unknown header members, reserved flag bits), `ChecksumTests`, `TimeoutAndDisconnectTests` (idle semantics, slow peers, slow consumers, caller cancellation, send timeout against a non-reading peer, clean vs mid-frame disconnect on every path, handshake failures), `FuzzTests` (750 deterministic cases: random garbage, plausible prefixes, mutated frames, truncation, checksum oracle), `StreamingAndTlsTests` (streaming send to every receive path, file-to-file, Smart streaming receive, `SslStream` end to end with handshake and checksums) and `DiagnosticsTests`. 872 tests in total.
- **`ChecksumBenchmarks`** in the BenchmarkDotNet project.

### Changed
- **All `NetworkStream` parameters are now `Stream`** (`SendAsync`, `SendChunkAsync`, `ReceiveAsync`, `ReceiveStreamingAsync`, `ReceiveChunksAsync`, `HandshakeAsync`). `SslStream` (TLS), named pipes, `MemoryStream` and `Pipe.Reader.AsStream()` now work directly. Source-compatible for every caller (a `NetworkStream` is a `Stream`); subclasses that override the old `NetworkStream` signatures must change them to `Stream`.
- **Receivers enforce the uniform-chunk rule**: in a multi-chunk message every non-final chunk must carry exactly the payload size of chunk 0 and the final chunk 1..that many bytes. Every sender since 0.1.0 produces exactly this, so nothing changes for conforming peers; it is what allows the size limit to be checked before any payload is read.
- **End-of-stream reporting is uniform across all paths**: a disconnect between messages is `PeerClosed` (the normal end of a session, with an inner `EndOfStreamException` as before), a disconnect inside a frame is `UnexpectedEndOfStream`. `ReceiveStreamingAsync` now wraps EOF in `HyperionProtocolException` like the buffered paths instead of leaking a raw `EndOfStreamException`.
- **Serializer failures on send** are now wrapped as `HyperionProtocolException` (`SerializationFailed`) instead of escaping as the serializer's own exception; the connection is untouched because nothing was written.
- **`SendAsync(T, PipeWriter)`** now throws `PeerClosed` when the reader has completed the pipe instead of silently truncating the message; `FlushResult.IsCanceled` surfaces as `OperationCanceledException`.
- **`SmartHyperionProtocol`** serializes a message once (it used to serialize twice on the chunked path), writes lightweight and direct frames with a single transport write, validates lightweight frame lengths (`< 1024`) like it already did for direct frames, and no longer double-wraps exceptions from the base class.
- **Chunk header JSON is produced and parsed by a System.Text.Json source generator** (`HyperionJsonContext`): no reflection on the hot path, no first-frame warm-up, trimming/AOT friendly. Output is byte-identical to 0.3.0.
- **`SendChunkAsync`** writes the length prefix and header in one call and coalesces small frames (header + payload up to 64 KiB) into a single transport write.
- **Null handling after deserialization** is identical on every path (previously the Smart paths rejected any `null` while the base path allowed `null` for `string` and nullable value types).
- **`HyperionProtocolOptions.Validate`** also validates `MaxMessageSize`, the timeouts and `ChunkChecksum`.
- Package tags extended; `InternalsVisibleTo` for the test and benchmark projects.

### Wire format
- Unchanged and backward compatible with 0.1.x, 0.2.x and 0.3.x in both directions. The optional `"Crc32C"` header member is ignored by older receivers (System.Text.Json skips unknown members) and never emitted unless `ChunkChecksum` is `Emit` or `Required`.
- Now formally specified in `docs/SPECIFICATION.md`, including the reserved first-byte range `0x80..0xFD` for future frame types and the reserved handshake phase 2 (feature bitmask) for protocol version 2.

### Migration notes (from 0.3.0)

| 0.3.0 | 0.4.0 |
| --- | --- |
| `override Task SendAsync<T>(T, NetworkStream, CancellationToken)` (and the other `NetworkStream` overrides) | Change the parameter type to `Stream`. Callers need no change. |
| Messages above 1 GiB worked if memory allowed | Set `MaxMessageSize` on both sides (for example `long.MaxValue` for unbounded streamed transfers). |
| `catch (EndOfStreamException)` around `ReceiveStreamingAsync` | `catch (HyperionProtocolException ex) when (ex.ErrorCode is PeerClosed or UnexpectedEndOfStream)`; the `EndOfStreamException` is still the inner exception. |
| `catch (InvalidOperationException)` for serializer failures on send | `catch (HyperionProtocolException ex) when (ex.ErrorCode == SerializationFailed)`; the original exception is the inner exception. |
| `SendAsync(T, PipeWriter)` returned silently when the reader had completed | It throws `PeerClosed`. |

---

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

[0.4.0]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.4.0
[0.3.0]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.3.0
[0.2.0]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.2.0
[0.1.4]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.1.4
[0.1.3]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.1.3
[0.1.0]: https://github.com/TechTeaStudio/HyperionProtocol/releases/tag/v0.1.0
