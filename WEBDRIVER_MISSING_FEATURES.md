# FenBrowser WebDriver - Missing Features Comparison

Comparison with W3C WebDriver Specification (https://www.w3.org/TR/webdriver2/)

## Currently Implemented ✅

| Endpoint | Command | Status |
|----------|---------|--------|
| `POST /session` | New Session | ✅ Basic |
| `DELETE /session/{id}` | Delete Session | ✅ |
| `POST /session/{id}/url` | Navigate To | ✅ |
| `GET /session/{id}/title` | Get Title | ✅ |
| `GET /session/{id}/screenshot` | Take Screenshot | ✅ |
| `POST /session/{id}/element` | Find Element | ✅ |
| `POST /session/{id}/element/{id}/click` | Element Click | ✅ |
| `POST /session/{id}/execute/sync` | Execute Script | ✅ |

---

## Missing Features ❌

### Session & Status (Priority: High)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `GET /status` | Status | Returns information about whether a remote end is in a state in which it can create new sessions |
| `GET /session/{id}/timeouts` | Get Timeouts | Gets timeout durations associated with the current session |
| `POST /session/{id}/timeouts` | Set Timeouts | Configure timeout durations (script, pageLoad, implicit) |

### Navigation (Priority: High)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `GET /session/{id}/url` | Get Current URL | Returns the current page URL |
| `POST /session/{id}/back` | Back | Navigate backwards in history |
| `POST /session/{id}/forward` | Forward | Navigate forwards in history |
| `POST /session/{id}/refresh` | Refresh | Refresh the current page |

### Window/Context Management (Priority: High)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `GET /session/{id}/window` | Get Window Handle | Returns the current window handle |
| `DELETE /session/{id}/window` | Close Window | Close the current window |
| `POST /session/{id}/window` | Switch To Window | Switch to a different window |
| `GET /session/{id}/window/handles` | Get Window Handles | Returns all window handles |
| `POST /session/{id}/window/new` | New Window | Create a new window |
| `POST /session/{id}/frame` | Switch To Frame | Switch to a frame |
| `POST /session/{id}/frame/parent` | Switch To Parent Frame | Switch to parent frame |
| `GET /session/{id}/window/rect` | Get Window Rect | Get window size and position |
| `POST /session/{id}/window/rect` | Set Window Rect | Set window size and position |
| `POST /session/{id}/window/maximize` | Maximize Window | Maximize the window |
| `POST /session/{id}/window/minimize` | Minimize Window | Minimize the window |
| `POST /session/{id}/window/fullscreen` | Fullscreen Window | Make window fullscreen |

### Element Retrieval (Priority: High)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `POST /session/{id}/elements` | Find Elements | Find multiple elements |
| `POST /session/{id}/element/{id}/element` | Find Element From Element | Find element from another element |
| `POST /session/{id}/element/{id}/elements` | Find Elements From Element | Find multiple elements from element |
| `GET /session/{id}/element/active` | Get Active Element | Get the currently focused element |
| `GET /session/{id}/element/{id}/shadow` | Get Element Shadow Root | Get shadow root of element |
| `POST /session/{id}/shadow/{id}/element` | Find Element From Shadow Root | Find element in shadow DOM |
| `POST /session/{id}/shadow/{id}/elements` | Find Elements From Shadow Root | Find elements in shadow DOM |

### Element State (Priority: High)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `GET /session/{id}/element/{id}/selected` | Is Element Selected | Check if element is selected |
| `GET /session/{id}/element/{id}/attribute/{name}` | Get Element Attribute | Get element's attribute value |
| `GET /session/{id}/element/{id}/property/{name}` | Get Element Property | Get element's property value |
| `GET /session/{id}/element/{id}/css/{property}` | Get Element CSS Value | Get computed CSS value |
| `GET /session/{id}/element/{id}/text` | Get Element Text | Get element's visible text |
| `GET /session/{id}/element/{id}/name` | Get Element Tag Name | Get element's tag name |
| `GET /session/{id}/element/{id}/rect` | Get Element Rect | Get element's bounding rectangle |
| `GET /session/{id}/element/{id}/enabled` | Is Element Enabled | Check if element is enabled |
| `GET /session/{id}/element/{id}/computedrole` | Get Computed Role | Get WAI-ARIA role |
| `GET /session/{id}/element/{id}/computedlabel` | Get Computed Label | Get accessible name |

### Element Interaction (Priority: High)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `POST /session/{id}/element/{id}/clear` | Element Clear | Clear a form element |
| `POST /session/{id}/element/{id}/value` | Element Send Keys | Type into an element |

### Document (Priority: Medium)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `GET /session/{id}/source` | Get Page Source | Get the page source HTML |
| `POST /session/{id}/execute/async` | Execute Async Script | Execute async JavaScript |

### Cookies (Priority: Medium)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `GET /session/{id}/cookie` | Get All Cookies | Get all cookies |
| `GET /session/{id}/cookie/{name}` | Get Named Cookie | Get a specific cookie |
| `POST /session/{id}/cookie` | Add Cookie | Add a cookie |
| `DELETE /session/{id}/cookie/{name}` | Delete Cookie | Delete a specific cookie |
| `DELETE /session/{id}/cookie` | Delete All Cookies | Delete all cookies |

### Actions API (Priority: Medium)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `POST /session/{id}/actions` | Perform Actions | Perform complex input actions (keyboard, mouse, touch) |
| `DELETE /session/{id}/actions` | Release Actions | Release all pressed keys/buttons |

### User Prompts/Alerts (Priority: Medium)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `POST /session/{id}/alert/dismiss` | Dismiss Alert | Dismiss a dialog |
| `POST /session/{id}/alert/accept` | Accept Alert | Accept a dialog |
| `GET /session/{id}/alert/text` | Get Alert Text | Get dialog text |
| `POST /session/{id}/alert/text` | Send Alert Text | Send text to prompt dialog |

### Screenshots (Priority: Low)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `GET /session/{id}/element/{id}/screenshot` | Take Element Screenshot | Screenshot of specific element |

### Print (Priority: Low)

| Endpoint | Command | Description |
|----------|---------|-------------|
| `POST /session/{id}/print` | Print Page | Print page to PDF |

---

## Summary Statistics

| Category | Implemented | Missing | Total |
|----------|-------------|---------|-------|
| Session/Status | 2 | 3 | 5 |
| Navigation | 2 | 4 | 6 |
| Window/Context | 0 | 13 | 13 |
| Element Retrieval | 1 | 7 | 8 |
| Element State | 0 | 10 | 10 |
| Element Interaction | 1 | 2 | 3 |
| Document | 1 | 2 | 3 |
| Cookies | 0 | 5 | 5 |
| Actions | 0 | 2 | 2 |
| Alerts | 0 | 4 | 4 |
| Screenshots | 1 | 1 | 2 |
| Print | 0 | 1 | 1 |
| **TOTAL** | **8** | **54** | **62** |

**Implementation Coverage: ~13%**

---

## Recommended Implementation Phases

### Phase 1 - Core Navigation & Element State (Essential for basic automation)
- `GET /session/{id}/url` - Get Current URL
- `POST /session/{id}/back` - Back
- `POST /session/{id}/forward` - Forward  
- `POST /session/{id}/refresh` - Refresh
- `GET /session/{id}/element/{id}/text` - Get Element Text
- `GET /session/{id}/element/{id}/attribute/{name}` - Get Element Attribute
- `GET /session/{id}/element/{id}/enabled` - Is Element Enabled
- `POST /session/{id}/element/{id}/clear` - Element Clear
- `POST /session/{id}/element/{id}/value` - Element Send Keys

### Phase 2 - Window Management
- `GET /session/{id}/window` - Get Window Handle
- `GET /session/{id}/window/rect` - Get Window Rect
- `POST /session/{id}/window/rect` - Set Window Rect
- `POST /session/{id}/window/maximize` - Maximize Window
- `POST /session/{id}/window/minimize` - Minimize Window
- `POST /session/{id}/window/fullscreen` - Fullscreen Window

### Phase 3 - Advanced Elements
- `POST /session/{id}/elements` - Find Elements (multiple)
- `POST /session/{id}/element/{id}/element` - Find Element From Element
- `POST /session/{id}/element/{id}/elements` - Find Elements From Element
- `GET /session/{id}/element/{id}/rect` - Get Element Rect
- `GET /session/{id}/element/{id}/css/{property}` - Get Element CSS Value
- `GET /session/{id}/element/{id}/name` - Get Element Tag Name

### Phase 4 - Cookies & Alerts
- All cookie management endpoints (5 endpoints)
- All alert handling endpoints (4 endpoints)

### Phase 5 - Actions API (Complex input simulation)
- `POST /session/{id}/actions` - Perform Actions
- `DELETE /session/{id}/actions` - Release Actions

---

## Reference

- W3C WebDriver Specification: https://www.w3.org/TR/webdriver2/
- Selenium WebDriver: https://selenium.dev/
- WebDriver endpoints table: https://www.w3.org/TR/webdriver2/#endpoints
