# FenBrowser

FenBrowser is an experimental, clean-slate browser engine written in C#. Instead of pinning onto an existing engine like Chromium or WebKit, this project is a ground-up implementation of the web stack—from the network layer and HTML parser to the CSS layout engine and Skia-based renderer.

> [!WARNING]
> **Experimental Status**: This is a hobby/research project. It's nowhere near being a "daily driver" browser. Many websites will not render correctly (or at all) yet. We're building this to understand the complexity of the modern web, not to replace your current browser.

## The Objective

The goal of FenBrowser is to implement a standards-compliant engine using modern .NET. We prioritize architectural clarity and modularity over a massive feature list.

### What's inside?

- **Custom Layout Engine**: Supporting Block, Inline, Flex, and Grid formatting contexts.
- **Standards-Based Parsing**: An HTML5 tree builder and CSS tokenizer following WHATWG/W3C specs as closely as possible.
- **Native Rendering**: Using SkiaSharp for primitive drawing, with a custom-built paint tree and z-index resolver.
- **Automation First**: Deep integration with the WebDriver protocol and a basic DevTools implementation for engine inspection.

## Project Structure

- **`FenBrowser.FenEngine`**: The core layout and rendering logic. This is where the "heavy lifting" happens (Measure/Arrange passes).
- **`FenBrowser.Core`**: Shared primitives, the DOM tree implementation, and the HTTP stack.
- **`FenBrowser.Host`**: The desktop shell. Currently Windows-focused for debugging and rapid prototyping.
- **`FenBrowser.WebDriver` & `DevTools`**: Specialized projects for controlling and inspecting the engine via standard protocols.

## For Developers

If you want to poke around the code:

1. Open `FenBrowser.sln` in Visual Studio 2022.
2. Run the `FenBrowser.Host` project.
3. Check the `logs/` folder. We output a lot of diagnostic data:
   - `dom_dump.txt`: The state of the DOM and Layout boxes.
   - `debug_screenshot.png`: A raw frame capture of the current render.
   - `fenbrowser_*.log`: Module-specific traces (CSS, Layout, Performance).

## License

MIT License. See [LICENSE](LICENSE) for details.
