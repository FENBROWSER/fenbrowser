# FenBrowser Third-Party Dependencies

> **Comprehensive Technical Reference**
> Last Updated: December 29, 2024

This document provides an exhaustive analysis of every third-party library used in FenBrowser, organized by project module. Each entry includes technical details, integration rationale, security considerations, licensing, and honest assessment of trade-offs.

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Dependency Matrix](#dependency-matrix)
3. [FenBrowser.Core Dependencies](#fenbrowsercore-dependencies)
4. [FenBrowser.FenEngine Dependencies](#fenbrowserfenengine-dependencies)
5. [FenBrowser.Host Dependencies](#fenbrowserhost-dependencies)
6. [FenBrowser.DevTools Dependencies](#fenbrowserdevtools-dependencies)
7. [FenBrowser.UI Dependencies](#fenbrowserui-dependencies)
8. [FenBrowser.Tests Dependencies](#fenbrowsertests-dependencies)
9. [Transitive Dependencies](#transitive-dependencies)
10. [Security Analysis](#security-analysis)
11. [License Compliance](#license-compliance)
12. [Future Considerations](#future-considerations)

---

## Executive Summary

| Metric                        | Value                             |
| ----------------------------- | --------------------------------- |
| **Total Direct Dependencies** | 17 packages                       |
| **Unique Libraries**          | 11 distinct libraries             |
| **Runtime Dependencies**      | 9 packages                        |
| **Test-Only Dependencies**    | 4 packages                        |
| **Target Framework**          | .NET 8.0 (host), .NET 9.0 (tests) |

### Dependency Philosophy

FenBrowser follows these principles when selecting dependencies:

1. **Modularity** – Each library solves one problem well
2. **Security** – Only battle-tested, actively maintained libraries
3. **Privacy** – No telemetry, analytics, or tracking in any dependency
4. **Reliability** – Preferring stability over bleeding-edge features
5. **Performance** – Native code acceleration where beneficial

---

## Dependency Matrix

| Package                      | Version | Core | FenEngine | Host | DevTools | UI  | Tests |
| ---------------------------- | ------- | :--: | :-------: | :--: | :------: | :-: | :---: |
| SkiaSharp                    | 2.88.9  |  ✓   |     ✓     |  ✓   |    ✓     |     |       |
| SkiaSharp.HarfBuzz           | 2.88.9  |      |     ✓     |  ✓   |          |     |       |
| SkiaSharp.NativeAssets.Linux | 2.88.9  |      |     ✓     |      |          |     |       |
| SkiaSharp.NativeAssets.Win32 | 2.88.9  |      |           |  ✓   |          |     |       |
| Svg.Skia                     | 3.2.1   |      |     ✓     |      |          |     |       |
| Silk.NET.Windowing           | 2.21.0  |      |           |  ✓   |          |     |       |
| Silk.NET.Input               | 2.21.0  |      |           |  ✓   |          |     |       |
| Silk.NET.OpenGL              | 2.21.0  |      |           |  ✓   |          |     |       |
| Topten.RichTextKit           | 0.4.164 |      |           |  ✓   |          |     |       |
| Avalonia                     | 11.3.9  |      |           |      |          |  ✓  |       |
| Avalonia.Desktop             | 11.3.9  |      |           |      |          |  ✓  |       |
| Avalonia.Fonts.Inter         | 11.3.9  |      |           |      |          |  ✓  |       |
| Avalonia.Themes.Fluent       | 11.3.9  |      |           |      |          |  ✓  |       |
| xunit                        | 2.9.2   |      |           |      |          |     |   ✓   |
| xunit.runner.visualstudio    | 2.8.2   |      |           |      |          |     |   ✓   |
| Microsoft.NET.Test.Sdk       | 17.12.0 |      |           |      |          |     |   ✓   |
| coverlet.collector           | 6.0.2   |      |           |      |          |     |   ✓   |

---

## FenBrowser.Core Dependencies

The Core project contains DOM, CSS models, and fundamental types shared across all modules.

### SkiaSharp (v2.88.9)

```xml
<PackageReference Include="SkiaSharp" Version="2.88.9" />
```

**Description:**
SkiaSharp is the .NET binding for Google's Skia 2D graphics library. Skia is the same rendering engine that powers Google Chrome, Android, Flutter, and Firefox's canvas implementation.

**Why We Use It:**

- Provides `SKColor`, `SKRect`, `SKPoint` types used throughout the codebase
- Foundation for all visual rendering operations
- Industry-proven reliability with billions of users

**Technical Details:**

- Written in C++ with C# P/Invoke bindings
- Contains managed wrappers around native Skia objects
- Uses reference counting for native resource management

**Pros:**

- ✅ Same engine as Chrome/Android – battle-tested at scale
- ✅ Excellent cross-platform support (Windows, Linux, macOS, iOS, Android, WebAssembly)
- ✅ GPU acceleration via OpenGL, Vulkan, Metal, or Direct3D backends
- ✅ Comprehensive 2D feature set (paths, gradients, shaders, masks, filters)
- ✅ Hardware-accelerated text rendering with subpixel anti-aliasing
- ✅ Active maintenance by Microsoft (.NET team) and community
- ✅ MIT License – permissive, commercial-friendly

**Cons:**

- ⚠️ Large binary footprint (~15MB per platform for native libraries)
- ⚠️ Requires platform-specific native assets to be bundled
- ⚠️ Memory management requires care with `SKObject.Dispose()` calls
- ⚠️ Breaking changes occasionally occur between major versions
- ⚠️ Canvas state management (save/restore) can be error-prone

**Security Assessment:**

- 🟢 Low Risk – Open source, widely audited, no network access
- Last CVE: None specific to SkiaSharp; Skia itself has had minor issues patched promptly

**Alternatives Considered:**
| Alternative | Why Rejected |
|-------------|--------------|
| System.Drawing | Windows-only, deprecated in .NET Core |
| ImageSharp | No GPU acceleration, slower for real-time rendering |
| Cairo | Complex C interop, less .NET integration |
| Direct2D | Windows-only |

---

## FenBrowser.FenEngine Dependencies

The FenEngine project contains the core browser engine: HTML parsing, CSS computation, layout algorithms, and rendering pipeline.

### SkiaSharp (v2.88.9)

_Same as Core – see above_

### SkiaSharp.NativeAssets.Linux.NoDependencies (v2.88.9)

```xml
<PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="2.88.9" />
```

**Description:**
Provides the native Skia libraries compiled for Linux without requiring external system dependencies like fontconfig or freetype to be pre-installed.

**Why We Use It:**

- Enables Linux support without requiring users to install system packages
- Self-contained deployment for AppImage, Flatpak, or Docker

**Technical Details:**

- Contains `libSkiaSharp.so` compiled with static linking
- Includes bundled FreeType, HarfBuzz, and libpng
- ~20MB addition to Linux deployment

**Pros:**

- ✅ Zero system dependencies required on Linux
- ✅ Works in minimal containers (Alpine, scratch)
- ✅ Consistent font rendering across distributions
- ✅ Enables truly portable Linux binaries

**Cons:**

- ⚠️ Larger binary size than system-dependent version
- ⚠️ Cannot use system font cache
- ⚠️ May have older versions of bundled libraries

**Security Assessment:**

- 🟢 Low Risk – Static linking isolates from system library vulnerabilities
- Should update promptly when SkiaSharp releases security patches

### SkiaSharp.HarfBuzz (v2.88.9)

```xml
<PackageReference Include="SkiaSharp.HarfBuzz" Version="2.88.9" />
```

**Description:**
HarfBuzz is the industry-standard text shaping engine used by Chrome, Firefox, LibreOffice, and Android. This package integrates HarfBuzz with SkiaSharp for complex script rendering.

**Why We Use It:**

- Essential for correct rendering of non-Latin scripts
- Handles Arabic (RTL), Hindi (Devanagari), Thai, Hebrew, and 100+ other scripts
- Enables proper ligatures (fi, fl, ffi combinations)
- OpenType feature support (small caps, stylistic alternates)

**Technical Details:**

- Provides `SKShaper` class for text shaping
- Converts Unicode text to positioned glyphs
- Handles bidirectional text algorithm (UAX #9)
- Supports OpenType, TrueType, and WOFF fonts

**Pros:**

- ✅ Industry standard – same engine as Chrome/Firefox
- ✅ Correct rendering for 100+ writing systems
- ✅ Full Unicode 15.0 support
- ✅ OpenType layout features (GSUB, GPOS)
- ✅ Kerning, ligatures, mark positioning
- ✅ Bidirectional text support

**Cons:**

- ⚠️ Adds ~5MB to binary size
- ⚠️ Slower than simple ASCII rendering (~10x for complex scripts)
- ⚠️ Requires careful font fallback for missing glyphs
- ⚠️ Some rare script combinations may fail

**Security Assessment:**

- 🟢 Low Risk – No network access, well-audited codebase
- HarfBuzz has received security audits from multiple organizations

**Alternatives Considered:**
| Alternative | Why Rejected |
|-------------|--------------|
| DirectWrite (Windows) | Windows-only |
| Core Text (macOS) | macOS-only |
| ICU LayoutEngine | Deprecated, less maintained |
| No shaping | Would break non-Latin web content |

### Svg.Skia (v3.2.1)

```xml
<PackageReference Include="Svg.Skia" Version="3.2.1" />
```

**Description:**
A library for parsing and rendering SVG (Scalable Vector Graphics) images using SkiaSharp as the rendering backend.

**Why We Use It:**

- Web pages frequently use SVG for logos, icons, and illustrations
- SVG is resolution-independent (crisp at any zoom level)
- Google, GitHub, and most modern sites use SVG extensively

**Technical Details:**

- Parses SVG XML into an internal DOM representation
- Converts SVG elements to Skia drawing commands
- Supports SVG 1.1 specification (partial SVG 2.0)
- Returns `SKPicture` for efficient cached rendering

**Pros:**

- ✅ Native SkiaSharp integration – seamless rendering pipeline
- ✅ Good coverage of SVG 1.1 specification
- ✅ Handles paths, shapes, gradients, patterns, masks
- ✅ Text rendering support
- ✅ CSS styling within SVG
- ✅ Resolution-independent output

**Cons:**

- ⚠️ Incomplete SVG 2.0 support
- ⚠️ Limited SVG animation (SMIL) support
- ⚠️ Some filter effects not implemented (feConvolveMatrix)
- ⚠️ CSS animations within SVG not supported
- ⚠️ Foreign object embedding limited
- ⚠️ Less maintained than core SkiaSharp

**Security Assessment:**

- 🟡 Medium Risk – Parses untrusted SVG from web pages
- XML parsing could theoretically be exploited (billion laughs, XXE)
- Mitigations: Use secure XML parser settings, sanitize input

**Alternatives Considered:**
| Alternative | Why Rejected |
|-------------|--------------|
| Svg.NET | Returns System.Drawing objects, not Skia-compatible |
| Rendering as raster | Loses resolution independence |
| Browser's native SVG | We're building the browser! |

---

## FenBrowser.Host Dependencies

The Host project is the main executable that creates the window, handles input, and orchestrates the browser components.

### Silk.NET.Windowing (v2.21.0)

```xml
<PackageReference Include="Silk.NET.Windowing" Version="2.21.0" />
```

**Description:**
Silk.NET is a high-performance, low-level .NET binding to various native APIs. The Windowing component provides cross-platform window creation and management.

**Why We Use It:**

- Creates the main browser window on Windows, Linux, and macOS
- Handles window events (resize, minimize, maximize, close, focus)
- Provides the OpenGL context for GPU-accelerated rendering

**Technical Details:**

- Abstracts over GLFW (default), SDL2, or native windowing APIs
- Provides `IWindow` interface for window management
- Supports multiple windows, fullscreen, and borderless modes
- Integrates with input and OpenGL components

**Pros:**

- ✅ True cross-platform (Windows, Linux, macOS, Android, iOS)
- ✅ Modern .NET 8 design with async support
- ✅ Minimal abstraction overhead
- ✅ No COM dependencies (unlike WPF/WinForms)
- ✅ GLFW backend is extremely stable
- ✅ MIT License

**Cons:**

- ⚠️ Younger ecosystem than WPF/Qt
- ⚠️ Documentation could be more comprehensive
- ⚠️ Fewer Stack Overflow answers for troubleshooting
- ⚠️ Native window decorations vary by platform
- ⚠️ High DPI handling requires manual work

**Security Assessment:**

- 🟢 Low Risk – Thin wrapper over battle-tested GLFW
- No network access, no file system access beyond window management

**Alternatives Considered:**
| Alternative | Why Rejected |
|-------------|--------------|
| WPF | Windows-only |
| WinForms | Windows-only, legacy |
| Avalonia (for main window) | Too heavy for real-time rendering |
| SDL2 | More game-focused, less .NET idiomatic |
| GLFW direct | More complex interop |

### Silk.NET.Input (v2.21.0)

```xml
<PackageReference Include="Silk.NET.Input" Version="2.21.0" />
```

**Description:**
Input handling component of Silk.NET, providing unified access to keyboard, mouse, gamepad, and touch input.

**Why We Use It:**

- Captures all user input events for the browser
- Provides keyboard events for typing in address bar, text fields
- Mouse events for clicking links, scrolling, text selection
- Scroll wheel for smooth scrolling

**Technical Details:**

- Provides `IKeyboard`, `IMouse`, `IGamepad`, `IJoystick` interfaces
- Event-driven model with callbacks
- Supports key repeat, modifiers (Ctrl, Alt, Shift)
- Cursor position in window coordinates

**Pros:**

- ✅ Unified API across all platforms
- ✅ Low-latency event delivery
- ✅ Complete coverage (keyboard, mouse, gamepad, touch)
- ✅ Modifier key tracking
- ✅ Text input events (for IME support)
- ✅ Multi-monitor cursor tracking

**Cons:**

- ⚠️ IME (Input Method Editor) support varies by platform
- ⚠️ Touch/stylus support less mature than desktop input
- ⚠️ Limited accessibility input integration

**Security Assessment:**

- 🟢 Low Risk – Only reads input, no network or file access

### Silk.NET.OpenGL (v2.21.0)

```xml
<PackageReference Include="Silk.NET.OpenGL" Version="2.21.0" />
```

**Description:**
Type-safe .NET bindings for OpenGL 4.6 and OpenGL ES 3.2, enabling GPU-accelerated rendering.

**Why We Use It:**

- Creates the GPU rendering context for SkiaSharp
- Enables hardware-accelerated compositing
- Provides smooth 60fps scrolling and animations

**Technical Details:**

- Generates C# bindings from OpenGL specification
- Type-safe wrappers around OpenGL functions
- Supports OpenGL 1.0 through 4.6, OpenGL ES 2.0 through 3.2
- Integrates with Silk.NET.Windowing for context creation

**Pros:**

- ✅ Hardware acceleration offloads CPU
- ✅ Smooth scrolling and animations
- ✅ Efficient texture management
- ✅ Type-safe API prevents common OpenGL errors
- ✅ Supports modern OpenGL features

**Cons:**

- ⚠️ OpenGL driver bugs (especially on older Intel graphics)
- ⚠️ State machine complexity
- ⚠️ Requires software fallback for unsupported GPUs
- ⚠️ Debugging GPU issues is challenging
- ⚠️ OpenGL is being deprecated on macOS (works but no updates)

**Security Assessment:**

- 🟢 Low Risk – GPU rendering, no network access
- Driver vulnerabilities are OS-level, not library-level

**Alternatives Considered:**
| Alternative | Why Rejected |
|-------------|--------------|
| DirectX | Windows-only |
| Vulkan | More complex, overkill for 2D |
| Metal | macOS/iOS-only |
| WebGPU | Not yet stable for desktop |

### SkiaSharp (v2.88.9) & SkiaSharp.HarfBuzz (v2.88.9)

_Duplicated in Host for direct access – same as FenEngine_

### SkiaSharp.NativeAssets.Win32 (v2.88.9)

```xml
<PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="2.88.9" />
```

**Description:**
Native Skia libraries compiled for Windows (x86 and x64).

**Why We Use It:**

- Required for SkiaSharp to function on Windows
- Contains `libSkiaSharp.dll` for Windows

**Technical Details:**

- x86 and x64 native DLLs
- DirectWrite integration for system font access
- Direct3D backend available

**Pros:**

- ✅ Native Windows performance
- ✅ DirectWrite font rendering
- ✅ System font access

**Cons:**

- ⚠️ ~15MB per architecture

**Security Assessment:**

- 🟢 Low Risk – Same as SkiaSharp

### Topten.RichTextKit (v0.4.164)

```xml
<PackageReference Include="Topten.RichTextKit" Version="0.4.164" />
```

**Description:**
A rich text layout and rendering library built on SkiaSharp. Provides text block layout, word wrapping, and hit testing.

**Why We Use It:**

- Complex text layout (multi-line paragraphs)
- Text wrapping with word and character breaking
- Hit testing (click position to character index)
- Mixed style runs (bold, italic, links) in one block

**Technical Details:**

- Built by Topten Software (author of PetaPoco, Cantabile)
- Uses HarfBuzz for text shaping internally
- Provides `TextBlock` class for paragraph layout
- Supports style runs for inline formatting

**Pros:**

- ✅ Production-quality text layout
- ✅ Word wrapping with hyphenation support
- ✅ Rich text with mixed styles
- ✅ Hit testing for text selection
- ✅ Bidirectional text support
- ✅ Efficient reflow for editing

**Cons:**

- ⚠️ Limited maintenance (last update 2021)
- ⚠️ Some edge cases in complex layouts
- ⚠️ Performance degrades with very long texts
- ⚠️ May need forking for bug fixes

**Security Assessment:**

- 🟢 Low Risk – Text processing only, no I/O

**Alternatives Considered:**
| Alternative | Why Rejected |
|-------------|--------------|
| Custom implementation | Would take months |
| Pango | C library, complex interop |
| DirectWrite | Windows-only |

---

## FenBrowser.DevTools Dependencies

The DevTools project provides the browser developer tools (element inspector, styles panel).

### SkiaSharp (v2.88.9)

_Same as Core – for rendering DevTools panels_

---

## FenBrowser.UI Dependencies

The UI project provides the Avalonia-based UI framework (currently used for alternate UI experiments).

### Avalonia (v11.3.9)

```xml
<PackageReference Include="Avalonia" Version="11.3.9" />
```

**Description:**
Avalonia is a cross-platform XAML UI framework inspired by WPF. It provides a rich widget toolkit with data binding, styling, and templating.

**Why We Use It:**

- Powers the DevTools inspector UI
- Provides familiar XAML-based development
- Rich control library (TreeView, DataGrid, etc.)

**Note:** Avalonia is **NOT** used for the main browser window or web content rendering. Those use custom SkiaSharp rendering for pixel-perfect CSS compatibility.

**Technical Details:**

- XAML dialect similar to WPF/UWP
- Skia-based rendering (same as FenBrowser)
- ReactiveUI integration for MVVM
- Ahead-of-time compilation support

**Pros:**

- ✅ Cross-platform (Windows, Linux, macOS, iOS, Android, WebAssembly)
- ✅ XAML familiar to WPF developers
- ✅ Rich built-in controls
- ✅ Powerful styling and templating
- ✅ Active development and community
- ✅ MIT License

**Cons:**

- ⚠️ Adds ~20MB to distribution
- ⚠️ Slower startup than native controls
- ⚠️ Higher memory usage
- ⚠️ Some WPF features missing
- ⚠️ Designer support less mature than WPF

**Security Assessment:**

- 🟢 Low Risk – UI framework, no network access

**Why Not Used for Main Browser:**

- Custom rendering gives us pixel-perfect CSS control
- Avalonia's layout model differs from CSS box model
- Performance: Custom Skia is faster for web content
- We need custom text selection, caret rendering

### Avalonia.Desktop (v11.3.9)

```xml
<PackageReference Include="Avalonia.Desktop" Version="11.3.9" />
```

**Description:**
Desktop platform support for Avalonia, enabling Windows, Linux, and macOS applications.

**Why We Use It:**

- Required for Avalonia to run on desktop platforms
- Provides platform-specific integrations

### Avalonia.Fonts.Inter (v11.3.9)

```xml
<PackageReference Include="Avalonia.Fonts.Inter" Version="11.3.9" />
```

**Description:**
Bundles the Inter font family for consistent typography across platforms.

**Why We Use It:**

- Ensures consistent UI appearance regardless of system fonts
- Inter is designed specifically for UI readability

### Avalonia.Themes.Fluent (v11.3.9)

```xml
<PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.9" />
```

**Description:**
Microsoft Fluent Design System theme for Avalonia applications.

**Why We Use It:**

- Modern, professional appearance
- Dark and light mode support
- Consistent with Windows 11 aesthetic

---

## FenBrowser.Tests Dependencies

The Tests project contains unit and integration tests.

### xunit (v2.9.2)

```xml
<PackageReference Include="xunit" Version="2.9.2" />
```

**Description:**
xUnit.net is a modern testing framework for .NET, known for its simplicity and extensibility.

**Why We Use It:**

- Industry standard for .NET testing
- Clean, attribute-based test definition
- Excellent parallel test execution

**Pros:**

- ✅ Most popular .NET test framework
- ✅ Simple `[Fact]` and `[Theory]` attributes
- ✅ Parallel test execution
- ✅ Strong community and documentation
- ✅ Excellent IDE integration

**Cons:**

- ⚠️ Less feature-rich than NUnit for complex scenarios
- ⚠️ Different conventions than NUnit (no `[SetUp]`)

### xunit.runner.visualstudio (v2.8.2)

```xml
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
```

**Description:**
Test adapter for running xUnit tests in Visual Studio and VS Code.

**Why We Use It:**

- Enables Test Explorer integration
- Required for `dotnet test` discovery

### Microsoft.NET.Test.Sdk (v17.12.0)

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
```

**Description:**
The MSBuild targets and properties for building .NET test projects.

**Why We Use It:**

- Required for `dotnet test` to work
- Provides test host process

### coverlet.collector (v6.0.2)

```xml
<PackageReference Include="coverlet.collector" Version="6.0.2" />
```

**Description:**
Code coverage collection for .NET tests.

**Why We Use It:**

- Generates code coverage reports
- Integrates with CI/CD pipelines
- Outputs in multiple formats (Cobertura, OpenCover)

---

## Transitive Dependencies

These libraries are pulled in automatically by our direct dependencies:

| Package                  | Pulled By          | Purpose                 |
| ------------------------ | ------------------ | ----------------------- |
| System.Memory            | SkiaSharp          | Span<T> support         |
| System.Buffers           | SkiaSharp          | Array pooling           |
| HarfBuzzSharp            | SkiaSharp.HarfBuzz | Native HarfBuzz         |
| ShimSkiaSharp            | Svg.Skia           | SVG compatibility layer |
| Silk.NET.Core            | Silk.NET.\*        | Core abstractions       |
| Silk.NET.GLFW            | Silk.NET.Windowing | Window backend          |
| Avalonia.Remote.Protocol | Avalonia           | Designer communication  |
| ReactiveUI               | Avalonia           | MVVM framework          |

---

## Security Analysis

### Overall Risk Assessment: 🟢 LOW

| Category               | Assessment                                           |
| ---------------------- | ---------------------------------------------------- |
| **Network Access**     | None of our dependencies make network calls          |
| **File System Access** | Limited to resource loading                          |
| **Native Code**        | SkiaSharp, HarfBuzz, GLFW (all widely audited)       |
| **Input Parsing**      | SVG parsing is the highest risk area                 |
| **Telemetry**          | Zero - no analytics in any dependency                |
| **Supply Chain**       | All packages from NuGet.org with verified publishers |

### Recommendations:

1. **Keep dependencies updated** – Monitor for security advisories
2. **Pin versions** – Use exact versions to prevent supply chain attacks
3. **Audit transitive deps** – Review what gets pulled in
4. **SVG sanitization** – Consider pre-processing untrusted SVG

---

## License Compliance

| Package            | License    | Commercial Use |
| ------------------ | ---------- | -------------- |
| SkiaSharp          | MIT        | ✅ Allowed     |
| SkiaSharp.HarfBuzz | MIT        | ✅ Allowed     |
| Svg.Skia           | MIT        | ✅ Allowed     |
| Silk.NET           | MIT        | ✅ Allowed     |
| Topten.RichTextKit | Apache 2.0 | ✅ Allowed     |
| Avalonia           | MIT        | ✅ Allowed     |
| xunit              | Apache 2.0 | ✅ Allowed     |

**Conclusion:** All dependencies use permissive open-source licenses compatible with commercial and proprietary use.

---

## Future Considerations

### Potential Additions:

- **Angle** – For better WebGL compatibility
- **ICU** – For locale-aware text processing
- **Brotli** – For compressed resource loading
- **libwebp** – For WebP image support

### Potential Removals:

- **Topten.RichTextKit** – Consider forking or replacing if maintenance stalls
- **Avalonia** – If DevTools moves to custom Skia UI

### Upgrade Strategy:

1. Test new versions in staging branch
2. Run full test suite
3. Visual regression testing
4. Performance benchmarking
5. Deploy to beta users first

---

_This document should be reviewed and updated with each major dependency change._
