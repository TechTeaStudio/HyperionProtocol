# Hyperion wire protocol specification

**Protocol version:** 2 (library 0.5.0); version 1 is still what every uncompressed message carries.
**Status:** stable; backward compatible with every frame emitted since library 0.1.0.

The key words MUST, MUST NOT, SHOULD and MAY are to be interpreted as in RFC 2119.

## 1. Scope

This document specifies the bytes a Hyperion peer puts on a reliable, ordered byte stream and the
rules a receiver applies to them. It is sufficient to write an independent implementation in any
language. Rationale and positioning live in [DESIGN.md](DESIGN.md); the review against the protocol
design checklist in [DESIGN_CHECKLIST.md](DESIGN_CHECKLIST.md).

## 2. Transport requirements

Hyperion runs over any transport that delivers bytes **reliably, in order, without duplication** and
signals **end of stream**: TCP, TLS over TCP, named pipes, Unix domain sockets, in-memory pipes. It
does not run over datagram transports (UDP, raw QUIC datagrams).

A connection is **full duplex**. The two directions are independent streams of messages and are
specified separately; nothing in one direction depends on the other except the optional handshake
(section 6).

Within one direction, frames are strictly sequential: a sender MUST finish writing one message before
starting the next, and a receiver processes one message at a time.

## 3. Conventions

- All integers are **big-endian** (network byte order).
- `int32` is a signed 32-bit integer; `uint16`/`uint32` are unsigned.
- Text is **UTF-8** without a byte-order mark.
- A **message** is the logical payload the application sent. A **frame** is one unit on the wire.
  A message is carried by exactly one lightweight or direct frame, or by one or more chunked frames.

## 4. Frame types

The first byte of a frame identifies its type:

| First byte | Frame type | Carries |
|---|---|---|
| `0x00`..`0x7F` | Chunked (section 4.1); the byte is the high byte of `HeaderLength` | any message |
| `0xFE` | Direct (section 4.3) | message of 0..65535 bytes |
| `0xFF` | Lightweight (section 4.2) | message of 0..1023 bytes |
| `0x80`..`0xFD` | **Reserved** for future frame types | receivers MUST reject with `InvalidHeaderLength` |

A `HeaderLength` is a positive `int32`, so its high byte is at most `0x7F`; this is what makes the
single-byte discrimination unambiguous. The reserved range is the extension point for new frame
types (control frames, checksummed small frames): a receiver that has not negotiated such a type
rejects it loudly instead of misinterpreting it.

Implementations come in two profiles:

- **Base profile** (`HyperionProtocol`): emits and accepts chunked frames only.
- **Smart profile** (`SmartHyperionProtocol`): emits the smallest applicable frame type and accepts
  all three. A Smart sender MUST use lightweight framing for payloads under 1024 bytes, direct
  framing for payloads under 65536 bytes, and chunked framing otherwise. A Smart receiver MUST
  accept any frame whose own length constraints hold, regardless of those thresholds.

Both profiles produce identical chunked frames, so a base receiver can read what a Smart sender
sends for large payloads, and a Smart receiver can read anything a base sender sends.

### 4.1 Chunked frame

```
offset 0   HeaderLength   int32 BE   1 .. MaxHeaderLength (default 65536)
offset 4   Header         UTF-8 JSON object, HeaderLength bytes
offset 4+HeaderLength
           Payload        DataLength bytes (DataLength is a header member)
```

A message of `L` bytes is sent as `N = max(1, ceil(L / S))` chunked frames, where `S` is the
sender's chunk size (default 1 MiB). Frames `0 .. N-2` carry exactly `S` payload bytes; frame
`N-1` carries the remainder, which is at least 1 byte unless `N = 1` and `L = 0`.

`L`, `S` and the remainder are **logical** lengths: bytes of the message before any compression. In
protocol version 2 the payload of a chunk may be compressed (section 4.1.3); `DataLength` then counts
the compressed bytes on the wire and `RawLength` the logical bytes they expand to. This document
calls the logical length of a chunk its `Length`: `RawLength` when the chunk is compressed,
`DataLength` otherwise.

#### 4.1.1 Header members

