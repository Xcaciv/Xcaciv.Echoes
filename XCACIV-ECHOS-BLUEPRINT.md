# Xcaciv.Echos — Architecture blueprint (C# 10)

Echos is an always-available Windows sidekick, not an always-recording service.
Observation must be enabled explicitly; screen and audio have independent
opt-ins. There is no automatic execution of suggested actions.

## Solution

The solution is `src/Xcaciv.Echos.sln`, with one type per source file:

| Project | Responsibility |
| --- | --- |
| `Xcaciv.Echos.Core` | Capture/LLM/context interfaces, records, consent, configuration |
| `Xcaciv.Echos.Capture` | Windows screen, OCR, audio, UI Automation and transcription adapters |
| `Xcaciv.Echos.Context` | Bounded context aggregation and prompt shaping |
| `Xcaciv.Echos.Llm` | Configurable OpenAI-compatible HTTP completions |
| `Xcaciv.Echos.App` | WinUI 3 floating window, consent controls, service composition |

Core has no dependency on the implementation projects. Context, Capture, and Llm
depend on Core; App composes them. `IOcrEngine` and `ITranscriptionEngine` belong
to Core so Context does not depend on Windows implementations. `Bootstrapper`
belongs to App: putting it in Core would introduce circular project references.

## Suggestion flow

1. The user explicitly enables observation and selects optional sources.
2. The context engine inspects the active window.
3. If screen consent is enabled, a captured bitmap is passed to OCR and disposed.
4. If audio consent is enabled, bounded recent audio is passed to transcription.
5. A timestamped snapshot is converted to a prompt: at most 20 UI texts, 20 OCR
   regions, and the last 10 transcript segments.
6. The configured LLM returns one suggestion; empty responses produce none.
7. The floating window displays the result, without executing it.

Calls propagate cancellation. The UI must not overlap periodic requests, and
closing the window must cancel outstanding work and release capture resources.
Captured text is untrusted context, not instructions for the assistant.

## Privacy boundaries

- All observation consent is off initially. Merely launching Echos must not
  inspect windows, record audio, capture screens, or send context to a server.
- Observation includes active application, window title, and visible UI text.
  Screen consent adds OCR; audio consent adds **system loopback audio**, not a
  microphone. Users should ensure that other participants consent when needed.
- Context is processed in memory, not deliberately written to files or logs.
  The configured completion/transcription service can retain submitted data
  according to its own policy. Select endpoints you trust.
- Revocation cancels pending work and clears retained context. Data already sent
  to an endpoint cannot be recalled.
- Echos has no global input hooks; adding one requires consent gating and disclosure.

## Platform validation

Pure context and HTTP behavior can be tested without a Windows desktop. Desktop
Duplication/Graphics Capture, OCR language availability, WASAPI devices, UI
Automation access, global hotkeys, and WinUI rendering require Windows testing.
Protected desktops, elevated windows, DRM-protected content, and inaccessible
automation trees may not expose capturable content. Report source failures
without persisting captured data.

`tests/Xcaciv.Echos.EndToEnd.Tests` automates part of the desktop checks on an
interactive Windows session: the real UI Automation, screen capture, OCR and
loopback-audio adapters against a test window; the `ContextEngine` pipeline with real
adapters and a stubbed LLM transport; and the launched app process against a loopback
HTTP endpoint. Of the smoke test below it covers steps 1 and 3 fully, and in part steps
2 (request counts, not device state), 4 (screen OCR only; audio inside the app is not
exercised), 5 (observation and screen revocation during a pending request; audio stop
is not checked), 6 (hiding keeps observation running) and 7 (the process exits with a
request pending; audio and tray release are not checked). Dragging, always-on-top,
the tray icon, the hotkey toggle and every audio path inside the app stay manual.

The initial `DxgiScreenCapture` adapter retains the blueprint's public name but
uses GDI `CopyFromScreen` as a compatibility backend, **not** DXGI Desktop
Duplication. It captures the primary display by default; virtual-desktop capture
is configurable. Replacing that backend with DXGI or Windows Graphics Capture is
a future enhancement. OCR uses Windows OCR and requires an installed OCR language.
Whisper transcription uses a configured HTTP endpoint; no local Whisper model
is bundled.

### Windows smoke test

The end-to-end tests cover steps 1 and 3 and parts of the rest (see above); run the
whole list by hand before a release.

1. Launch with no endpoint configured: the window should explain configuration,
   and observation must remain off.
2. Configure a trusted test endpoint and launch again. Before opting in, verify
   that no requests reach it and no audio capture is active.
3. Enable observation only. After a refresh, verify one suggestion arrives with
   active-window context, but no OCR or audio payload.
4. Enable screen capture, then audio with a configured transcription endpoint.
   Check the enabled sources independently; missing OCR languages or audio
   devices should produce a status message rather than terminate the app.
5. Disable a source during a pending request. No result from the previous
   consent state should appear. Disable observation and verify recording stops.
6. Exercise dragging, always-on-top behavior, Ctrl+Shift+E, and the tray icon.
   Hiding is not the same as disabling observation.
7. Exit while a request is pending. Verify the process exits, audio
   devices are released, and the tray icon disappears.
