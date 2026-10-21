# Xcaciv.Echos

Echos is a consent-first Windows desktop assistant. It combines active-window
context, optional screen OCR, and optional system-audio transcription to request
one concise suggestion from an OpenAI-compatible endpoint.

See [XCACIV-ECHOS-BLUEPRINT.md](XCACIV-ECHOS-BLUEPRINT.md) for the architecture,
privacy boundaries, and implementation notes.

## Development

- Windows 10 version 2004 (build 19041) or later, with a .NET 8 SDK and the
  Windows App SDK/WinUI development prerequisites, is required for the desktop app.
- The unpackaged app requires the Windows App Runtime 1.8.
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