| Member | JSON type | Required | Meaning |
|---|---|---|---|
| `Version` | integer | no | Protocol version, `1` or `2` (section 7.1). Absent or `0` MUST be read as `1` (frames from library 0.1.x and 0.2.x omit it). |
| `Magic` | string | yes | MUST be `"TTS"`. |
| `PacketId` | string | yes | RFC 4122 UUID in hyphenated text form. Identical for every chunk of one message; SHOULD be freshly random per message. |
| `ChunkNumber` | integer | yes | Zero-based index of this chunk within the message. |
| `TotalChunks` | integer | yes | Number of chunks in the message, `>= 1`, identical for every chunk. |
| `DataLength` | integer | yes | Number of payload bytes following the header on the wire, `0 .. ChunkSize`. |
| `Flags` | integer (0..255) | yes | Bit 0 (`0x01`): set on the final chunk only. Bits 1..7 are reserved: senders MUST write `0`, receivers MUST ignore them. |
| `Crc32C` | integer (uint32) | no | CRC-32C of the payload bytes of this chunk as transmitted (section 5). Present only when the sender emits checksums. |
| `Compression` | string | no | Version 2. Compression applied to the payload of this chunk. The only defined value is `"zstd"` (section 4.1.3). Absent or `null`: the payload is raw. |
| `RawLength` | integer | with `Compression` | Version 2. Logical (uncompressed) length of this chunk, `1 .. ChunkSize`. Required when `Compression` is present; MUST be ignored when it is absent. |

Rules for the header object:

- Member names are case-sensitive.
- Member order is not significant. This implementation emits them in the order of the table above
  and omits `Crc32C`, `Compression` and `RawLength` when they do not apply, so a version 1 header is
  byte-identical to what library 0.4.x emits.
- Receivers MUST ignore members they do not recognise. This is the primary extension mechanism:
  new optional members may be added without a version change, as long as ignoring them is harmless
  (section 7.1).
- A member of the wrong JSON type, a value out of range for its type, or any JSON text that is not a
  single object MUST be rejected as `MalformedHeader`. Trailing content after the object within
  `HeaderLength`, other than JSON whitespace, MUST be rejected as `MalformedHeader`.
- Senders MUST NOT emit a member twice. Receivers MAY reject a duplicated member or use its last
  occurrence (this implementation uses the last).
- A frame whose `Magic` member is absent MUST be rejected (`InvalidMagic`). A receiver MAY treat any
  other absent required member as `0` and let the validation rules of section 4.1.2 decide (this
  implementation does; for example an absent `TotalChunks` fails as `InvalidChunkCount`).

#### 4.1.2 Reassembly state machine

A receiver keeps the following state per message: `PacketId` (unset until the first chunk),
`TotalChunks`, `Received` (count of accepted chunks), `S` (logical length of chunk 0) and
`TotalBytes` (logical bytes so far). For each frame it performs the steps below **in this order**, stopping at the first
failure. Error names refer to section 8.

1. Read 4 bytes. End of stream after 0 bytes with `Received = 0` is `PeerClosed` (clean end of
   session); after 1..3 bytes, or at any later point of a frame, it is `UnexpectedEndOfStream`.
2. `HeaderLength` MUST be in `1 .. MaxHeaderLength`, otherwise `InvalidHeaderLength`. Nothing is
   allocated before this check.
3. Read `HeaderLength` bytes and parse the header (`MalformedHeader` on failure).
4. Validate the header against the state:
   - `Magic == "TTS"` (`InvalidMagic`);
   - effective version in `MinSupportedVersion .. MaxSupportedVersion` (`UnsupportedVersion`);
   - `TotalChunks >= 1` (`InvalidChunkCount`);
   - `0 <= ChunkNumber < TotalChunks` (`InvalidChunkNumber`);
   - `0 <= DataLength <= ChunkSize` (`InvalidDataLength`);
   - end flag set if and only if `ChunkNumber == TotalChunks - 1` (`EndFlagMismatch`);
   - if the state has a `PacketId`: same `PacketId` (`PacketIdMismatch`) and same `TotalChunks`
     (`InvalidChunkCount`);
   - `ChunkNumber == Received` (`ChunkOutOfOrder`).
5. Determine the chunk's logical `Length`. If `Compression` is absent, `Length = DataLength` (and a
   `RawLength` member, if any, is ignored). If `Compression` is present:
   - the receiver does not decompress, by policy or because it has no codec
     (`CompressionNotSupported`);
   - the value is not `"zstd"` (`CompressionNotSupported`);
   - `RawLength` is absent or outside `1 .. ChunkSize` (`InvalidDataLength`);
   - `DataLength == 0` (`InvalidDataLength`);
   - otherwise `Length = RawLength`.
