# FenBrowser Media Engine — Design

Status: DESIGNED. Date: 2026-09-17. Nothing in this document is implemented yet.

Today FenBrowser has no media engine. `<video>` and `<audio>` map to their interface names
(`HtmlElementInterfaceCatalog.cs`), `<video>` lays out as a 300×150 replaced box
(`ReplacedElementSizing.cs`), and the `HTMLMediaElement` members are deliberately
unpublished because nothing backs them (`docs/ENGINE_ROADMAP.md`). This document is the
plan for building a production media stack. It does not describe a demo.

The design is measured against the project motto. Every section says which part it serves:

| Pillar | What it means for media |
|---|---|
| **Secure by design** | Hostile bytes never reach a decoder in a privileged process. Every parser has ceilings. Fuzzing is part of Done, not a follow-up. |
| **Performance driven** | Frames are not copied, pipelines do not poll, the audio thread does not allocate, and hardware decode is used when present. Budgets are measured in CI. |
| **Spec driven** | The HTML media element algorithms are implemented step by step with step-numbered comments, and WPT is the acceptance gate. |
| **Loggable** | Every player has an ID, a structured event stream, a `media-internals` view and a CDP `Media` domain. |
| **Modular** | Demuxers, decoders and sinks are registry entries behind interfaces. The codec backend can be swapped or killed without touching the DOM. |
| **Contributor friendly** | A headless `fenplay` CLI, generated fixtures, one file per format, and a conformance dashboard. Nobody needs the whole browser to work on a decoder. |

---

## 1. What the other engines do, and what we take or avoid

### 1.1 Reference architectures

**Chromium** (`media/`, `third_party/blink/renderer/core/html/media/`)
- Blink `HTMLMediaElement` → `WebMediaPlayer` → `WebMediaPlayerImpl` → `PipelineImpl` on a media thread.
- `FFmpegDemuxer` handles files and `ChunkDemuxer` handles MSE. `RendererImpl` drives `AudioRendererImpl` and `VideoRendererImpl`.
- `DecoderSelector` walks a prioritised decoder list: hardware (`MojoVideoDecoder` in the GPU process) first, then software (dav1d, libvpx, FFmpeg).
- Audio leaves through a shared-memory ring to the separate **audio service** process.
- Video frames go to viz as a **compositor layer**, not through Blink paint.
- WebCodecs reuses the same decoder stack.
- The CDP `Media` domain and `chrome://media-internals` expose per-player event logs.

**Firefox** (`dom/media/`)
- `HTMLMediaElement` → `MediaDecoder` → `MediaDecoderStateMachine` (on its own task queue) → `MediaFormatReader`.
- There is one demuxer per container. The MP4 parser is **Rust** (`mp4parse`), replacing a C++ parser that produced CVEs.
- Decoders sit behind a `PlatformDecoderModule` abstraction (FFmpeg/ffvpx, dav1d, WMF, VideoToolbox, Android). They run in the sandboxed **RDD** (Remote Data Decoder) process.
- Audio goes out through **cubeb**. Video goes through `VideoSink` → `ImageContainer` → WebRender, asynchronously.
- H.264 comes from OS decoders, which avoids codec licensing problems.
- `about:support` and the media logging modules provide diagnostics.

**Ladybird** (`Libraries/LibMedia`, `Libraries/LibWeb/HTML/HTMLMediaElement.*`)
- It started with a hand-written Matroska demuxer and VP9 decoder. It now has an `FFmpeg/` integration layer plus a `VideoToolbox/` path.
- Components are organised as `DemuxerRegistry`, `DecoderRegistry` and `MediaPipelineNode` (split into `Producers/`, `Processors/` and `Sinks/`), with `PlaybackStates/`.
- It has `MediaClock` / `MonotonicMediaClock`, a `VideoFramePool`, a `DemuxerScanThread` and `IncrementallyPopulatedStream` for progressive loading.
- Frames are painted through the normal display list by the video paintable.

### 1.2 Adopt

