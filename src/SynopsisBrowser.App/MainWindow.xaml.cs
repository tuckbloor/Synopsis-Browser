using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SynopsisBrowser.AI;
using SynopsisBrowser.App.Services;
using SynopsisBrowser.App.ViewModels;
using SynopsisBrowser.Core;
using SynopsisBrowser.Diagnostics;
using SynopsisBrowser.Projects;
using Microsoft.Win32;

namespace SynopsisBrowser.App;

public partial class MainWindow : Window
{
    private const string StartPageHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Synopsis</title>
<style>
:root{color-scheme:dark;font-family:Segoe UI,system-ui,sans-serif;background:#080d13;color:#f4f7fa}
*{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(circle at 50% -10%,#18344a 0,#0b121a 38%,#080d13 70%);display:grid;place-items:center}
main{width:min(980px,90vw);padding:56px 0 72px}.mark{display:flex;align-items:center;gap:12px;color:#62d6ff;font-weight:800;letter-spacing:.16em;font-size:13px}.logo{width:34px;height:34px;border-radius:9px;background:#62d6ff;color:#071019;display:grid;place-items:center;font-size:20px;letter-spacing:0}
h1{font-size:54px;line-height:1.05;margin:26px 0 12px;letter-spacing:-.035em}.lead{font-size:20px;line-height:1.55;color:#a8bac9;max-width:760px;margin:0 0 34px}.omnibox{border:1px solid #3a5065;background:#0b121a;border-radius:14px;padding:18px 20px;font-size:17px;color:#dbe8f2;box-shadow:0 16px 60px #0008}.omnibox b{color:#62d6ff}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;margin-top:22px}.card{border:1px solid #26394a;background:#101923;border-radius:14px;padding:18px;min-height:128px}.eyebrow{font-size:11px;font-weight:800;letter-spacing:.12em;color:#62d6ff}.card h2{font-size:17px;margin:10px 0 6px}.card p{color:#91a5b6;line-height:1.45;margin:0;font-size:14px}.keys{margin-top:26px;color:#7f94a6;font:13px Consolas,monospace}.keys span{border:1px solid #34495b;background:#111d28;border-radius:6px;padding:4px 7px;color:#dce8f1}@media(max-width:760px){.grid{grid-template-columns:1fr}h1{font-size:42px}}
</style>
</head>
<body>
<main>
  <div class="mark"><div class="logo">F</div> SYNOPSIS / DEVELOPER BROWSER</div>
  <h1>Browse. Inspect. Fix.</h1>
  <p class="lead">A browser built for web development. Use the address bar above for any URL, localhost port, or normal web search.</p>
  <div class="omnibox"><b>Try it:</b> click the address bar, type <code>example.com</code>, <code>localhost:8080</code>, or a search such as <code>Laravel validation</code>, then press Enter or click GO.</div>
  <div class="grid">
    <div class="card"><div class="eyebrow">SECURITY</div><h2>HTTPS and TLS at a glance</h2><p>Certificate state, protocol, headers and security findings live in the status strip and Security panel.</p></div>
    <div class="card"><div class="eyebrow">DIAGNOSTICS</div><h2>Errors in one place</h2><p>JavaScript, HTTP failures, network faults and linked server logs feed the Synopsis Error Centre.</p></div>
    <div class="card"><div class="eyebrow">LOCAL AI</div><h2>Ollama when available</h2><p>Synopsis remains a normal browser without AI. If Ollama is running, diagnostics can be analysed locally.</p></div>
  </div>
  <div class="keys"><span>Ctrl+L</span> address &nbsp; <span>Ctrl+T</span> new tab &nbsp; <span>F12</span> Chromium DevTools &nbsp; <span>DEV</span> Synopsis tools</div>
</main>
</body>
</html>
""";
    private readonly MainViewModel _vm = new();
    private readonly DiagnosticHub _hub = new();
    private readonly SecretRedactor _redactor = new();
    private readonly TlsInspector _tlsInspector = new();
    private readonly UrlResolver _urlResolver = new();
    private const string OllamaApiKeySecret = "ollama-api-key";
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly IProjectLinkService _projects;
    private IOllamaClient _ollama;
    private readonly BrowserSettings _settings;
    private readonly WebViewRuntimeService _webViewRuntime;
    private readonly List<BrowserTabSession> _tabs = [];
    private BrowserTabSession? _activeTab;
    private bool _showApiKey;
    private bool _loadingSettingsUi;
    private readonly Queue<DiagnosticItem> _autoAnalysisQueue = new();
    private readonly HashSet<Guid> _autoAnalysisKnown = [];
    private readonly Dictionary<Guid, AiRecommendation> _aiRecommendations = [];
    private readonly Dictionary<Guid, string> _aiRecommendationStatuses = [];
    private readonly HashSet<Guid> _manualReviewInProgress = [];
    private readonly IncidentCorrelator _incidentCorrelator = new();
    private readonly Dictionary<Guid, AiRecommendation> _incidentReviews = [];
    private readonly Dictionary<Guid, string> _incidentReviewStatuses = [];
    private readonly HashSet<Guid> _incidentReviewInProgress = [];
    private bool _autoAnalysisWorkerRunning;
    private Window? _detachedDevToolsWindow;
    private bool _mainWindowClosing;

    private string AppDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SynopsisBrowser");
    private string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.6.2";
    private string AppDataDirectory => Path.Combine(AppDataRoot, "Profiles", AppVersion);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Each released build gets its own clean profile. Nothing is imported from
        // older Synopsis versions automatically. This guarantees a
        // genuine first-run experience for public builds while still remembering
        // settings between launches of the same version.
        _settingsStore = new JsonSettingsStore(AppDataDirectory);
        _secretStore = new DpapiSecretStore(AppDataDirectory);
        _settings = _settingsStore.Load();
        _settings.Ai ??= new AiSettings();

        // Keep the Chromium/WebView2 cache shared between versions; it contains browser
        // runtime data, not Synopsis AI settings or linked-project metadata.
        _webViewRuntime = new WebViewRuntimeService(Path.Combine(AppDataRoot, "WebView2"));
        _projects = new ProjectLinkService(AppDataDirectory);
        _ollama = CreateOllamaClientFromSavedSettings();
        LoadSettingsUi();

        _hub.DiagnosticAdded += Hub_DiagnosticAdded;
        _hub.ConsoleAdded += Hub_ConsoleAdded;
        _hub.NetworkChanged += Hub_NetworkChanged;
        _projects.LogDiagnostic += (_, item) => _hub.Publish(item);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.DevToolsVisible = _settings.DevToolsOpenByDefault;
        DevToolsRow.Height = new GridLength(_vm.DevToolsVisible ? Math.Max(240, _settings.DevToolsSize) : 0);
        await OpenStartTabAsync();
        await RefreshOllamaAsync();

        if (_vm.DevToolsVisible && _settings.DevToolsDock.Equals("Detached", StringComparison.OrdinalIgnoreCase))
            await Dispatcher.BeginInvoke(new Action(DetachDevTools));
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _mainWindowClosing = true;
        _settings.DevToolsOpenByDefault = _vm.DevToolsVisible;
        _settings.DevToolsDock = _detachedDevToolsWindow is null ? "Bottom" : "Detached";
        if (_detachedDevToolsWindow is null && _vm.DevToolsVisible && DevToolsRow.ActualHeight > 100)
            _settings.DevToolsSize = DevToolsRow.ActualHeight;
        _settings.Ai.PreferredModel = _vm.SelectedModel;
        _settingsStore.Save(_settings);
        foreach (var tab in _tabs) tab.Dispose();
        _projects.Dispose();
    }

    private async Task AddTabAsync(Uri uri)
    {
        var environment = await _webViewRuntime.GetEnvironmentAsync();
        var tab = new BrowserTabSession(_hub, _tlsInspector, environment);
        tab.StateChanged += Tab_StateChanged;
        tab.SecurityChanged += Tab_SecurityChanged;
        _tabs.Add(tab);

        // WebView2CompositionControl is a visual-hosted control. Attach it to the
        // loaded WPF tree before creating its CoreWebView2 controller so the
        // composition surface has a real size/window from its first frame.
        SwitchTab(tab);
        _vm.PageStatus = "STARTING WEBVIEW...";

        try
        {
            await tab.InitializeAsync(uri);
            _vm.PageStatus = "READY";
            UpdateAddressBox(tab.Url, force: true);
        }
        catch (Exception ex)
        {
            _vm.PageStatus = "WEBVIEW ERROR";
            _hub.Publish(DiagnosticItem.Create(
                DiagnosticSeverity.Critical,
                DiagnosticKind.Browser,
                "WebView initialization failed",
                ex.Message,
                details: ex.ToString()));
            throw;
        }

        RenderBrowserTabs();
    }

    private void SwitchTab(BrowserTabSession tab)
    {
        _activeTab = tab;
        BrowserHost.Children.Clear();
        tab.View.HorizontalAlignment = HorizontalAlignment.Stretch;
        tab.View.VerticalAlignment = VerticalAlignment.Stretch;
        tab.View.IsHitTestVisible = true;
        BrowserHost.Children.Add(tab.View);
        _vm.Address = tab.Url;
        UpdateAddressBox(tab.Url, force: true);
        _vm.PageTitle = tab.Title;
        _vm.Security = tab.Security;
        UpdateSecurityStatus(tab.Security);
        UpdateProjectForActiveHost();
        RenderBrowserTabs();
    }

    private void CloseTab(BrowserTabSession tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        var wasActive = ReferenceEquals(tab, _activeTab);
        _tabs.RemoveAt(index);
        tab.Dispose();

        if (_tabs.Count == 0)
        {
            _ = OpenStartTabAsync();
            return;
        }
        if (wasActive) SwitchTab(_tabs[Math.Clamp(index - 1, 0, _tabs.Count - 1)]);
        RenderBrowserTabs();
    }

    private void RenderBrowserTabs()
    {
        BrowserTabsPanel.Children.Clear();
        foreach (var tab in _tabs)
        {
            var active = ReferenceEquals(tab, _activeTab);
            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#1B2937" : "#101720")),
                BorderBrush = active ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("LineBrush"),
                BorderThickness = active ? new Thickness(0, 0, 0, 3) : new Thickness(0, 0, 1, 1),
                MinWidth = 170,
                MaxWidth = 270,
                Height = 41,
                Margin = new Thickness(0)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new Button
            {
                Content = string.IsNullOrWhiteSpace(tab.Title) ? "New Tab" : tab.Title,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(11, 3, 6, 3),
                ToolTip = tab.Url
            };
            title.Click += (_, _) => SwitchTab(tab);
            var close = new Button { Content = "x", Width = 30, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            close.Click += (_, _) => CloseTab(tab);
            Grid.SetColumn(close, 1);
            grid.Children.Add(title);
            grid.Children.Add(close);
            border.Child = grid;
            BrowserTabsPanel.Children.Add(border);
        }
    }

    private void Tab_StateChanged(object? sender, EventArgs e)
    {
        if (sender is not BrowserTabSession tab) return;
        Dispatcher.Invoke(() =>
        {
            if (ReferenceEquals(tab, _activeTab))
            {
                _vm.Address = tab.Url;
                UpdateAddressBox(tab.Url);
                _vm.PageTitle = tab.Title;
                _vm.PageStatus = tab.IsLoading ? "LOADING..." : "READY";
                UpdateProjectForActiveHost();
            }
            RenderBrowserTabs();
        });
    }

    private void Tab_SecurityChanged(object? sender, SecuritySnapshot e)
    {
        if (!ReferenceEquals(sender, _activeTab)) return;
        Dispatcher.Invoke(() =>
        {
            _vm.Security = e;
            UpdateSecurityStatus(e);
        });
    }

    private void UpdateSecurityStatus(SecuritySnapshot security)
    {
        _vm.HttpsStatus = security.IsHttps
            ? security.CertificateValid == false ? "TLS ERROR" : "HTTPS"
            : string.IsNullOrWhiteSpace(security.Url) ? "- TLS" : "HTTP";
    }

    private void Hub_DiagnosticAdded(object? sender, DiagnosticItem item)
    {
        Dispatcher.Invoke(() =>
        {
            _vm.Diagnostics.Insert(0, item);
            while (_vm.Diagnostics.Count > 1000) _vm.Diagnostics.RemoveAt(_vm.Diagnostics.Count - 1);

            // V1.5 correlates related error signals into developer incidents. The raw
            // diagnostics remain available in Error Centre, while AI Code Review shows a
            // cleaner incident inbox (HTTP + server log + JS symptom can become one item).
            if (item.Kind != DiagnosticKind.Ai && item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical)
            {
                _vm.AiDiagnostics.Insert(0, item); // retained for compatibility/export
                while (_vm.AiDiagnostics.Count > 500) _vm.AiDiagnostics.RemoveAt(_vm.AiDiagnostics.Count - 1);
                UpsertIncident(_incidentCorrelator.Add(item));
            }
            _vm.NotifyCounts();

            // Synopsis Browser/Ollama's own diagnostics are visible for troubleshooting, but are
            // never eligible for AI analysis. This prevents AI-error recursion.
            if (item.Kind == DiagnosticKind.Ai) return;

            if (item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical)
            {
                // New errors go into the Code Review inbox without stealing the current
                // selection. Clicking an inbox item is the explicit request to ask Ollama.
                if (_settings.Ai.AutoAnalyzeErrors && !_settings.Ai.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                    EnqueueAutoAnalysis(item);
                else if (_vm.SelectedDiagnostic is null)
                    _vm.AiPanelStatus = $"New error ready for code review: {item.Kind} - {item.Title}. Click it in AI Code Review.";
            }
        });
    }

    private void UpsertIncident(DeveloperIncident incident)
    {
        var existing = _vm.AiIncidents.FirstOrDefault(x => x.Id == incident.Id);
        if (existing is null)
        {
            _vm.AiIncidents.Insert(0, incident);
            while (_vm.AiIncidents.Count > 300) _vm.AiIncidents.RemoveAt(_vm.AiIncidents.Count - 1);
        }
        else
        {
            var gainedSignals = incident.SignalCount > existing.SignalCount;
            var index = _vm.AiIncidents.IndexOf(existing);
            _vm.AiIncidents[index] = incident;
            if (gainedSignals)
            {
                _incidentReviews.Remove(incident.Id);
                _incidentReviewStatuses.Remove(incident.Id);
            }
            if (_vm.SelectedIncident?.Id == incident.Id)
            {
                _vm.SelectedIncident = incident;
                _vm.SelectedDiagnostic = incident.Primary;
                if (gainedSignals)
                {
                    _vm.AiRecommendation = null;
                    _vm.AiPanelStatus = $"A new related signal was added to this incident ({incident.SignalCount} total). Review it again to include the new evidence.";
                    UpdateAiIncidentEvidence(incident);
                }
            }
        }
        _vm.NotifyCounts();
    }

    private void Hub_ConsoleAdded(object? sender, ConsoleEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            _vm.ConsoleEntries.Add(entry);
            while (_vm.ConsoleEntries.Count > 1500) _vm.ConsoleEntries.RemoveAt(0);
        });
    }

    private void Hub_NetworkChanged(object? sender, NetworkEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _vm.NetworkEntries.FirstOrDefault(x => x.RequestId == entry.RequestId);
            if (existing is not null)
            {
                var index = _vm.NetworkEntries.IndexOf(existing);
                _vm.NetworkEntries[index] = entry;
            }
            else _vm.NetworkEntries.Insert(0, entry);

            while (_vm.NetworkEntries.Count > 2000) _vm.NetworkEntries.RemoveAt(_vm.NetworkEntries.Count - 1);
            _vm.NotifyNetworkMetrics();

            if (_activeTab is not null && entry.Type.Equals("Document", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(entry.Url, UriKind.Absolute, out var requestUri) && Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var activeUri) &&
                requestUri.Host.Equals(activeUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                var reqCount = _vm.NetworkEntries.Count(x => Uri.TryCreate(x.Url, UriKind.Absolute, out var u) && u.Host.Equals(activeUri.Host, StringComparison.OrdinalIgnoreCase));
                _vm.PageStatus = $"{entry.Status?.ToString() ?? "..."}  |  {entry.DurationMs:F0}ms  |  {reqCount} req";
            }
        });
    }

    private async Task RefreshOllamaAsync()
    {
        if (_settings.Ai.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            _vm.OllamaModels.Clear();
            _vm.SelectedModel = null;
            _vm.OllamaStatus = "AI DISABLED";
            AiSettingsStatusText.Text = "AI is disabled in Synopsis Settings.";
            return;
        }

        _vm.OllamaStatus = "AI CHECKING...";
        var status = await _ollama.GetStatusAsync();
        _vm.OllamaModels.Clear();
        if (!status.Available)
        {
            _vm.OllamaStatus = "AI OFFLINE";
            AiSettingsStatusText.Text = $"Connection failed: {status.Error}";
            return;
        }

        foreach (var model in status.Models) _vm.OllamaModels.Add(model);
        _vm.SelectedModel = ChooseBestModel(status.Models, _settings.Ai.PreferredModel);
        _vm.OllamaStatus = status.Models.Count == 0
            ? $"OLLAMA {status.Version ?? "REMOTE"} | NO MODELS"
            : $"OLLAMA {status.Version ?? "REMOTE"} | READY";
        AiSettingsStatusText.Text = status.Models.Count == 0
            ? "Connected successfully, but this endpoint returned no models."
            : $"Connected. {status.Models.Count} model(s) available. Selected: {_vm.SelectedModel}";

        if (_settings.Ai.AutoAnalyzeErrors && status.Models.Count > 0 && _autoAnalysisQueue.Count > 0)
            StartAutoAnalysisWorker();
    }

    private static string? ChooseBestModel(IReadOnlyList<string> models, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var saved = models.FirstOrDefault(x => x.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (saved is not null) return saved;
        }

        return models
            .OrderByDescending(ModelScore)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int ModelScore(string model)
    {
        var value = model.ToLowerInvariant();
        var score = 0;
        if (value.Contains("coder")) score += 100;
        if (value.Contains("devstral")) score += 90;
        if (value.Contains("qwen")) score += 50;
        if (value.Contains("gpt-oss")) score += 45;
        if (value.Contains("30b")) score += 10;
        if (value.Contains("32b")) score += 10;
        return score;
    }

    private void UpdateProjectForActiveHost()
    {
        if (_activeTab is null || !Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var uri)) { _vm.Project = null; return; }
        _vm.Project = FindProjectLink(uri);
    }

    private ProjectLink? FindProjectLink(Uri uri)
        => _projects.Find(uri.Authority) ?? _projects.Find(uri.Host); // host fallback migrates early V1 links

    private ProjectLink? FindProjectForDiagnostic(DiagnosticItem diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.Url) && Uri.TryCreate(diagnostic.Url, UriKind.Absolute, out var diagnosticUri))
        {
            var exact = FindProjectLink(diagnosticUri);
            if (exact is not null) return exact;
        }

        if (_activeTab is not null && Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var activeUri))
            return FindProjectLink(activeUri);

        return _vm.Project;
    }

    private ProjectDiagnosticContext? UpdateAiProjectEvidence(DiagnosticItem? diagnostic)
    {
        if (diagnostic is null)
        {
            _vm.AiProjectStatus = "Select an error to inspect linked project context.";
            _vm.AiSourceEvidence = string.Empty;
            _vm.AiSourcePath = null;
            _vm.AiSourceLine = null;
            _vm.AiSourcePreview = string.Empty;
            _vm.AiLogEvidence = string.Empty;
            _vm.AiSourceResolution = string.Empty;
            return null;
        }

        var link = FindProjectForDiagnostic(diagnostic);
        if (link is null)
        {
            _vm.AiProjectStatus = "No project folder is linked for this error/site. Link the current site to let Ollama inspect referenced source code and server logs.";
            _vm.AiSourceEvidence = string.Empty;
            _vm.AiSourcePath = null;
            _vm.AiSourceLine = null;
            _vm.AiSourcePreview = string.Empty;
            _vm.AiLogEvidence = string.Empty;
            _vm.AiSourceResolution = string.Empty;
            return null;
        }

        try
        {
            var context = _projects.BuildDiagnosticContext(link, diagnostic);
            _vm.AiProjectStatus = context.SourcePath is null
                ? $"Linked: {link.Framework} | {link.Path}. No exact source file was resolved; recent linked logs/project identity will still be supplied to Ollama."
                : $"Linked: {link.Framework} | {link.Path}. Synopsis resolved the source below and will include the nearby code in the Ollama request.";
            _vm.AiSourceEvidence = context.Evidence;
            _vm.AiSourcePath = context.SourcePath;
            _vm.AiSourceLine = context.SourceLine;
            _vm.AiSourcePreview = context.SourceExcerpt;
            _vm.AiLogEvidence = context.LogEvidence;
            _vm.AiSourceResolution = context.SourceResolution;
            return context;
        }
        catch (Exception ex)
        {
            _vm.AiProjectStatus = $"Project is linked, but Synopsis could not gather source context: {ex.Message}";
            _vm.AiSourceEvidence = string.Empty;
            _vm.AiSourcePath = null;
            _vm.AiSourceLine = null;
            _vm.AiSourcePreview = string.Empty;
            _vm.AiLogEvidence = string.Empty;
            _vm.AiSourceResolution = string.Empty;
            return null;
        }
    }

    private ProjectDiagnosticContext? UpdateAiIncidentEvidence(DeveloperIncident? incident)
    {
        if (incident is null)
        {
            _vm.AiProjectStatus = "Select an incident to inspect linked project context.";
            _vm.AiSourceEvidence = string.Empty;
            _vm.AiSourcePath = null;
            _vm.AiSourceLine = null;
            _vm.AiSourcePreview = string.Empty;
            _vm.AiLogEvidence = string.Empty;
            _vm.AiSourceResolution = string.Empty;
            return null;
        }

        ProjectLink? link = null;
        foreach (var signal in incident.Signals)
        {
            if (!string.IsNullOrWhiteSpace(signal.Url) && Uri.TryCreate(signal.Url, UriKind.Absolute, out var signalUri))
            {
                link = FindProjectLink(signalUri);
                if (link is not null) break;
            }
        }
        link ??= FindProjectForDiagnostic(incident.Primary);
        if (link is null)
        {
            _vm.AiProjectStatus = "No project folder is linked for this incident/site. Link the current site so Synopsis can review real source code and logs.";
            _vm.AiSourceEvidence = string.Empty;
            _vm.AiSourcePath = null;
            _vm.AiSourceLine = null;
            _vm.AiSourcePreview = string.Empty;
            _vm.AiLogEvidence = string.Empty;
            _vm.AiSourceResolution = string.Empty;
            return null;
        }

        try
        {
            var context = _projects.BuildIncidentContext(link, incident);
            _vm.AiProjectStatus = context.SourcePath is null
                ? $"Linked: {link.Framework} | {link.Path}. Synopsis did not resolve an exact file, so Ollama will review the correlated signals plus bounded project/log evidence."
                : $"Linked: {link.Framework} | {link.Path}. Synopsis resolved source code for this incident and will include it in the review.";
            _vm.AiSourceEvidence = context.Evidence;
            _vm.AiSourcePath = context.SourcePath;
            _vm.AiSourceLine = context.SourceLine;
            _vm.AiSourcePreview = context.SourceExcerpt;
            _vm.AiLogEvidence = context.LogEvidence;
            _vm.AiSourceResolution = context.SourceResolution;
            return context;
        }
        catch (Exception ex)
        {
            _vm.AiProjectStatus = $"Project is linked, but Synopsis could not gather incident source context: {ex.Message}";
            _vm.AiSourceEvidence = string.Empty;
            _vm.AiSourcePath = null;
            _vm.AiSourceLine = null;
            _vm.AiSourcePreview = string.Empty;
            _vm.AiLogEvidence = string.Empty;
            _vm.AiSourceResolution = string.Empty;
            return null;
        }
    }

    private void UpdateAddressBox(string url, bool force = false)
    {
        if (!force && AddressBox.IsKeyboardFocusWithin) return;
        AddressBox.Text = url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ? string.Empty : url;
        AddressBox.CaretIndex = AddressBox.Text.Length;
    }

    private void NavigateFromAddress()
    {
        if (_activeTab is null) return;
        var input = AddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        try
        {
            var target = _urlResolver.Resolve(input, _settings.SearchEngineTemplate);

            // V1.5.1 treats an omnibox navigation to a different URL as a fresh
            // developer-diagnostics session. This prevents Console/Network/Error/AI
            // evidence from the previous site being mistaken for evidence from the next.
            // Persistent settings and project-link mappings are deliberately preserved.
            if (IsDifferentAddress(_activeTab.Url, target))
                ClearAllDeveloperData("NEW URL - DIAGNOSTICS CLEARED");

            _vm.PageStatus = "NAVIGATING...";
            _activeTab.Navigate(target);
            AddressBox.Text = target.ToString();
            AddressBox.CaretIndex = AddressBox.Text.Length;
            _activeTab.View.Focus();
        }
        catch (Exception ex)
        {
            _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Warning, DiagnosticKind.Network, "Invalid address", ex.Message));
            _vm.PageStatus = "INVALID ADDRESS";
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
    }

    private static bool IsDifferentAddress(string currentUrl, Uri target)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)) return true;
        if (current.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)) return true;

        static string Normalize(Uri uri)
        {
            if (!uri.IsAbsoluteUri) return uri.ToString();
            var value = uri.AbsoluteUri;
            if (uri.Query.Length == 0 && uri.Fragment.Length == 0)
                value = value.TrimEnd('/');
            return value;
        }

        return !string.Equals(Normalize(current), Normalize(target), StringComparison.OrdinalIgnoreCase);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateFromAddress();
            return;
        }

        if (e.Key == Key.Escape && _activeTab is not null)
        {
            e.Handled = true;
            UpdateAddressBox(_activeTab.Url, force: true);
            AddressBox.SelectAll();
        }
    }


    private void Go_Click(object sender, RoutedEventArgs e) => NavigateFromAddress();
    private async Task OpenStartTabAsync()
    {
        await AddTabAsync(new Uri("about:blank"));
        ShowStartPage();
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        }));
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) => await OpenStartTabAsync();
    private void Back_Click(object sender, RoutedEventArgs e) => _activeTab?.Back();
    private void Forward_Click(object sender, RoutedEventArgs e) => _activeTab?.Forward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _activeTab?.Reload();
    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        if (string.IsNullOrWhiteSpace(_settings.HomePage))
        {
            ShowStartPage();
            return;
        }

        try
        {
            var target = _urlResolver.Resolve(_settings.HomePage, _settings.SearchEngineTemplate);
            _activeTab.Navigate(target);
        }
        catch
        {
            ShowStartPage();
        }
    }
    private void NativeDevTools_Click(object sender, RoutedEventArgs e) => _activeTab?.OpenNativeDevTools();

    private void ShowStartPage()
    {
        if (_activeTab is null) return;
        _activeTab.NavigateToHtml(StartPageHtml);
        _vm.PageStatus = "READY";
        _vm.Address = string.Empty;
        UpdateAddressBox(string.Empty, force: true);
    }

    private void OpenLab_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        _activeTab.NavigateToHtml("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Synopsis Diagnostics Lab</title>
<style>
:root{color-scheme:dark;font-family:Segoe UI,system-ui,sans-serif;background:#0b0f14;color:#f4f7fa}
body{margin:0;padding:56px;max-width:1050px}h1{font-size:42px;margin:0 0 8px}.sub{color:#9fb0c0;font-size:18px;margin-bottom:30px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px}.card{border:1px solid #2b3a49;border-radius:14px;background:#111821;padding:18px}
button{width:100%;padding:12px;border-radius:9px;border:1px solid #3e566d;background:#17212d;color:#f4f7fa;font-weight:700;cursor:pointer}button:hover{background:#223142}
.tag{font:700 11px Consolas;color:#62d6ff}.hint{color:#9fb0c0;line-height:1.45}code{font-family:Consolas;color:#ffc95c}
</style>
</head>
<body>
<div class="tag">INTERNAL / DIAGNOSTICS LAB</div><h1>Synopsis test page</h1>
<p class="sub">Use these controls to verify the custom Console, Error Centre and Network inspector without changing one of your projects.</p>
<div class="grid">
 <div class="card"><h3>Console messages</h3><p class="hint">Creates log, warning and error messages.</p><button onclick="console.log('Synopsis lab log');console.warn('Synopsis lab warning');console.error('Synopsis lab error')">RUN CONSOLE TEST</button></div>
 <div class="card"><h3>JavaScript exception</h3><p class="hint">Throws an uncaught exception for the Error Centre.</p><button onclick="setTimeout(()=>{throw new Error('Synopsis lab exception')},10)">THROW EXCEPTION</button></div>
 <div class="card"><h3>Correlated incident</h3><p class="hint">Creates a console error and matching uncaught exception so V1.5 can group related signals.</p><button onclick="console.error('Synopsis correlated incident');setTimeout(()=>{throw new Error('Synopsis correlated incident')},25)">RUN CORRELATED INCIDENT</button></div>
 <div class="card"><h3>Network failure</h3><p class="hint">Requests a deliberately unreachable local endpoint.</p><button onclick="fetch('http://127.0.0.1:59999/synopsis-test').catch(()=>{})">RUN FAILED FETCH</button></div>
 <div class="card"><h3>Console evaluator</h3><p class="hint">Open Synopsis Console and run <code>document.title</code>.</p><button onclick="document.body.dataset.synopsis='ready'">SET TEST VALUE</button></div>
</div>
</body></html>
""");
    }

    private void ToggleDevTools_Click(object sender, RoutedEventArgs e)
    {
        if (_detachedDevToolsWindow is not null)
        {
            if (_detachedDevToolsWindow.IsVisible)
            {
                _detachedDevToolsWindow.Hide();
                _vm.DevToolsVisible = false;
            }
            else
            {
                _detachedDevToolsWindow.Show();
                _detachedDevToolsWindow.Activate();
                _vm.DevToolsVisible = true;
            }
            return;
        }

        _vm.DevToolsVisible = !_vm.DevToolsVisible;
        DevToolsRow.Height = new GridLength(_vm.DevToolsVisible ? Math.Max(240, _settings.DevToolsSize) : 0);
    }

    private void EnsureDevToolsVisible(double minimumHeight = 240)
    {
        _vm.DevToolsVisible = true;
        if (_detachedDevToolsWindow is not null)
        {
            if (!_detachedDevToolsWindow.IsVisible) _detachedDevToolsWindow.Show();
            _detachedDevToolsWindow.Activate();
            return;
        }

        DevToolsRow.Height = new GridLength(Math.Max(minimumHeight, _settings.DevToolsSize));
    }

    private double ClampDevToolsHeight(double requested)
    {
        var max = Math.Max(260, ActualHeight - 280);
        return Math.Clamp(requested, 190, max);
    }

    private void DevToolsResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_detachedDevToolsWindow is not null) return;
        EnsureDevToolsVisible();
        var current = DevToolsRow.ActualHeight > 0 ? DevToolsRow.ActualHeight : Math.Max(240, _settings.DevToolsSize);
        var next = ClampDevToolsHeight(current - e.VerticalChange);
        DevToolsRow.Height = new GridLength(next);
        _settings.DevToolsSize = next;
    }

    private void ShrinkDevTools_Click(object sender, RoutedEventArgs e)
    {
        if (_detachedDevToolsWindow is not null) return;
        var current = DevToolsRow.ActualHeight > 0 ? DevToolsRow.ActualHeight : Math.Max(240, _settings.DevToolsSize);
        var next = ClampDevToolsHeight(current - 140);
        DevToolsRow.Height = new GridLength(next);
        _settings.DevToolsSize = next;
        _vm.DevToolsVisible = true;
    }

    private void ExpandDevTools_Click(object sender, RoutedEventArgs e)
    {
        if (_detachedDevToolsWindow is not null) return;
        var current = DevToolsRow.ActualHeight > 0 ? DevToolsRow.ActualHeight : Math.Max(240, _settings.DevToolsSize);
        var next = ClampDevToolsHeight(current + 140);
        DevToolsRow.Height = new GridLength(next);
        _settings.DevToolsSize = next;
        _vm.DevToolsVisible = true;
    }

    private void HideDevTools_Click(object sender, RoutedEventArgs e)
    {
        if (_detachedDevToolsWindow is not null)
        {
            _detachedDevToolsWindow.Hide();
            _vm.DevToolsVisible = false;
            return;
        }
        if (DevToolsRow.ActualHeight > 100) _settings.DevToolsSize = DevToolsRow.ActualHeight;
        DevToolsRow.Height = new GridLength(0);
        _vm.DevToolsVisible = false;
    }

    private void DetachDevTools_Click(object sender, RoutedEventArgs e) => DetachDevTools();

    private void DetachDevTools()
    {
        if (_detachedDevToolsWindow is not null)
        {
            if (!_detachedDevToolsWindow.IsVisible) _detachedDevToolsWindow.Show();
            _detachedDevToolsWindow.Activate();
            return;
        }

        if (DevToolsRow.ActualHeight > 100) _settings.DevToolsSize = DevToolsRow.ActualHeight;
        if (DevTabs.Parent is Panel currentParent) currentParent.Children.Remove(DevTabs);

        var root = new Grid { Background = (Brush)FindResource("PanelBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(10, 16, 23)),
            LastChildFill = true
        };
        var dockButton = new Button
        {
            Content = "DOCK BACK",
            Margin = new Thickness(5),
            Padding = new Thickness(12, 3, 12, 3),
            FontWeight = FontWeights.Bold,
            BorderBrush = (Brush)FindResource("AccentBrush")
        };
        dockButton.Click += (_, _) => ReattachDevTools();
        DockPanel.SetDock(dockButton, Dock.Right);
        header.Children.Add(dockButton);
        header.Children.Add(new TextBlock
        {
            Text = "SYNOPSIS DEVELOPER TOOLS - DETACHED",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush")
        });
        root.Children.Add(header);

        Grid.SetRow(DevTabs, 1);
        root.Children.Add(DevTabs);

        var window = new Window
        {
            Title = "Synopsis Developer Tools",
            Width = Math.Max(1000, ActualWidth * 0.72),
            Height = Math.Max(560, ActualHeight * 0.68),
            MinWidth = 760,
            MinHeight = 420,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBrush"),
            Content = root
        };
        window.Closed += DetachedDevToolsWindow_Closed;

        _detachedDevToolsWindow = window;
        _settings.DevToolsDock = "Detached";
        _vm.DevToolsVisible = true;
        DevToolsRow.Height = new GridLength(0);
        window.Show();
    }

    private void DetachedDevToolsWindow_Closed(object? sender, EventArgs e)
    {
        if (_mainWindowClosing) return;
        ReattachDevTools(closeWindow: false);
    }

    private void ReattachDevTools(bool closeWindow = true)
    {
        var window = _detachedDevToolsWindow;
        if (window is null) return;

        _detachedDevToolsWindow = null;
        window.Closed -= DetachedDevToolsWindow_Closed;
        if (DevTabs.Parent is Panel parent) parent.Children.Remove(DevTabs);
        Grid.SetRow(DevTabs, 1);
        if (!DevToolsDockGrid.Children.Contains(DevTabs)) DevToolsDockGrid.Children.Add(DevTabs);

        if (closeWindow)
        {
            window.Content = null;
            window.Close();
        }

        _settings.DevToolsDock = "Bottom";
        _vm.DevToolsVisible = true;
        DevToolsRow.Height = new GridLength(Math.Max(240, _settings.DevToolsSize));
        Activate();
    }

    private void SecurityStatus_Click(object sender, RoutedEventArgs e)
    {
        EnsureDevToolsVisible(300);
        DevTabs.SelectedItem = SecurityTab;
    }

    private void ErrorsStatus_Click(object sender, RoutedEventArgs e)
    {
        EnsureDevToolsVisible(300);
        DevTabs.SelectedItem = ErrorsTab;
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || string.IsNullOrWhiteSpace(_activeTab.Url)) return;
        if (!_settings.Bookmarks.Contains(_activeTab.Url, StringComparer.OrdinalIgnoreCase)) _settings.Bookmarks.Add(_activeTab.Url);
        _settingsStore.Save(_settings);
        _vm.PageStatus = "BOOKMARK SAVED";
    }

    private void LoadSettingsUi()
    {
        _loadingSettingsUi = true;
        try
        {
            AiProviderBox.ItemsSource = new[] { "Ollama Local", "Ollama Remote / Cloud", "Disabled" };
            AiTimeoutBox.ItemsSource = new[] { 60, 120, 180, 300, 600 };
            SearchEngineBox.ItemsSource = new[] { "Google", "Bing", "DuckDuckGo", "Custom" };

            HomePageBox.Text = _settings.HomePage;
            SearchTemplateBox.Text = _settings.SearchEngineTemplate;
            SearchEngineBox.SelectedItem = DetectSearchEngine(_settings.SearchEngineTemplate);
            DevToolsDefaultBox.IsChecked = _settings.DevToolsOpenByDefault;
            SettingsPathText.Text = AppDataDirectory;

            AiProviderBox.SelectedItem = string.IsNullOrWhiteSpace(_settings.Ai.Provider) ? "Ollama Local" : _settings.Ai.Provider;
            if (AiProviderBox.SelectedItem is null) AiProviderBox.SelectedItem = "Ollama Local";
            OllamaEndpointBox.Text = string.IsNullOrWhiteSpace(_settings.Ai.BaseUrl) ? "http://localhost:11434/" : _settings.Ai.BaseUrl;
            AiTimeoutBox.SelectedItem = new[] { 60, 120, 180, 300, 600 }.Contains(_settings.Ai.TimeoutSeconds) ? _settings.Ai.TimeoutSeconds : 180;
            IncludeProjectContextBox.IsChecked = _settings.Ai.IncludeProjectContext;
            AutoAnalyzeErrorsBox.IsChecked = _settings.Ai.AutoAnalyzeErrors;
            ApiKeyBox.Password = _secretStore.GetSecret(OllamaApiKeySecret) ?? string.Empty;
            ApiKeyVisibleBox.Text = ApiKeyBox.Password;
        }
        finally
        {
            _loadingSettingsUi = false;
        }
        ApplyAiProviderUiState();
    }

    private IOllamaClient CreateOllamaClientFromSavedSettings()
    {
        var apiKey = _settings.Ai.Provider.Equals("Ollama Remote / Cloud", StringComparison.OrdinalIgnoreCase)
            ? _secretStore.GetSecret(OllamaApiKeySecret)
            : null;
        return new OllamaClient(_redactor, new OllamaConnectionOptions(
            _settings.Ai.BaseUrl,
            apiKey,
            _settings.Ai.TimeoutSeconds));
    }

    private OllamaConnectionOptions BuildOllamaOptionsFromUi()
    {
        var endpoint = OllamaEndpointBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "http://localhost:11434/";
        if (!endpoint.Contains("://", StringComparison.Ordinal)) endpoint = "http://" + endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS Ollama endpoint.");

        var timeout = AiTimeoutBox.SelectedItem is int seconds ? seconds : 180;
        var provider = AiProviderBox.SelectedItem?.ToString() ?? "Ollama Local";
        var key = provider.Equals("Ollama Remote / Cloud", StringComparison.OrdinalIgnoreCase) ? GetApiKeyFromUi() : null;
        return new OllamaConnectionOptions(endpoint, key, timeout);
    }

    private string GetApiKeyFromUi() => _showApiKey ? ApiKeyVisibleBox.Text.Trim() : ApiKeyBox.Password.Trim();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        EnsureDevToolsVisible(420);
        DevTabs.SelectedItem = SettingsTab;
    }

