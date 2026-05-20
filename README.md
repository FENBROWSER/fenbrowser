# FenBrowser

FenBrowser is an experimental, clean-slate browser engine written in C#. Instead of pinning onto an existing engine like Chromium or WebKit, this project is a ground-up implementation of the web stack—from the network layer and HTML parser to the CSS layout engine and Skia-based renderer.

> [!WARNING]
> **Experimental Status**: This is a hobby/research project. It's nowhere near being a "daily driver" browser. Many websites will not render correctly (or at all) yet. We're building this to understand the complexity of the modern web, not to replace your current browser.

## The Objective

The goal of FenBrowser is to implement a standards-compliant engine using modern .NET. We prioritize architectural clarity and modularity over a massive feature list.

### What's inside?

- **FenJS Runtime**: A standalone C# ECMAScript engine foundation with lexer, parser, AST validation, bytecode, interpreter, logical heap, native intrinsics, and a Test262-first runner.
- **Custom Layout Engine**: Supporting Block, Inline, Flex, and Grid formatting contexts.
- **Standards-Based Parsing**: An HTML5 tree builder and CSS tokenizer following WHATWG/W3C specs as closely as possible.
- **Native Rendering**: Using SkiaSharp for primitive drawing, with a custom-built paint tree and z-index resolver.
- **Automation First**: Deep integration with the WebDriver protocol and a basic DevTools implementation for engine inspection.

## Project Structure

- **`FenBrowser.FenEngine`**: The core layout and rendering logic. This is where the "heavy lifting" happens (Measure/Arrange passes).
- **`FenBrowser.Js`**: The standalone ECMAScript engine layer used for spec-driven JS work before browser embedding.
- **`FenBrowser.Js.Shell`**: A small CLI for engine smoke checks such as `--eval`, `--dump-tokens`, `--dump-ast`, and `--dump-bytecode`.
- **`FenBrowser.Js.Test262`**: Test262 enumeration, dry-run, parser-subset, runtime-subset, dashboard, and gate verification tooling.
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
4. For standalone JS engine work, use the focused projects first:
   - `dotnet run --project FenBrowser.Js.Shell/FenBrowser.Js.Shell.csproj -- --eval "Object(null) instanceof Object"`
   - `dotnet run --project FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -- --runtime-subset --root external/test262 --test262 built-ins/Object --max 10`

## License

MIT License. See [LICENSE](LICENSE) for details.