| Idea | From | Why |
|---|---|---|
| Registries for demuxers and decoders, and pipeline nodes as producer → processor → sink | Ladybird | This is the most contributor-friendly decomposition. A new format is one file plus one registration. |
| Decoders out of the renderer, in a dedicated sandboxed media process | Firefox (RDD), Chromium (GPU decode) | Media parsers are one of the largest sources of browser CVEs. |
| Memory-safe parsing for container formats | Firefox (`mp4parse`) | C# gives us this for free if **we** write the container parsers and only hand FFmpeg elementary streams. |
| Prioritised decoder selection with fallback: hardware → software | Chromium `DecoderSelector` | When a hardware decoder fails on a real stream, playback falls back silently instead of failing. |
| Audio as master clock, with a monotonic clock when there is no audio track | All three | This is the standard A/V-sync model. |
| Video frames as a compositor layer | Chromium, Firefox | A new frame must not dirty paint or layout. |
| One decoder stack shared by `<video>`, MSE and WebCodecs | Chromium | This avoids three divergent code paths. |
| Per-player structured event log, an internals page and a CDP `Media` domain | Chromium | This makes playback failures debuggable from logs alone. |
| Frame pool with bounded capacity | Ladybird, Chromium | Allocation is steady and back-pressure is built in. |

### 1.3 Avoid