6. Apply the **uniform-chunk rule** to `Length` (`InvalidDataLength` on failure):
   - if this is chunk 0 and `TotalChunks > 1`: `Length >= 1`; remember `S = Length`;
   - if `0 < ChunkNumber < TotalChunks - 1`: `Length == S`;
   - if `ChunkNumber == TotalChunks - 1 > 0`: `1 <= Length <= S`.
7. Apply the **message size limit** (`MessageTooLarge` on failure):
   - on chunk 0 with `TotalChunks > 1`: `(TotalChunks - 1) * S + 1 <= MaxMessageSize`, which is the
     smallest message the header can describe; this rejects oversized messages before any payload
     byte is read;
   - on every chunk: `TotalBytes + Length <= MaxMessageSize`; then `TotalBytes += Length`.
8. If `Crc32C` is absent and the receiver's checksum policy is *Required*: `ChecksumMissing`.
9. Read `DataLength` payload bytes.
10. If `Crc32C` is present, compute CRC-32C over the bytes just read and compare
    (`ChecksumMismatch`). This happens before any decompression.
11. If `Compression` is present, decompress the payload into a buffer of exactly `RawLength` bytes.
    The whole payload MUST be consumed and MUST produce exactly `RawLength` bytes; anything else
    (not a Zstandard frame, truncated frame, bytes after the frame, fewer or more bytes than
    announced) is `DecompressionFailed`. A receiver MUST NOT produce more than `RawLength` bytes of
    output for a chunk.
12. Accept the chunk: `Received += 1`. If `Received == TotalChunks` the message is complete.

Steps 1 to 8 use the header alone: a chunk that fails any of them is rejected before one payload
byte is read or allocated. The uniform-chunk rule costs nothing for conforming senders (every sender
since 0.1.0 produces uniform chunks) and gives the receiver an upper and lower bound on the message
from the first header. `DataLength` of compressed chunks is not uniform and need not be.

#### 4.1.3 Compressed chunks (protocol version 2)

Compression is optional, per chunk, and exists only in chunked frames. Lightweight and direct frames
are never compressed.

Sender rules:

- A compressed chunk carries `Compression: "zstd"` and `RawLength`, and its payload is **one complete
  Zstandard frame** as defined by RFC 8878, produced from the `RawLength` logical bytes of that chunk
  alone. No dictionary is used and no compressor state is carried from one chunk to the next, so
  every chunk can be decoded on its own.
- The Zstandard content checksum SHOULD NOT be enabled; `Crc32C` covers the same bytes.
- A sender MUST send a chunk compressed only if the Zstandard frame is strictly smaller than the
  logical bytes (`DataLength < RawLength`). Otherwise it sends the chunk raw, without `Compression`
  and `RawLength`. A message may therefore mix compressed and raw chunks. Receivers do not check
  that a compressed chunk is smaller than its logical length.
- An empty chunk (the only chunk of an empty message) MUST NOT be compressed.
- Every chunk that carries `Compression` MUST carry `Version: 2`. A sender decides before writing
  chunk 0 whether it will offer the chunks of the message to the compressor; if so, **every** chunk
  of that message carries `Version: 2`, including chunks that end up raw. All other messages are
  version 1 (section 7.1).

Receiver rules are steps 5, 10 and 11 of section 4.1.2. A receiver MAY accept a payload that
consists of several complete Zstandard frames whose combined output is exactly `RawLength` bytes
(this implementation does, because its decoder treats a buffer as a sequence of frames); senders
MUST NOT emit that.

Compression is **not negotiated**. A sender may only compress towards a receiver that it knows, by
configuration, to accept compressed chunks. If it compresses anyway, the receiver fails on the first
chunk of the message and never delivers compressed bytes as a message: a version 2 receiver whose
policy is off answers `CompressionNotSupported`, and receivers that predate version 2 (library 0.3.0
to 0.4.1) answer `UnsupportedVersion`. This is the reason compressed messages carry a new version
although the two members are optional: an older receiver would ignore the members and hand the
compressed bytes to the application.

### 4.2 Lightweight frame

```
offset 0   Magic     0xFF
offset 1   Length    uint16 BE   0 .. 1023
offset 3   Payload   Length bytes
```

