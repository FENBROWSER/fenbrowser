# FenBrowser — Master Security Audit

**Date:** 2026-02-24
**Auditor:** Claude Code (automated static analysis — two passes)
**Scope:** FenBrowser.FenEngine, FenBrowser.Core — JS engine, WebAPIs, DOM, networking, storage, rendering

---

## Executive Summary

Two automated audit passes were performed.
**11 code-level vulnerabilities found and fixed.**
A third section documents the broader browser security roadmap.

---

## Part 1 — Code Vulnerabilities (Pass 1)

First pass covered: ModuleLoader, FetchApi, XMLHttpRequest, InMemoryCookieStore, FenRuntime eval().

| #   | Issue                                             | File                                      | Severity     | CWE      | Status   |
| --- | ------------------------------------------------- | ----------------------------------------- | ------------ | -------- | -------- |
| 1   | Path traversal via `package.json` `"main"` field  | `Core/ModuleLoader.cs:264`                | **CRITICAL** | CWE-22   | ✅ FIXED |
| 2   | Unrestricted `eval()` — no permission check       | `Core/FenRuntime.cs:6986`                 | **HIGH**     | CWE-95   | ✅ FIXED |
| 3   | SSRF in `fetch()` — no URL scheme/IP validation   | `WebAPIs/FetchApi.cs:102`                 | **HIGH**     | CWE-611  | ✅ FIXED |
| 4   | HTTP method injection — no whitelist              | `FetchApi.cs:241`, `XMLHttpRequest.cs:70` | **HIGH**     | CWE-444  | ✅ FIXED |
| 5   | Cookie domain attribute — no host suffix check    | `DOM/InMemoryCookieStore.cs:97`           | **MEDIUM**   | CWE-1021 | ✅ FIXED |
| 6   | Memory DoS — no size limit on `package.json` read | `Core/ModuleLoader.cs:262`                | **MEDIUM**   | CWE-1025 | ✅ FIXED |

### Fix Details — Pass 1