| Mistake or gap | Where it happened | Our rule |
|---|---|---|
| Hand-written codecs that cannot keep up with the format space | Ladybird's first approach | **We write containers, not codecs.** Codecs come from a proven library behind `IVideoDecoder` / `IAudioDecoder`. |
| A giant do-everything player class (`WebMediaPlayerImpl` is thousands of lines) | Chromium | The element-side player is a thin proxy. State lives in a small state machine and each concern has its own node. |
| Deep thread-hopping state machine that is hard to reason about | Firefox MDSM | Each player gets one media task queue. Nodes are single-threaded on that queue, and only decoders and the audio callback run elsewhere. |
| Demuxing third-party formats in the renderer with a C library | Chromium (FFmpeg demux in the renderer) | Demuxing happens in managed code in the media process. FFmpeg never sees a raw container. |
| Platform-specific playback forks (Android `MediaPlayerBridge`) | Chromium (historical) | There is one pipeline. Platform code exists only as decoder and sink plug-ins. |
| Re-painting the page for every video frame | Ladybird | `VideoPaintNode` holds a stable frame-source handle, and the compositor samples the latest frame. |
| Autoplay, background-tab and power policy added late | All three (retrofitted) | Policy is a first-class component from Phase 1. |
| Captions bolted on (Firefox's WebVTT started as JS) | Firefox | The WebVTT parser and cue rendering are native from the text-track phase onward. |
| No diagnosability for "the video is black" | Common | Every stall, drop or error has a typed event with a reason code. |

---

## 2. Architecture

### 2.1 Projects (modular by construction)

| Project | Depends on | Contents |
|---|---|---|
| `FenBrowser.Media` | BCL only | Core types (`MediaTime`, `TimeRanges`, `EncodedPacket`, `VideoFrame`, `AudioBlock`), interfaces, registries, pipeline, clock, container parsers (WebM/Matroska, MP4/ISOBMFF, Ogg, WAV, MP3, ADTS, FLAC), MSE byte-stream logic, WebVTT parser. `Nullable=enable`, `TreatWarningsAsErrors=true`, `AllowUnsafeBlocks=false`. |
| `FenBrowser.Media.Codecs.Ffmpeg` | `FenBrowser.Media` | FFmpeg decoder adapter (P/Invoke into dynamically linked libavcodec). This is the only place FFmpeg is visible. |
| `FenBrowser.Media.Codecs.Windows` | `FenBrowser.Media` | Media Foundation hardware decode (DXVA/D3D11). Loaded only on Windows. Added in a later phase. |
| `FenBrowser.Media.Audio` | `FenBrowser.Media` | `IAudioOutput` implementations: WASAPI (Windows), PulseAudio/PipeWire (Linux), CoreAudio (macOS), plus a null/file sink for tests. |
| `FenBrowser.Media.Tests` | `FenBrowser.Media` | xUnit tests: parsers, the state machine, clock, MSE and WebVTT. No browser involved. |
| `FenBrowser.Media.Fuzz` | `FenBrowser.Media` | Structure-aware fuzzers for every container parser, the MSE append path and WebVTT. |
| `FenBrowser.Media.Shell` (`fenplay`) | all of the above | Headless CLI: `probe`, `play --null-sink`, `dump-frames --hash`, `mse-append`, `vtt`. |
| FenEngine / Core / Host changes | — | DOM element, bindings, layout and paint node, IPC channel, media process target. |

`FenBrowser.Media` must not reference FenEngine, Core, Skia or Silk.NET. Logging and tracing go
through an `IMediaLogSink` interface that the host adapts to `FenLogger`, the same way
`FenBrowser.Js` stays host-agnostic.

### 2.2 Process placement (secure by design)

```
 Renderer process                  Media process (new utility target)      Browser / GPU
 ───────────────────               ────────────────────────────────────   ─────────────────
 HTMLMediaElement (spec SM)        MediaSession per player                 Network broker
   │                                 ├─ ByteSource ◄──── range reads ────────┘
   ▼                                 ├─ Demuxer (managed, bounded)
 MediaPlayerProxy ── IPC ──────────► ├─ DecoderSelector → IVideoDecoder     GPU/compositor
   ▲  (state, time, events)          │                   IAudioDecoder       ▲
   │                                 ├─ VideoRenderer ── shared-mem frames ─┘ (latest-frame
   │                                 └─ AudioRenderer ── ring buffer ──► Audio output   sampling)
 VideoPaintNode(frameSourceId)                                           (browser process)
```

- **Renderer**: runs spec algorithms, events, `TimeRanges`, the MSE `SourceBuffer` API surface and text-track cue selection. It holds **no codec code**.
- **Media process**: a new `TargetProcessKind.Media` modeled on `UtilityProcessIpc`, with profile `media_process` and capabilities `media-decode,shared-memory`.
  - It has **no network and no filesystem access**. Bytes arrive only through a brokered `ByteSource` that can serve only what the renderer's origin could fetch.
  - A crash fails only its players. The element fires `error` with `MEDIA_ERR_DECODE`, and the tab survives (see `PROCESS_MODEL.md`, "Decoder crash").
  - Hardware decode needs a GPU device, so a later phase moves only the hardware decoder into the GPU process behind the same `IVideoDecoder` contract, as Chromium does.
- **Audio output** lives in the browser process: device access is a privilege, and it enables one global mixer, device selection (`setSinkId`) and per-tab muting.
- **In-process mode** (`FEN_PROCESS_ISOLATION=in-process`) uses the same interfaces with a direct transport, so tests and debugging never need a second code path.
- **IPC** follows `IPC_MODEL.md`: typed, bounded, correlation-ID'd control messages. Frames and audio travel over **shared memory rings, never JSON**. Every handle is scoped to the session generation and invalidated on peer exit.

### 2.3 Pipeline (inside the media process)

```
ByteSource → Demuxer → [per track] PacketQueue → Decoder → FrameQueue/Pool → Renderer(node) → Sink
                                                                     ▲
                                                          MediaClock (audio-master)
```

- **`IByteSource`** has three implementations:
  - `NetworkByteSource`: HTTP range requests through the broker, with a sparse cache and `IncrementallyPopulatedStream`-style reads.
  - `MseByteSource`: fed by `SourceBuffer.appendBuffer`.
  - `BlobByteSource`.
- **`IDemuxer`** is chosen by sniffing (the WHATWG MIME Sniffing "audio or video" pattern table), not by file extension. It emits `EncodedPacket { TrackId, Pts, Dts, Duration, Keyframe, Data (pooled) }`. The `DemuxerRegistry` lists `Probe(ReadOnlySpan<byte>) → confidence` for each format.
- **Decoders**: `IVideoDecoder` / `IAudioDecoder` expose `ConfigureAsync(config)`, `DecodeAsync(packet)`, `FlushAsync()` and `Reset()`. The `DecoderRegistry` returns an ordered list of candidates for a given `CodecConfig`, and `DecoderSelector` tries them in order, recording each attempt's result.
- **Nodes** run on the player's single media task queue. Decoders may use worker threads and post results back to that queue.
- **Back-pressure**: bounded queues (video frames, audio samples, packet bytes). A full queue pauses the upstream node, so nothing polls and nothing grows without limit.
- **Seeking**: flush every node, then have the demuxer seek to the preceding keyframe. Frames before the target are decoded and discarded (accurate seek) unless `fastSeek()` is used.

### 2.4 Clock and A/V sync

- `MediaClock` has two implementations:
  - `AudioMasterClock`: time comes from the frame count the device has actually played, corrected by the reported output latency.
  - `MonotonicMediaClock`: used for video-only or muted-without-audio content.
- `playbackRate` is applied through the clock. Audio uses a pitch-preserving time-stretch processor node when `preservesPitch` is true.
- For each vsync, the video renderer selects the frame whose `[pts, pts+duration)` range contains the clock time predicted for that vsync. Late frames are dropped and counted for `getVideoPlaybackQuality()`.
- Clock discontinuities (device change, underrun) produce a `ClockDiscontinuity` log event.

### 2.5 Renderer-side integration (FenEngine / Core)

- **DOM**: `HTMLMediaElement`, `HTMLVideoElement`, `HTMLAudioElement`, `HTMLSourceElement`, `HTMLTrackElement`, `TextTrack`, `TextTrackCue`, `VTTCue`, `TimeRanges`, `MediaError` and the track lists, all declared as IDL in `FenBrowser.Core/WebIDL/Idl/` so bindings are generated rather than hand-written.
- **Events** are queued on the **media element event task source** through `EventLoopCoordinator`, never dispatched synchronously from IPC callbacks.
- **Layout**: `<video>` is already a replaced element. Intrinsic size comes from the video's natural size once metadata loads, and `object-fit` / `object-position` are applied at paint.
- **Paint**:
  - A new `VideoPaintNode { FrameSourceId, DestRect, ObjectFit }` goes through `IRenderBackend.DrawVideoFrame(...)`, so `SKCanvas` stays out of the media path.
  - A new frame triggers a **compositor-only** invalidation (`DamageTracker` rect), never a restyle or relayout.
  - Poster images reuse `ImageLoader`.
- **Controls**: a UA shadow tree built from ordinary DOM and CSS (the same approach Chromium and Firefox use), so it is styleable, accessible and testable.
- **Canvas and WebGL**: `drawImage(video)` and `texImage2D(video)` read the current frame. The canvas is tainted unless the resource passed CORS (`crossorigin`).

---

## 3. Spec coverage (spec driven)

Each algorithm is implemented with numbered comments that mirror its spec steps (Tier 1 in the
Definition of Done). Specs to cite:

| Area | Spec | Key algorithms / members |
|---|---|---|
| Media elements | WHATWG HTML §4.8.11 | resource selection, load, fetch, `readyState` / `networkState`, `play()` promise, `pause`, seeking, `TimeRanges`, `preload`, `loop`, `muted`, `volume`, `playbackRate`, `preservesPitch`, `canPlayType`, `MediaError`, media element event task source |
| Tracks | WHATWG HTML §4.8.11.11, WebVTT (W3C) | `<track>`, `TextTrack` modes, cue timeline updates, WebVTT parsing and rendering rules, `AudioTrackList` / `VideoTrackList` |
| MIME | WHATWG MIME Sniffing §7 | audio/video pattern matching, `codecs=` parameter parsing (RFC 6381) |
| Fetch / security | WHATWG Fetch, CSP3 (`media-src`), Mixed Content (upgrade audio/video) | range requests, CORS mode from `crossorigin`, opaque-response tainting |
| MSE | W3C Media Source Extensions 2 | `MediaSource`, `SourceBuffer`, segment parser loop, coded frame processing / removal / eviction, `ManagedMediaSource`, MSE in workers |
| WebCodecs | W3C WebCodecs | `VideoDecoder`, `AudioDecoder`, `VideoFrame`, `EncodedVideoChunk` (shared decoder stack) |
| Capabilities | W3C Media Capabilities | `decodingInfo` answered from the decoder registry, never hard-coded |
| Autoplay | W3C Autoplay Policy Detection | `navigator.getAutoplayPolicy`, sticky user activation |
| Session | W3C Media Session | metadata and action handlers (OS media keys) |
| Frame callbacks | WICG `requestVideoFrameCallback`, `getVideoPlaybackQuality` | per-presented-frame metadata |
| PiP / audio output | W3C Picture-in-Picture, Audio Output Devices | later phases |
| Web Audio | W3C Web Audio API | separate follow-on project that reuses `FenBrowser.Media.Audio` |
| EME | W3C Encrypted Media Extensions | **Clear Key only** (see decisions) |

**Conformance gate.** The WPT directories are `html/semantics/embedded-content/media-elements`,
`media-source`, `webvtt`, `webcodecs`, `media-capabilities`, `mediasession`, `video-rvfc`,
`autoplay-policy-detection`, `encrypted-media` (Clear Key) and `webaudio`. Each phase names the
directories it must pass. Note: the local WPT checkout at `D:\wpt` was not
present on 2026-09-17, so baseline counts are still to be taken.

---

## 4. Security (secure by design)

Use the `SECURITY_MODEL.md` task template for every item below.

- **Threat**: attacker-controlled container and codec bytes, attacker-chosen dimensions, timestamps, track counts and sample tables, plus MSE append patterns built to exhaust memory.
- **Boundary**: renderer ⇄ media process ⇄ browser. The media process holds no ambient authority, and its sandbox profile denies network, filesystem, process launch and window access.
- **Hard limits** are enforced in the parsers before any allocation. They are configurable and every violation is logged:

| Limit | Default |
|---|---|
| Max video dimension | 8192 × 8192; max pixel count 8K UHD |
| Max tracks per resource | 64 |
| Max sample-table entries | 16M |
| Max single packet | 64 MiB (video), 1 MiB (audio) |
| Max EBML / box nesting depth | 32 |
| Max decoded frames queued per player | 8 (video), 2 s (audio) |
| MSE buffer quota | 150 MiB video / 12 MiB audio per `SourceBuffer` (eviction per spec) |
| Max players per process | 64; per-player memory ceiling with `MEDIA_ERR_DECODE` on breach |

- **Container parsers are managed and bounds-checked.** The native codec library only ever sees elementary-stream packets that have already been validated.
- **Codec allowlist and kill switch**: `FEN_MEDIA_DISABLE_CODECS=vp8,h264`. A decoder that crashes N times is quarantined for the session.
- **Origin rules**:
  - CORS mode comes from `crossorigin`.
  - Cross-origin non-CORS media taints canvas, WebGL and WebCodecs frame reads, and `captureStream`.
  - CSP `media-src` is checked before fetch.
  - Mixed audio/video content is upgraded or blocked.
- **Timing side channels**: buffered ranges and error details for cross-origin opaque resources are limited as the spec requires, and error messages carry no response details.
- **Fuzzing is part of Done.** Every parser and IPC message ships with a `FenBrowser.Media.Fuzz` target. The corpus is seeded from `test_assets/media/` and must run 10,000 iterations clean (as `DEFINITION_OF_DONE.md` requires for parsers and IPC). IPC additions update `IpcFuzzHarness`.
- **Native library hygiene**: pinned, checksum-verified FFmpeg build with only the needed decoders compiled in (no demuxers, no network protocols, no filters), recorded in `THIRD_PARTY_DEPENDENCIES.md`.

---

## 5. Performance (performance driven)

| Budget | Target | Measured by |
|---|---|---|
| Time to first frame (local 1080p WebM, preload=auto) | ≤ 150 ms after `loadedmetadata` | `fenplay play --metrics`, CI |
| Dropped frames, 1080p60 software decode on the reference machine | < 0.5 % | `getVideoPlaybackQuality`, CI |
| Frame copies between decode and composite | 0 on the hardware path, ≤ 1 on software (into shared memory) | counters |
| Audio callback allocations | 0 (asserted in debug builds) | allocation tracker |
| Audio underruns in steady state | 0 | sink counters |
| Renderer main-thread work per presented frame | none (compositor-only) | `DamageTracker` / frame trace |
| Idle player cost (paused, off-screen) | no decode threads, frames released | counters |

Techniques:
- Pooled `ArrayPool` / `MemoryPool` packet buffers and a shared-memory frame ring (NV12/I420 plus stride metadata). Colour conversion happens on the GPU when available.
- `preload=metadata` is the effective default on metered connections, and range reads are fetched lazily.
- **Background policy**: hidden-tab video pauses decoding and keeps only audio (Chromium's background video optimisation). Off-screen muted autoplay is paused.
- Hardware decode (Media Foundation / VA-API / VideoToolbox) is selected first, and software decode is the fallback.
- A benchmark suite in `FenBrowser.Media.Shell bench` reports no more than a 5 % regression (Tier 2 gate).

---

## 6. Observability (loggable)

- **New log category** `LogCategory.Media = 1 << 30`, the last free bit in the flags enum. It follows the rules in the logging-architecture note: there is **no per-frame or per-packet logging** on hot paths, only counters and state transitions.
- **Player identity**: each player gets a `PlayerId` (tab, frame, element serial) that is carried on every IPC message and log line.
- **Typed events** (`MediaEvent`, JSONL): `PlayerCreated`, `SourceSelected`, `SniffResult`, `DemuxerChosen`, `TrackAdded`, `DecoderAttempt{name, result, reason}`, `DecoderChosen`, `ReadyStateChanged`, `NetworkStateChanged`, `Buffering{ranges}`, `Stall{reason}`, `SeekStart/End`, `FrameDropBurst{count, reason}`, `AudioUnderrun`, `ClockDiscontinuity`, `LimitExceeded{limit, value}`, `MseAppend{bytes, result}`, `Error{code, reason}`, `ProcessCrash`, `PlayerDestroyed`.
- **Rolling summary** once per second per active player: decoded, dropped and presented frames; buffer depth; clock drift; decode latency p50/p95.
- **Surfaces**:
  - `fen://media-internals`, a live table of players with their event logs.
  - A CDP `Media` domain (`playersCreated`, `playerPropertiesChanged`, `playerEventsAdded`, `playerMessagesLogged`, `playerErrorsRaised`), matching Chrome so existing DevTools front-ends work.
  - A `debug-site` bundle section with `media.json`.
- `scripts/fenlog.py` gains a `media` view.

---

## 7. Contributor experience (contributor friendly)

- **`fenplay` CLI**: work on a demuxer or decoder without building or launching the browser.
  - `fenplay probe file.webm` prints tracks and codecs.
  - `fenplay dump-frames file --hash` prints deterministic frame hashes used as golden tests.
- **One file per container format**, each registered in one place (`DemuxerRegistry.Default`). `docs/media/ADDING_A_FORMAT.md` is a checklist: parser, limits, fuzz target, fixtures, WPT.
- **Fixtures**: `scripts/media/gen_fixtures.py` generates small, licence-clean test files (with an FFmpeg CLI, run offline) plus a manifest of expected metadata and hashes. The files are committed under `test_assets/media/`.
- **Tests without the browser**: the state machine is a pure class driven by a fake player, so HTML media algorithm tests run in milliseconds.
- **Dashboard**: `scripts/media_wpt_report.py` → `docs/media_wpt_results.md` (same model as `docs/test262_results.md`).
- **Docs**: a Volume section (`docs/VOLUME_III_FENENGINE.md` + a new `docs/VOLUME_VII_MEDIA.md`) with line maps once code exists, plus a glossary of the media terms used here (PTS, keyframe, coded frame group, etc.).

---

## 8. Phased roadmap (each phase ends verified, committed and pushed)

Each phase has exit criteria. Phases are ordered by site-breakage value, not by spec order.

| Phase | Scope | Exit criteria |
|---|---|---|
| **M0 Foundations** | `FenBrowser.Media` project, core types, registries, `MediaClock`, `IMediaLogSink`, `LogCategory.Media`, `fenplay probe`, fixture generator, the ADRs in §9 accepted | builds warning-clean; unit tests; ADRs merged |
| **M1 Element without playback** | IDL for media interfaces; spec state machine (resource selection, load, `networkState`/`readyState`, events, `play()` promise rejection paths); `canPlayType` answered from registries; `poster` painting; `object-fit`; autoplay policy object | media-elements WPT subset for the state machine and `canPlayType` green; feature detection truthful |
| **M2 Audio** | WAV/MP3/ADTS/Ogg/FLAC demuxers; Opus/Vorbis/MP3/FLAC/PCM decoders; `IAudioOutput` (WASAPI first); audio-master clock; volume/muted/rate; media process target plus IPC; fuzz targets | `<audio>` plays on real sites; fuzz 10k clean; crash-containment test |
| **M3 Video (WebM)** | Matroska/WebM demuxer; VP8/VP9/AV1 through the codec adapter; shared-memory frame ring; `VideoPaintNode`; compositor-only invalidation; seeking; `getVideoPlaybackQuality`; `requestVideoFrameCallback` | perf budgets in §5 met; seek WPT green |
| **M4 MP4** | ISOBMFF demuxer (fragmented and progressive); H.264/AAC (subject to decision D2); HEVC gated | MP4 fixtures and sites play |
| **M5 Controls and tracks** | UA shadow-DOM controls with accessibility; WebVTT parser and renderer; `<track>`; audio/video track lists; Media Session | webvtt + track WPT green |
| **M6 MSE** | `MediaSource`/`SourceBuffer`, segment parser loop, coded frame processing and eviction, quotas; `ManagedMediaSource` | `media-source` WPT; YouTube-class players start |
| **M7 Hardware decode and power** | Media Foundation → VA-API → VideoToolbox decoders in the GPU process; background-tab policy; GPU colour conversion | zero-copy counters; power/idle budgets |
| **M8 WebCodecs and Media Capabilities** | on the shared decoder stack | `webcodecs` and `media-capabilities` WPT |
| **M9 Extras** | Clear Key EME, Picture-in-Picture, `setSinkId`, `captureStream`, Web Audio (separate design) | respective WPT |

Every phase follows the Commit & Push discipline: small, verified, human-style commits.

### Progress

| Phase | Status | Notes |
|---|---|---|
| M0 | DONE (2026-09-17, branch `feat/media-engine`) | `FenBrowser.Media` core: `MediaTime`, `MediaTimeRanges`, tracks and codecs, `MediaLimits`, `IMediaLogSink`, the spec-exact `MediaSniffer`, pooled `EncodedPacket` / `VideoFrame` / `AudioBlock` on a private zeroing pool, the pipeline contracts, `DemuxerRegistry`, `DecoderRegistry` with kill switch and quarantine, `DecoderSelector`, `AudioMasterClock`, `MonotonicMediaClock`. `LogCategory.Media`, ADR-0001 to ADR-0006, `scripts/media/gen_fixtures.py` with 16 fixtures, `fenplay probe`/`limits`, `FenBrowser.Media.Fuzz` (sniffer, demuxer selection, time ranges, audio clock; 10,000+ iterations per parser, mutation-checked), `EngineLogMediaSink` routing media events into the engine log, and a blocking CI job for tests and fuzzing. |
| M1 | DONE (2026-09-17) | `HtmlMediaElementController` (the §4.8.11 state machine: resource selection over `src` and `source` children, load, network/ready states, events, `play()` promise paths, seeking, rates, volume) with 400+ controller tests; `canPlayType` from `MimeType`/`CodecString`/`MediaTypeSupport` over the registries; the browser binding (`BrowserScriptEngineRuntime.Media.cs`: media element tasks on the JS worker, stable states as microtasks, DOM mutation routing, promises rooted until settled, media elements started in document order before each blocking script) answering the `HTMLMediaElement`/`HTMLVideoElement` IDL, `MediaError`, `TimeRanges`, the interface constants, `new Audio()`, and `HTMLVideoElement`/`HTMLAudioElement : HTMLMediaElement`; IDL metadata files; `MediaFetchResource` running the resource fetch algorithm on the browser network stack (destination audio/video, crossorigin modes, `media-src`) so the selection pointer waits on the network and a fetching element delays the document load event; `MediaAutoplayPolicy` (`FEN_MEDIA_AUTOPLAY`, sticky activation) with `navigator.getAutoplayPolicy`; poster frame painting, intrinsic sizing from the poster and `object-fit: contain` UA default via `MediaPresentation`. DOM work it needed: `Node.moveBefore`, the namespaced attribute methods, `window[n]`/`frames` before frame load. WPT bed fixed (`wpt run` argument order, autoplay allowed): `docs/media_wpt_results.md` — `loading-the-media-resource` 48/57 subtests, 44 of 49 files clean. Left for M2 because they need a playable resource: `autoplay*.html`, `load-events-networkState`, `load-removes-queued-error-event` (second case), `resource-selection-currentSrc`, `ready-states`, `seeking`, `offsets`, `error.html` "after successful load", the optional `canPlayType` codec probes. Not media work, tracked as engine gaps: `resource-selection-source-media-env-change` (a parent writing a function onto a child frame's window; realms are separate heaps), `fragmented-mp4-end` (Window named access `window.<id>`, then H.264), `autoplaypolicy.html` AudioContext case (Web Audio), `muted-playbackrate.tentative` (tentative, not in the spec). |
| M2 | DONE (2026-09-18) | Demuxers for WAV, MP3, ADTS, Ogg (Opus, Vorbis) and FLAC in `FenBrowser.Media/Containers`, each with a fuzz target in `FenBrowser.Media.Fuzz`; the managed PCM decoder and `FenBrowser.Media.Codecs.Ffmpeg` (ADR-0001: about twenty P/Invoke entry points into a dynamically linked libavcodec/libavutil pinned to majors 63/61, found through `FEN_FFMPEG_DIR`, the application directory or the winget package; a missing library registers nothing and logs once) decoding Opus, Vorbis, FLAC and MP3; `MediaPlayer` with the audio renderer, accurate seeks (decoded audio trimmed before the target), volume/muted, `playbackRate` with a WSOLA time stretcher for `preservesPitch`, and `AudioMasterClock` driving `currentTime`; `IAudioOutput` with the null sink and `FenBrowser.Media.Audio/Windows/WasapiAudioOutput` (ADR-0003; PulseAudio and CoreAudio are still to come); `fenplay play`. The media process (ADR-0004): `TargetProcessKind.Media` (profile `media_process`, capabilities `media-decode,shared-memory`, `--media-child`) started by the renderer child on first use, control over `MediaOpen/Read/Seek/Close` envelopes bounded by `MediaIpcLimits`, bytes and PCM over named shared memory, `MediaProcessClient` turning a child exit into `MEDIA_ERR_DECODE` on the players it served while the tab survives and the next open relaunches; `FEN_MEDIA_PROCESS=in-process` for debugging; `MediaIpcFuzzEndpoint` in `IpcFuzzHarness` with a 10,000-iteration test. Real site: the w3schools `<audio>` demo (Ogg Vorbis served as `video/ogg`, written into an about:blank iframe) plays to `ended`; that needed the HTML fallback base URL for about:blank frames and the document-open URL step. WPT (`docs/media_wpt_results.md`): `loading-the-media-resource` 55/57, `mime-types` every non-optional case, `canPlayType` WAV probes. What remains in those directories needs video (M3: `autoplay.html`, `ready-states`, `seeking`, `offsets`, `playing-the-media-resource` pause/move cases, `error.html` "after successful load", `autoplaypolicy_media_element`), MSE and MediaStream (`resource-selection-currentSrc`), or is an engine gap already listed under M1. |
| M3 | NEXT | |

---

## 9. Decisions (all accepted 2026-09-17)

Recorded as ADR-0001 to ADR-0006 in `docs/DECISION_RECORDS/`. D1 maps to ADR-0001, D2 to ADR-0002, and so on.

| ID | Decision | Recommendation |
|---|---|---|
| **D1** | Software codec backend | **FFmpeg (libavcodec only), dynamically linked (LGPL), our own minimal P/Invoke layer**, built with only the decoders we need. Rejected: hand-written codecs (Ladybird's dead end), FFmpeg.AutoGen (a large generated surface we would have to audit), platform-only decoders (no AV1/Opus parity on Linux). Also add dav1d for AV1 if FFmpeg's native AV1 decoder is insufficient. |
| **D2** | Patent-encumbered codecs (H.264, AAC, HEVC) | Use **OS decoders only** (Media Foundation / VideoToolbox / VA-API), as Firefox does. Do not ship them in our FFmpeg build. |
| **D3** | Audio output | **Direct platform backends** (WASAPI, PulseAudio/PipeWire, CoreAudio) behind `IAudioOutput`. OpenAL was considered because Silk.NET is already a dependency, but it lacks reliable latency reporting and device-change events, which A/V sync needs. |
| **D4** | Process placement | Add a **dedicated media target process** (as in §2.2). Until `BLOCK-PROC-002` is resolved, run the same contract in-process, with parser limits active in both modes. |
| **D5** | EME / DRM | **Clear Key only.** Widevine and PlayReady need commercial licensing and are out of scope. Sites that require them get a clear, logged `NotSupportedError`. |
| **D6** | Log category bit | Use `LogCategory.Media = 1 << 30`, the last free bit. Widening the enum to `long` is a separate change if more categories are needed. |

---

## 10. Definition of Done for media work (in addition to `DEFINITION_OF_DONE.md`)

- The spec section and algorithm name are cited in the code.
- Any new parser or IPC message has a fuzz target that runs 10,000 iterations clean.
- Every new limit has a negative fixture and a `LimitExceeded` log assertion.
- There is no per-frame logging and no allocations in the audio callback, and the relevant counter is checked by a test.
- The WPT directories for the phase show no regressions, and `docs/media_wpt_results.md` is updated.
- `fenplay` can reproduce any bug that is fixed (a fixture plus a command line in the test).