A `Length` of 1024 or more MUST be rejected as `InvalidDataLength`.

### 4.3 Direct frame

```
offset 0   Magic     0xFE
offset 1   Length    int32 BE   0 .. 65535
offset 5   Payload   Length bytes
```

A negative `Length` or a `Length` of 65536 or more MUST be rejected as `InvalidDataLength`.

Lightweight and direct payloads are additionally subject to `MaxMessageSize` (`MessageTooLarge`).

## 5. Checksum

The checksum algorithm is **CRC-32C** (Castagnoli): polynomial `0x1EDC6F41`, reflected form
`0x82F63B78`, initial value `0xFFFFFFFF`, final XOR `0xFFFFFFFF`, reflected input and output. Check
value: `CRC32C("123456789") = 0xE3069283`; `CRC32C("") = 0`.

The checksum covers the **payload bytes of one chunk only**, exactly as transmitted, not the header
and not other chunks. For a compressed chunk (section 4.1.3) that is the Zstandard frame on the
wire, not the logical bytes it expands to, and the receiver verifies it **before** decompressing, so
corrupted bytes are rejected as `ChecksumMismatch` and never reach the decoder. It is transmitted as
the `Crc32C` header member.

Because the header is outside the checksum, header corruption is caught only by the validation rules
of section 4.1.2. One case is not caught by them: corruption that turns the `Compression` member
name into an unknown name makes the receiver read the Zstandard frame as a raw payload. Links on
which that matters need TLS, as they do against any deliberate modification.

Policy is local to each side:

| Mode | Sender emits | Receiver, checksum present | Receiver, checksum absent |
|---|---|---|---|
| Off (default) | no | verifies | accepts |
| Emit | yes | verifies | accepts (legacy peer) |
| Required | yes | verifies | rejects (`ChecksumMissing`) |

A receiver MUST verify a checksum that is present whatever its own mode. CRC-32C detects all
single-bit and burst errors up to 32 bits and random corruption with probability `1 - 2^-32`; it is
**not** a message authentication code and provides no protection against a deliberate attacker. Use
TLS for that.

Lightweight and direct frames carry no checksum in protocol versions 1 and 2; they rely on the transport
(TCP checksum, TLS record integrity).

## 6. Handshake

The handshake is optional. When used, both peers MUST perform it at the same point of the connection,
before any frame is sent, and each peer MUST write its part without waiting for the other's (the two
writes cross on the wire; waiting first would deadlock).

```
offset 0   Magic     0x54 0x54 0x53 0x48   ("TTSH")
offset 4   Version   int32 BE              the protocol version the sender advertises
```

Each peer reads the other's 8 bytes. If the magic differs: `HandshakeFailed`. If the remote version
is below the local `MinSupportedVersion`: `HandshakeFailed`. Otherwise the negotiated version is
`min(localVersion, remoteVersion)` and both peers MUST speak that version from then on.

This implementation advertises version 1 unless the caller passes another value. The handshake does
not negotiate compression (section 4.1.3). Applications that want a check at connection time can
agree that a peer advertises 2 exactly when it accepts compressed chunks, and compress only when the
negotiated version is 2; that convention lives in the application, not in the protocol.

**Extension (reserved for protocol version 3).** When the negotiated version is 3 or higher, both
peers continue with a second exchange of `[Features: uint32 BE]`, a bitmask of optional capabilities
(for example control frames). A capability is enabled only when both masks have the bit set. Peers
that advertise version 1 or 2 never send or expect the second exchange, so the extension is backward
compatible by construction. Until library 0.4.1 this reservation was written against "version 2";
it was renumbered when version 2 was assigned to compressed chunks. The extension has never been
implemented or emitted.

## 7. Versioning and compatibility policy

### 7.1 What the protocol version means

`Version` identifies the frame grammar. The version MUST be incremented when:

- the layout of an existing frame type changes;
- a header member becomes required, or the meaning of an existing member changes;
- a header member is added that a receiver cannot safely ignore, for example one that changes how
  the payload bytes are interpreted (`Compression`);
- a frame type is added that is not gated behind handshake negotiation.

The version is NOT incremented for:

- a new **optional** header member that is harmless to ignore (receivers ignore unknown members);
- use of a reserved `Flags` bit whose absence of handling is harmless (receivers ignore reserved bits);
- a new frame type or behaviour that is only used after both peers negotiated it.

