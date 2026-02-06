# FenBrowser Agent Alignment Manifest (`AGENTS.md`)

This document defines the **Coding Philosophy** and **Operational Standards** for any AI Agent working on the FenBrowser codebase. Adherence is mandatory to maintain the project's long-term architectural integrity and documentation accuracy.

---

## 1. The "Living Bible" Policy (CRITICAL)

To prevent the **Technical Reference** from becoming stale, every agent interaction MUST follow the **Documentation Synchronization Rule**:

- **No Task is "Done" until Documentation is Updated**: If you add a feature, refactor a class, or move a file, you MUST update the corresponding `Volume_*` and `Part_*` files in `docs/TechnicalReference/` and `docs/`.
- **Logic Mapping**:
  1. Check **[SYSTEM_MANIFEST.md](file:///C:/Users/udayk/Videos/FENBROWSER/docs/SYSTEM_MANIFEST.md)** to identify the impacted layer.
  2. Locate the **Master Index** (`Volume_*_Tree.md`) for that layer.
  3. Update the specific **Part** documentation with new logic summaries, line ranges, and function descriptions.
- **Atomic Integrity**: Documentation updates should be included in the same PR/Commit series as the code changes.

---

## 2. Fenbrowser Development Guidelines

### Core Identity & Philosophy

- **Project Identity**: The browser is referred to as `fen` or `fenbrowser`.
- **Core Philosophy**: Development emphasizes **modularity, security, privacy, and reliability**.
- **Industry Standards**: Implement best practices from leading browsers (Chromium, Firefox, Webkit) while proactively avoiding their historical pitfalls and architectural mistakes.
- **Motto**: **"Architecture is Destiny"**. Every technical decision must serve long-term structural integrity over short-term features.

### Implementation Excellence

- **Professional Rigor**: Conduct thorough analysis from the perspective of a **Senior System Architect and Senior Software Engineer** before any implementation.
- **Feature Completeness**: Deliver fully functional, production-ready features. **Never build stubs or "half-baked" implementations**; every component must be feature-complete.
- **Performance Optimization**: Write highly optimized and **optimizable code where every second is very important**. Every millisecond is critical. Utilize low-level optimizations (Zero-copy, pooling) and efficient algorithms for maximum responsiveness.
- **Documentation**: Maintain clear, concise documentation for all modules and architectural decisions to support modularity and long-term maintainability.

---

## 3. High-Precision AI Alignment

### Critical Vocabulary

To avoid terminology drift, agents MUST use these project-specific terms:

- **Box Tree**: The visual layout tree generated from the DOM.
- **Compositor**: The logic in the Host that blends Web Content with UI Widgets.
- **The Strut**: The baseline/line-height metric used for text vertical alignment.
- **Renderer**: Specifically the `SkiaDomRenderer` which translates the Box Tree to Skia commands.

### Threading Boundaries

- **UI Thread ONLY**: All `Widget` modifications, Win32 event handling, and Window management.
- **Render Thread ONLY**: All `Box` calculations, Layout algorithms, and `Paint` operations.
- **Task Loop**: Use the `EventLoopCoordinator` for microtasks to avoid blocking the hot-paths.

### Avoid Legacy Pitfalls (Learnings from Chromium/Webkit)

To avoid the architectural mistakes of existing browsers, agents must:

- **Reject Library Dominance**: Never let a library (like Skia) dictate layout metrics. We use libraries for primitive drawing only (Rule 1).
- **Enforce State Isolation**: Unlike legacy browsers where global state often causes rendering artifacts, FenBrowser enforces strict `PipelineContext` passing.
- **Abstract the GPU/OS**: Mask the hardware backend (Rule 4) so we don't end up locked into a specific OS-native graphics API.

---

## 4. Senior Engineering Safeguards (MANDATORY)

### 1. Memory Discipline (Native Resource Ownership)

Since FenBrowser relies heavily on SkiaSharp, we are dealing with native C++ objects. If an AI agent forgets to dispose of an `SKPaint` or `SKPath` inside a render loop, the browser will leak memory until it crashes.

- **Rule**: Mandatory `using` blocks for all Skia native types and a preference for object pooling over allocation in the `FenEngine`.

### 2. Regression-Reproduction First

In browser engineering, a "fix" is often invisible unless you prove the failure first.

- **Rule**: Mandate that an agent must reproduce the bug in the logs and `debug_screenshot.png` **before** writing a single line of the fix. This prevents "blind coding."

### 3. Hard Security Constraints (Rule 3 Enforcement)

The Constitution mentions SVG sandboxing, but we must codify the **Hard Limits** for the AI to enforce during implementation.

- **Limits**:
  - **Max SVG Depth**: 32 levels.
  - **Max Filters**: 10 per element.
  - **Max Render Time**: 100ms per frame (to prevent UI freeze).

### 4. Cross-Platform Vision (Linux, macOS, Mobile)

FenBrowser is **"Windows-First, but not Windows-Forever."**

- **Platform Abstraction**: Avoid using `Win32` APIs directly outside the `FenBrowser.Host` project.
- **Agnostic Logic**: All logic in `Engine`, `Core`, and `WebDriver` must remain platform-agnostic to support future ports to Linux, macOS, Android, and iOS.

---

## 5. Operational Workflow (The Fen Loop)

### Execution & Debugging Lifecycle

Always follow this clean-state execution process after every build:

1.  **Process Management**: Kill any existing `fenbrowser` / `Fenbrowser.host` executable processes.
2.  **Log Cleanup**: Clear all files in the `fenbrowser/logs` folder.
3.  **The 45-Second Rule**: Run the solution build (`Fenbrowser.host`) and **wait at least 45 seconds** before analyzing logs. This allows for proper data loading and engine stabilization. **Do NOT kill within 10 seconds.**
4.  **Log Locations**: Logs are located in:
    - `C:\Users\udayk\Videos\FENBROWSER\logs`
    - `C:\Users\udayk\Videos\FENBROWSER\`
      _(Do not check incorrect folders)_.

### Post-Build Diagnostics Priority

Analyze resources in this strict order:

1.  **`debug_screenshot.png`**: **Visual Confirmation**. This must be checked every time logs are analyzed.
2.  **`logs/raw_source_*.html`**: The raw HTML processed by the parser.
3.  **`dom_dump.txt`**: The parsed DOM tree and computed layout boxes.
4.  **`logs/fenbrowser_*.log`**: Module logs (`[CSS-DIAG]`, `[BOX]`, `[Layout]`).

---

> [!IMPORTANT]
> **To the Agent:**
> Your goal is not just to "solve the ticket," but to harden the browser and keep its "Project Bible" current. Stale documentation is as dangerous as bad code. Architecture is Destiny.
