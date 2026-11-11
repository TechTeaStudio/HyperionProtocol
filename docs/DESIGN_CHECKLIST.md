# Protocol design checklist: Hyperion 0.4.0

A modern network protocol has to answer the 50 questions below. This document answers each of them
for Hyperion, states where the answer lives (code, option, document) and, where the answer is "not
in this library", says which layer owns it and how to wire it up.

**Legend**

| Status | Meaning |
|---|---|
| ✅ Built in | Implemented and tested in the library. |
| 🧩 Delegated | Deliberately handled by a lower layer (TCP, TLS) or by the application, with the recipe given here. Not a gap. |
| 🟡 Partial | Partly covered; the remaining piece is named and on the roadmap. |
| 📋 Planned | Designed in the specification, not implemented yet. |

**Score:** 50 items reviewed. 29 built in, 15 delegated with a recipe, 5 partial, 1 planned.

Updated for 0.5.0: item 36 (compression) moved from delegated to built in, and the handshake
extension that items 27, 28 and 32 refer to is now reserved for protocol version 3.

## Architecture

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 1 | Transport Protocol | ✅ Built in | Any reliable, ordered byte stream: TCP (`NetworkStream`), TLS (`SslStream`), named pipes, Unix domain sockets, in-memory `Pipe`. Not UDP/QUIC datagrams. All APIs take `Stream` or `PipeReader`/`PipeWriter`. | `HyperionProtocol` Stream and Pipelines APIs; SPECIFICATION §2 |
| 2 | Communication Model | ✅ Built in | Point-to-point message passing between two peers you both own. Request/response, push and bidirectional streaming are application patterns on the same primitive. | DESIGN §3.1 |
| 3 | Connection Model | ✅ Built in | Connection-oriented and long-lived. One message at a time per direction; protocol objects hold no per-connection state, so one instance serves every connection and survives reconnects. | DESIGN §5, SPECIFICATION §2 |
| 4 | Duplex Mode | ✅ Built in | Full duplex: the two directions are independent (a send and a receive may run concurrently on one connection). Two concurrent sends on one connection are forbidden. | SPECIFICATION §2; tests `StreamingAndTlsTests` |
| 5 | Encoding Format | ✅ Built in | Binary framing with big-endian lengths; chunked frames carry a UTF-8 JSON header (readable, extensible); payload encoding is pluggable through `ISerializer`. | SPECIFICATION §3, §4 |
| 6 | State Management | ✅ Built in | Explicit per-message reassembly state machine on the receiver (`PacketId`, `TotalChunks`, `Received`, chunk size, bytes so far). No session state in the library. | `HyperionProtocol.ReassemblyState`; SPECIFICATION §4.1.2 |

## Message Specification

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 7 | Message Structure | ✅ Built in | Three frame layouts (lightweight 3-byte prefix, direct 5-byte prefix, chunked with JSON header) documented byte by byte. | SPECIFICATION §4 |
| 8 | Message Types | 🧩 Delegated | No type field at the framing layer: it would cost bytes on every 3-byte frame and duplicate what every serializer already offers (MessagePack unions, `System.Text.Json` polymorphism, protobuf `oneof`). Recipe: an envelope `record Envelope(string Type, Guid CorrelationId, byte[] Body)` or a serializer-level discriminator. | README "Message types and correlation" |
| 9 | Message Framing | ✅ Built in | Length-prefixed, self-delimiting frames; one-byte discriminator selects the layout; reserved byte range `0x80..0xFD` for future frame types. | `SmartHyperionProtocol`; SPECIFICATION §4 |
| 10 | Message Size Limits | ✅ Built in | `ChunkSize` (1 MiB), `MaxHeaderLength` (64 KiB), `MaxMessageSize` (1 GiB), lightweight < 1 KiB, direct < 64 KiB. Enforced before allocation; the message limit is enforced from the first header, before any payload byte, and on the send side before anything is written. | `HyperionProtocolOptions`; `LimitsAndValidationTests` |
| 11 | Message Identification | ✅ Built in | Every chunked message carries a random `PacketId` (UUID) that ties its chunks together and is validated for continuity. Small frames need no id (nothing to reassemble). | `PacketHeader.PacketId`; SPECIFICATION §4.1.1 |
| 12 | Correlation ID | 🧩 Delegated | Correlation is an application concept (which request does this reply answer?). `PacketId` is per transmission, not per conversation, and is not exposed to small frames. Recipe: a correlation id in the envelope (see item 8), propagated to `Activity` baggage if tracing. | README "Message types and correlation" |
| 13 | Serialization Rules | ✅ Built in | `ISerializer` contract: `Serialize` returns bytes (null object → empty), `Deserialize` of empty bytes → empty string / empty array / default; span and `IBufferWriter` overloads for zero-copy; `string` and `byte[]` bypass JSON in the default serializer; null results are rejected for non-nullable targets with `SerializationFailed`. | `ISerializer`, `DefaultSerializer`, `HyperionProtocol.DeserializePayload` |