Defined versions:

| Version | Since library | Grammar |
|---|---|---|
| `0` or absent | 0.1.0 | Legacy spelling of version 1. |
| `1` | 0.3.0 | Sections 4.1 to 4.3 without `Compression` and `RawLength`. |
| `2` | 0.5.0 | Version 1 plus the optional `Compression` and `RawLength` members of chunked frames (section 4.1.3). |

A sender MUST emit the **lowest version that can express the message**, decided before the first
chunk is written: version 2 for a message whose chunks may be compressed, version 1 for everything
else. A message that needs nothing from version 2 therefore stays readable by version 1 receivers,
and a sender that never compresses never emits version 2.

Every library release states `ProtocolVersion` (what it emits by default and advertises in the
handshake), `MaxSupportedProtocolVersion` (the newest it accepts) and `MinSupportedProtocolVersion`
(the oldest it accepts). Library 0.5.0: `1`, `2` and `0` (where `0` is the legacy spelling of `1`).
Library 0.4.x: `1`, `1` and `0`.

### 7.2 Backward and forward compatibility rules

- Receivers MUST accept frames from any sender whose version is within the supported range, including
  frames that omit optional members.
- Receivers MUST ignore unknown header members and reserved flag bits (forward compatibility).
- Senders MUST NOT emit a frame type or a required member that the negotiated version does not define.
- Senders MUST NOT emit compressed chunks unless the receiver is known to accept them (section 4.1.3).
  A version 2 chunk without `Compression` is an ordinary raw chunk and every version 2 receiver
  accepts it, whatever its compression policy.
- Both ends SHOULD agree on `ChunkSize` and `MaxHeaderLength`; a sender with a smaller chunk size
  than the receiver is always acceptable, a larger one is rejected per chunk.

### 7.3 Deprecation policy

- The NuGet package follows semantic versioning. Below 1.0, a minor release MAY contain source-level
  breaking changes to the .NET API, always listed in `CHANGELOG.md` with a migration table. The wire
  format is never broken in a minor release.
- A wire feature (frame type, header member, version) is deprecated by announcing it in
  `CHANGELOG.md` at least one minor release before it stops being emitted, and it continues to be
  **accepted** for at least two further minor releases.
- `MinSupportedProtocolVersion` is raised only in a major release and only for versions that were
  deprecated under the rule above.
- Defaults of limits (`MaxMessageSize`, timeouts) MAY be tightened in a minor release; each such
  change is listed under "Changed" in the changelog together with the option that restores the old
  behaviour.

## 8. Errors

Every failure surfaces as `HyperionProtocolException` carrying a stable `HyperionErrorCode`.

| Code | Value | Raised when | Connection afterwards |
|---|---|---|---|
| `Unspecified` | 0 | legacy constructors only | close |
| `InvalidMagic` | 1 | header `Magic` is not `"TTS"` | close |
| `UnsupportedVersion` | 2 | header version outside the supported range | close |
| `InvalidHeaderLength` | 3 | length prefix is `<= 0` or `> MaxHeaderLength` (also: reserved first byte) | close |
| `MalformedHeader` | 4 | header is not a valid JSON `PacketHeader` | close |
| `InvalidChunkCount` | 5 | `TotalChunks < 1`, or it changed between chunks | close |
| `InvalidChunkNumber` | 6 | `ChunkNumber` outside `0 .. TotalChunks-1` | close |
| `InvalidDataLength` | 7 | `DataLength` out of range or uniform-chunk rule violated; `RawLength` absent or out of range, or `DataLength` of 0, on a compressed chunk; small-frame length out of range | close |
| `EndFlagMismatch` | 8 | end flag inconsistent with chunk position | close |
| `PacketIdMismatch` | 9 | chunk belongs to another message | close |
| `ChunkOutOfOrder` | 10 | `ChunkNumber != Received` | close |
| `MessageTooLarge` | 11 | message exceeds `MaxMessageSize` | receive: close; send: **still usable** (nothing was written) |
| `ChecksumMismatch` | 12 | CRC-32C does not match the payload as transmitted | close |
| `ChecksumMissing` | 13 | policy is Required and the chunk has no checksum | close |
| `CompressionNotSupported` | 14 | the chunk carries `Compression` but the receiver does not decompress (policy off, or no codec), or the algorithm is not `"zstd"`; raised from the header | close |
| `DecompressionFailed` | 15 | the payload of a compressed chunk is not a valid Zstandard frame or does not expand to exactly `RawLength` bytes | close |
| `PeerClosed` | 20 | end of stream at a message boundary (receive) or the pipe reader completed (send) | already closed; normal end of session |
| `UnexpectedEndOfStream` | 21 | end of stream inside a frame | close |
| `Timeout` | 22 | no progress within `ReceiveTimeout` / `SendTimeout` | close |
| `HandshakeFailed` | 23 | bad magic, unsupported version or connection lost during the handshake | close |
| `SerializationFailed` | 30 | the serializer threw or returned `null` for a non-nullable target | send: **still usable**; receive: close |
| `SendFailed` | 31 | transport error while writing | close |
| `ReceiveFailed` | 32 | transport error while reading | close |

