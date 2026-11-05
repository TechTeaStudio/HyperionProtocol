# Hyperion Protocol: design and rationale

This document answers four questions about Hyperion: what it is, why it exists, what it is for
(and not for), and why you would pick it over the alternatives. The wire format itself is in
[SPECIFICATION.md](SPECIFICATION.md); the item-by-item review against the protocol design checklist
is in [DESIGN_CHECKLIST.md](DESIGN_CHECKLIST.md).

## 1. What Hyperion is

Hyperion is a **message framing and chunking layer for point-to-point binary communication between
.NET processes over a reliable byte stream**: TCP, TLS, named pipes, Unix domain sockets, or anything
else that exposes a `Stream` or a `PipeReader`/`PipeWriter`.

It turns an endless stream of bytes into a sequence of discrete, size-bounded, validated, optionally
integrity-checked **messages**. It has no opinion about what a message means or how it is encoded: a
message is whatever your serializer produces, from three bytes to many gigabytes.

```
┌─────────────────────────────────────────────────────────────┐
│ Application                                                 │  message types, correlation ids,
│ request/response, push, file transfer, game state, telemetry│  retries, auth decisions: yours
├─────────────────────────────────────────────────────────────┤
│ Serialization                                               │  ISerializer: System.Text.Json,
│ objects <-> bytes                                           │  MessagePack, protobuf-net, raw bytes
├─────────────────────────────────────────────────────────────┤
│ Hyperion                                                    │  framing, chunking, limits, integrity,
│ bytes <-> messages                                          │  timeouts, versioning, telemetry
├─────────────────────────────────────────────────────────────┤
│ Security                                                    │  TLS via SslStream, client certificates
├─────────────────────────────────────────────────────────────┤
│ Transport                                                   │  TCP, named pipes, Unix sockets,
│ reliable ordered byte stream                                │  in-memory pipes (tests)
└─────────────────────────────────────────────────────────────┘
```

Hyperion is the third layer. Everything below it is provided by the operating system and the .NET
base class library; everything above it is the application's business. The layer boundaries are the
reason the library stays small: it never duplicates what TLS or TCP already do, and it never guesses
what the application wants.

## 2. Why it exists

Every long-lived TCP service written against raw sockets ends up re-implementing the same five things,
and the first version of each is usually wrong in a way that only shows up in production:

| Problem | Typical first attempt | What goes wrong |
|---|---|---|
| Message boundaries | `[length:4][payload]` loop | Partial reads, `int` overflow on a hostile length, allocating 2 GiB because a peer said so |
| Large payloads | Read the whole thing into a `byte[]` | 2 GiB array limit, memory spikes, no way to stream to disk |
| Serializer coupling | Framing code calls `JsonSerializer` directly | Cannot switch to MessagePack without touching the wire code |
| Validation | Trust the header | Corrupt or malicious frames are detected late, or never |
| Operational behaviour | None | No timeouts, no limits, no metrics, exceptions with no classification |

The existing alternatives sit at the two extremes. On one side are frameworks (gRPC, SignalR) that
solve all of this but bring HTTP/2 or WebSocket stacks, code generation, their own hosting model and
a large dependency graph. On the other side is raw `TcpClient`, where everything is yours to write.
Hyperion fills the gap: the framing problem is solved once, properly, with tests, and nothing else is
imposed.

## 3. Purpose and scope

### 3.1 The model

- **Point-to-point.** Exactly two peers, both under your control. There is no routing, no broker,
  no topic.
- **Message-oriented over a stream.** Peers exchange discrete messages of 0 bytes to `MaxMessageSize`
  (default 1 GiB, configurable up to `long.MaxValue` for streamed transfers).
- **Full duplex.** The two directions are independent. Request/response, server push, and
  bidirectional streaming are all application patterns built on the same primitive.
