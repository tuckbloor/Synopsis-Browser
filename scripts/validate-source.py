from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
errors = []

for xaml in root.rglob('*.xaml'):
    try:
        ET.parse(xaml)
    except Exception as exc:
        errors.append(f'{xaml.relative_to(root)}: invalid XML: {exc}')

for csproj in root.rglob('*.csproj'):
    try:
        tree = ET.parse(csproj)
    except Exception as exc:
        errors.append(f'{csproj.relative_to(root)}: invalid project XML: {exc}')
        continue
    for ref in tree.findall('.//ProjectReference'):
        target = (csproj.parent / ref.attrib['Include'].replace('\\', '/')).resolve()
        if not target.exists():
            errors.append(f'{csproj.relative_to(root)}: missing ProjectReference {target}')

main_xaml = root / 'src/SynopsisBrowser.App/MainWindow.xaml'
main_cs = root / 'src/SynopsisBrowser.App/MainWindow.xaml.cs'
xaml_text = main_xaml.read_text(encoding='utf-8')
cs_text = main_cs.read_text(encoding='utf-8')
handlers = set(re.findall(r'\b(?:Click|KeyDown|SelectionChanged)="([A-Za-z_][A-Za-z0-9_]*)"', xaml_text))
for handler in sorted(handlers):
    if not re.search(rf'\b{re.escape(handler)}\s*\(', cs_text):
        errors.append(f'MainWindow.xaml: handler {handler} not found in code-behind')

# Basic lexical balance for C# source. This is not a compiler, but catches common generation mistakes.
def lexical_check(path: Path):
    s = path.read_text(encoding='utf-8')
    stack = []
    state = 'code'
    i = 0
    line = 1
    pairs = {'{': '}', '[': ']', '(': ')'}
    while i < len(s):
        c = s[i]
        n = s[i+1] if i + 1 < len(s) else ''
        if c == '\n': line += 1
        if state == 'code':
            if c == '/' and n == '/': state = 'line'; i += 2; continue
            if c == '/' and n == '*': state = 'block'; i += 2; continue
            if s.startswith('"""', i): state = 'raw'; i += 3; continue
            if c == '@' and n == '"': state = 'verbatim'; i += 2; continue
            if c == '"': state = 'string'; i += 1; continue
            if c == "'": state = 'char'; i += 1; continue
            if c in pairs: stack.append((c, line))
            elif c in '}])':
                if not stack: return f'extra {c} at line {line}'
                opening, opening_line = stack.pop()
                if pairs[opening] != c: return f'mismatched {opening} at {opening_line} and {c} at {line}'
        elif state == 'line':
            if c == '\n': state = 'code'
        elif state == 'block':
            if c == '*' and n == '/': state = 'code'; i += 2; continue
        elif state == 'string':
            if c == '\\': i += 2; continue
            if c == '"': state = 'code'
        elif state == 'verbatim':
            if c == '"' and n == '"': i += 2; continue
            if c == '"': state = 'code'
        elif state == 'char':
            if c == '\\': i += 2; continue
            if c == "'": state = 'code'
        elif state == 'raw':
            if s.startswith('"""', i): state = 'code'; i += 3; continue
        i += 1
    if state not in ('code', 'line'): return f'unclosed lexical state {state}'
    if stack: return f'unclosed {stack[-1][0]} from line {stack[-1][1]}'
    return None

for cs in root.rglob('*.cs'):
    issue = lexical_check(cs)
    if issue:
        errors.append(f'{cs.relative_to(root)}: {issue}')

# Cross-check references to locally declared enum members. This catches generation
# mistakes such as DiagnosticKind.Browser when Browser was not added to the enum.
all_cs = list(root.rglob('*.cs'))
all_text = {p: p.read_text(encoding='utf-8') for p in all_cs}
for declaring_path, source in all_text.items():
    for match in re.finditer(r'\b(?:public|internal|private|protected)?\s*enum\s+(\w+)\s*\{([^}]*)\}', source, re.S):
        enum_name = match.group(1)
        members = set()
        for raw_member in match.group(2).split(','):
            member = raw_member.strip()
            if not member:
                continue
            member = re.split(r'\s*=\s*', member, maxsplit=1)[0].strip()
            if re.fullmatch(r'[A-Za-z_]\w*', member):
                members.add(member)
        for use_path, use_source in all_text.items():
            for use in re.finditer(rf'\b{re.escape(enum_name)}\.(\w+)', use_source):
                member = use.group(1)
                if member not in members:
                    line = use_source.count('\n', 0, use.start()) + 1
                    errors.append(f'{use_path.relative_to(root)}:{line}: {enum_name}.{member} is not declared')