## Reliability

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 14 | Delivery Guarantees | 🧩 Delegated | Within a connection, TCP delivers every byte exactly once and in order, so every message is delivered exactly once or the connection fails. Across connection loss the guarantee is at-most-once; stronger guarantees need application acknowledgements and idempotency keys. | DESIGN §3.3, SPECIFICATION §2 |
| 15 | Acknowledgements | 🧩 Delegated | No protocol-level ACK frame: TCP acknowledges bytes, and in the dominant request/response pattern the response *is* the acknowledgement. An ACK frame would double the frame count for no gain. Recipe: reply message per request; for one-way streams, a periodic application-level "received up to N". | DESIGN §3.3 |
| 16 | Retries | 🧩 Delegated | The library never retries: it cannot know whether a message is idempotent. After any error (other than the two send-side validation errors) the connection is closed; the application reconnects and re-sends under its own idempotency key. | `HyperionErrorCode` remarks; README "Reconnection" |
| 17 | Timeouts | ✅ Built in | `ReceiveTimeout` (idle: time waiting for bytes, paused while in user code), `SendTimeout` (per chunk and flush, protects against a peer that stops reading), handshake bounded by a cancellation token. Surface as `HyperionErrorCode.Timeout`. | `HyperionProtocolOptions`, `OperationGuard`; `TimeoutAndDisconnectTests` |
| 18 | Duplicate Detection | 🧩 Delegated | TCP prevents duplicates within a connection; the receiver additionally rejects any chunk whose number is not the next expected one. Duplicates across reconnects (a re-sent request) are detected by the application using its own message ids. | SPECIFICATION §4.1.2 step 4 |
| 19 | Ordering Guarantees | ✅ Built in | Messages are delivered in send order within a direction (TCP order plus sequential, validated chunk numbers). Chunks of one message are never interleaved with another message. | SPECIFICATION §2, §4.1.2 |
| 20 | Error Handling | ✅ Built in | One exception type, `HyperionProtocolException`, with a stable `HyperionErrorCode`; every code documents whether the connection is still usable. Clean disconnect (`PeerClosed`) is distinguished from a connection lost mid-frame (`UnexpectedEndOfStream`). Fuzz tests prove no other exception type escapes. | `HyperionErrorCode`; SPECIFICATION §8; `FuzzTests` |

## Security

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 21 | Authentication | 🧩 Delegated | TLS client certificates via `SslStream` (the API takes any `Stream`), or an application-defined first message carrying a token that the server verifies before processing anything else. The library never authenticates by itself; doing it in a framing layer would duplicate TLS badly. | README "Security"; `StreamingAndTlsTests.SslStream_*` |
| 22 | Authorization | 🧩 Delegated | Entirely application-level (what may this authenticated peer do?). The library offers nothing to decide this on, by design. | DESIGN §3.3 |
| 23 | Encryption | 🧩 Delegated | TLS through `SslStream`, in-box, tested end to end with handshake and all three framings over it. Unencrypted TCP is for trusted networks only. | README "Security"; `StreamingAndTlsTests` |
| 24 | Integrity | ✅ Built in | Optional CRC-32C per chunk (`ChunkChecksum` = Off / Emit / Required), hardware accelerated, verified before a chunk is handed to the application. Catches corruption that slips past TCP's 16-bit checksum on long transfers and in storage. Cryptographic integrity against an attacker comes from TLS. Small frames rely on the transport (planned: checksummed variants). | `Crc32C`, `PacketHeader.Checksum`; `ChecksumTests` |
| 25 | Replay Protection | 🧩 Delegated | TLS record sequence numbers make replay within a connection impossible. Replay of application messages across connections is prevented by application nonces or idempotency keys; the framing layer cannot know which messages are sensitive. | SPECIFICATION §10 |
| 26 | Rate Limiting | 🟡 Partial | Per-connection cost bounds are built in (message size, chunk size, header size, idle timeout), so one connection cannot consume unbounded memory or time. Per-peer message *rate* and connection *count* limits belong to the host's accept loop (recipe: `SemaphoreSlim` for connections, a token bucket per connection in the receive loop). A built-in rate limiter is on the session-helper roadmap. | README "Resource limits"; DESIGN §7 |

