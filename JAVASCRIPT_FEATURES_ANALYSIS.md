# FenBrowser JavaScript Features Analysis

## Executive Summary

FenBrowser employs a custom, lightweight JavaScript engine (`FenBrowser.FenEngine.Core`) rather than embedding a standard engine like V8 or SpiderMonkey. Critical observations:

- **Fragile Implementation**: A significant portion of standard APIs (`addEventListener`, `fetch`, `localStorage`, `console`) are implemented via **Regex pattern matching** on the source code string before execution. This means `console.log("hi")` works, but `var l = console.log; l("hi")` would fail.
- **Missing Core Standards**: No native `Promise` implementation; `fetch` is simulated with a custom "then-able" object.
- **Partial DOM**: Basic node query and manipulation exists, but critical features like `element.style` (CSSOM) and node traversal (`parentNode`, `nextSibling`) are missing.

---

## 1. Core Engine & Runtime

**Engine**: Custom implementation (`FenBrowser.FenEngine.Core.Interpreter`) with potential legacy `MiniJs` roots.

| Feature            | Status     | Notes                                                                 |
| :----------------- | :--------- | :-------------------------------------------------------------------- |
| **ES6+ Support**   | ❌ Missing | No arrows, classes, let/const scoping, destructuring observed.        |
| **Promises**       | ❌ Missing | No native `Promise` or `async/await`. `fetch` is a hack.              |
| **Error Handling** | ⚠️ Basic   | Basic try/catch support in interpreter, but stack traces are minimal. |
| **Variables**      | ⚠️ Mixed   | Regex interception prevents robust variable aliasing for system APIs. |

---

## 2. DOM (Document Object Model)

**Implementation**: `JavaScriptEngine.Dom.cs` defines `JsDocument` and `JsDomElement`.

### ✅ Implemented

- **Selection**:
  - `getElementById`, `getElementsByTagName`, `getElementsByClassName`.
  - `querySelector`, `querySelectorAll` (Supports ID, Class, Tag, Attributes `[a=b]`, `^=`, `$=`, `*=`).
- **Manipulation**:
  - `createElement`, `createTextNode`.
  - `appendChild`, `removeChild`.
  - `innerHTML` (via `HtmlLiteParser`), `innerText`, `textContent`.
  - `setAttribute`, `getAttribute`.
  - `classList` (Full support: `add`, `remove`, `toggle`, `contains`).
- **Properties**: `id`, `tagName`, `value` (input/textarea).

### ❌ Missing / Critical Gaps

- **CSSOM (`element.style`)**: **CRITICAL**. Elements do not expose a `style` property. cannot set `el.style.display = 'none'`.
- **Event Listeners on Elements**: `element.addEventListener` is **NOT** a method on the node. It is intercepted via Regex from the source string.
  - _Pattern supported_: `document.getElementById('x').addEventListener(...)`.
  - _Pattern failing_: `var el = document.getElementById('x'); el.addEventListener(...)`.
- **Traversal**: No `parentNode`, `nextSibling`, `childNodes` exposed on `JsDomElement`.
- **Dimensions**: No `offsetWidth`, `offsetHeight`, `getBoundingClientRect()`.

---

## 3. BOM (Browser Object Model)

Most BOM features are "simulated" by intercepting specific code patterns using Regex in `HandlePhase123Builtins`.

| API                          | Implementation Method | Limitations                                                                  |
| :--------------------------- | :-------------------- | :--------------------------------------------------------------------------- |
| `console.log/error`          | **Regex Match**       | Only works as direct `console.log(...)` statement.                           |
| `setTimeout` / `setInterval` | **Regex Match**       | Only works as direct call.                                                   |
| `localStorage`               | **Regex Match**       | Supports `setItem`, `getItem`, `removeItem`, `clear`. Sync logic via `lock`. |
| `document.cookie`            | **Regex Match**       | Intercepts assignments and gets.                                             |
| `window.location`            | **Regex Match**       | Intercepts `assign`, `replace`, `href`.                                      |
| `history`                    | **Regex Match**       | `pushState`, `replaceState`, `go`, `back`, `forward`.                        |
| `alert`                      | **Host Delegate**     | Basic support.                                                               |

**Missing BOM**:

- `navigator` (completely missing).
- `screen` (width/height).
- `window` metrics (innerWidth, innerHeight, scrollX, scrollY).

---

## 4. Web APIs

### Canvas 2D

**Status**: ⚠️ **Basic / Skeletal**

- **Context**: `getContext('2d')` returns a wrapper around SkiaSharp.
- **Supported**: `fillRect`, `clearRect`, `beginPath`, `moveTo`, `lineTo`, `stroke`, `fill`.
- **Properties**: `fillStyle`, `strokeStyle`, `lineWidth`.
- **Missing**:
  - `drawImage` (Critical for games/apps).
  - `fillText` / `strokeText`.
  - `arc`, `bezierCurveTo`.
  - Transformations (`scale`, `rotate`, `translate`, `save`, `restore`).

### Network (`fetch`)

**Status**: ⚠️ **Hack / Non-Standard**

- Implemented via Regex: `fetch(url).then(fn)`.
- **Limitations**:
  - Not a real Promise.
  - Only supports `.then(fn)`. No `.catch()`.
  - Callback receives an object `{ ok, status, text(), json() }`.
  - No `Post` support or header configuration visible in Regex parsing.

### MutationObserver

**Status**: ✅ **Basic Support**

- Implemented in `JavaScriptEngine.cs`.
- Supports `childList` and `attributes` mutations.
- Triggers via internal `_pendingMutations` list.

---

## 5. Summary of Critical Deficiencies

1.  **Regex-Based API Calls**: This is the single biggest point of failure. It prevents using any JS minifiers, bundlers, or frameworks (React, Vue, jQuery) because they often alias functions or call them dynamically.
2.  **No CSS Manipulation**: The inability to set `element.style` renders most dynamic UI logic impossible.
3.  **Missing Promises**: Breaks 99% of modern web code using `async/await` or `fetch`.
4.  **No DOM Traversal**: Scripts cannot "walk" the DOM to find related elements.

## Recommendations

1.  **Stop using Regex for APIs**: Bind actual C# methods to the `FenFunction` delegates in the `JsContext` global scope.
2.  **Implement `element.style`**: Map `JsDomElement.style` to a proxy object that updates the underlying `LiteElement` attributes string.
3.  **Implement `Promise`**: Use a C# `TaskCompletionSource` wrapper to expose a real Promise implementation to the JS engine.
