# Synopsis Browser for Developers

**Synopsis Browser for Developers** is a Windows developer browser built with **C# / .NET 8 / WPF** and powered by **Microsoft WebView2 / Chromium**.

It is designed for developers who want normal browsing, browser diagnostics, local project awareness, server logs, and optional **local Ollama-powered code review** in one application.

> **Browse. Inspect. Fix.**

---

## What Synopsis does

Synopsis combines a browser with a developer workspace that can inspect:

- JavaScript console output
- uncaught JavaScript exceptions
- unhandled Promise rejections
- network requests
- HTTP 4xx / 5xx responses
- API responses
- TLS and certificate information
- performance information
- local project links
- development logs
- source files
- source maps
- correlated incidents
- optional Ollama AI code reviews

Synopsis does **not** automatically change your project source code. AI suggestions are read-only.

---

# Screenshots and feature tour

## 1. Home page and AI settings

![Synopsis home page and AI settings](docs/images/synopsis-home-ai-settings.png)

This is the main **Synopsis Browser for Developers** interface.

The upper half of the application is the normal browser area. It contains:

- browser tabs
- Back
- Forward
- Reload
- Home
- URL/search field
- GO
- SAVE
- LAB
- Chromium DevTools shortcut
- Settings
- Synopsis developer tools

The lower half is the **Synopsis Developer Workspace**.

In this screenshot the **Settings** tab is open. This is where the local AI provider can be configured.

Synopsis supports running without AI, but when **Ollama Local** is selected it can connect to a locally running Ollama server.

The default local endpoint is:

```text
http://localhost:11434
```

Installed models can then be selected and used for error/code review.

The status strip above the developer workspace also shows information such as:

```text
HTTP / HTTPS
error count
warning count
Ollama version
Ollama connection state
```

The **CLEAR ALL** button resets the current diagnostic session without deleting saved project links or files.

---

## 2. Browsing a real development application

![Synopsis running a local Football Manager application](docs/images/synopsis-live-browser-network.png)

Synopsis is not limited to test pages.

This screenshot shows a real local web application running at:

```text
http://localhost:8082/
```

inside Synopsis.

The application continues running normally while the developer workspace remains available underneath it.

In this example the **Network** tab is open and is capturing the application's live API traffic.

Repeated requests to:

```text
/sim/v1/career/live/tick
```

are visible while the match continues to run above.

This makes it possible to develop and debug an application without continually switching between the browser, terminal and separate debugging windows.

The Network table records details such as:

- method
- HTTP status
- resource type
- URL
- request duration
- response size

---

## 3. Link the running website to its local source code

![Synopsis project linking](docs/images/synopsis-project-linking.png)

The **Project** tab connects a website being viewed in Synopsis to the source code on the developer's computer.

In this example:

```text
http://localhost:8082/
```

is linked to:

```text
E:\projects\Football-Manager
```

Once a project is linked, Synopsis can use that directory as context when diagnosing errors.

It can inspect:

- project structure
- framework indicators
- development logs
- source files
- stack-trace filenames
- source maps
- nearby source code

This is what allows Synopsis to move beyond a normal browser error such as:

```text
HTTP 500
```

and potentially find the actual local file associated with that failure.

The link can also be removed from Synopsis without deleting or changing the real project directory.

---

## 4. Network request inspector

![Synopsis network request inspector](docs/images/synopsis-network-inspector.png)

The Network tab is designed for API and application debugging.

The left side contains captured requests while the right side contains the **Request Inspector**.

A selected request can expose information such as:

- request headers
- request payload
- status
- response information
- timing
- request size

This is especially useful for:

- REST APIs
- Laravel applications
- Vue applications
- authentication requests
- failed AJAX/fetch requests
- validation errors
- background polling
- live match/game APIs

HTTP failures can also be forwarded into the Error Centre and AI Code Review system.

---

## 5. AI Code Review

![Synopsis AI code review](docs/images/synopsis-ai-code-review.png)

The **AI Code Review** tab is the developer-intelligence part of Synopsis.

Instead of sending a single isolated error message to AI, Synopsis can correlate related signals into an **incident**.

An incident can include evidence such as:

```text
JavaScript error
HTTP failure
network failure
server log
response body
stack trace
linked source file
nearby source code
```

The workflow is:

```text
ERROR
  ↓
INCIDENT
  ↓
LINKED PROJECT
  ↓
SOURCE / LOG EVIDENCE
  ↓
OLLAMA
  ↓
LIKELY ROOT CAUSE
POSSIBLE FIX
SUGGESTED CODE
INVESTIGATION STEPS
```

The screen is organised around:

```text
INCIDENT → SOURCE → FIX
```

Synopsis can perform a **Quick Review** or a more detailed **Deep Review** depending on how much context and model time you want to use.

Local Ollama keeps the AI request on the developer's own machine when using the local provider.

---

# Requirements

Synopsis currently targets **Windows x64**.

You need:

- Windows 10 or Windows 11
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime
- Git if cloning the repository
- Ollama only if you want AI code review

Check .NET with:

```powershell
dotnet --version
```

You should see an 8.x SDK or a compatible newer SDK.

---

# Installation from GitHub source

Clone the repository:

```powershell
cd E:\projects

git clone https://github.com/tuckbloor/Synopsis-Browser.git

cd Synopsis-Browser
```

Alternatively, use GitHub's **Code → Download ZIP** option and extract the repository.

---

# Start Synopsis

The simplest way to run Synopsis from source is:

```text
OPEN-SYNOPSIS.cmd
```

Double-click it in Windows Explorer.

Or run:

```powershell
.\OPEN-SYNOPSIS.cmd
```

The launcher starts:

```powershell
dotnet run --project .\src\SynopsisBrowser.App\SynopsisBrowser.App.csproj
```

`dotnet run` automatically restores NuGet dependencies and builds the project when required.

The first launch may therefore take longer.

---

# First-run behaviour

Every clean release profile starts without personal project or AI configuration.

A fresh profile begins with:

```text
AI provider       Disabled/default
API key           Empty
Project links     Empty
Diagnostics       Empty
AI reviews        Empty
```

Settings configured while using that version can be remembered for future launches of the same local profile.

Synopsis does not ship GitHub releases containing a developer's personal project mappings or AI secrets.

---

# Normal browsing

Enter either a full URL:

```text
https://example.com
```

a development URL:

```text
http://localhost:8082
```

or normal search text into the address bar.

Press:

```text
Enter
```

or click:

```text
GO
```

Synopsis supports:

- multiple tabs
- Back
- Forward
- Reload
- Home
- normal web browsing
- localhost development sites

---

# Developer workspace

Click:

```text
DEVELOPER TOOLS
```

to show or hide the lower developer workspace.

It contains:

```text
ERROR CENTRE
CONSOLE
NETWORK
API / RESPONSE
PERFORMANCE
RESPONSIVE
SECURITY + TLS
AI CODE REVIEW
SETTINGS
PROJECT
```

The workspace can also be:

- resized vertically
- shrunk
- expanded
- detached into a separate window
- docked back into the browser

---

# Clear the current session

Click:

```text
CLEAR ALL
```

to remove current diagnostic data.

It clears:

- Console entries
- Network entries
- Error Centre entries
- incidents
- AI reviews
- API/response selections
- performance/session information
- current TLS/security snapshot

It does **not** delete:

- linked source folders
- files
- Ollama
- saved application configuration

Entering a different site manually in the URL bar can also begin a clean diagnostic session.

---

# Console

The Console captures:

```javascript
console.log(...)
console.info(...)
console.warn(...)
console.error(...)
console.debug(...)
```

It also captures:

- uncaught JavaScript exceptions
- unhandled Promise rejections

Errors can feed into the Error Centre and AI incident system.

---

# Error Centre

The Error Centre collects significant failures detected by Synopsis.

Examples include:

- JavaScript exceptions
- console errors
- network failures
- HTTP 4xx
- HTTP 5xx
- certificate/TLS problems
- linked development-log errors

---

# Network

The Network tab captures requests made by the current page.

Information can include:

```text
Method
Status
Type
URL
Duration
Bytes
Headers
Payload
Response
```

Failed requests can become diagnostic signals.

Where available, bounded error response bodies can also be supplied to AI review.

---

# Project linking

Open:

```text
PROJECT
```

while viewing the development website.

Link the current site to the project folder on disk.

Example:

```text
Website:
http://localhost:8082

Source:
E:\projects\Football-Manager
```

Synopsis can then use that source directory to locate files related to browser/server errors.

---

# Source resolution

When diagnosing an incident, Synopsis can use:

- JavaScript stack traces
- filenames
- source maps
- server logs
- HTTP routes
- response bodies
- linked project structure

