# Version 2 Roadmap

V2 should extend the diagnostic platform rather than replace V1.

## Error intelligence

- Incident correlation engine that groups HTTP 500 + server exception + JavaScript rejection into one root-cause incident
- Source-map resolution for bundled JS/TS
- Duplicate-error collapsing and regression tracking
- “new since last reload” mode
- per-project diagnostic history

## Framework adapters

Introduce `IFrameworkAdapter` implementations for:

- Laravel
- Symfony
- ASP.NET Core
- Express / Node
- Next.js
- Vue / Vite
- React / Vite

Adapters can locate logs, parse framework exception formats, discover common development ports and understand project metadata.

## Docker awareness

- detect `docker-compose.yml` / `compose.yml`
- list project containers
- live logs per container
- restart selected development service
- map browser HTTP failures to container errors
- no destructive action without explicit user confirmation

## AI patch workflow

Keep AI read-only by default, then add:

1. Diagnose
2. Generate proposed patch
3. Show unified diff
4. Run configured test command in a sandbox/process
5. Developer explicitly approves
6. Apply patch
7. Re-run affected page/tests

Never silently write source code.

## Network/API

- response-body viewer through CDP `Network.getResponseBody`
- JSON tree and schema inference
- replay/edit request
- GraphQL inspector
- WebSocket inspector
- Server-Sent Events inspector
- HAR import/export
- request grouping by page/initiator

## Security

- cookie Secure/HttpOnly/SameSite audit
- Permissions-Policy checks
- cross-origin isolation headers
- mixed-content detection via CDP security events
- certificate chain viewer
- expiry notifications for linked staging/production hosts
- explicit development-certificate trust workflow

## Performance

- Core Web Vitals
- request waterfall visualization
- CPU/network throttling presets
- long-task detection
- memory snapshots
- bundle/resource budgets per project
- compare current reload against baseline

## Browser productivity

- persistent history UI
- bookmark manager
- download manager
- named development workspaces
- restore tab groups
- per-project browser profiles
- device/responsive emulator presets
- command palette

## Source/editor integration

- VS Code URI integration
- JetBrains/PHPStorm integration
- source file + exact line opening
- Git status for linked project
- blame/commit context for an error line
- optional GDR provider through a separate integration contract

## Storage

Migrate persistence behind `ISettingsStore` to SQLite with versioned migrations. Keep browser profile/cookie storage owned by WebView2.

## UI architecture

If V2 UI complexity grows substantially, split the WPF shell into feature controls/view models without changing Core/Diagnostics/AI/Projects. A future WinUI 3 shell remains possible because Core has no WPF dependency.
