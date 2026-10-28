# Xcaciv.Echos

Windows sidekick that, only with explicit opt-in, observes the foreground UI (plus optional screen OCR and system loopback audio), sends a bounded prompt to an OpenAI-compatible LLM, and displays one suggestion. It never executes suggestions. See `XCACIV-ECHOS-BLUEPRINT.md` for the full design.

## Stack

- C# 10 (`LangVersion` 10.0), nullable + implicit usings, **net8.0** (`net8.0-windows10.0.19041.0` for Capture and App)
- WinUI 3 (Windows App SDK), NAudio.Wasapi, System.Drawing.Common, Microsoft.Extensions.DependencyInjection/Http
- xUnit 2.x, coverlet; Roslyn MCP via `.mcp.json` (`cwm-roslyn-navigator`)
- No database, auth, cache or messaging. Configuration comes from `ECHOS_*` environment variables.
- Do not use C# 11+ or .NET 9/10 features (raw string literals, `required`, file-scoped types, collection expressions, `field`) until `Directory.Build.props` is bumped.

### Tech-stack decisions

| Dimension | Choice | Notes |
| --- | --- | --- |
| Database | None | Nothing is persisted, by design. Do not add one. |
| Auth | None | Optional bearer token for the configured endpoint only, read from `ECHOS_*_API_KEY`. No user identity. |
| Caching | None | Context lives in memory for one cycle and is cleared. |
| Messaging | None | Single process. `CaptureConsent.Changed` is the only event bus. |
| Observability | `ILogger` (Microsoft.Extensions.Logging) | Log exception types and status only. No Serilog, no OpenTelemetry. |
| Resilience | None | Cancellation-driven, no retries. A retry or hedging policy re-sends captured context and needs a privacy review first. |
| HTTP | `IHttpClientFactory` named clients `llm` and `transcription` | `AllowAutoRedirect = false`, infinite client timeout, per-call cancellation. |
| Testing | xUnit 2.5 + coverlet | Hand-written test doubles. No Testcontainers, no mocking library. |

Kit guidance that assumes .NET 10, C# 14, xUnit v3, Testcontainers, Minimal APIs or EF Core does not apply here. The C# 10 pin and the rules in this file win over any skill default.

## Architecture: Clean Architecture (ports and adapters), single deployable

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
- Solution: `src/Xcaciv.Echos.sln`. Tests: `tests/Xcaciv.Echos.Tests` (net8.0, platform-neutral), `tests/Xcaciv.Echos.Capture.Tests` (Windows TFM) and `tests/Xcaciv.Echos.EndToEnd.Tests` (Windows TFM; real adapters and the launched app, needs an interactive desktop). Only the .NET 10 runtime may be installed, so run tests with `DOTNET_ROLL_FORWARD=Major`.

### Dependency rules (for `/arch-check`)

- Core references no project. Context, Llm and Capture reference Core only and never each other. App references everything.
- `Xcaciv.Echos.Tests` references Core, Context and Llm. `Xcaciv.Echos.Capture.Tests` references Core and Capture. `Xcaciv.Echos.EndToEnd.Tests` references Core, Context, Capture and Llm. None references App; the end-to-end project builds the app with an MSBuild target and launches the exe instead.
- Ports (`IScreenCapture`, `IOcrEngine`, `IUiInspector`, `IAudioCapture`, `ITranscriptionEngine`, `ILlmClient`, `IContextEngine`) live in Core. Adapters are registered as singletons in `Bootstrapper.Build` with `ValidateOnBuild` and `ValidateScopes`.
- Layout is flat: one type per file at each project root, no feature or layer subfolders. Test files are `{Type}Tests.cs` beside the shared doubles in the test project root.

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

### Detected code style (consistent across all source files; match it)

