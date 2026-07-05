# WPT Full Sweep - Runs ALL WPT categories through FenBrowser
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-wpt-full-sweep.ps1
param(
    [switch]$Fresh = $false
)

$repoRoot = $PSScriptRoot | Split-Path -Parent
Set-Location $repoRoot

# All WPT test categories (directories with test files)
$allCategories = @(
    "accelerometer","accessibility","accname","acid","ai","ambient-light",
    "animation-worklet","annotation-model","annotation-protocol","annotation-vocab",
    "apng","appmanifest","attribution-reporting","audio-output","audio-session",
    "autoplay-policy-detection","avif","background-fetch","background-sync","badging",
    "battery-status","beacon","bluetooth","browsing-topics",
    "captured-mouse-events","clear-site-data","client-hints","clipboard-apis",
    "close-watcher","compat","compression","compute-pressure",
    "connection-allowlist","console","contacts","container-timing","content-dpr",
    "content-index","content-security-policy","contenteditable","cookies","cookiestore",
    "core-aam","cors","cpu-performance","credential-management","css","cssom",
    "custom-elements","density-size-correction","deprecation-reporting",
    "device-bound-session-credentials","device-memory","device-posture",
    "digital-credentials","direct-sockets","document-picture-in-picture","document-policy",
    "dom","domparsing","domxpath","dpub-aam","dpub-aria","ecmascript","editing",
    "element-timing","encoding","encoding-detection","encrypted-media","entries-api",
    "event-timing","eventsource","eyedropper","fedcm","fenced-frame","fetch",
    "file-system-access","FileAPI","fledge","focus","font-access",
    "forced-colors-mode","fs","fullscreen","gamepad","generic-sensor","geolocation",
    "geolocation-sensor","gif","gpc","graphics-aam","graphics-aria","gyroscope",
    "hr-time","hsts","html","html-aam","html-longdesc","html-media-capture",
    "html-ruby-extensions","https-upgrades","idle-detection","imagebitmap-renderingcontext",
    "images","import-maps","IndexedDB","inert","infrastructure",
    "input-device-capabilities","input-events","installedapp",
    "intersection-observer","intervention-reporting","is-input-pending","jpegxl","js",
    "js-self-profiling","keyboard-lock","keyboard-map","largest-contentful-paint",
    "layout-instability","loading","logs","long-animation-frame","longtask-timing",
    "magnetometer","managed","mathml","measure-memory","media-capabilities",
    "media-playback-quality","media-source","mediacapture-extensions",
    "mediacapture-fromelement","mediacapture-handle","mediacapture-image",
    "mediacapture-insertable-streams","mediacapture-record","mediacapture-region",
    "mediacapture-streams","mediasession","merchant-validation","mimesniff",
    "mixed-content","mst-content-hint","nav-tracking-mitigations","navigation-api",
    "navigation-timing","netinfo","network-error-logging","notifications",
    "old-tests","orientation-event","orientation-sensor","page-lifecycle",
    "page-visibility","paint-timing","parakeet","payment-method-basic-card",
    "payment-method-id","payment-request","performance-timeline",
    "periodic-background-sync","permissions","permissions-policy","permissions-request",
    "permissions-revoke","picture-in-picture","png","pointerevents","pointerlock",
    "preload","presentation-api","print","private-aggregation",
    "private-click-measurement","proximity","push-api","quirks",
    "referrer-policy","remote-playback","reporting","requestidlecallback",
    "resize-observer","resource-timing","sanitizer-api","savedata","scheduler",
    "screen-capture","screen-details","screen-orientation","screen-wake-lock",
    "scroll-animations","scroll-performance-timing","scroll-to-text-fragment",
    "secure-contexts","secure-payment-confirmation","selection","serial",
    "server-timing","service-workers","shadow-dom","shape-detection",
    "shared-storage","shared-storage-selecturl-limit","signed-exchange",
    "soft-navigation-heuristics","speculation-rules","speech-api","storage",
    "storage-access-api","streams","subapps","subresource-integrity","svg",
    "svg-aam","timing-entrytypes-registry","top-level-storage-access-api",
    "touch-events","trust-tokens","trusted-types","ua-client-hints","uievents",
    "upgrade-insecure-requests","url","urlpattern","user-timing",
    "vibration","video-rvfc","viewport","viewport-segments",
    "virtual-keyboard","visual-viewport","wai-aria","wasm","web-animations",
    "web-bundle","web-extensions","web-install","web-locks","web-share",
    "web-based-payment-handler","webcodecs","WebCryptoAPI",
    "webhid","webidl","webmcp","webmessaging","webmidi","webnn","webnn",
    "webrtc","webrtc-encoded-transform","webrtc-extensions","webrtc-identity",
    "webrtc-ice","webrtc-priority","webrtc-stats","webrtc-svc",
    "websockets","webstorage","webtransport","webusb","webvtt","webxr",
    "window-management","worklets","workers","x-frame-options","xhr","xml"
)

$FreshFlag = if ($Fresh) { "-Fresh" } else { "" }

Write-Host "Starting full WPT sweep over $($allCategories.Count) categories..."
Write-Host ""

# Run in batches to avoid any single-category issues blocking the whole sweep
& "$PSScriptRoot\run-wpt-categories.ps1" -Categories $allCategories -TimeoutSeconds 180 -StallTimeoutSec 45 @FreshFlag

Write-Host ""
Write-Host "Full sweep complete! Results in Results/wpt_categories/_summary.json"
