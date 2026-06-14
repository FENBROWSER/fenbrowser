<p align="center">
  <img src="images/fenbrowser-logo.png" alt="FenBrowser logo" width="720" />
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET_10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/Skia-0B9BD7?style=for-the-badge&logo=google&logoColor=white" alt="SkiaSharp" />
  <img src="https://img.shields.io/badge/License-MIT-yellow?style=for-the-badge" alt="MIT License" />
  <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey?style=for-the-badge" alt="Platform" />
</p>

# 🦊 FenBrowser

**A ground-up, standards-driven web browser engine written entirely in C#.**

FenBrowser is an experimental browser engine that implements the web platform from scratch — HTML parsing, CSS cascade & layout, GPU-accelerated rendering, and a custom ECMAScript runtime — all in modern .NET, without wrapping Chromium, WebKit, or Gecko.

> [!NOTE]
> **This is a research & learning project.** FenBrowser is not a daily-driver browser. Many websites won't render correctly (or at all) yet. We're building this to deeply understand the modern web stack, not to replace your current browser.

---

## ✨ Highlights

| | Feature | Details |
|---|---|---|
| 📄 | **Standards-Based HTML Parser** | WHATWG-compliant HTML5 tree builder with incremental streaming, interleaved tokenize/parse for large documents, and conformance guardrails |
| 🎨 | **Full CSS Pipeline** | Tokenizer → Cascade → Computed Style → Used Values. Supports Selectors Level 4 (`:has()`, `nth-child(... of ...)`, attribute flags), Media Queries Level 4, CSS Color 4 (`oklch`, `oklab`), and modern shorthand parsing |
| 📐 | **Multi-Context Layout Engine** | Block, Inline, Flex, Grid, Table, and Float formatting contexts with spec-aligned sizing, placement, and baseline synthesis |
| 🖼️ | **Skia-Powered Rendering** | GPU-accelerated paint pipeline via SkiaSharp + OpenGL, with compositing stability controls, damage-region tracking, partial rasterization, and z-index resolution |
| ⚡ | **FenJS — Custom ECMAScript Engine** | Lexer → Parser → AST → Bytecode compiler → Interpreter, with a logical heap, native intrinsics, and Test262-first verification |
| 🔬 | **Built-In DevTools** | Native element inspector, style panel, debugging overlays, and remote debugging protocol |
| 🤖 | **WebDriver Protocol** | W3C WebDriver support for automated testing and CI integration |
| 🔒 | **Security-First Design** | Centralized navigation/network security policy, process isolation interfaces (in-process + brokered modes), POSIX sandbox launcher support |
| 🌍 | **Cross-Platform Foundation** | Windows primary, with Linux/macOS PAL support (POSIX shared memory, sandbox helpers) |

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    FenBrowser.Host                       │
│          Windowing · Input · Event Loop · OS Shell       │
├────────────────────────┬────────────────────────────────┤
│   FenBrowser.DevTools  │       FenBrowser.WebDriver     │
│   Inspector · Debug    │       W3C Automation           │
├────────────────────────┴────────────────────────────────┤
│                  FenBrowser.FenEngine                    │
│  HTML Parser · CSS Cascade · Layout · Paint · Scripting │
├─────────────────────────────────────────────────────────┤
│                    FenBrowser.Core                       │
│      DOM · CSS Types · Networking · Security · Logging  │
└─────────────────────────────────────────────────────────┘
```

| Layer | Project | Responsibility |
|-------|---------|----------------|
| **Core** | `FenBrowser.Core` | DOM nodes, CSS value types, HTTP stack, security policy, logging |
| **Engine** | `FenBrowser.FenEngine` | HTML/CSS parsing, style resolution, layout computation, Skia rendering, JS execution |
| **Host** | `FenBrowser.Host` | OS window, input capture, frame timer, navigation lifecycle |
| **DevTools** | `FenBrowser.DevTools` | Native inspector UI, remote debug protocol |
| **WebDriver** | `FenBrowser.WebDriver` | W3C WebDriver endpoint for automation |
| **JS Engine** | `FenBrowser.Js` | Standalone ECMAScript engine (lexer, parser, bytecode, VM) |

---

## 📂 Repository Structure

```
FenBrowser/
├── FenBrowser.Core/            # DOM, networking, CSS types, security primitives
├── FenBrowser.FenEngine/       # Layout engine, CSS cascade, Skia renderer, scripting
├── FenBrowser.Host/            # Desktop shell (entry point, windowing, input)
├── FenBrowser.DevTools/        # Built-in developer tools
├── FenBrowser.WebDriver/       # W3C WebDriver implementation
├── FenBrowser.Js/              # Standalone ECMAScript engine
├── FenBrowser.Js.Shell/        # JS engine CLI (--eval, --dump-ast, --dump-bytecode)
├── FenBrowser.Js.Test262/      # Test262 conformance runner
├── FenBrowser.Tests/           # Unit & integration tests
├── FenBrowser.Conformance/     # Web platform conformance tests
├── FenBrowser.Tooling/         # Harness tooling (Acid2, WPT, debug captures)
├── docs/                       # Canonical technical documentation
├── scripts/                    # Build, test, and CI scripts
└── FenBrowser.sln              # Solution file
```

---

## 🚀 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned via `global.json`)
- Visual Studio 2022+ or any editor with .NET support
- Windows 10/11 (primary), Linux or macOS (experimental)

### Build & Run

```bash
# Clone the repository
git clone https://github.com/user/FenBrowser.git
cd FenBrowser