to try to identify the most relevant source file.

For generated assets such as:

```text
/build/assets/app-D4K8F.js
```

Synopsis can inspect Source Map v3 files where available and attempt to map the generated position back to original Vue/JavaScript/TypeScript source.

---

# Ollama

Ollama is optional.

Synopsis works as a normal developer browser without it.

To use local AI:

1. Install Ollama for Windows.
2. Start Ollama.
3. Install a model.
4. Open Synopsis Settings.
5. Select `Ollama Local`.
6. Refresh models.
7. Select the model you want.

Check installed models:

```powershell
ollama list
```

Example coding model:

```powershell
ollama pull qwen2.5-coder:7b
```

Default local endpoint:

```text
http://localhost:11434
```

Local Ollama does not require an API key.

---

# CPU-only Ollama

If your GPU backend is unstable, Ollama can be started in CPU mode.

Example:

```powershell
$env:OLLAMA_LLM_LIBRARY="cpu_avx2"
$env:OLLAMA_DEBUG="1"
ollama serve
```

Keep that PowerShell window running while using Synopsis AI features.

Smaller models generally give faster CPU responses.

---

# AI Code Review

Once a site is linked to source, Synopsis can combine browser diagnostics with local code context.

Typical evidence:

```text
HTTP 500
+
JavaScript failure
+
Laravel log entry
+
Controller filename
+
nearby PHP source
```

Ollama can then return:

```text
Likely root cause
Confidence
Explanation
Possible fix
Suggested code
Investigation steps
```

Synopsis does not silently apply that suggested code.

---

# Build the Windows executable

From the repository root:

```powershell
dotnet publish ".\src\SynopsisBrowser.App\SynopsisBrowser.App.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ".\dist\win-x64"
```

The Windows executable will be generated under:

```text
dist\win-x64\
```

with the main application:

```text
SynopsisBrowser.exe
```

Run:

```powershell
.\dist\win-x64\SynopsisBrowser.exe
```

---

# Repository structure

```text
Synopsis-Browser/
│
├── OPEN-SYNOPSIS.cmd
├── SynopsisBrowser.sln
├── README.md
├── LICENSE
├── VERSION.txt
│
├── docs/
│   └── images/
│       ├── synopsis-home-ai-settings.png
│       ├── synopsis-live-browser-network.png
│       ├── synopsis-project-linking.png
│       ├── synopsis-network-inspector.png
│       └── synopsis-ai-code-review.png
│
└── src/
    ├── SynopsisBrowser.App/
    ├── SynopsisBrowser.Core/
    ├── SynopsisBrowser.Diagnostics/
    ├── SynopsisBrowser.AI/
    └── SynopsisBrowser.Projects/
```

---

# Git workflow

After making changes:

```powershell
git add .
git commit -m "Describe the change"
git push
```

Create a version tag:

```powershell
git tag -a v1.6.2 -m "Synopsis Browser for Developers v1.6.2"
git push origin v1.6.2
```

---

# Troubleshooting

## `dotnet` is not recognised

Install the .NET 8 SDK.

Then reopen PowerShell and run:

```powershell
dotnet --version
```

---

## Synopsis does not open

Run it from PowerShell:

```powershell
.\OPEN-SYNOPSIS.cmd
```

and inspect the build/runtime error displayed in the terminal.

---

## WebView2 error

Install or repair the **Microsoft Edge WebView2 Runtime**.

---

## Ollama unavailable

Check:

```powershell
ollama list
```

and confirm Ollama is listening on:

```text
http://localhost:11434
```

---

## AI review cannot find source

Open:

```text
PROJECT
```

and verify the current website is linked to the correct local source directory.

Not every browser error contains enough information to identify an exact source file, but Synopsis can also use logs, source maps, routes and related incident evidence.

---

# Technology

Synopsis Browser for Developers uses:

- C#
- .NET 8
- WPF
- Microsoft WebView2
- Chromium DevTools Protocol
- HTTP/TLS diagnostics
- local filesystem/project inspection
- optional Ollama local AI

WebView2/Chromium provides the web rendering engine.

The Synopsis project provides the custom browser shell, developer workspace, diagnostics, project linking, incident correlation and AI code-review features.

---

# License

See:

```text
LICENSE
```

for the repository licence.

---

# Synopsis Browser for Developers

**Browse. Inspect. Fix.**