- **Payload-agnostic.** Bytes in, bytes out. The serializer is a one-interface plug-in.
- **Stateless library objects.** A `HyperionProtocol` instance holds configuration only; every piece
  of connection state lives in the stream. One instance serves any number of connections from any
  number of threads.

### 3.2 Domains it covers

1. **Client applications talking to their own backend** over TCP or TLS. This is where the library
   comes from: it carries the traffic of the Hyperion Omni Client. Small control messages travel
   with 3 bytes of overhead; large downloads are chunked and streamed.
2. **Service-to-service binary messaging inside a .NET system** where gRPC is more machinery than the
   problem deserves: a coordinator and its workers, agents and a control plane, a sidecar and its host.
3. **File and blob transfer.** `SendStreamingAsync` reads from a file and `ReceiveStreamingAsync`
   writes to one; nothing is buffered in full, every chunk can carry a CRC-32C, and the per-chunk
   structure makes application-level resume (send from an offset) trivial.
4. **Game and simulation servers.** Many tiny state updates per second (lightweight framing) mixed with
   occasional large snapshots (chunked framing) on the same connection, with the sender picking the
   layout automatically.
5. **Edge and gateway software** on constrained links: deterministic overhead, bounded memory per
   connection, no HTTP stack.
6. **Local inter-process communication** over named pipes or Unix domain sockets, where a framing
   layer is needed but a network protocol stack is not.
7. **Test harnesses.** Because the API takes a `Stream`, protocol logic can be exercised over
   `MemoryStream` or an in-memory `Pipe` with no sockets at all.

### 3.3 Non-goals

Hyperion deliberately does **not** do the following, and the checklist explains for each where the
responsibility lies instead:

- Browser interoperability (use WebSocket or SignalR).
- Remote procedure call semantics, service definitions, code generation (use gRPC).
- Publish/subscribe, queues, persistence, routing (use NATS, MQTT, RabbitMQ).
- Multiplexing several logical streams over one connection. A connection carries one message at a
  time per direction; open a second connection for a second concurrent transfer.
- Built-in encryption or authentication. TLS does this better than any application protocol can; the
  `Stream` API accepts an `SslStream` directly.
- Exactly-once delivery across connection loss. Within a connection, TCP guarantees order and
  completeness; across reconnects, idempotency keys belong to the application.

## 4. Advantages over the alternatives

| | Hyperion | gRPC | SignalR | Raw WebSocket | StreamJsonRpc | Hand-rolled TCP |
|---|---|---|---|---|---|---|
| Overhead of a 100-byte message | 3 bytes | ~100+ bytes (HTTP/2 frame + HEADERS + 5-byte gRPC prefix per call) | ~60+ bytes (hub protocol envelope) | 2 to 14 bytes + masking | ~80+ bytes of JSON-RPC envelope | whatever you write |
| Message size | up to `MaxMessageSize` (1 GiB default), unlimited when streamed | 4 MiB default, configurable | 32 KiB default, configurable | transport frames only, you reassemble | one JSON document | whatever you write |
| Streaming large payloads to disk | yes, both directions, chunk by chunk | server-streaming RPCs (you design the chunk message) | streaming hub methods | manual | no | whatever you write |
| Per-chunk integrity | optional CRC-32C | no (TLS only) | no | no | no | no |
| Serializer | any, one interface | protobuf (others possible with effort) | JSON or MessagePack hub protocols | yours | JSON | yours |
| Dependencies | `System.IO.Pipelines` | `Grpc.*`, HTTP/2 stack | `Microsoft.AspNetCore.SignalR.*` | in-box | `StreamJsonRpc` + Nerdbank.Streams | none |
| TLS | `SslStream`, in-box | built in | built in | built in | depends on the stream | `SslStream` |
| Resource limits, timeouts, error codes, metrics, tracing | built in | built in | built in | you | partial | you |
| Works over named pipes, in-memory streams | yes | no (HTTP/2 only) | no | no | yes | yes |
| Browser clients | no | grpc-web | yes | yes | no | no |

