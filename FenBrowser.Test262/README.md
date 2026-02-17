# FenBrowser.Test262

This project uses `Test262Harness` + generated NUnit test classes to run the ECMAScript conformance suite against FenRuntime.

## Regenerate the Suite

From repo root:

```powershell
.\FenBrowser.Test262\generate_test262.ps1
```

Optional:

```powershell
.\FenBrowser.Test262\generate_test262.ps1 -Test262Directory C:\path\to\test262
```

Notes:

- The script resolves the local `test262` checkout and invokes `dotnet test262 generate`.
- Generated files are written to `FenBrowser.Test262/Generated`.
- Exclusions are defined in `FenBrowser.Test262/Test262Harness.settings.json`.

## Run Tests

```powershell
dotnet test FenBrowser.Test262/FenBrowser.Test262.csproj
```

Run a narrower slice during engine bring-up:

```powershell
dotnet test FenBrowser.Test262/FenBrowser.Test262.csproj --filter "FullyQualifiedName~Literals"
```
