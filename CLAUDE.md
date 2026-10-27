# Xcaciv.Echos

Windows sidekick that, only with explicit opt-in, observes the foreground UI (plus optional screen OCR and system loopback audio), sends a bounded prompt to an OpenAI-compatible LLM, and displays one suggestion. It never executes suggestions. See `XCACIV-ECHOS-BLUEPRINT.md` for the full design.

## Stack

- C# 10 (`LangVersion` 10.0), nullable + implicit usings, **net8.0** (`net8.0-windows10.0.19041.0` for Capture and App)
- WinUI 3 (Windows App SDK), NAudio.Wasapi, System.Drawing.Common, Microsoft.Extensions.DependencyInjection/Http
- xUnit 2.x, coverlet; Roslyn MCP via `.mcp.json` (`cwm-roslyn-navigator`)
- No database, auth, cache or messaging. Configuration comes from `ECHOS_*` environment variables.
- Do not use C# 11+ or .NET 9/10 features (raw string literals, `required`, file-scoped types, collection expressions, `field`) until `Directory.Build.props` is bumped.

## Architecture: ports and adapters (Clean-style), single deployable

```
Core      -> nothing (interfaces, records, CaptureConsent)
Context   -> Core            (ContextEngine, PromptBuilder)
Llm       -> Core            (HttpLlmClient, LlmOptions)
Capture   -> Core            (Windows adapters: DXGI, OCR, WASAPI, UIA, Whisper)
App       -> all of the above (composition root: Bootstrapper, WinUI window)
```

- Core and Context must stay platform-neutral. New capture or OCR contracts go in Core, implementations in Capture.
- `Bootstrapper` lives in App. Putting it in Core would create a circular reference.
- One type per file. Central package versions are in `src/Directory.Packages.props`. Add versions there, never in a csproj.
- Solution: `src/Xcaciv.Echos.sln`. Tests: `tests/Xcaciv.Echos.Tests` (net8.0, platform-neutral) and `tests/Xcaciv.Echos.Capture.Tests` (Windows TFM). Only the .NET 10 runtime may be installed, so run tests with `DOTNET_ROLL_FORWARD=Major`.

## Privacy and consent rules (non-negotiable)

- All consent is off at launch. Starting the app must not inspect, capture, record or send anything.
- Every collection or network step must start through `ContextEngine.StartAuthorized`, which checks `CaptureConsent` at the moment of the call. Do not add a capture path that bypasses it.
- Revocation cancels in-flight work (epoch token) and clears buffered audio. Preserve that behavior in any change.
- Captured text is untrusted. It is never treated as instructions, and `PromptBuilder` states this and bounds every field.
- No persisting or logging of captured content (window text, OCR, audio, transcripts, prompts, API keys). Log types and status only.
- Echos has no global input hooks; adding one requires consent gating and disclosure.
- Endpoints must be HTTPS (or loopback HTTP), with no userinfo or fragment. HTTP clients disable auto-redirect, since the API key and context are in the request.
- Any new data sent off-machine requires updating `Disclosure` and the README.

## Conventions

- Propagate `CancellationToken` through every async call. Cap sizes (bytes, characters, item counts) on anything crossing a boundary.
- Seal classes not designed for inheritance. Prefer records for data carriers in Core.
- Dispose bitmaps and clear audio buffers after use. Borrowed dependencies are owned by the DI container, not by `ContextEngine`.
- UI updates go through the dispatcher. Do not block the UI thread. Authorize through `CaptureConsent.TryStart`, which holds its private lock only for the check, and never run slow or UI-affine work inside it.

## Testing

- Pure logic (context, prompt, HTTP, consent) is tested without a desktop. Tests that need Windows use `[WindowsFact]`.
- Behavior tests over implementation tests. For consent changes, add a revocation-race test in `ContextEngineTests`.
- Stub HTTP with `StubHttpMessageHandler`. No real network in tests.

## Commands

```
dotnet build src/Xcaciv.Echos.sln
dotnet test  src/Xcaciv.Echos.sln
dotnet format src/Xcaciv.Echos.sln --verify-no-changes
```

## Skills and tools

Use the Roslyn MCP tools before reading files: `find_symbol`, `find_references`, `get_diagnostics`, `detect_antipatterns`. If the server is down (check `/mcp`), fall back to Grep and Read. Useful skills: `/code-review`, `/security-scan`, `/health-check`, `/de-sloppify`, `/verify`.