    private void AiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettingsUi) return;
        var provider = AiProviderBox.SelectedItem?.ToString() ?? "Ollama Local";
        if (provider == "Ollama Local") OllamaEndpointBox.Text = "http://localhost:11434/";
        else if (provider == "Ollama Remote / Cloud" && (string.IsNullOrWhiteSpace(OllamaEndpointBox.Text) || OllamaEndpointBox.Text.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
            OllamaEndpointBox.Text = "https://ollama.com/";
        ApplyAiProviderUiState();
    }

    private void ApplyAiProviderUiState()
    {
        var provider = AiProviderBox.SelectedItem?.ToString() ?? "Ollama Local";
        var disabled = provider == "Disabled";
        var remote = provider == "Ollama Remote / Cloud";
        OllamaEndpointBox.IsEnabled = !disabled && remote;
        ApiKeyBox.IsEnabled = !disabled && remote;
        ApiKeyVisibleBox.IsEnabled = !disabled && remote;
        ShowApiKeyButton.IsEnabled = !disabled && remote;
        SettingsModelBox.IsEnabled = !disabled;
        AiTimeoutBox.IsEnabled = !disabled;
        IncludeProjectContextBox.IsEnabled = !disabled;
        AutoAnalyzeErrorsBox.IsEnabled = !disabled;
        if (provider == "Ollama Local") AiSettingsStatusText.Text = "Local Ollama uses http://localhost:11434 and does not require an API key.";
        else if (provider == "Ollama Remote / Cloud") AiSettingsStatusText.Text = "Remote/cloud mode uses the endpoint above. API keys are stored encrypted for the current Windows user.";
        else AiSettingsStatusText.Text = "AI diagnosis is disabled. All browser and developer tools remain available.";
    }

    private void ToggleApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (!_showApiKey)
        {
            ApiKeyVisibleBox.Text = ApiKeyBox.Password;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyVisibleBox.Visibility = Visibility.Visible;
            ShowApiKeyButton.Content = "HIDE";
            _showApiKey = true;
            ApiKeyVisibleBox.Focus();
            ApiKeyVisibleBox.CaretIndex = ApiKeyVisibleBox.Text.Length;
        }
        else
        {
            ApiKeyBox.Password = ApiKeyVisibleBox.Text;
            ApiKeyVisibleBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Visibility = Visibility.Visible;
            ShowApiKeyButton.Content = "SHOW";
            _showApiKey = false;
            ApiKeyBox.Focus();
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = string.Empty;
        ApiKeyVisibleBox.Text = string.Empty;
        _secretStore.DeleteSecret(OllamaApiKeySecret);
        _ollama = CreateOllamaClientFromSavedSettings();
        AiSettingsStatusText.Text = "Stored Ollama API key cleared for this Windows user.";
    }

    private async void TestAiConnection_Click(object sender, RoutedEventArgs e)
    {
        await TestAiConnectionFromUiAsync(saveAfterSuccess: false);
    }

    private async void RefreshSettingsModels_Click(object sender, RoutedEventArgs e)
    {
        await TestAiConnectionFromUiAsync(saveAfterSuccess: false);
    }

    private async void TestAiModel_Click(object sender, RoutedEventArgs e)
    {
        var provider = AiProviderBox.SelectedItem?.ToString() ?? "Ollama Local";
        if (provider == "Disabled")
        {
            AiSettingsStatusText.Text = "AI is disabled.";
            return;
        }

        try
        {
            AiSettingsStatusText.Text = "Testing the selected model with a real diagnostic request...";
            var options = BuildOllamaOptionsFromUi();
            var testClient = new OllamaClient(_redactor, options);
            var status = await testClient.GetStatusAsync();
            if (!status.Available)
            {
                AiSettingsStatusText.Text = $"Connection failed before model test: {status.Error}";
                return;
            }

            var model = SettingsModelBox.SelectedItem?.ToString() ?? _vm.SelectedModel
                        ?? ChooseBestModel(status.Models, _settings.Ai.PreferredModel);
            if (string.IsNullOrWhiteSpace(model))
            {
                AiSettingsStatusText.Text = "No model is selected or installed.";
                return;
            }

            var testDiagnostic = DiagnosticItem.Create(
                DiagnosticSeverity.Error,
                DiagnosticKind.JavaScript,
                "Synopsis AI self-test",
                "TypeError: Cannot read properties of undefined (reading 'name'). The value user.profile is undefined.",
                "http://localhost/synopsis-ai-self-test",
                "app.js",
                42);

            var result = await testClient.AnalyzeAsync(testDiagnostic, model, fastMode: true);
            AiSettingsStatusText.Text = $"MODEL TEST PASSED - {model}. Root cause: {result.RootCause}";
        }
        catch (Exception ex)
        {
            AiSettingsStatusText.Text = $"MODEL TEST FAILED - {ex.Message}";
        }
    }

    private async Task<bool> TestAiConnectionFromUiAsync(bool saveAfterSuccess)
    {
        var provider = AiProviderBox.SelectedItem?.ToString() ?? "Ollama Local";
        if (provider == "Disabled")
        {
            AiSettingsStatusText.Text = "AI is disabled.";
            return true;
        }

        try
        {
            AiSettingsStatusText.Text = "Testing connection...";
            var testClient = new OllamaClient(_redactor, BuildOllamaOptionsFromUi());
            var status = await testClient.GetStatusAsync();
            if (!status.Available)
            {
                AiSettingsStatusText.Text = $"Connection failed: {status.Error}";
                return false;
            }

            var current = _vm.SelectedModel;
            _vm.OllamaModels.Clear();
            foreach (var model in status.Models) _vm.OllamaModels.Add(model);
            _vm.SelectedModel = ChooseBestModel(status.Models, current ?? _settings.Ai.PreferredModel);
            AiSettingsStatusText.Text = status.Models.Count == 0
                ? "Connected successfully, but no models were returned."
                : $"Connected successfully. {status.Models.Count} model(s) found. Selected: {_vm.SelectedModel}";

            if (saveAfterSuccess) SaveAiSettingsCore();
            return true;
        }
        catch (Exception ex)
        {
            AiSettingsStatusText.Text = $"Connection failed: {ex.Message}";
            return false;
        }
    }

    private async void SaveAiSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var provider = AiProviderBox.SelectedItem?.ToString() ?? "Ollama Local";
            if (provider != "Disabled" && !await TestAiConnectionFromUiAsync(saveAfterSuccess: false)) return;
            SaveAiSettingsCore();
            AiSettingsStatusText.Text = provider == "Disabled"
                ? "AI settings saved. AI diagnosis is disabled."
                : "AI settings saved securely and connection verified.";
            await RefreshOllamaAsync();
        }
        catch (Exception ex)
        {
            AiSettingsStatusText.Text = $"Could not save AI settings: {ex.Message}";
        }
    }

    private void SaveAiSettingsCore()
    {
        var provider = AiProviderBox.SelectedItem?.ToString() ?? "Ollama Local";
        var options = provider == "Disabled"
            ? new OllamaConnectionOptions("http://localhost:11434/", null, AiTimeoutBox.SelectedItem is int disabledTimeout ? disabledTimeout : 180)
            : BuildOllamaOptionsFromUi();

        _settings.Ai.Provider = provider;
        _settings.Ai.BaseUrl = options.BaseUrl;
        _settings.Ai.TimeoutSeconds = options.TimeoutSeconds;
        _settings.Ai.IncludeProjectContext = IncludeProjectContextBox.IsChecked == true;
        _settings.Ai.AutoAnalyzeErrors = AutoAnalyzeErrorsBox.IsChecked == true;
        _settings.Ai.PreferredModel = _vm.SelectedModel;

        if (provider == "Ollama Remote / Cloud")
        {
            var key = GetApiKeyFromUi();
            if (string.IsNullOrWhiteSpace(key)) _secretStore.DeleteSecret(OllamaApiKeySecret);
            else _secretStore.SetSecret(OllamaApiKeySecret, key);
        }

        _settingsStore.Save(_settings);
        _ollama = CreateOllamaClientFromSavedSettings();
        if (_settings.Ai.AutoAnalyzeErrors && !_settings.Ai.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            QueueExistingErrorsForAutoAnalysis();
    }

    private void SaveBrowserSettings_Click(object sender, RoutedEventArgs e)
    {
        var home = HomePageBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(home))
        {
            try { _ = _urlResolver.Resolve(home, _settings.SearchEngineTemplate); }
            catch
            {
                MessageBox.Show(this, "Enter a valid home page URL.", "Synopsis Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var template = SearchTemplateBox.Text.Trim();
        if (!template.Contains("{query}", StringComparison.Ordinal))
        {
            MessageBox.Show(this, "The search template must contain {query}.", "Synopsis Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.HomePage = home;
        _settings.SearchEngineTemplate = template;
        _settings.DevToolsOpenByDefault = DevToolsDefaultBox.IsChecked == true;
        _settingsStore.Save(_settings);
        _vm.PageStatus = "SETTINGS SAVED";
    }

    private void SearchEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettingsUi) return;
        SearchTemplateBox.Text = SearchEngineBox.SelectedItem?.ToString() switch
        {
            "Google" => "https://www.google.com/search?q={query}",
            "Bing" => "https://www.bing.com/search?q={query}",
            "DuckDuckGo" => "https://duckduckgo.com/?q={query}",
            _ => SearchTemplateBox.Text
        };
    }

    private static string DetectSearchEngine(string template)
    {
        if (template.Contains("google.", StringComparison.OrdinalIgnoreCase)) return "Google";
        if (template.Contains("bing.com", StringComparison.OrdinalIgnoreCase)) return "Bing";
        if (template.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase)) return "DuckDuckGo";
        return "Custom";
    }

    private async void AnalyzeSelected_Click(object sender, RoutedEventArgs e)
    {
        var diagnostic = _vm.SelectedDiagnostic ?? FindLatestDiagnostic();
        if (diagnostic is null)
        {
            _vm.AiPanelStatus = "Nothing to review yet. Browse the site or use LAB to produce an error.";
            return;
        }
        _vm.SelectedDiagnostic = diagnostic;
        await AnalyzeDiagnosticAsync(diagnostic);
    }

    private async void AnalyzeLatest_Click(object sender, RoutedEventArgs e)
    {
        var diagnostic = FindLatestDiagnostic();
        if (diagnostic is null)
        {
            _vm.AiPanelStatus = "Nothing to analyse yet. Open LAB and trigger an error, then come back here.";
            return;
        }
        _vm.SelectedDiagnostic = diagnostic;
        await AnalyzeDiagnosticAsync(diagnostic);
    }

    private DiagnosticItem? FindLatestDiagnostic()
        => _vm.Diagnostics.FirstOrDefault(x => x.Kind != DiagnosticKind.Ai && x.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical)
           ?? _vm.Diagnostics.FirstOrDefault(x => x.Kind != DiagnosticKind.Ai);

    private void QueueExistingErrorsForAutoAnalysis()
    {
        if (!_settings.Ai.AutoAnalyzeErrors || _settings.Ai.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var diagnostic in _vm.Diagnostics
                     .Where(x => x.Kind != DiagnosticKind.Ai && x.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical)
                     .Reverse())
        {
            EnqueueAutoAnalysis(diagnostic);
        }
    }

    private void EnqueueAutoAnalysis(DiagnosticItem diagnostic)
    {
        if (diagnostic.Kind == DiagnosticKind.Ai) return;
        if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Critical)) return;
        if (!_autoAnalysisKnown.Add(diagnostic.Id)) return;

        _autoAnalysisQueue.Enqueue(diagnostic);
        StartAutoAnalysisWorker();
    }

    private void StartAutoAnalysisWorker()
    {
        if (_autoAnalysisWorkerRunning || _autoAnalysisQueue.Count == 0) return;
        _ = ProcessAutoAnalysisQueueAsync();
    }

    private async Task ProcessAutoAnalysisQueueAsync()
    {
        if (_autoAnalysisWorkerRunning) return;
        _autoAnalysisWorkerRunning = true;
        try
        {
            while (_autoAnalysisQueue.Count > 0)
            {
                if (!_settings.Ai.AutoAnalyzeErrors || _settings.Ai.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.IsNullOrWhiteSpace(_vm.SelectedModel) || !_vm.OllamaStatus.Contains("READY", StringComparison.OrdinalIgnoreCase))
                {
                    await RefreshOllamaAsync();
                    if (string.IsNullOrWhiteSpace(_vm.SelectedModel) || !_vm.OllamaStatus.Contains("READY", StringComparison.OrdinalIgnoreCase))
                    {
                        _vm.AiPanelStatus = $"{_autoAnalysisQueue.Count} error(s) waiting for Ollama. Open Settings > AI Admin and restore the connection.";
                        return; // Keep the queue intact; RefreshOllamaAsync will restart it when ready.
                    }
                }

                var diagnostic = _autoAnalysisQueue.Dequeue();
                await Task.Delay(150); // Small debounce so related browser details can arrive first.
                await AnalyzeDiagnosticAsync(diagnostic, showUnavailableMessage: false, automatic: true);
            }
        }
        finally
        {
            _autoAnalysisWorkerRunning = false;
            if (_autoAnalysisQueue.Count > 0
                && _settings.Ai.AutoAnalyzeErrors
                && !_settings.Ai.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_vm.SelectedModel)
                && _vm.OllamaStatus.Contains("READY", StringComparison.OrdinalIgnoreCase))
            {
                StartAutoAnalysisWorker();
            }
        }
    }

    private async Task AnalyzeDiagnosticAsync(DiagnosticItem diagnostic, bool showUnavailableMessage = true, bool automatic = false)
    {
        if (diagnostic.Kind == DiagnosticKind.Ai)
        {
            _vm.AiPanelStatus = "Synopsis AI-internal diagnostics are never sent back to Ollama. Inspect the error text directly or retry the original website/server incident.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.SelectedModel))
        {
            await RefreshOllamaAsync();
            if (string.IsNullOrWhiteSpace(_vm.SelectedModel))
            {
                _vm.AiPanelStatus = "Ollama is not ready with an installed model. Open Settings > AI Admin and test the connection.";
                if (showUnavailableMessage)
                    MessageBox.Show(this, "Ollama was not detected with an installed model. Open Settings > AI Admin and test the connection.", "Ollama unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        DispatcherTimer? progressTimer = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            _vm.OllamaStatus = automatic ? "AI AUTO..." : "CODE REVIEW...";

            // Never leave a blank diagnosis panel while a local model is working.
            // This also makes it obvious that Synopsis has captured the error and Ollama
            // is the part currently doing work.
            if (_vm.SelectedDiagnostic?.Id == diagnostic.Id)
            {
                _vm.AiRecommendation = new AiRecommendation
                {
                    RootCause = "Ollama is reviewing this error and its linked code...",
                    Confidence = "Working",
                    Explanation = automatic
                        ? "Fast Auto AI is running with bounded context/output. The result will replace this message automatically."
                        : "Ollama is reviewing the selected error, linked source code, and server-log evidence. The code review will appear here automatically.",
                    InvestigationSteps = ["No action needed while this request is running."],
                    SuggestedFix = "Waiting for Ollama code review...",
                    SuggestedCode = string.Empty,
                    RelatedSignals = [$"Model: {_vm.SelectedModel}", $"Queue waiting: {_autoAnalysisQueue.Count}"]
                };
            }

            void UpdateProgress()
            {
                var prefix = automatic ? "Auto analysing" : "Analysing";
                _vm.AiPanelStatus = $"{prefix} {diagnostic.Kind}: {diagnostic.Title} with {_vm.SelectedModel} - {elapsed.Elapsed.TotalSeconds:F0}s elapsed - {_autoAnalysisQueue.Count} queued";
            }

            UpdateProgress();
            progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            progressTimer.Tick += (_, _) => UpdateProgress();
            progressTimer.Start();

            var sourceContext = UpdateAiProjectEvidence(diagnostic);
            var projectContext = !_settings.Ai.IncludeProjectContext || sourceContext is null
                ? null
                : sourceContext.Evidence;
            var recommendation = await _ollama.AnalyzeAsync(diagnostic, _vm.SelectedModel, projectContext, fastMode: automatic);
            progressTimer?.Stop();
            var completedStatus = $"Code review complete in {elapsed.Elapsed.TotalSeconds:F0}s for {diagnostic.Kind}: {diagnostic.Title}";
            _aiRecommendations[diagnostic.Id] = recommendation;
            _aiRecommendationStatuses[diagnostic.Id] = completedStatus;
            _vm.OllamaStatus = "OLLAMA | READY";

            // Auto analysis must not overwrite the visible diagnosis for another incident.
            // When this incident is selected, show its own stored result immediately.
            if (_vm.SelectedDiagnostic?.Id == diagnostic.Id)
            {
                _vm.AiRecommendation = recommendation;
                _vm.AiPanelStatus = completedStatus;
            }

            // Manual analysis intentionally opens the AI panel. Automatic analysis stays
            // unobtrusive unless the developer is already looking at this incident.
            if (!automatic)
            {
                _vm.SelectedDiagnostic = diagnostic;
                _vm.AiRecommendation = recommendation;
                _vm.AiPanelStatus = completedStatus;
                EnsureDevToolsVisible(300);
                DevTabs.SelectedItem = AiTab;
            }
        }
        catch (Exception ex)
        {
            progressTimer?.Stop();
            var reason = ex.Message;
            _vm.OllamaStatus = "AI ERROR";
            var failureRecommendation = new AiRecommendation
            {
                RootCause = "Ollama code review did not complete",
                Confidence = "N/A",
                Explanation = reason,
                InvestigationSteps =
                [
                    "Confirm Ollama still shows Connected in Settings > AI Admin.",
                    $"Confirm the selected model '{_vm.SelectedModel}' can be run directly in Ollama.",
                    "If this is the first request after loading a model, increase the AI timeout and try again.",
                    "Select the original website/server error in AI Code Review and click REVIEW AGAIN to retry."
                ],
                SuggestedFix = reason.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                    ? "Increase the AI timeout in Settings > AI Admin (180 or 300 seconds is recommended for slower local machines), then retry."
                    : "Review the exact Ollama error above, correct the model/endpoint if needed, then retry the original incident.",
                SuggestedCode = string.Empty,
                RelatedSignals = [ $"Model: {_vm.SelectedModel}", $"Incident: {diagnostic.Kind} - {diagnostic.Title}" ]
            };
            var failureStatus = $"Code review failed for {diagnostic.Kind}: {diagnostic.Title}. {reason}";
            _aiRecommendations[diagnostic.Id] = failureRecommendation;
            _aiRecommendationStatuses[diagnostic.Id] = failureStatus;
            if (_vm.SelectedDiagnostic?.Id == diagnostic.Id)
            {
                _vm.AiRecommendation = failureRecommendation;
                _vm.AiPanelStatus = failureStatus;
            }
            if (!automatic)
            {
                _vm.SelectedDiagnostic = diagnostic;
                _vm.AiRecommendation = failureRecommendation;
                _vm.AiPanelStatus = failureStatus;
                EnsureDevToolsVisible(300);
                DevTabs.SelectedItem = AiTab;
            }
            _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Warning, DiagnosticKind.Ai, "Ollama analysis failed", reason, diagnostic.Url, details: ex.ToString()));
        }
        finally
        {
            progressTimer?.Stop();
            elapsed.Stop();
        }
    }

    private async void AnalyzeSecurity_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.Security;
        var diagnostic = DiagnosticItem.Create(DiagnosticSeverity.Info, DiagnosticKind.Security, "Security review",
            $"Review the security posture for {s.Url}", s.Url,
            details: $"HTTPS={s.IsHttps}; CertificateValid={s.CertificateValid}; Certificate={s.CertificateStatus}; Protocol={s.Protocol}; Cipher={s.CipherSuite}; HSTS={s.HasHsts}; CSP={s.HasCsp}; XFrameOptions={s.HasXFrameOptions}; ReferrerPolicy={s.HasReferrerPolicy}; Expires={s.ValidTo}");
        await AnalyzeDiagnosticAsync(diagnostic);
    }

    private async void RefreshOllama_Click(object sender, RoutedEventArgs e) => await RefreshOllamaAsync();

    private void Diagnostics_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var diagnostic = _vm.SelectedDiagnostic;
        if (diagnostic is null) return;
        UpdateAiProjectEvidence(diagnostic);

        if (_aiRecommendations.TryGetValue(diagnostic.Id, out var recommendation))
        {
            _vm.AiRecommendation = recommendation;
            _vm.AiPanelStatus = _aiRecommendationStatuses.TryGetValue(diagnostic.Id, out var status)
                ? status
                : $"Diagnosis available for {diagnostic.Kind}: {diagnostic.Title}";
            return;
        }

        _vm.AiRecommendation = null;
        if (diagnostic.Kind != DiagnosticKind.Ai
            && diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical
            && _settings.Ai.AutoAnalyzeErrors
            && !_settings.Ai.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            EnqueueAutoAnalysis(diagnostic);
            _vm.AiPanelStatus = $"Queued for AI analysis: {diagnostic.Kind} - {diagnostic.Title}";
        }
        else
        {
            _vm.AiPanelStatus = $"No stored code review for {diagnostic.Kind}: {diagnostic.Title}. Select it in AI Diagnosis or click REVIEW AGAIN.";
        }
    }


    private async void AiDiagnostics_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The AI inbox is deliberately simple: selecting a real error is the explicit
        // request to review it. Synopsis gathers linked source/log evidence first, then asks
        // Ollama for a code review. Stored reviews are restored instantly and are not
        // regenerated unless the developer presses REVIEW AGAIN.
        Diagnostics_SelectionChanged(sender, e);
        var diagnostic = _vm.SelectedDiagnostic;
        if (diagnostic is null || diagnostic.Kind == DiagnosticKind.Ai) return;
        if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Critical)) return;
        if (_aiRecommendations.ContainsKey(diagnostic.Id)) return;
        if (!_manualReviewInProgress.Add(diagnostic.Id)) return;

        try
        {
            _vm.AiPanelStatus = $"Preparing code review for {diagnostic.Kind}: {diagnostic.Title}...";
            await AnalyzeDiagnosticAsync(diagnostic, showUnavailableMessage: false, automatic: false);
        }
        finally
        {
            _manualReviewInProgress.Remove(diagnostic.Id);
        }
    }

    private async void AiIncidents_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var incident = _vm.SelectedIncident;
        if (incident is null) return;

        _vm.SelectedDiagnostic = incident.Primary;
        UpdateAiIncidentEvidence(incident);

        if (_incidentReviews.TryGetValue(incident.Id, out var stored))
        {
            _vm.AiRecommendation = stored;
            _vm.AiPanelStatus = _incidentReviewStatuses.TryGetValue(incident.Id, out var storedStatus)
                ? storedStatus
                : $"Stored incident review available for {incident.Title}.";
            return;
        }

        if (!_incidentReviewInProgress.Add(incident.Id)) return;
        try
        {
            await ReviewIncidentAsync(incident, deepReview: false, showUnavailableMessage: false);
        }
        finally
        {
            _incidentReviewInProgress.Remove(incident.Id);
        }
    }

    private async void ReviewIncident_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedIncident is null)
        {
            _vm.AiPanelStatus = "Select an incident from the left first.";
            return;
        }
        await ReviewIncidentAsync(_vm.SelectedIncident, deepReview: false, showUnavailableMessage: true);
    }

    private async void DeepReviewIncident_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedIncident is null)
        {
            _vm.AiPanelStatus = "Select an incident from the left first.";
            return;
        }
        await ReviewIncidentAsync(_vm.SelectedIncident, deepReview: true, showUnavailableMessage: true);
    }

    private async Task ReviewIncidentAsync(DeveloperIncident incident, bool deepReview, bool showUnavailableMessage)
    {
        if (string.IsNullOrWhiteSpace(_vm.SelectedModel))
        {
            await RefreshOllamaAsync();
            if (string.IsNullOrWhiteSpace(_vm.SelectedModel))
            {
                _vm.AiPanelStatus = "Ollama is not ready with an installed model. Open Settings > AI Admin and test the connection.";
                if (showUnavailableMessage)
                    MessageBox.Show(this, "Ollama was not detected with an installed model. Open Settings > AI Admin and test the connection.", "Ollama unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        DispatcherTimer? timer = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            _vm.SelectedIncident = incident;
            _vm.SelectedDiagnostic = incident.Primary;
            var context = UpdateAiIncidentEvidence(incident);
            var projectContext = !_settings.Ai.IncludeProjectContext || context is null ? null : context.Evidence;

            _vm.OllamaStatus = deepReview ? "DEEP REVIEW..." : "CODE REVIEW...";
            _vm.AiRecommendation = new AiRecommendation
            {
                RootCause = deepReview ? "Ollama is performing a deeper incident review..." : "Ollama is reviewing this incident...",
                Confidence = "Working",
                Explanation = $"Synopsis correlated {incident.SignalCount} signal(s) and gathered the best linked source/log evidence available.",
                InvestigationSteps = ["No action needed while the model is running."],
                SuggestedFix = "Waiting for Ollama...",
                SuggestedCode = string.Empty,
                RelatedSignals = incident.Signals.Select(x => $"{x.Kind}: {x.Title}").Take(5).ToList()
            };

            void UpdateProgress() => _vm.AiPanelStatus = $"{(deepReview ? "Deep reviewing" : "Reviewing")} incident with {_vm.SelectedModel} - {elapsed.Elapsed.TotalSeconds:F0}s elapsed - {incident.SignalCount} correlated signal(s)";
            UpdateProgress();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => UpdateProgress();
            timer.Start();

            var recommendation = await _ollama.AnalyzeIncidentAsync(incident, _vm.SelectedModel, projectContext, fastMode: !deepReview);
            timer.Stop();
            var status = $"{(deepReview ? "Deep" : "Quick")} code review complete in {elapsed.Elapsed.TotalSeconds:F0}s | {incident.SignalCount} correlated signal(s)";
            _incidentReviews[incident.Id] = recommendation;
            _incidentReviewStatuses[incident.Id] = status;
            _vm.AiRecommendation = recommendation;
            _vm.AiPanelStatus = status;
            _vm.OllamaStatus = "OLLAMA | READY";
        }
        catch (Exception ex)
        {
            timer?.Stop();
            var failure = new AiRecommendation
            {
                RootCause = "Ollama incident review did not complete",
                Confidence = "N/A",
                Explanation = ex.Message,
                InvestigationSteps = ["Confirm Ollama is running and the selected model can answer a normal prompt.", "Retry QUICK REVIEW, or use a smaller local model."],
                SuggestedFix = "Resolve the Ollama/model error shown above, then retry this incident.",
                SuggestedCode = string.Empty,
                RelatedSignals = incident.Signals.Select(x => $"{x.Kind}: {x.Title}").Take(5).ToList()
            };
            _incidentReviews[incident.Id] = failure;
            _incidentReviewStatuses[incident.Id] = ex.Message;
            _vm.AiRecommendation = failure;
            _vm.AiPanelStatus = ex.Message;
            _vm.OllamaStatus = "AI ERROR";
            _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Warning, DiagnosticKind.Ai, "Ollama incident review failed", ex.Message, incident.Url, details: ex.ToString()));
        }
        finally
        {
            timer?.Stop();
            elapsed.Stop();
        }
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDiagnostic is null) return;
        Clipboard.SetText($"{_vm.SelectedDiagnostic.Kind} - {_vm.SelectedDiagnostic.Title}\n{_vm.SelectedDiagnostic.Message}\n{_vm.SelectedDiagnostic.Details}");
    }

    private void CopyNetworkUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNetwork is not null) Clipboard.SetText(_vm.SelectedNetwork.Url);
    }

    private void CopyAsCurl_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNetwork is null) return;
        var n = _vm.SelectedNetwork;
        var escapedUrl = n.Url.Replace("\"", "\\\"");
        var command = new StringBuilder("curl -X " + n.Method + " \"" + escapedUrl + "\"");
        if (!string.IsNullOrWhiteSpace(n.PostData))
        {
            var escapedData = n.PostData.Replace("'", "'\\''");
            command.Append(" --data-raw '" + escapedData + "'");
        }
        Clipboard.SetText(command.ToString());
    }

    private async void LoadResponseBody_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _vm.SelectedNetwork is null) return;
        try
        {
            _vm.ApiResponseBody = await _activeTab.GetResponseBodyAsync(_vm.SelectedNetwork.RequestId);
        }
        catch (Exception ex)
        {
            _vm.ApiResponseBody = $"Response body unavailable: {ex.Message}";
        }
    }

    private void PrettyJson_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.ApiResponseBody)) return;
        try
        {
            using var json = JsonDocument.Parse(_vm.ApiResponseBody);
            _vm.ApiResponseBody = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            _vm.PageStatus = "RESPONSE IS NOT JSON";
        }
    }

    private void CopyApiResponse_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_vm.ApiResponseBody)) Clipboard.SetText(_vm.ApiResponseBody);
    }

    private void CopyFetch_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNetwork is null) return;
        var n = _vm.SelectedNetwork;
        var body = string.IsNullOrWhiteSpace(n.PostData) ? string.Empty : $",\n  body: {JsonSerializer.Serialize(n.PostData)}";
        Clipboard.SetText($"fetch({JsonSerializer.Serialize(n.Url)}, {{\n  method: {JsonSerializer.Serialize(n.Method)}{body}\n}});");
    }

    private void CopyPhpRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNetwork is null) return;
        var n = _vm.SelectedNetwork;
        var escapedUrl = n.Url.Replace("'", "\\'");
        var escapedBody = (n.PostData ?? string.Empty).Replace("'", "\\'");
        var php = $"$response = Http::withBody('{escapedBody}', 'application/json')->send('{n.Method}', '{escapedUrl}');";
        Clipboard.SetText(php);
    }

    private void CopyCSharpRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNetwork is null) return;
        var n = _vm.SelectedNetwork;
        var escapedUrl = n.Url.Replace("\"", "\\\"");
        var code = new StringBuilder();
        code.AppendLine("using var http = new HttpClient();");
        code.AppendLine($"using var request = new HttpRequestMessage(HttpMethod.{ToHttpMethodProperty(n.Method)}, \"{escapedUrl}\");");
        if (!string.IsNullOrWhiteSpace(n.PostData))
        {
            var payload = n.PostData.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
            code.AppendLine($"request.Content = new StringContent(\"{payload}\", Encoding.UTF8, \"application/json\");");
        }
        code.AppendLine("using var response = await http.SendAsync(request);");
        code.AppendLine("var body = await response.Content.ReadAsStringAsync();");
        Clipboard.SetText(code.ToString());
    }

    private static string ToHttpMethodProperty(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "Get",
        "POST" => "Post",
        "PUT" => "Put",
        "DELETE" => "Delete",
        "PATCH" => "Patch",
        "HEAD" => "Head",
        "OPTIONS" => "Options",
        _ => "Get"
    };

    private async void RunConsole_Click(object sender, RoutedEventArgs e) => await RunConsoleAsync();
    private async void ConsoleInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RunConsoleAsync(); }

    private async Task RunConsoleAsync()
    {
        if (_activeTab is null || string.IsNullOrWhiteSpace(ConsoleInput.Text)) return;
        var script = ConsoleInput.Text;
        try
        {
            var result = await _activeTab.ExecuteScriptAsync(script);
            _hub.Publish(new ConsoleEntry(DateTimeOffset.Now, "result", result));
        }
        catch (Exception ex)
        {
            _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Error, DiagnosticKind.JavaScript, "Console execution failed", ex.Message, _activeTab.Url));
        }
        ConsoleInput.Clear();
    }

    private void ClearDiagnostics_Click(object sender, RoutedEventArgs e)
        => ClearAllDeveloperData("DIAGNOSTICS CLEARED");

    private void ClearAll_Click(object sender, RoutedEventArgs e)
        => ClearAllDeveloperData("ALL DEVELOPER DATA CLEARED");

    private void ClearAllDeveloperData(string status)
    {
        // This is a transient-session reset only. It intentionally does NOT touch:
        // - Settings / Ollama configuration
        // - Saved project-link mappings
        // - Bookmarks
        // - Files in linked project directories
        _hub.Clear();

        // Clear the per-WebView request/cache state too, otherwise a late CDP event
        // from the previous page could repopulate the freshly-cleared Network/Error tabs.
        foreach (var tab in _tabs)
            tab.ResetDiagnosticSession();

        _vm.Diagnostics.Clear();
        _vm.AiDiagnostics.Clear();
        _vm.AiIncidents.Clear();
        _vm.ConsoleEntries.Clear();
        _vm.NetworkEntries.Clear();

        _autoAnalysisQueue.Clear();
        _autoAnalysisKnown.Clear();
        _aiRecommendations.Clear();
        _aiRecommendationStatuses.Clear();
        _manualReviewInProgress.Clear();
        _incidentCorrelator.Clear();
        _incidentReviews.Clear();
        _incidentReviewStatuses.Clear();
        _incidentReviewInProgress.Clear();

        _vm.SelectedIncident = null;
        _vm.SelectedDiagnostic = null;
        _vm.SelectedNetwork = null;
        _vm.AiRecommendation = null;
        _vm.ApiResponseBody = string.Empty;

        _vm.AiProjectStatus = "No linked project context for the selected error.";
        _vm.AiSourceEvidence = string.Empty;
        _vm.AiSourcePath = null;
        _vm.AiSourceLine = null;
        _vm.AiSourcePreview = string.Empty;
        _vm.AiLogEvidence = string.Empty;
        _vm.AiSourceResolution = string.Empty;
        _vm.AiPanelStatus = "Select an incident. Synopsis will correlate the signals, resolve linked source, and ask Ollama for a code review.";

        _vm.Security = new SecuritySnapshot();
        UpdateSecurityStatus(_vm.Security);

        if (ConsoleInput is not null)
            ConsoleInput.Clear();

        _vm.NotifyCounts();
        _vm.NotifyNetworkMetrics();
        _vm.PageStatus = status;
    }

    private void CopyAiCode_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_vm.AiRecommendation?.SuggestedCode)) Clipboard.SetText(_vm.AiRecommendation.SuggestedCode);
    }

    private void LinkProject_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || !Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var uri)) return;
        var projectKey = uri.Authority;
        var dialog = new OpenFolderDialog { Title = $"Link {projectKey} to its source project", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _vm.Project = _projects.Link(projectKey, dialog.FolderName);
            _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Info, DiagnosticKind.Project, "Project linked",
                $"{projectKey} -> {dialog.FolderName}", _activeTab.Url, source: dialog.FolderName));
            UpdateAiProjectEvidence(_vm.SelectedDiagnostic);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not link project", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenAiSource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.AiSourcePath) || !File.Exists(_vm.AiSourcePath))
        {
            _vm.AiPanelStatus = "No exact source file has been resolved for the selected error yet.";
            return;
        }

        try
        {
            var target = _vm.AiSourceLine is > 0 ? $"{_vm.AiSourcePath}:{_vm.AiSourceLine}" : _vm.AiSourcePath;
            Process.Start(new ProcessStartInfo("code", $"-g \"{target}\"") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_vm.AiSourcePath}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void UnlinkProject_Click(object sender, RoutedEventArgs e)
    {
        var link = _vm.Project;
        if (link is null)
        {
            MessageBox.Show(this, "There is no project link for the current site.", "Synopsis Project Link", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            $"Unlink {link.Host} from:\n\n{link.Path}\n\nThis removes only Synopsis Browser's saved link. No files or folders will be deleted, moved, or changed.",
            "Unlink project from Synopsis?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        _projects.Remove(link.Host);
        _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Info, DiagnosticKind.Project, "Project unlinked",
            $"Removed Synopsis mapping only: {link.Host} -> {link.Path}. Project files were not changed.", _activeTab?.Url));
        UpdateProjectForActiveHost();
        UpdateAiProjectEvidence(_vm.SelectedDiagnostic);
    }

    private void OpenProjectFolder_Click(object sender, RoutedEventArgs e) => StartExternal("explorer.exe", _vm.Project?.Path);
    private void OpenVsCode_Click(object sender, RoutedEventArgs e) => StartExternal("code", _vm.Project?.Path);

    private static void StartExternal(string executable, string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return;
        try { Process.Start(new ProcessStartInfo(executable, $"\"{argument}\"") { UseShellExecute = true }); } catch { }
    }

    private async void PhonePreset_Click(object sender, RoutedEventArgs e) => await ApplyDevicePresetAsync(390, 844, 3, "PHONE 390x844");
    private async void TabletPreset_Click(object sender, RoutedEventArgs e) => await ApplyDevicePresetAsync(768, 1024, 2, "TABLET 768x1024");
    private async void LaptopPreset_Click(object sender, RoutedEventArgs e) => await ApplyDevicePresetAsync(1366, 768, 1, "LAPTOP 1366x768");
    private async void DesktopPreset_Click(object sender, RoutedEventArgs e) => await ApplyDevicePresetAsync(1920, 1080, 1, "DESKTOP 1920x1080");

    private async Task ApplyDevicePresetAsync(int width, int height, double dpr, string label)
    {
        if (_activeTab is null) return;
        await _activeTab.SetDeviceMetricsAsync(width, height, dpr, mobile: width < 1000);
        _vm.PageStatus = label;
    }

    private async void ResetDevicePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        await _activeTab.ClearDeviceMetricsAsync();
        _vm.PageStatus = "REAL WINDOW VIEWPORT";
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.L)
        {
            AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.T)
        {
            await OpenStartTabAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.W && _activeTab is not null)
        {
            CloseTab(_activeTab); e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            _activeTab?.OpenNativeDevTools(); e.Handled = true;
        }
    }
}