# Catch missing System.IO imports for the BCL file-system types used by this solution.
for cs, source in all_text.items():
    uses_io_symbol = re.search(r'(?<![A-Za-z0-9_.])(?:File|Directory|Path)\.', source)
    has_io_using = re.search(r'^using\s+System\.IO\s*;', source, re.M)
    uses_fully_qualified_io = re.search(r'\bSystem\.IO\.(?:File|Directory|Path)\.', source)
    if uses_io_symbol and not has_io_using and not uses_fully_qualified_io:
        errors.append(f'{cs.relative_to(root)}: uses File/Directory/Path without using System.IO;')

# V1.3 AI/settings invariants. These are deliberate product guarantees rather than
# general C# validation: masked key entry, DPAPI CurrentUser protection, and Bearer auth.
main_xaml_text = main_xaml.read_text(encoding='utf-8')
secret_store = (root / 'src/SynopsisBrowser.App/Services/DpapiSecretStore.cs').read_text(encoding='utf-8')
ollama_client = (root / 'src/SynopsisBrowser.AI/OllamaClient.cs').read_text(encoding='utf-8')
models_text = (root / 'src/SynopsisBrowser.Core/Models.cs').read_text(encoding='utf-8')
if 'x:Name="ApiKeyBox"' not in main_xaml_text or 'PasswordChar="*"' not in main_xaml_text:
    errors.append('AI settings: API key input must remain a masked PasswordBox')
if 'DataProtectionScope.CurrentUser' not in secret_store or 'ProtectedData.Protect' not in secret_store:
    errors.append('AI settings: DpapiSecretStore must protect secrets for CurrentUser')
if 'AuthenticationHeaderValue("Bearer"' not in ollama_client:
    errors.append('AI settings: remote Ollama API key must use Bearer authentication')
ai_match = re.search(r'public sealed class AiSettings\s*\{(.*?)\n\}', models_text, re.S)
if ai_match and re.search(r'ApiKey|Password|Secret', ai_match.group(1), re.I):
    errors.append('AI settings: plaintext credentials must not be persisted in AiSettings')

# V1.5 Developer Intelligence guarantees.
main_v15 = (root / 'src/SynopsisBrowser.App/MainWindow.xaml.cs').read_text(encoding='utf-8')
ollama_v15 = (root / 'src/SynopsisBrowser.AI/OllamaClient.cs').read_text(encoding='utf-8')
contracts_v15 = (root / 'src/SynopsisBrowser.Core/Contracts.cs').read_text(encoding='utf-8')
models_v15 = (root / 'src/SynopsisBrowser.Core/Models.cs').read_text(encoding='utf-8')
projects_v15 = (root / 'src/SynopsisBrowser.Projects/ProjectLinkService.cs').read_text(encoding='utf-8')
vm_v15 = (root / 'src/SynopsisBrowser.App/ViewModels/MainViewModel.cs').read_text(encoding='utf-8')
browser_tab_v15 = (root / 'src/SynopsisBrowser.App/Services/BrowserTabSession.cs').read_text(encoding='utf-8')
incident_v15 = (root / 'src/SynopsisBrowser.Diagnostics/IncidentCorrelator.cs').read_text(encoding='utf-8')
source_map_v15 = (root / 'src/SynopsisBrowser.Projects/SourceMapResolver.cs').read_text(encoding='utf-8')

if 'DeveloperIncident' not in models_v15 or 'IncidentCorrelator' not in incident_v15:
    errors.append('V1.5 incidents: correlated incident model/service is missing')
if 'AiIncidents' not in vm_v15 or 'ItemsSource="{Binding AiIncidents}"' not in main_xaml_text:
    errors.append('V1.5 incidents: incident inbox is missing')
if 'AiIncidents_SelectionChanged' not in main_xaml_text or 'async void AiIncidents_SelectionChanged' not in main_v15:
    errors.append('V1.5 incidents: selecting an incident must trigger the review path')
if 'QUICK REVIEW' not in main_xaml_text or 'DEEP REVIEW' not in main_xaml_text:
    errors.append('V1.5 review: quick/deep review controls are missing')
if 'BuildIncidentContext' not in contracts_v15 or 'BuildIncidentContext' not in projects_v15:
    errors.append('V1.5 review: correlated linked-project context service is missing')
if 'AnalyzeIncidentAsync' not in contracts_v15 or 'AnalyzeIncidentAsync' not in ollama_v15:
    errors.append('V1.5 review: Ollama incident review API is missing')
if 'INCIDENT → SOURCE → FIX' not in main_xaml_text or 'AiSourcePreview' not in vm_v15:
    errors.append('V1.5 workspace: side-by-side incident/source/fix workspace is missing')
if 'DecodeToOriginal' not in source_map_v15 or 'Source Map v3' not in source_map_v15:
    errors.append('V1.5 source maps: source-map decoder is missing')