The honest summary: Hyperion wins when both ends are .NET, you own both, and you want the smallest
protocol that is still safe to expose to a network. It loses when you need browsers, polyglot
clients, or RPC tooling.

### 4.1 Costs you accept

- Chunked frames carry a JSON header of roughly 120 to 160 bytes. On a 1 MiB chunk that is 0.015%
  and it buys a human-readable, trivially extensible header; on small messages the adaptive framing
  avoids it entirely.
- No multiplexing means head-of-line blocking within a connection: a 1 GiB transfer delays a small
  message sent after it on the same connection. The intended fix is a second connection, which is
  cheap on TCP.
- The reference implementation is .NET only. The wire format is simple enough to port in an
  afternoon, and the specification exists for that purpose.

## 5. Design principles

1. **Boring on the wire.** Big-endian length prefixes, a JSON header, no bit packing. A frame can be
   read in a hex dump and the header pasted into any JSON viewer.
2. **Receiver-side paranoia.** Every length is validated before anything is allocated; message size
   limits are enforced from the first header, before a payload byte is read; every failure is a
   `HyperionProtocolException` with a stable `HyperionErrorCode`. Fuzz tests enforce that no other
   exception type can escape and that a receiver can never hang on bad input.
3. **No state in the library.** Connection state lives in the stream. This makes instances shareable,
   reconnection trivial, and the threading contract simple: one sender and one receiver per connection.
4. **Pay only for what you use.** Checksums are off by default, telemetry costs nothing unless
   something listens, timeouts are off unless configured, and an `Activity` is never allocated
   without a listener.
5. **Compatibility by construction.** Receivers ignore unknown header members and reserved flag bits;
   `Version` 0 is read as 1; features are added as optional members. The protocol version only moves
   when a frame becomes unreadable to an older peer, and the handshake exists to negotiate that.

## 6. What the design review changed (0.4.0)

Walking the 50-item checklist against 0.3.0 produced three kinds of findings:

- **Gaps that needed code.** No upper bound on a message (a hostile first header could drive a
  receiver to exhaust memory), no timeouts (a silent peer held a receive forever), no integrity check
  beyond TCP's 16-bit checksum, EOF handling that differed between code paths, exceptions with no
  classification, no telemetry, no way to send a file without loading it, and an API that only
  accepted `NetworkStream` and therefore could not carry TLS. All of these are fixed in 0.4.0 without
  changing a byte of the existing wire format.
- **Items that are correctly someone else's job.** Encryption, authentication, authorization, replay
  protection, delivery guarantees, acknowledgements, retries, keep-alive, reconnection, compression,
  message typing, correlation. For each, the checklist names the layer that owns it and gives the
  recipe, so that "not in the library" never means "not thought about".
- **Items that are designed but intentionally deferred.** Control frames (ping/close), feature
  negotiation beyond the version number, per-frame compression. They need a new frame type and a
  negotiated capability, which is exactly what the reserved magic byte range and the two-phase
  handshake extension in the specification are for.

## 7. Roadmap

Ordered by how much they change the wire:

1. **Session helper (no wire change).** A `HyperionConnection` type that owns a stream, serializes
   sends, runs a receive loop, applies a reconnection policy and raises typed events. Everything it
   needs already exists in 0.4.0.
2. **Handshake phase 2 (compatible).** After both peers agree on a version >= 2, exchange a 32-bit
   feature bitmask. Older peers never see the second phase.
3. **Control frames (needs phase 2).** Magic bytes in the reserved `0x80..0xFD` range for
   ping/pong, graceful close and "go away", enabling protocol-level heartbeats and clean shutdown.
4. **Per-chunk compression (compatible, needs phase 2 to be safe).** An optional `Encoding` header
   member; receivers that do not understand it must have declined the feature in the handshake.
5. **Checksummed small frames (compatible via new magic bytes).** Lightweight/direct variants that
   carry a CRC-32C for links without TLS.
