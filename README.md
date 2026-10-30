# Xcaciv.Echos

Echos is a consent-first Windows desktop assistant. It combines active-window
context, optional screen OCR, and optional system-audio transcription to request
one concise suggestion from an OpenAI-compatible endpoint.

See [XCACIV-ECHOS-BLUEPRINT.md](XCACIV-ECHOS-BLUEPRINT.md) for the architecture,
privacy boundaries, and implementation notes.

## Development

- Windows 10 version 2004 (build 19041) or later, with a .NET 8 SDK and the
  Windows App SDK/WinUI development prerequisites, is required for the desktop app.
- The unpackaged app requires the Windows App Runtime 1.8. If the installed runtime is
  older than the SDK package requires, the app shows "This application could not be
  started"; build it with `-p:RuntimeIdentifier=win-x64 -p:SelfContained=false
  -p:WindowsAppSDKSelfContained=true` to bundle the runtime instead (the end-to-end
  tests build it that way).
- C# is pinned to version 10 in `Directory.Build.props`.
- The platform-independent Core, Context, and Llm projects can be built on Linux.
  Windows capture and WinUI must be exercised on a Windows desktop.

From the repository root:

```shell
dotnet restore src/Xcaciv.Echos.sln
dotnet build src/Xcaciv.Echos.sln
```

Build the desktop app on Windows for a supported architecture:

```shell
dotnet build src/Xcaciv.Echos.App -p:Platform=x64
```

Run the portable context/HTTP tests (also supported on Linux):

```shell
dotnet test tests/Xcaciv.Echos.Tests/Xcaciv.Echos.Tests.csproj
```

Run everything, including the Windows end-to-end suite, on an interactive Windows
desktop:

```shell
dotnet test src/Xcaciv.Echos.sln
```

`tests/Xcaciv.Echos.EndToEnd.Tests` exercises the real UI Automation, screen capture,
OCR and loopback-audio adapters, runs the suggestion pipeline against a stubbed LLM
transport, and launches the built app against a loopback HTTP listener to confirm that
nothing is sent before opt-in, that one bounded request follows opt-in, that revoking
consent stops further requests, and that the process exits while a request is pending.
Those tests take over the foreground window for about a minute, launch the app, OCR
the real screen and play a short tone through the default audio device; they skip
themselves when no OCR language or audio render device is installed. To run only the
portable and adapter suites:

```shell
dotnet test src/Xcaciv.Echos.sln --filter "FullyQualifiedName!~EndToEnd"
```

## Endpoint configuration

Set these environment variables before launching the desktop app:

| Variable | Meaning |
| --- | --- |
| `ECHOS_LLM_ENDPOINT` | Full OpenAI-compatible chat completion URL, including `/v1/chat/completions` |
| `ECHOS_LLM_MODEL` | Model identifier supported by that endpoint |
| `ECHOS_LLM_API_KEY` | Optional bearer token; keep it outside the repository |
| `ECHOS_TRANSCRIPTION_ENDPOINT` | Full transcription URL, including `/v1/audio/transcriptions` |
| `ECHOS_TRANSCRIPTION_MODEL` | Transcription model identifier |
| `ECHOS_TRANSCRIPTION_API_KEY` | Optional transcription bearer token |

HTTPS is required except for local loopback HTTP services. Transcription is
optional when audio is disabled. There is no default remote endpoint or API key.
The app displays the configured destinations before observation is enabled.
Do not put credentials in endpoint URLs or commit them to configuration files.

Observation starts disabled on every launch. Enabling it permits active-window
inspection and submission of that context to the configured LLM. Screen and
system-audio capture each require another opt-in. Disabling observation cancels
pending work; it cannot recall data already transmitted to a provider.

### Repository-local dotnet-claude-kit

The repository enables the
[dotnet-claude-kit marketplace plugin](https://github.com/codewithmukesh/dotnet-claude-kit)
in `.claude/settings.json`. Open this repository in Claude Code and approve the
marketplace/plugin installation when prompted. If your client does not install
enabled plugins automatically, run its documented commands:

```text
/plugin marketplace add codewithmukesh/dotnet-claude-kit
/plugin install dotnet-claude-kit@dotnet-claude-kit --scope project
```

Restore the pinned, **local** Roslyn MCP tool (no global installation required):

```shell
dotnet tool restore
```

`.mcp.json` runs that local tool against `src/Xcaciv.Echos.sln`; approve the
project MCP server in your client. The plugin's bundled server expects a global
tool, so use the project server when working with this local installation.
Claude Code is not required to build or run Echos. The kit's newer-language
recommendations do not override this repository's C# 10 requirement.