- File-scoped namespaces everywhere. The namespace is the project name (`Xcaciv.Echos.Core`, `Xcaciv.Echos.Capture`, and so on).
- Adapters, engines, options and event args are `public sealed class`. Core data carriers are positional `public record`. App-internal helpers and test doubles are `internal sealed class`. `Bootstrapper` and `PromptBuilder` are `static class`. No `InternalsVisibleTo`.
- Private fields are `_camelCase`. `var` by default. Async methods end in `Async` (all 22 do). `ConfigureAwait(false)` on awaits in Context, Llm, Capture and the non-UI paths of App; `MainWindow` continuations stay on the dispatcher.
- Options (`LlmOptions`, `WhisperTranscriptionOptions`, `ScreenCaptureOptions`, `UiInspectionOptions`) are plain sealed classes, two with a `Validate()` method, populated from `ECHOS_*` environment variables in `AppConfiguration`. No `IOptions<T>`, no `appsettings.json`.
- Timestamps use `DateTimeOffset.UtcNow` directly. If a test needs to control time, inject `TimeProvider` (built into .NET 8) rather than a custom clock.
- XML doc comments on Core public types and on any member with a locking or threading contract (see `CaptureConsent`).
- Logging uses structured templates (`"Audio start failed ({ExceptionType})"`) with the exception type name as the only argument.
- No analyzer settings (`TreatWarningsAsErrors`, `AnalysisLevel`) and no `.editorconfig`; `dotnet format` runs with defaults. The build is warning-free; keep it so.

### Deliberate deviations from the kit's anti-pattern list

`detect_antipatterns`, `/de-sloppify` and `/code-review` will flag these. They are intentional; do not "fix" them.

- Tests construct `new HttpClient(new StubHttpMessageHandler(...))` directly. No `IHttpClientFactory` in tests.
- Revocation-race tests block with bounded `.Wait(TimeSpan)` on `ManualResetEventSlim` and `SemaphoreSlim` to sequence threads deterministically. Keep every wait bounded.
- xUnit 2.x test methods take no `CancellationToken`.
- App-layer handlers catch `Exception` to keep the window alive and log only the exception type. `catch (OperationCanceledException) { }` at the UI boundary is intentional.
- Capture adapters catch provider errors (COM, UI Automation, WASAPI) with `when` filters and report failure as an empty result or a failure event, never with captured content in the message. The audio callback filters exclude `OutOfMemoryException` and `AccessViolationException`.
- `WasapiLoopbackAudioCapture` waits a bounded five seconds for the capture thread while stopping.

The rest of the kit's list still applies in production code: no `async void` (WinUI event handlers excepted; there are none today), no `.Result` or `.Wait()` on tasks in async paths, no `new HttpClient()`, no string interpolation in log templates, no `DateTime.Now`.

## Testing

- Pure logic (context, prompt, HTTP, consent) is tested without a desktop. Tests that need Windows use `[WindowsFact]`.
- End-to-end tests (`tests/Xcaciv.Echos.EndToEnd.Tests`) run the real Windows adapters, the real `ContextEngine` pipeline against a stubbed LLM transport, and the built app process against a loopback `HttpListener`. They use `[DesktopFact]` (interactive Windows session), `[OcrFact]` (an installed OCR language) and `[AudioFact]` (a default render endpoint), run serially (parallelization is disabled for the assembly), own the foreground window while they run, and keep every captured string in memory only: assert on real desktop text with `Assert.True(condition, "fixed message")` so nothing captured reaches the test output.
- The end-to-end project restores and builds the app itself (`Platform=x64`, `RuntimeIdentifier=win-x64`, `SelfContained=false`, `WindowsAppSDKSelfContained=true`) because the unpackaged app only starts when the Windows App Runtime it finds matches the SDK; pass `-p:BuildEchosApp=false` to skip that build (the tests then fail fast because `EchosApp.path` is removed).
- A full `dotnet test src/Xcaciv.Echos.sln` therefore takes over the foreground for about a minute, launches the app, OCRs the real screen and plays a short tone through the default audio device. Skip the desktop suite with `dotnet test src/Xcaciv.Echos.sln --filter "FullyQualifiedName!~EndToEnd"`.
- Behavior tests over implementation tests. For consent changes, add a revocation-race test in `ContextEngineTests`.
- Stub HTTP with `StubHttpMessageHandler`. No real network in tests.
- Test classes are `public sealed class {Type}Tests`. Names describe behavior, either as a sentence (`RevokingConsentDuringInspectionPreventsLaterNetworkCalls`) or as `Scenario_Expectation` (`TooManyChunks_Throws`). Use `[Theory]` + `[InlineData]` for value tables.
- Test doubles are hand-written `internal sealed` classes in `tests/Xcaciv.Echos.Tests` (`ContextSources`, `StubHttpMessageHandler`, `TrackingHttpContent`, `CancellationBlockingStream`, `NonSeekableMemoryStream`). `Capture.Tests` and `EndToEnd.Tests` link `StubHttpMessageHandler` with `<Compile Include>`; `EndToEnd.Tests` keeps its own desktop helpers (`ForegroundTestWindow`, `LoopbackLlmServer`, `EchosAppProcess`, `AppAutomation`, `PromptSections`, `LlmStub`, the `Rejecting*` engines). No Moq or NSubstitute.
- Coverage comes from the coverlet collector. No Testcontainers, no `WebApplicationFactory`, no snapshot testing.

