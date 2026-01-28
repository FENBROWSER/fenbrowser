# FenBrowser 🚀

Welcome to **FenBrowser**, a modern, high-performance browser engine built from the ground up with modularity, privacy, and speed in mind. This isn't just another Chromium skin—it's a clean-slate implementation designed to explore the future of web rendering and secure browsing.

## Why FenBrowser?

Most modern browsers are built on engines with decades of legacy baggage. FenBrowser is our attempt to build something fresh, focusing on:

- **Clean Architecture**: A strictly decoupled engine, core, and host system.
- **Privacy First**: Deny-by-default security model and a custom networking stack.
- **Performance**: High-efficiency layout and rendering powered by SkiaSharp.
- **Developer Friendly**: Built-in high-fidelity WebDriver and DevTools protocol support.

## Project Structure

- **`FenBrowser.FenEngine`**: The heart of the project. Handles HTML/CSS parsing, layout (BFC/IFC/Flex/Grid), and painting.
- **`FenBrowser.Core`**: Shared types, logging infrastructure, and fundamental DOM definitions.
- **`FenBrowser.Host`**: The desktop application wrapper (Windows-first).
- **`FenBrowser.WebDriver` & `DevTools`**: Standards-compliant automation and debugging interfaces.

## Getting Started

Since we're in active development, things move fast.

1. **Clone the repo.**
2. **Open `FenBrowser.sln`** in Visual Studio 2022.
3. **Build and Run** the `FenBrowser.Host` project.
4. Check the `logs/` directory for detailed execution traces and `debug_screenshot.png` for visual verification.

## License

This project is licensed under the **MIT License**. See the [LICENSE](LICENSE) file for more details.

---

_Built with ❤️ by the FenBrowser Team._
