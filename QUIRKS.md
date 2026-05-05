# FenBrowser Compatibility Quirks

Last updated: 2026-05-05

This file tracks temporary compatibility behaviors that are intentionally isolated and must have removal conditions.

## Active quirks

| Quirk | Location | Reason | Risk | Test / Repro | Owner | Removal condition |
|---|---|---|---|---|---|---|
| Google search challenge recovery navigation | `FenBrowser.FenEngine/Rendering/BrowserApi.cs` (`TryResolveGoogleSearchRecoveryNavigation`) | Some Google search challenge responses return a fallback page that must be followed to reach expected content | Site-specific logic can mask generic navigation defects | Repro with Google challenge page (`id=yvlrue` / `emsg=SG_REL`) | FenEngine | Replace with generic, standards-driven recovery path and prove no regression in reduced navigation tests |
| Google challenge DOM sanitation | `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs` (`ForceGoogleChallengeBannerVisible`) | Mitigates hidden challenge banner states that break visible content flow | Site-specific mutation can diverge from generic parser/render pipeline | Repro on challenge responses where banner visibility is script-controlled | FenEngine | Remove once parser + script/runtime behavior reproduces expected visibility without host-targeted mutations |
| X/Twitter bootstrap diagnostics hook | `FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs` (`LogXBootstrapState`) | Extra diagnostics for high-volume bootstrap failures on X/Twitter | Host-targeted diagnostics may grow into behavior coupling if not constrained | Repro by loading X/Twitter and inspecting bootstrap diagnostics | FenEngine | Keep diagnostics-only or remove after generic bootstrap telemetry provides equivalent signal |

## Rules

1. New quirks must include a concrete removal condition.
2. Quirks must not silently expand into general engine behavior.
3. Every quirk should map to a reduced test or deterministic repro artifact.