## Connection Management

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 27 | Handshake | ✅ Built in | Optional `HandshakeAsync`: 8-byte exchange (`"TTSH"` + version), both peers write first then read, negotiated version is the minimum. Phase 2 (feature bitmask) is reserved for version 3. | `HyperionProtocol.HandshakeAsync`; SPECIFICATION §6 |
| 28 | Heartbeat | 🟡 Partial | No ping/pong frames in versions 1 and 2 (they would need a new frame type and negotiation). Liveness today: TCP keep-alive (item 29) plus `ReceiveTimeout`, and an application-level ping message when needed. Control frames are the first item on the version-3 roadmap. | DESIGN §7 |
| 29 | Keep-Alive | 🧩 Delegated | TCP keep-alive on the socket: `socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true)` plus `TcpKeepAliveTime` / `TcpKeepAliveInterval` / `TcpKeepAliveRetryCount` (.NET 5+). Detects dead peers without any protocol traffic. | README "Keep-alive and heartbeats" |
| 30 | Reconnection | 🧩 Delegated | Policy (backoff, jitter, max attempts) is the application's; the library makes it cheap: protocol objects are stateless and reusable, every failure says whether the connection is still usable, and the handshake can be re-run. A session helper with a built-in policy is on the roadmap. | README "Reconnection"; DESIGN §7 |
| 31 | Session Resume | 🧩 Delegated | There is no session identity on the wire; nothing to resume at this layer. For transfers, resume is an application offset: `SendStreamingAsync` from a file position, `ReceiveStreamingAsync` appending. | README "Working with large payloads" |
| 32 | Graceful Shutdown | 🟡 Partial | Half-close at a message boundary is recognised and reported as `PeerClosed` (normal end of session, nothing lost); mid-frame closure is reported separately. Recipe: finish the last send, `Socket.Shutdown(SocketShutdown.Send)`, drain receives until `PeerClosed`. An explicit CLOSE / GOAWAY control frame is part of the version-3 control frames. | `HyperionErrorCode.PeerClosed`; README "Graceful shutdown" |

## Performance

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 33 | Backpressure | ✅ Built in | Pipelines path: native `PipeWriter`/`PipeReader` backpressure. Stream path: the TCP window, with `SendTimeout` bounding a stalled peer. Streaming receive yields one bounded chunk at a time and the idle timer pauses while the consumer works, so a slow consumer slows the producer instead of overflowing memory. | Pipelines API; `ReceiveStreamingAsync`; `OperationGuard` |
| 34 | Buffer Management | ✅ Built in | `ArrayPool` for headers, frames and the streaming send buffer; header + small payload coalesced into one write; source-generated JSON for headers (no reflection on the hot path); payload arrays sized exactly from validated lengths; streaming paths never hold a whole message. | `HyperionProtocol.SendChunkAsync`, `HyperionJsonContext` |
| 35 | Batching | 🧩 Delegated | No automatic batching: the framing layer cannot know which messages may be delayed. Recipe: send a `List<T>` as one message (the serializer handles it), or use the `PipeWriter` overload and let the pipe coalesce writes. The adaptive framing already keeps per-message overhead at 3 bytes, which removes the main reason to batch. | README "Batching" |
| 36 | Compression | ✅ Built in | Opt-in per-chunk Zstandard on .NET 11 (`ChunkCompression`: `Off` / `Accept` / `Compress`, `CompressionThreshold`), off by default. Each chunk is an independent frame, sent compressed only if smaller. Not negotiated: compressed messages carry `Version: 2` and peers that cannot read them fail on the first chunk. Decompression is bounded by `RawLength <= ChunkSize` and `MaxMessageSize`. Below .NET 11, or for other algorithms, the recipe stands: wrap `ISerializer` and cap the decompressed size. | `HyperionProtocolOptions.ChunkCompression`; SPECIFICATION §4.1.3, §10; `CompressionTests` |
| 37 | Resource Limits | ✅ Built in | Per connection: at most `MaxMessageSize` buffered (default 1 GiB), `ChunkSize` in flight when streaming, `MaxHeaderLength` per header, plus time limits. Receiver memory is bounded by bytes actually received plus `MaxMessageSize`; a hostile header cannot trigger a large allocation. With compression accepted, a buffered receive can reach `MaxMessageSize` on far fewer transmitted bytes. | `HyperionProtocolOptions`; SPECIFICATION §10 |
| 38 | Concurrency Limits | 🟡 Partial | Thread-safety contract: one instance for any number of connections and threads; per connection at most one sender and one receiver at a time (frames would interleave otherwise). Connection-count limits are the host's job (accept loop with a `SemaphoreSlim`). A send-serialising session helper is on the roadmap. | README "Concurrency"; DESIGN §7 |

