# Web Audio design

Part of the media engine (see `MEDIA_ENGINE_DESIGN.md`, milestone M9). Specification:
W3C Web Audio API 1.0 (https://www.w3.org/TR/webaudio/), referred to as "WA" below; section
numbers are that document's.

## 1. What this is for

A page builds a graph of audio nodes on a `BaseAudioContext`, schedules parameter changes,
and either hears the result (`AudioContext`) or gets it back as samples
(`OfflineAudioContext`). Games, music tools, voice chat, notification sounds and a large
share of fingerprinting scripts all use it. The WPT suite (`webaudio/`, 347 files) is
dominated by offline rendering compared sample by sample against values the test computes
in script, so the renderer has to follow the spec's arithmetic, not merely sound right.

## 2. Decisions

| # | Decision | Why |
|---|----------|-----|
| WA-D1 | **The graph and every DSP kernel are C# in `FenBrowser.Media/WebAudio`**, BCL only. The realm holds a script-side mirror of the API (WebIDL shapes, validation, events) that talks to the graph through `__fenWa*` natives. | DSP in the page's interpreter would be orders of magnitude too slow; keeping the engine BCL-only makes it unit-testable without a realm, like the rest of `FenBrowser.Media`. |
| WA-D2 | **A render quantum is 128 frames** (`renderSizeHint` other than `"default"` is accepted and still renders 128). All processing is planar `float` per WA 2.4. | The spec's default; every WPT expectation is phrased in 128-frame quanta. |
| WA-D3 | **Control thread / rendering thread split exactly as WA 2.2**: API calls enqueue control messages; the rendering thread drains the queue at the start of each quantum. Nothing on the rendering thread allocates in steady state. | Makes scheduling deterministic and keeps the real-time callback allocation-free (media design section 5). |
| WA-D4 | **`AudioBuffer` lives in the realm** as `Float32Array`s. The engine copies a buffer's channels when WA's "acquire the content" happens (setting `buffer`, `start()`, `ConvolverNode.buffer`), never shares memory with script. | WA 1.4 requires exactly that copy-on-acquire behaviour; it also means script can never write into memory the rendering thread is reading. |
| WA-D5 | **`OfflineAudioContext` renders on a worker thread**, as fast as it can, and resolves `startRendering()` on the media element task source. `suspend(t)` stops at the quantum containing `t` until `resume()`. | Offline rendering is the test path and must not block the page's thread. |
| WA-D6 | **`AudioContext` output goes through `MediaEngineServices.AudioOutputs`** (`IAudioOutputFactory`) with a pull callback that renders quanta on demand. If the device opens at another rate, a fixed windowed-sinc resampler converts; the context's `sampleRate` is what the page asked for, or the device rate when it asked for nothing. | Reuses the M2 output backends; no second device path. |
| WA-D7 | **`decodeAudioData` reuses the demuxer and decoder registries** (the same code paths the fuzzers cover) and resamples to the context rate with the same resampler. Input is capped by `MediaLimits`. | No new parser surface. |
| WA-D8 | **`MediaElementAudioSourceNode` taps the element's decoded PCM before the output**; a CORS-cross-origin element contributes silence (WA 1.21). `MediaStreamAudioSourceNode`/`Destination` bridge to the M9 `MediaStreamModel`. | Security requirement of the spec; reuses existing models. |
| WA-D9 | **`AudioWorklet` runs each `AudioWorkletGlobalScope` in its own FenJS isolate on the rendering thread.** | WA 1.32 requires processors to run synchronously inside the render quantum. It is the last phase because it needs worklet module loading. |
| WA-D10 | **`PannerNode` HRTF uses a synthetic spherical-head model** (interaural time and level differences), not a measured HRIR set. `equalpower` is exact per spec. | Measured HRIR databases carry licences; the spec leaves HRTF data to the implementation. |
| WA-D11 | Logging under `LogCategory.Media` with `MediaEventKind.WebAudio*`: context state changes, render underruns, decode failures. Nothing per quantum. | Media logging architecture. |

## 3. Security

- Everything the page controls (channel counts, sample rates, buffer lengths, FFT sizes,
  curve lengths, IIR coefficients, delay times) is validated in the realm **and** clamped
  again in the engine against `WebAudioLimits` (32 channels, 3 000-768 000 Hz, buffer length
  bounded by `MediaLimits`), so a bad native call cannot allocate without bound.
- IIR filters are checked for stability only as the spec requires; an unstable filter
  produces NaN/Inf, which a device output flushes to zero so no non-finite sample ever
  reaches a device. An `OfflineAudioContext`'s rendered buffer keeps them: it only reaches
  script, and the spec's computation is observable there.
- A cross-origin media element or stream contributes silence to the graph (WA 1.21, 1.24).
- `decodeAudioData` parses untrusted bytes only through the existing, fuzzed demuxers.

## 4. Phases

Each phase ends with its WPT directories run and the unexpected results classified.

| Phase | Scope | WPT |
|-------|-------|-----|
| WA1 | Engine core: bus, channel mixing (WA 4), graph order and cycles (WA 2.4), AudioParam timeline (WA 1.6), control message queue, offline renderer. Realm: `BaseAudioContext`, `OfflineAudioContext`, `AudioContext` (silent), `AudioBuffer`, `AudioNode`/`AudioParam`, `AudioDestinationNode`, `GainNode`, `ConstantSourceNode`, `AudioBufferSourceNode`, `ChannelSplitterNode`, `ChannelMergerNode`. | audioparam, audiobuffer, audiobuffersourcenode, offlineaudiocontext, gainnode, constantsourcenode, channelmerger/splitter, audionode, destinationnode |
| WA2 | `OscillatorNode`, `PeriodicWave`, `DelayNode` (cycles), `BiquadFilterNode`, `IIRFilterNode`, `WaveShaperNode`, `StereoPannerNode` | matching directories |
| WA3 | `PannerNode`, `AudioListener`, `ConvolverNode`, `DynamicsCompressorNode`, `AnalyserNode` | matching directories |
| WA4 | Real-time `AudioContext` output, `suspend`/`resume`/`close`, latency attributes, `decodeAudioData` | audiocontext-interface, processing-model |
| WA5 | `MediaElementAudioSourceNode`, `MediaStreamAudioSourceNode`, `MediaStreamAudioDestinationNode`, `ScriptProcessorNode` | matching directories |
| WA6 | `AudioWorklet`, `AudioWorkletNode`, `AudioWorkletProcessor` | audioworklet-interface |

## 5. Progress

| Phase | Status | Notes |
|-------|--------|-------|
| WA1 | in progress | |
