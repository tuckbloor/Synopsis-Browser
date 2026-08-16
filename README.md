Synopsis Browser for Developers

Synopsis Browser for Developers is a Windows developer browser built with C# / .NET 8 / WPF and powered by Microsoft WebView2.

It is designed for web developers who want the browser, diagnostics, local project source, server logs, and optional local AI code review in one place.

Synopsis can:

browse normal websites and local development sites

capture JavaScript console output

capture uncaught JavaScript errors and promise rejections

inspect HTTP/network requests

detect HTTP 4xx/5xx failures

inspect TLS/security information

group related failures into incidents

link a website/localhost port to a local source-code folder

read relevant source files and development logs

resolve source maps where available

send error/source context to a local Ollama model for code review

suggest likely causes and possible fixes

keep AI/project data local to the current Synopsis version

clear the complete diagnostic session when required

resize or detach the developer workspace

Synopsis does not automatically modify your project source code. AI review is read-only.

Screenshots

Synopsis home and AI settings



Network monitor



Linked local project



Live network inspector



AI code review



Requirements

Synopsis currently targets Windows x64.

You need:

Windows 10 or Windows 11

.NET 8 SDK

Microsoft Edge WebView2 Runtime

Git, if cloning from GitHub

Ollama only if you want local AI code review

Check .NET:

dotnet --version

You should see an 8.x SDK or newer compatible SDK.

WebView2 is already installed on most current Windows systems because it is used by Microsoft applications and Windows components.

Install from source

1. Clone the repository

Open PowerShell:

cd E:\projects
git clone https://github.com/tuckbloor/Synopsis-Browser.git
cd Synopsis-Browser

Or download the repository ZIP from GitHub and extract it anywhere you want.

For example:

E:\projects\Synopsis-Browser

2. Start Synopsis

The simplest method is to double-click:

OPEN-SYNOPSIS.cmd

Or run it from PowerShell:

.\OPEN-SYNOPSIS.cmd

The launcher starts the WPF application with:

dotnet run --project .\src\SynopsisBrowser.App\SynopsisBrowser.App.csproj

dotnet run automatically restores required NuGet packages and builds the application when necessary.

The first launch can therefore take longer than later launches.

First run

A new version of Synopsis starts with a clean application profile.

A fresh version begins with:

AI provider          Disabled
Saved AI model       None
API key              Empty
Linked projects      None
Diagnostics          Empty
AI reviews           Empty

Settings created while using the same version are remembered between launches.

Application data is stored under:

%LOCALAPPDATA%\SynopsisBrowser\

Synopsis does not include saved project links, API keys, or personal configuration in the GitHub source repository.

Basic browsing

At the top of Synopsis is the normal browser toolbar.

You can enter:

https://example.com

or a local development address:

http://localhost:8080
http://localhost:8081
http://localhost:8082

You can also enter ordinary search text.

Press:

Enter

or click:

GO

Synopsis supports:

Back

Forward

Reload

Home

multiple browser tabs

normal web browsing

localhost development sites

When you manually enter a different URL/site in the address bar, Synopsis clears the previous transient diagnostic session before loading the new site.

Normal links clicked inside the current website do not clear the session.

Developer workspace

Click:

DEVELOPER TOOLS

to show or hide the Synopsis developer workspace.

The workspace can be:

resized vertically by dragging the resize bar

shrunk

expanded

hidden

detached into its own window

docked back into the browser

The same developer session is preserved when the workspace is detached.

CLEAR ALL

Use:

CLEAR ALL

to clear the current debugging session.

It clears transient data such as:

Console entries

Network requests

Error Centre entries

AI incidents and reviews

API/response inspection data

performance/session diagnostics

current TLS/security snapshot

current selections

It does not delete:

linked project folders

files in linked projects

Ollama installation

saved Synopsis configuration

Error Centre

The ERROR CENTRE collects important failures detected while browsing.

Examples include:

uncaught JavaScript exceptions

console.error

failed network requests

HTTP 4xx responses

HTTP 5xx responses

TLS/certificate problems

linked development-server errors

Related signals can be correlated into the same incident for AI review.

Console

The CONSOLE tab captures browser JavaScript console output.

Supported messages include:

console.log(...)
console.info(...)
console.warn(...)
console.error(...)
console.debug(...)

Synopsis also captures:

uncaught JavaScript exceptions

unhandled Promise rejections

Console capture uses Chromium/WebView2 diagnostic APIs plus a page bridge for additional reliability.

Network

The NETWORK tab records browser requests.

You can inspect information such as:

HTTP method

status code

resource type

URL

request duration

transferred bytes

request headers

payload

response information

Failed HTTP responses are also fed into the diagnostic system.

Where possible, bounded error response bodies are included as evidence for AI code review.

API / Response

The API / RESPONSE tab is intended for examining API traffic and responses from the current application.

This is especially useful for:

JSON APIs

Laravel validation responses

REST endpoints

failed requests

development error responses

Security + TLS

The SECURITY + TLS tab provides developer-focused security information.

Synopsis can inspect areas such as:

HTTP vs HTTPS

TLS connection status

certificate information

certificate problems

common security headers

mixed-content/security findings

You can test certificate handling with intentionally broken TLS test sites such as BadSSL.

Performance

The PERFORMANCE tab provides runtime and page diagnostic information useful while developing and testing applications.

More extensive performance tooling can be added in later versions.

Responsive

Use the RESPONSIVE tab for development-oriented viewport/device testing.

Linking a website to a local project

One of Synopsis' main features is the ability to connect a running website to the source code on your PC.

For example:

http://localhost:8082

could be linked to:

E:\projects\Football-Manager

