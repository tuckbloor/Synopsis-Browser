# Architecture

## Design rule

The WebView is a rendering engine, not the application architecture.

V1 keeps browser hosting, diagnostics, AI and local-project knowledge separated so each can be replaced or extended independently.

## Dependency direction

```text
SynopsisBrowser.Core
     ↑      ↑      ↑
     │      │      │
Diagnostics AI   Projects
     ↑      ↑      ↑
     └──────┼──────┘
            │
       SynopsisBrowser.App
```

`SynopsisBrowser.Core` has no WPF or WebView2 dependency. That is important for V2 testing and for any future WinUI/Avalonia shell experiment.

## Important V1 seams

### `IDiagnosticHub`

All browser, HTTP, JavaScript, TLS, security and project-log events are normalised into `DiagnosticItem` records. V1.5 adds `IncidentCorrelator` above this stream so related signals can be grouped without changing any capture source.

### `IOllamaClient`

The UI depends on an interface rather than the Ollama implementation. V2 can add multiple local AI providers or a user-approved remote provider without changing the Error Centre.

### `ITlsInspector`

Certificate/security inspection is independent from WebView2 event capture. This avoids coupling the security UI to a specific browser control API.

### `IProjectLinkService`

The browser only knows that a host can map to a source project. V1.5 keeps source resolution behind this boundary, including Source Map v3 decoding and bounded fallback search. Framework-specific adapters, Docker discovery and Git integration can continue to evolve without changing the browser shell.

### `WebViewRuntimeService`

All tabs share one WebView2 environment/user-data directory. V2 profiles can turn this into one environment per profile without redesigning tab code.

## Data

V1 uses small JSON files under `%LOCALAPPDATA%\SynopsisBrowser` for ordinary settings/project mappings. The model is kept behind `ISettingsStore`, making a V2 migration to SQLite straightforward. Secrets are deliberately separate: `ISecretStore` protects AI credentials with Windows DPAPI scoped to the current Windows user.

## Safety

- TLS failures are reported, never automatically trusted.
- Ollama is optional.
- Secret redaction occurs before AI requests.
- AI output is recommendation-only in V1.
- Linked source files are not modified.


## V1.3 AI configuration seam

`AiSettings` stores non-secret provider configuration. `OllamaConnectionOptions` is passed into `OllamaClient`; the client never reads WPF controls or settings files. Remote API keys come from `ISecretStore` at the application boundary. This gives V2 a clean place to add other provider clients, per-project AI profiles, or credential-manager backends.


## V1.5 developer-intelligence seam

`DeveloperIncident` is a Core model, while `IncidentCorrelator` lives in Diagnostics. The App only receives incident snapshots and presents them. This keeps future correlation scoring/rules testable without coupling them to WPF.

`SourceMapResolver` lives inside the Projects layer and is deliberately read-only. `ProjectDiagnosticContext` returns separate source excerpts, log evidence and a human-readable resolution description as well as the combined AI evidence. The AI client therefore does not need filesystem access.