if 'SourceMapResolver.TryResolve' not in projects_v15:
    errors.append('V1.5 source maps: linked-project resolver does not use source maps')
if 'Column' not in models_v15 or 'columnNumber' not in browser_tab_v15:
    errors.append('V1.5 source maps: generated column capture is missing')
if 'FindRelatedSourceEvidence' not in projects_v15 or 'RefreshLinkMetadata' not in projects_v15:
    errors.append('AI code review: related-source discovery / link metadata refresh is missing')
if 'ConnectionAborted' not in browser_tab_v15 or 'about:blank' not in browser_tab_v15:
    errors.append('Diagnostics: benign WebView2 about:blank abort filtering is missing')
if 'uri.Authority' not in main_v15 or 'FindProjectLink' not in main_v15:
    errors.append('Project linking: localhost port/authority-aware matching is missing')
if 'DiagnosticKind.Ai' not in main_v15 or not ('item.Kind != DiagnosticKind.Ai' in main_v15 or 'item.Kind == DiagnosticKind.Ai' in main_v15):
    errors.append('AI code review: AI-internal diagnostics are not excluded from review')
if 'think = false' not in ollama_v15 or 'keep_alive = "10m"' not in ollama_v15:
    errors.append('AI code review: predictable Ollama settings are missing')
if 'DO NOT return JSON' not in ollama_v15 or 'ParseCodeReview' not in ollama_v15:
    errors.append('AI code review: tolerant non-JSON review protocol is missing')
if 'AutoAnalyzeErrorsBox' not in main_xaml_text or 'AutoAnalyzeErrors' not in models_v15:
    errors.append('AI code review: optional background review setting is missing')

# V1.6.2 clean-session, clean-first-run, and simple source-launcher guarantees.
open_cmd = root / 'OPEN-SYNOPSIS.cmd'
if not open_cmd.exists() or 'dotnet run' not in open_cmd.read_text(encoding='utf-8', errors='ignore'):
    errors.append('V1.6.2 launcher: OPEN-SYNOPSIS.cmd is missing or does not use dotnet run')
if 'ClearAll_Click' not in main_xaml_text or 'Content="CLEAR ALL"' not in main_xaml_text:
    errors.append('V1.5.1 clear: global CLEAR ALL control is missing')
if 'ClearAllDeveloperData("NEW URL - DIAGNOSTICS CLEARED")' not in main_v15 or 'IsDifferentAddress' not in main_v15:
    errors.append('V1.5.1 clear: entering a different omnibox URL must clear the prior diagnostic session first')
if 'SelectedNetwork = null' not in main_v15 or 'ApiResponseBody = string.Empty' not in main_v15:
    errors.append('V1.5.1 clear: Network/API selection state is not fully reset')
if 'ResetDiagnosticSession' not in browser_tab_v15 or '_diagnosticGeneration++' not in browser_tab_v15:
    errors.append('V1.5.1 clear: WebView diagnostic request state / late-event generation guard is missing')
if 'Path.Combine(AppDataRoot, "Profiles", AppVersion)' not in main_v15:
    errors.append('V1.6.2 fresh profile: settings/projects must use a version-specific profile directory')
if 'Path.Combine(AppDataRoot, "WebView2")' not in main_v15:
    errors.append('V1.6.2 fresh profile: WebView2 runtime data should remain separate from versioned settings')
if 'public string Provider { get; set; } = "Disabled";' not in models_v15:
    errors.append('V1.6.2 fresh profile: AI must start disabled with no selected personal configuration')
if 'PreferredOllamaModel' in main_v15 or 'PreferredOllamaModel' in models_v15:
    errors.append('V1.6.2 fresh profile: legacy AI preference migration is still present')

# V1.4.3 Console capture reliability invariants.
if 'AddScriptToExecuteOnDocumentCreatedAsync' not in browser_tab_v15 or 'WebMessageReceived' not in browser_tab_v15:
    errors.append('Console: WebView2 page-message fallback bridge is missing')
if "window.addEventListener('error'" not in browser_tab_v15 or "window.addEventListener('unhandledrejection'" not in browser_tab_v15:
    errors.append('Console: uncaught exception / promise rejection bridge is missing')
if 'PublishJavaScriptException' not in browser_tab_v15 or 'new ConsoleEntry' not in browser_tab_v15:
    errors.append('Console: JavaScript exceptions must be published to the Console stream')

if errors:
    print('SOURCE VALIDATION FAILED')
    for error in errors: print(' -', error)
    sys.exit(1)

print('SOURCE VALIDATION PASSED')
print(f'XAML files: {len(list(root.rglob("*.xaml")))}')
print(f'C# files:   {len(list(root.rglob("*.cs")))}')
print(f'Projects:   {len(list(root.rglob("*.csproj")))}')
print(f'Handlers:   {len(handlers)}')