"Close" means the stream position is undefined (possibly in the middle of a frame) and the
connection MUST be closed; it cannot be resynchronised.

## 9. Timeouts

- `ReceiveTimeout` is an **idle** timeout: the maximum time a receiver waits for the peer to deliver
  any bytes. The timer is armed before each read and disarmed after it, so time spent in application
  code (for example between two streamed chunks) does not count. Default: none.
- `SendTimeout` bounds each chunk write (at most `ChunkSize` bytes plus the header) and the final
  flush. It protects a sender from a peer that stops reading. Default: none.
- The handshake has no built-in timeout; callers bound it with a cancellation token.
- Time spent compressing or decompressing a chunk is local computation and is covered by neither
  timeout. It is bounded by `ChunkSize`.

A timeout surfaces as `Timeout` and leaves the connection unusable.

## 10. Security considerations

**Threat model.** A receiver may be reachable by arbitrary clients (a server port), so every byte
from the peer is untrusted until validated. The protocol defends against resource exhaustion and
corruption; it does not provide confidentiality, authentication or protection against an active
attacker, which are TLS's job.

**Bounded allocation.** Every allocation is preceded by a bounds check against a configured limit:
header length (`MaxHeaderLength`, default 64 KiB), chunk payload (`ChunkSize`, default 1 MiB),
lightweight (1 KiB) and direct (64 KiB) payloads, and the whole message (`MaxMessageSize`, default
1 GiB), the last of which is enforced from the first header using the uniform-chunk rule. A peer
cannot make a receiver allocate more than it actually transmits plus `MaxMessageSize` for a buffered
receive, or more than `ChunkSize` plus per-chunk bookkeeping for a streamed receive.

**Bounded decompression.** A receiver that does not accept compressed chunks (the default) performs
no decompression at all and rejects a compressed chunk from its header. A receiver that accepts them
decompresses each chunk into a buffer of exactly `RawLength` bytes, and `RawLength <= ChunkSize` is
checked from the header before the payload is read. Output beyond that buffer is impossible: a frame
that would expand further fails as `DecompressionFailed` (this is the decompression-bomb guard), as
does one that expands to less. `MaxMessageSize` counts logical bytes and is still enforced from the
first header. The compressed payload itself is bounded by `DataLength <= ChunkSize`.

What remains is amplification within those limits: a peer can make a buffered receive hold up to
`MaxMessageSize` bytes while transmitting far fewer (highly compressible data shrinks by a factor of
thousands). The memory bound is unchanged, but it is no longer paid for byte by byte on the
network. Receivers that accept compression from untrusted peers SHOULD set `MaxMessageSize` to what
they are willing to hold per connection, or use a streamed receive, which holds one chunk. If the
application compresses payloads itself, above the protocol, it MUST bound the decompressed size itself.

**Serializer surface.** After framing, the payload is handed to the serializer. Serializers have
their own security settings and limits (for example `MaxDepth` in System.Text.Json, or the untrusted
data mode of MessagePack); applications exposed to untrusted peers MUST configure them.

**Timeouts.** Without `ReceiveTimeout`, a peer can hold a receive (and the resources behind it)
forever by sending part of a frame and stalling. Servers SHOULD set it.

**Integrity vs. authenticity.** CRC-32C detects accidental corruption only. TLS record integrity
protects against modification by an attacker; use TLS on any untrusted network.

**Authentication and authorisation** are not part of the protocol. Recommended: TLS with client
certificates (`SslStream`, `RemoteCertificateValidationCallback`), or an application-defined first
message carrying a token, verified before any other message is processed.