# Build the solution
dotnet build FenBrowser.sln

# Run the browser
dotnet run --project FenBrowser.Host/FenBrowser.Host.csproj
```

### Run Tests

```bash
# Run the full test suite
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj

# Run a specific test slice
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~Layout"
```

### Try the JS Engine

```bash
# Evaluate an expression
dotnet run --project FenBrowser.Js.Shell/FenBrowser.Js.Shell.csproj -- --eval "1 + 2"

# Dump the AST
dotnet run --project FenBrowser.Js.Shell/FenBrowser.Js.Shell.csproj -- --dump-ast "function hello() { return 42; }"

# Run Test262 subset
dotnet run --project FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -- \
  --runtime-subset --root external/test262 --test262 built-ins/Object --max 10
```

### Diagnostic Output

When running, FenBrowser writes rich diagnostic artifacts to `logs/`:

| File | What It Shows |
|------|---------------|
| `debug_screenshot.png` | Raw frame capture of the current render |
| `dom_dump.txt` | Full DOM tree and layout box state |
| `fenbrowser_*.log` | Module-specific traces (CSS, Layout, Performance) |

---

## 🛠️ Tech Stack

| Component | Technology | Purpose |
|-----------|------------|---------|
| Language | **C# / .NET 10** | Core implementation language |
| 2D Graphics | **SkiaSharp 2.88** | GPU-accelerated rendering (same engine as Chrome/Android) |
| Text Shaping | **HarfBuzz** (via SkiaSharp.HarfBuzz) | Complex script rendering for 100+ writing systems |
| Text Layout | **RichTextKit** | Paragraph layout, word wrapping, hit testing |
| Windowing | **Silk.NET** | Cross-platform window creation, input, OpenGL context |
| SVG | **Svg.Skia** | SVG parsing and rendering |
| Testing | **xUnit** | Unit and integration test framework |

All runtime dependencies are **MIT or Apache 2.0 licensed**. Zero telemetry. Zero analytics.

---

## 📋 Standards Compliance

FenBrowser targets conformance with core web standards:

- **HTML**: WHATWG HTML Living Standard (HTML5 tree builder)
- **CSS**: Selectors Level 4, CSS Color 4, Media Queries Level 4, Flexbox, Grid, Table
- **DOM**: Core DOM interfaces, `querySelector`/`querySelectorAll`, element state management
- **ECMAScript**: Test262-driven development — see `docs/test262_results.md` for current pass rates
- **WebDriver**: W3C WebDriver protocol
- **Rendering**: Acid2 conformance testing (currently ~98% pixel similarity)

> Compliance is tracked honestly. See [`docs/COMPLIANCE.md`](docs/COMPLIANCE.md) for the full status breakdown.

---

## 📖 Documentation

FenBrowser maintains a canonical documentation set:

| Volume | Scope |
|--------|-------|
| [**Volume I — System Manifest**](docs/VOLUME_I_SYSTEM_MANIFEST.md) | Architecture overview, build notes, roadmap |
| [**Volume II — Core**](docs/VOLUME_II_CORE.md) | DOM, networking, CSS types |
| [**Volume III — FenEngine**](docs/VOLUME_III_FENENGINE.md) | Layout, CSS cascade, rendering pipeline |
| [**Volume IV — Host**](docs/VOLUME_IV_HOST.md) | Windowing, input, OS integration |
| [**Volume V — DevTools**](docs/VOLUME_V_DEVTOOLS.md) | Inspector, debugging, profiling |
| [**Volume VI — Extensions & Verification**](docs/VOLUME_VI_EXTENSIONS_VERIFICATION.md) | WebDriver, testing, conformance |

---

## 🤝 Contributing

We welcome contributions! Please read [`CONTRIBUTING_ENGINE.md`](CONTRIBUTING_ENGINE.md) for our working contract:

1. **Fix one behavior per change** — keep scope minimal and local
2. **Reproduce first** — identify the broken pipeline stage before coding
3. **Test everything** — add or update focused tests for every behavior change
4. **No site-specific hacks** — fixes belong in the engine, not per-domain branches
5. **Document honestly** — partial support is fine; false claims are not

```bash
# Minimum verification per change
dotnet build <touched-project>.csproj -v minimal
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~<affected-slice>" -v minimal
```

---

## 📜 License

FenBrowser is released under the **MIT License**. See [LICENSE](LICENSE) for details.

---

<p align="center">
  <sub>Built with curiosity, caffeine, and a mass of web specifications.</sub>
</p>