Open:

PROJECT

Then link the current site to the correct project directory.

Synopsis stores the relationship between the website and the local folder.

The Project tab shows information such as:

current site

linked directory

detected framework where possible

detected development log where possible

Unlinking a project

Use:

UNLINK

to remove the relationship between Synopsis and that folder.

UNLINK does not delete the project directory.

It only deletes Synopsis' saved mapping.

Your source files remain untouched.

AI code review with Ollama

Ollama support is optional.

Synopsis remains usable as a developer browser without AI.

If Ollama is available, Synopsis can send an error plus relevant local project evidence to a local model for code review.

The intended workflow is:

Application error
      ↓
Synopsis captures diagnostic signals
      ↓
Signals are correlated into an incident
      ↓
Synopsis checks the linked project
      ↓
Relevant source/log evidence is collected
      ↓
Ollama reviews the incident
      ↓
Likely root cause
Possible fix
Suggested code
Investigation steps

Synopsis does not automatically apply the suggested code.

Install Ollama

Install Ollama for Windows from the official Ollama website.

After installation, confirm it is running:

ollama list

Pull a coding model, for example:

ollama pull qwen2.5-coder:7b

or another model appropriate for your hardware.

Then open:

SETTINGS

and configure the AI provider as:

Ollama Local

Default local endpoint:

http://localhost:11434

Refresh the model list and select your installed model.

Local Ollama does not require an API key.

Ollama on systems with GPU problems

If Ollama's GPU backend is unstable, it can be run using the CPU backend.

Example:

$env:OLLAMA_LLM_LIBRARY="cpu_avx2"
$env:OLLAMA_DEBUG="1"
ollama serve

Keep that PowerShell window open while testing Synopsis.

CPU inference is slower than GPU inference, so smaller models can be useful for quick reviews.

AI Code Review

Open:

AI CODE REVIEW

Synopsis displays detected incidents.

The review workspace is organised around:

INCIDENT
   ↓
SOURCE
   ↓
OLLAMA REVIEW

When a linked project is available, Synopsis attempts to find the real source that caused the failure.

Evidence can include:

JavaScript error/stack trace

HTTP failure

response body

server log

local source file

source line

nearby source code

related diagnostic signals

Source maps

When a JavaScript build reports an error in a generated asset such as:

/build/assets/app-D4K8F.js

Synopsis can inspect Source Map v3 information where available and attempt to resolve the generated location back to original source such as:

resources/js/pages/Dashboard.vue

If no usable source map exists, Synopsis falls back to other linked-project resolution strategies.

Quick Review and Deep Review

AI Code Review can provide different review depths.

Quick Review

Designed for a smaller/faster prompt and faster local inference.

Deep Review

Allows more source/evidence context and a longer response.

A smaller Ollama model can be used for fast investigation while a larger coding model can be used when deeper analysis is required.

Privacy

Synopsis is designed for local development.

Project linking reads only the local evidence needed for diagnostics and code review.

When using Ollama Local, AI requests are sent to your locally running Ollama server.

Sensitive values should never be deliberately hard-coded into source code.

Synopsis attempts to bound and redact diagnostic evidence before AI analysis, but developers should still avoid placing secrets in application logs.

Build Synopsis as a Windows EXE

To create a self-contained Windows x64 release, run from the repository root:

dotnet publish ".\src\SynopsisBrowser.App\SynopsisBrowser.App.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ".\dist\win-x64"

The executable will be generated under:

dist\win-x64\

The primary executable is:

SynopsisBrowser.exe

Run it with:

.\dist\win-x64\SynopsisBrowser.exe

A self-contained build includes the .NET runtime, which is why the resulting application can be considerably larger than the source code.

Keep any required runtimes files/folders beside the executable when distributing a published build.

Debug build

Developers can simply use:

.\OPEN-SYNOPSIS.cmd

or:

dotnet run --project .\src\SynopsisBrowser.App\SynopsisBrowser.App.csproj

Repository structure

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
│
├── src/
│   ├── SynopsisBrowser.App/
│   ├── SynopsisBrowser.Core/
│   ├── SynopsisBrowser.Diagnostics/
│   ├── SynopsisBrowser.AI/
│   └── SynopsisBrowser.Projects/
│
└── scripts/

Recommended Git workflow

After cloning:

git status

Create changes normally:

git add .
git commit -m "Describe the change"
git push

For releases:

git tag -a v1.6.2 -m "Synopsis Browser for Developers v1.6.2"
git push origin v1.6.2

Troubleshooting

dotnet is not recognised

Install the .NET 8 SDK and reopen PowerShell.

Check:

dotnet --version

WebView2 fails to start

Install or repair the Microsoft Edge WebView2 Runtime.

Ollama says unavailable

Check:

ollama list

Then make sure the Ollama service is running and Synopsis is configured for:

http://localhost:11434

AI review has no source code

Make sure the current website is linked to the correct local project under:

PROJECT

Some browser errors do not contain enough source information to identify an exact file. Synopsis will use logs, stack traces, source maps and bounded source searching where possible.

Old errors are still visible

Click:

CLEAR ALL

Entering a completely different URL in the address bar also starts a clean diagnostic session.

Current status

Synopsis Browser for Developers is an actively developed developer-tooling project.

The current architecture intentionally separates:

browser UI

core application logic

diagnostics

local project awareness

AI integration

The browser-rendering engine itself is provided by Microsoft WebView2 / Chromium.

Synopsis-specific UI, diagnostics, project linking, incident correlation and AI code-review functionality are implemented by this project.

License

See the repository LICENSE file for the current licence terms.

Synopsis Browser for Developers

Browse. Inspect. Fix.