**Replay.** TLS prevents replay within a connection. Replay of application messages across
connections (a captured and resent "transfer money" message) must be prevented by the application
with nonces or idempotency keys.

## 11. Worked examples

Lightweight frame carrying the UTF-8 string `hi`:

```
FF 00 02 68 69
│  │     └──── payload "hi"
│  └────────── Length = 2 (uint16 BE)
└───────────── lightweight magic
```

Direct frame carrying 1024 zero bytes (one byte above the lightweight limit):

```
FE 00 00 04 00 00 00 00 ... (1024 bytes of 00)
│  └─────────┘
│  Length = 1024 (int32 BE)
└── direct magic
```

Chunked frame carrying the 3-byte message `abc` in a single chunk, without checksum
(`HeaderLength` = 134 = `0x86`; the UUID is illustrative):

```
00 00 00 86
{"Version":1,"Magic":"TTS","PacketId":"6f1d2c3e-4b5a-4c6d-8e7f-9a0b1c2d3e4f","ChunkNumber":0,"TotalChunks":1,"DataLength":3,"Flags":1}
61 62 63
```

The same message with checksums enabled gains the member `,"Crc32C":910901175` (`0x364B3FB7`, the
CRC-32C of `abc`) before the closing brace and a correspondingly larger `HeaderLength`.

A 2.5 MiB message with the default chunk size is three chunked frames with the same `PacketId`,
`TotalChunks` = 3, `ChunkNumber` 0, 1, 2, `DataLength` 1048576, 1048576, 524288 and `Flags` 0, 0, 1.

A version 2 chunked frame carrying the message `abc` as a compressed chunk (`HeaderLength` = 170 =
`0xAA`). The payload is a 12-byte Zstandard frame that stores the three bytes in a raw block; it is
larger than the message, so a conforming sender would send this chunk raw, but a receiver accepts it
and it makes a compact test vector:

```
00 00 00 AA
{"Version":2,"Magic":"TTS","PacketId":"6f1d2c3e-4b5a-4c6d-8e7f-9a0b1c2d3e4f","ChunkNumber":0,"TotalChunks":1,"DataLength":12,"Flags":1,"Compression":"zstd","RawLength":3}
28 B5 2F FD 20 03 19 00 00 61 62 63
│           │  │  │        └──────── block content "abc"
│           │  │  └───────────────── block header: last block, raw, 3 bytes (0x000019 little-endian)
│           │  └──────────────────── frame content size = 3
│           └─────────────────────── frame header descriptor: single segment, no checksum, no dictionary
└─────────────────────────────────── Zstandard magic number
```

With checksums enabled the header of that frame gains `"Crc32C"` between `"Flags"` and
`"Compression"`, computed over the 12 payload bytes, not over `abc`.

A message of 3 MiB of highly compressible data sent by a compressing sender with the default chunk
size is three frames with `Version` 2, `RawLength` 1048576 each and three different, much smaller
`DataLength` values. If the middle megabyte were incompressible, the middle frame would carry
`DataLength` 1048576 and neither `Compression` nor `RawLength`, still with `Version` 2.

## 12. Change history

| Library | Protocol version | Change |
|---|---|---|
| 0.1.0 | (unversioned) | Chunked frames with JSON header: `Magic`, `PacketId`, `ChunkNumber`, `TotalChunks`, `DataLength`, `Flags`. |
| 0.1.x | (unversioned) | Smart profile: lightweight (`0xFF`) and direct (`0xFE`) frames. |
| 0.3.0 | 1 | `Version` header member (absent = 1); optional `TTSH` handshake. |
| 0.4.0 | 1 | Optional `Crc32C` header member; uniform-chunk rule and `MaxMessageSize` enforced by receivers; reserved first-byte range `0x80..0xFD` documented; error codes; handshake phase 2 reserved for version 2. |
| 0.5.0 | 2 | Optional per-chunk Zstandard compression: `Compression` and `RawLength` header members, `Version: 2` on messages offered to the compressor, error codes `CompressionNotSupported` and `DecompressionFailed`; uniform-chunk rule and `MaxMessageSize` defined on logical lengths; `Crc32C` defined on the bytes as transmitted. Uncompressed messages stay version 1 and byte-identical. Handshake phase 2 reservation renumbered to version 3. |