## Compatibility

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 39 | Versioning | ✅ Built in | `ProtocolVersion` / `MinSupportedProtocolVersion` constants, `Version` header member, handshake negotiation, and a written rule for what does and does not bump the version. Package follows SemVer. | SPECIFICATION §7 |
| 40 | Backward Compatibility | ✅ Built in | Frames from 0.1.x, 0.2.x (no `Version`) and 0.3.x (no `Crc32C`) are accepted; with checksums off, 0.4.0 emits byte-identical headers to 0.3.0. Covered by byte-level tests. | `LimitsAndValidationTests` (legacy header tests) |
| 41 | Feature Negotiation | 📋 Planned | Version 1 negotiates only the version number. The two-phase extension (feature bitmask after agreeing on version >= 2) is specified so that it can be added without breaking version-1 peers. | SPECIFICATION §6 |
| 42 | Extensibility | ✅ Built in | Unknown header members are ignored, reserved `Flags` bits are ignored, first-byte range `0x80..0xFD` is reserved for new frame types and rejected loudly today, `PacketHeader` is a plain extensible class. Tested with unknown members and reserved bits. | SPECIFICATION §4, §7.2; `LimitsAndValidationTests` |
| 43 | Deprecation Policy | ✅ Built in | Written policy: wire features are announced as deprecated one minor release ahead and accepted for two more; `MinSupportedProtocolVersion` moves only in a major release; limit defaults may tighten in a minor release with the restoring option named in the changelog. | SPECIFICATION §7.3 |

## Observability & Testing

| # | Item | Status | How Hyperion handles it | Where |
|---|---|---|---|---|
| 44 | Logging | 🧩 Delegated | No logging dependency in a transport library. Every failure carries a code and a message meant to be logged as-is at the call site; the `Meter` and `ActivitySource` are the structured hooks for logging pipelines (OpenTelemetry exporters, `dotnet-counters`, `dotnet-trace`). | README "Observability" |
| 45 | Metrics | ✅ Built in | `System.Diagnostics.Metrics` meter `TechTeaStudio.Protocols.Hyperion`: messages, bytes and frames sent/received (tagged by framing mode) and errors (tagged by code and direction). Zero cost without a listener. | `HyperionDiagnostics`; `DiagnosticsTests` |
| 46 | Tracing | ✅ Built in | `ActivitySource` with one `Hyperion.Send` (Producer) / `Hyperion.Receive` (Consumer) activity per message, tagged with mode, size and frame count, error status on failure. Context propagation across the wire is an application envelope concern (item 12). | `HyperionDiagnostics`; `DiagnosticsTests` |
| 47 | Error Codes | ✅ Built in | `HyperionErrorCode`: 21 stable, documented codes with connection-usability semantics; exceptions print as `[Code] message`. | `HyperionErrorCode`; SPECIFICATION §8 |
| 48 | Fuzz Testing | ✅ Built in | Deterministic, seed-driven fuzz suite (1470 cases): random garbage, garbage with plausible prefixes, mutated valid frames, mutated compressed frames and Zstandard payloads, truncated frames, and a checksum oracle (a corrupted payload is never returned). Oracle: every receiver either returns a payload or throws `HyperionProtocolException`, never anything else, never hangs. Coverage-guided fuzzing (SharpFuzz) is a reasonable next step. | `FuzzTests` |
| 49 | Load Testing | ✅ Built in | NUnit `Performance` and `Stress` categories (throughput floors, 100 concurrent clients, sustained small-message load) and a BenchmarkDotNet project (throughput, latency, concurrency, checksum cost). | `PerformanceAndStressTests`; Benchmarks project |
| 50 | Chaos Testing | 🟡 Partial | Fault injection is tested in-process: peer silent, peer stalls mid-frame, connection dropped at every frame position, reader completing a pipe, socket closed under a writer, slow consumer. Network-level chaos (latency, loss, partition via toxiproxy or `tc netem`) is not automated. | `TimeoutAndDisconnectTests`; `FuzzTests` |

## Reading the result

The 15 delegated items are not omissions; they are the layer boundaries that keep the library at a
few files. Every one of them has a recipe in the README so that a team adopting Hyperion knows
exactly what they still have to decide. The 5 partial items and the planned feature negotiation all
converge on the same two pieces of work: a session helper above the protocol, and control frames
negotiated by the version-3 handshake. Both are designed in the specification and neither requires
touching the existing frames.