**#1 Path Traversal (ModuleLoader.cs)**
`Path.GetFullPath()` is now computed before use; a `StartsWith(safeRoot)` guard rejects any path that escapes the package directory. A 10 MB file-size cap was added at the same time (fixes #6).

**#2 eval() Permission (FenRuntime.cs)**
`eval()` now checks `_context.Permissions.Check(JsPermissions.Eval)` before executing. `JsPermissions.Eval` is granted by default in browser contexts (`JavaScriptEngine.cs`) but revoked in `BrowserApi.cs` when the page's CSP `script-src` lacks `'unsafe-eval'`. Input size capped at 1 MB.

**#3 SSRF (FetchApi.cs)**
`ValidateFetchUrl()` helper added — rejects non-http/https schemes and blocks private/reserved IP ranges: loopback (127.x, ::1, localhost), RFC-1918 (10.x, 172.16-31.x, 192.168.x), link-local (169.254.x — AWS metadata), CGNAT (100.64-127.x), IPv6 unique-local (fc00::/7).

**#4 Method Injection (FetchApi.cs + XMLHttpRequest.cs)**
`ValidateMethod()` helper added — whitelists `GET POST PUT DELETE HEAD OPTIONS PATCH`. Called in `JsRequest` constructor and `XMLHttpRequest.Open()`.

**#5 Cookie Domain (InMemoryCookieStore.cs)**
Domain attribute value now validated against the request host: must be an exact match or a proper hostname suffix. Invalid domains are silently ignored and the cookie defaults to the request host. `HttpOnly` cookies are now excluded from `GetCookieString(fromScript: true)` used by `document.cookie`.

---

## Part 2 — Code Vulnerabilities (Pass 2)

Second pass covered: Function constructor, CRLF injection, prototype pollution, forbidden headers, setTimeout string code, innerHTML re-execution, storage isolation, worker validation.

| #   | Issue                                             | File                                | Severity     | CWE      | Status   |
| --- | ------------------------------------------------- | ----------------------------------- | ------------ | -------- | -------- |
| 7   | `Function()` constructor bypasses eval permission | `Core/FenRuntime.cs:9653`           | **CRITICAL** | CWE-95   | ✅ FIXED |
| 8   | CRLF injection in XHR `setRequestHeader()`        | `WebAPIs/XMLHttpRequest.cs:82`      | **HIGH**     | CWE-113  | ✅ FIXED |
| 9   | CRLF injection in Fetch `Headers.set/append()`    | `WebAPIs/FetchApi.cs:116`           | **HIGH**     | CWE-113  | ✅ FIXED |
| 10  | Prototype pollution via `__proto__` assignment    | `Core/FenObject.cs:127`             | **MEDIUM**   | CWE-1321 | ✅ FIXED |
| 11  | WHATWG forbidden headers not blocked              | `FetchApi.cs` + `XMLHttpRequest.cs` | **MEDIUM**   | CWE-96   | ✅ FIXED |

### Confirmed Safe (Pass 2)

| Area                                  | Verdict                                         |
| ------------------------------------- | ----------------------------------------------- |
| `setTimeout("string code", ...)` eval | ✅ SAFE — only function callbacks accepted      |
| `innerHTML` script re-execution       | ✅ SAFE — tokenizer does not re-execute scripts |
| `localStorage` origin isolation       | ✅ SAFE — scoped per origin in StorageApi.cs    |
| Worker script URL validation          | ✅ SAFE — URL validated before execution        |

### Fix Details — Pass 2

**#7 Function() constructor (FenRuntime.cs)**
Same `JsPermissions.Eval` check added to the `Function` constructor as for `eval()`. `new Function("code")()` is now blocked by the same CSP `unsafe-eval` enforcement.

**#8 + #9 CRLF Injection (XMLHttpRequest.cs + FetchApi.cs)**
`SanitizeHeaderValue()` helper strips `\r` and `\n` from all header names and values before storage and forwarding. Applied in `XMLHttpRequest.SetRequestHeader()`, `JsHeaders.SetHeader()`, and `JsHeaders.Append()`.

**#10 Prototype Pollution (FenObject.cs)**
`FenObject.SetWithReceiver()` intercepts the `__proto__` key and routes it to `SetPrototype()` instead of storing it as a data property, preventing `Object.prototype` pollution.

**#11 Forbidden Headers (FetchApi.cs + XMLHttpRequest.cs)**
Full WHATWG forbidden request header list implemented: `accept-charset`, `accept-encoding`, `access-control-request-*`, `connection`, `content-length`, `cookie`, `cookie2`, `date`, `dnt`, `expect`, `host`, `keep-alive`, `origin`, `referer`, `te`, `trailer`, `transfer-encoding`, `upgrade`, `via`. Any header with a `proxy-` or `sec-` prefix is also rejected.

---

## Part 3 — Broader Browser Security Roadmap

These are not code bugs found in audit, but architectural security features a production browser must implement. Ordered by risk vs. effort.

### 3.1 Network & Transport

| Feature                                                                       | Risk if Missing                                     | Effort | Status             |
| ----------------------------------------------------------------------------- | --------------------------------------------------- | ------ | ------------------ |
| TLS certificate validation (expired, revoked, mismatch)                       | Users silently connect to MITM proxies              | Medium | ⚠️ Needs review    |
| HSTS enforcement + persistence per origin                                     | Strips HTTPS downgrade protection                   | Medium | ⚠️ Needs review    |
| Mixed content blocking (active: scripts/XHR over HTTP on HTTPS page)          | Scripts can be MITM'd on HTTPS pages                | Medium | ⚠️ Needs review    |
| Mixed content warning (passive: images/video)                                 | Privacy leak                                        | Low    | ⚠️ Needs review    |
| DNS rebinding mitigation (re-validate IP per request, not just on SSRF check) | Bypasses our SSRF fix after initial DNS TTL expires | High   | ❌ Not implemented |
| `upgrade-insecure-requests` CSP directive                                     | Sub-resources silently load over HTTP               | Low    | ⚠️ Needs review    |
| `Sec-Fetch-Site/Mode/Dest` header injection                                   | Servers cannot distinguish cross-origin requests    | Low    | ❌ Not implemented |

### 3.2 Isolation & Sandboxing

| Feature                                               | Risk if Missing                                  | Effort | Status                                                                                                                      |
| ----------------------------------------------------- | ------------------------------------------------ | ------ | --------------------------------------------------------------------------------------------------------------------------- |
| `<iframe sandbox>` attribute enforcement              | Framed content gets full JS access               | Medium | ⚠️ Needs review                                                                                                             |
| `X-Frame-Options` / `CSP frame-ancestors` enforcement | Clickjacking on any page FenBrowser renders      | Low    | ✅ Infrastructure done (parsing + BrowserHost.CurrentXFrameOptions); full enforcement activates with iframe content loading |
| `document.domain` lowering blocked                    | Origin relaxation allows cross-origin DOM access | Low    | ❌ Not implemented                                                                                                          |
| `SameSite` cookie enforcement on outgoing requests    | CSRF on sites that rely on SameSite              | Medium | ⚠️ Needs review                                                                                                             |
| Storage partitioned by top-level site                 | Cross-site tracking via shared localStorage      | High   | ❌ Not implemented                                                                                                          |
| COOP/COEP headers for SharedArrayBuffer gating        | Spectre side-channel timing attacks              | High   | ❌ Not implemented                                                                                                          |

### 3.3 Content Security

| Feature                                                           | Risk if Missing                                      | Effort | Status             |
| ----------------------------------------------------------------- | ---------------------------------------------------- | ------ | ------------------ |
| Subresource Integrity (SRI) — `integrity=` on `<script>`/`<link>` | Compromised CDN silently delivers malicious scripts  | Medium | ❌ Not implemented |
| `X-Content-Type-Options: nosniff`                                 | MIME-sniffed JS execution from non-JS responses      | Low    | ⚠️ Needs review    |
| `Content-Disposition: attachment` — force download, not render    | Uploaded HTML/SVG served as attachment could execute | Low    | ⚠️ Needs review    |
| SVG script execution policy (apply CSP to inline SVG scripts)     | SVG `<script>` bypasses CSP                          | Medium | ⚠️ Needs review    |
| XXE disabled in XML/SVG parser                                    | External entity injection in crafted SVG             | Medium | ⚠️ Needs review    |

### 3.4 Privacy & Fingerprinting

| Feature                                                     | Risk if Missing                                     | Effort | Status             |
| ----------------------------------------------------------- | --------------------------------------------------- | ------ | ------------------ |
| Referrer policy default (`strict-origin-when-cross-origin`) | Full URLs leaked to third-party servers             | Low    | ⚠️ Needs review    |
| Third-party cookie blocking                                 | Cross-site tracking                                 | Medium | ⚠️ Needs review    |
| Canvas/WebGL fingerprint noise                              | Unique device fingerprint via pixel-level rendering | High   | ❌ Not implemented |
| IDN homograph attack warning (punycode in address bar)      | Lookalike phishing domains invisible to users       | Medium | ❌ Not implemented |
| `navigator.userAgent` normalization                         | Reduces browser fingerprint surface                 | Low    | ❌ Not implemented |

### 3.5 Safe Browsing & Phishing

| Feature                                                             | Risk if Missing                                      | Effort | Status             |
| ------------------------------------------------------------------- | ---------------------------------------------------- | ------ | ------------------ |
| Malicious URL check (Google Safe Browsing or equivalent)            | Users land on phishing/malware pages without warning | High   | ❌ Not implemented |
| HTTP form on HTTPS page warning                                     | Credentials submitted over unencrypted connection    | Low    | ❌ Not implemented |
| Dangerous download warning (`.exe`, `.bat`, `.ps1`, `.msi`, `.scr`) | Drive-by download execution                          | Low    | ❌ Not implemented |
| Lookalike/typosquat domain detection                                | Phishing via `paypa1.com` etc.                       | High   | ❌ Not implemented |

### 3.6 UI Security

| Feature                                                               | Risk if Missing                              | Effort | Status             |
| --------------------------------------------------------------------- | -------------------------------------------- | ------ | ------------------ |
| Address bar JS-spoof prevention (JS cannot modify URL bar display)    | Phishing pages show fake URLs                | Low    | ⚠️ Needs review    |
| Permission prompt anti-spam (rate limit, user gesture required)       | Sites spam camera/location prompts           | Low    | ❌ Not implemented |
| Clipboard write requires user gesture                                 | Silent clipboard hijacking                   | Low    | ⚠️ Needs review    |
| `window.open` popup blocker (require user gesture)                    | Ad/redirect popup storms                     | Low    | ⚠️ Needs review    |
| Modal dialog abuse prevention (`alert`/`confirm`/`prompt` rate limit) | Sites block navigation with infinite dialogs | Low    | ❌ Not implemented |

### 3.7 Storage & Credentials

| Feature                                                   | Risk if Missing                               | Effort | Status             |
| --------------------------------------------------------- | --------------------------------------------- | ------ | ------------------ |
| Per-origin storage quota (localStorage, IndexedDB, Cache) | Disk exhaustion DoS                           | Low    | ❌ Not implemented |
| Private/Incognito mode — no disk writes, wipe on close    | Session data persists after private browsing  | Medium | ⚠️ Needs review    |
| Saved password encryption (OS keychain)                   | Plaintext credentials on disk                 | High   | ❌ Not implemented |
| Autofill cross-origin iframe block                        | Autofill leaks credentials to embedded frames | Medium | ⚠️ Needs review    |

---

## Combined Vulnerability Tracker

| #   | Issue                                      | Severity | Status   |
| --- | ------------------------------------------ | -------- | -------- |
| 1   | Path traversal — ModuleLoader package.json | CRITICAL | ✅ FIXED |
| 7   | Function() constructor eval bypass         | CRITICAL | ✅ FIXED |
| 2   | eval() unrestricted                        | HIGH     | ✅ FIXED |
| 3   | SSRF — fetch() URL validation              | HIGH     | ✅ FIXED |
| 4   | HTTP method injection                      | HIGH     | ✅ FIXED |
| 8   | CRLF injection — XHR headers               | HIGH     | ✅ FIXED |
| 9   | CRLF injection — Fetch headers             | HIGH     | ✅ FIXED |
| 5   | Cookie domain validation + HttpOnly        | MEDIUM   | ✅ FIXED |
| 6   | Memory DoS — package.json size             | MEDIUM   | ✅ FIXED |
| 10  | Prototype pollution via `__proto__`        | MEDIUM   | ✅ FIXED |
| 11  | WHATWG forbidden headers                   | MEDIUM   | ✅ FIXED |

**All 11 code-level vulnerabilities: FIXED**

---

## Immediate Action Items (Top 5 from Roadmap)

1. ✅ **TLS cert validation** — strict by default; `SslPolicyErrors` now flow into `FetchResult`, error page shows specific per-error headline (name mismatch / expired / untrusted CA / no cert), cert details (subject, issuer, expiry, fingerprint, SANs) shown in Advanced panel. SSL detection upgraded to `HttpRequestError.SecureConnectionError` enum.
2. ✅ **`X-Frame-Options` enforcement** — `XFrameOptionsPolicy` enum added to `FenBrowser.Core`; parsed from every HTTP response into `FetchResult.XFrameOptions`; stored as `BrowserHost.CurrentXFrameOptions` after each top-level navigation; reset to `None` on new navigation. Full iframe-content blocking activates automatically when `<iframe>` content loading is implemented.
3. **SRI for external scripts** — `<script integrity="sha384-...">` prevents CDN compromise
4. **`X-Content-Type-Options: nosniff`** — one-line check, prevents MIME-sniffed script execution
5. **Per-origin storage quota** — prevents any single page from filling the user's disk