## Commands

Run from the repository root. Set `DOTNET_ROLL_FORWARD=Major` first (`$env:DOTNET_ROLL_FORWARD='Major'` in PowerShell).

```
dotnet tool restore                                               # once, restores the local Roslyn MCP tool
dotnet build src/Xcaciv.Echos.sln
dotnet build src/Xcaciv.Echos.App -p:Platform=x64                 # desktop app, Windows only
dotnet test  src/Xcaciv.Echos.sln
dotnet test  tests/Xcaciv.Echos.Tests/Xcaciv.Echos.Tests.csproj   # portable tests, also run on Linux
dotnet test  tests/Xcaciv.Echos.EndToEnd.Tests/Xcaciv.Echos.EndToEnd.Tests.csproj   # interactive Windows desktop only; launches the app
dotnet test  src/Xcaciv.Echos.sln --collect:"XPlat Code Coverage"
dotnet format src/Xcaciv.Echos.sln --verify-no-changes
```

Baseline on 2026-10-28: build with 0 warnings, 129 tests passing (47 portable, 53 Capture, 29 end-to-end on an interactive Windows desktop), no project cycles, `dotnet format --verify-no-changes` clean.

## Workflow

- Plan first (`/plan`) for anything that touches consent, capture, prompt building, HTTP or shutdown, or that needs three or more steps.
- Verify before done: build, test and format check from the Commands section, then `get_diagnostics`. Report failures verbatim.
- Privacy review for every change in Context, Llm, Capture or App: re-read "Privacy and consent rules", confirm no new path bypasses `ContextEngine.StartAuthorized`, and confirm no captured content can reach a log, file, exception message or on-disk test fixture.
- Fix bugs autonomously. If an approach goes sideways, stop and re-plan instead of pushing through.
- Record corrections and non-obvious discoveries with `instinct-system` so they persist across sessions.

## Skills and tools

Use the Roslyn MCP tools before reading files: `find_symbol`, `find_references`, `get_diagnostics`, `detect_antipatterns`. If the server is down (check `/mcp`), fall back to Grep and Read. Useful skills: `/code-review`, `/security-scan`, `/health-check`, `/de-sloppify`, `/verify`.

### MCP

The server is the repository-local `cwm-roslyn-navigator` tool pinned in `dotnet-tools.json` and launched by `.mcp.json`, not the global install the kit templates describe. Run `dotnet tool restore` once.

- Before modifying a type: `find_symbol`, then `get_public_api` or `get_symbol_source`.
- Before adding or removing a reference: `find_references` and `find_callers`.
- To check dependency direction: `get_project_graph` and `detect_circular_dependencies`.
- To find the adapters for a port: `find_implementations`.
- After changes: `get_diagnostics` at solution scope.

### Kit skills that fit this project

`/plan`, `/code-review`, `/security-scan`, `/health-check`, `/arch-check` (declared architecture: Clean Architecture), `/de-sloppify`, `/verify`, `/build-fix`, `/outdated`, `httpclient-factory`, `dependency-injection`, `testing` (xUnit 2.x; ignore its v3 and Testcontainers defaults), `instinct-system`, `workflow-mastery`.

### Kit skills that do not apply

`modern-csharp` (C# 14 guidance conflicts with the C# 10 pin), `ef-core`, `minimal-api`, `openapi`, `scalar`, `api-versioning`, `authentication`, `caching`, `messaging`, `aspire`, `docker`, `container-publish`, `serilog`, `opentelemetry`, `resilience`, and the .NET upgrade path of `migrate`. There is no CI pipeline yet; if one is added with `ci-cd`, it needs a Windows runner and `DOTNET_ROLL_FORWARD=Major`.
