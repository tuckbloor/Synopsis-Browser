using System.Text.Json;
using SynopsisBrowser.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SynopsisBrowser.App.Services;

public sealed class BrowserTabSession : IDisposable
{
    private readonly IDiagnosticHub _hub;
    private readonly ITlsInspector _tlsInspector;
    private readonly CoreWebView2Environment _environment;
    private readonly Dictionary<string, NetworkEntry> _network = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mainDocumentHeaders = new(StringComparer.OrdinalIgnoreCase);
    private CoreWebView2DevToolsProtocolEventReceiver? _consoleReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _exceptionReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _requestReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _responseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _finishedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _failedReceiver;
    private readonly Dictionary<string, DateTimeOffset> _recentConsoleSignals = new(StringComparer.Ordinal);
    private long _diagnosticGeneration;

    public Guid Id { get; } = Guid.NewGuid();
    public WebView2CompositionControl View { get; } = new();
    public string Title { get; private set; } = "New Tab";
    public string Url => View.Source?.ToString() ?? "about:blank";
    public bool IsLoading { get; private set; }
    public SecuritySnapshot Security { get; private set; } = new();

    public event EventHandler? StateChanged;
    public event EventHandler<SecuritySnapshot>? SecurityChanged;

    public BrowserTabSession(IDiagnosticHub hub, ITlsInspector tlsInspector, CoreWebView2Environment environment)
    {
        _hub = hub;
        _tlsInspector = tlsInspector;
        _environment = environment;
    }

    public async Task InitializeAsync(Uri startUri)
    {
        await View.EnsureCoreWebView2Async(_environment);
        var core = View.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsWebMessageEnabled = true;

        core.DocumentTitleChanged += (_, _) =>
        {
            Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New Tab" : core.DocumentTitle;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        core.SourceChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        core.NavigationStarting += (_, _) =>
        {
            IsLoading = true;
            _mainDocumentHeaders.Clear();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        core.NavigationCompleted += async (_, e) =>
        {
            IsLoading = false;
            if (!e.IsSuccess)
            {
                var status = e.WebErrorStatus.ToString();
                var currentUrl = Url;
                var isBenignBlankAbort = status.Equals("ConnectionAborted", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(currentUrl) || currentUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase));

                // WebView2 can report ConnectionAborted for its internal about:blank page
                // while a real navigation replaces it. That is browser lifecycle noise,
                // not a developer code error, so keep it out of the AI error inbox.
                if (!isBenignBlankAbort)
                {
                    _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Error, DiagnosticKind.Network,
                        "Navigation failed", status, currentUrl));
                }
            }
            await RefreshTlsAsync();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            var cert = e.ServerCertificate;
            string? details = null;
            if (cert is not null)
            {
                var validFrom = cert.ValidFrom;
                var validTo = cert.ValidTo;
                details = $"Subject: {cert.Subject}\nIssuer: {cert.Issuer}\nValid: {validFrom:g} - {validTo:g}\nError: {e.ErrorStatus}";
            }
            _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Critical, DiagnosticKind.Ssl,
                "TLS certificate error", e.ErrorStatus.ToString(), e.RequestUri, details: details));
            // Deliberately leave the default browser action in place; V1 never auto-trusts invalid certificates.
        };

        await EnablePageConsoleBridgeAsync(core);
        await EnableDevToolsProtocolAsync(core);
        Navigate(startUri);
    }

    private async Task EnableDevToolsProtocolAsync(CoreWebView2 core)
    {
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

        _consoleReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
        _consoleReceiver.DevToolsProtocolEventReceived += (_, e) => HandleConsole(e.ParameterObjectAsJson);

        _exceptionReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
        _exceptionReceiver.DevToolsProtocolEventReceived += (_, e) => HandleException(e.ParameterObjectAsJson);

        _requestReceiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        _requestReceiver.DevToolsProtocolEventReceived += (_, e) => HandleRequest(e.ParameterObjectAsJson);

        _responseReceiver = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        _responseReceiver.DevToolsProtocolEventReceived += (_, e) => HandleResponse(e.ParameterObjectAsJson);

        _finishedReceiver = core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        _finishedReceiver.DevToolsProtocolEventReceived += (_, e) => HandleFinished(e.ParameterObjectAsJson);

        _failedReceiver = core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        _failedReceiver.DevToolsProtocolEventReceived += (_, e) => HandleFailed(e.ParameterObjectAsJson);
    }

    private async Task EnablePageConsoleBridgeAsync(CoreWebView2 core)
    {
        core.WebMessageReceived += HandleWebMessageReceived;
        await core.AddScriptToExecuteOnDocumentCreatedAsync("""
(() => {
    if (window.__synopsisConsoleBridgeInstalled) return;
    window.__synopsisConsoleBridgeInstalled = true;

    const send = (payload) => {
        try { chrome.webview.postMessage(JSON.stringify({ __synopsisConsole: payload })); } catch (_) {}
    };

    const printable = (value) => {
        try {
            if (typeof value === 'string') return value;
            if (value instanceof Error) return value.stack || value.message || String(value);
            if (value === undefined) return 'undefined';
            if (value === null) return 'null';
            return JSON.stringify(value);
        } catch (_) {
            try { return String(value); } catch (_) { return '[unprintable]'; }
        }
    };

    for (const level of ['log', 'info', 'warn', 'error', 'debug']) {
        const original = console[level] && console[level].bind(console);
        if (!original) continue;
        console[level] = (...args) => {
            original(...args);
            send({
                kind: 'console',
                level,
                message: args.map(printable).join(' '),
                source: location.href
            });
        };
    }

    window.addEventListener('error', (event) => {
        send({
            kind: 'exception',
            level: 'error',
            message: (event.error && (event.error.stack || event.error.message)) || event.message || 'JavaScript exception',
            source: event.filename || location.href,
            line: event.lineno || null,
            column: event.colno || null
        });
    });

    window.addEventListener('unhandledrejection', (event) => {
        send({
            kind: 'exception',
            level: 'error',
            message: 'Unhandled promise rejection: ' + printable(event.reason),
            source: location.href,
            line: null,
            column: null
        });
    });
})();
""");
    }

    private void HandleWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("__synopsisConsole", out var payload)) return;

            var kind = payload.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "console" : "console";
            var level = payload.TryGetProperty("level", out var levelEl) ? levelEl.GetString() ?? "log" : "log";
            var message = payload.TryGetProperty("message", out var messageEl) ? messageEl.GetString() ?? string.Empty : string.Empty;
            var source = payload.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : null;
            int? line = null;
            int? column = null;
            if (payload.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number)
                line = lineEl.GetInt32();
            if (payload.TryGetProperty("column", out var columnEl) && columnEl.ValueKind == JsonValueKind.Number)
                column = columnEl.GetInt32();

            if (kind.Equals("exception", StringComparison.OrdinalIgnoreCase))
                PublishJavaScriptException(message, source, line, column, json);
            else
                PublishConsole(level, message, source, line);
        }
        catch { }
    }

    private bool IsDuplicateConsoleSignal(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (_recentConsoleSignals.TryGetValue(key, out var previous) && (now - previous).TotalMilliseconds < 350)
            return true;

        _recentConsoleSignals[key] = now;
        if (_recentConsoleSignals.Count > 250)
        {
            var cutoff = now.AddSeconds(-10);
            foreach (var stale in _recentConsoleSignals.Where(x => x.Value < cutoff).Select(x => x.Key).ToArray())
                _recentConsoleSignals.Remove(stale);
        }
        return false;
    }

    private void PublishConsole(string level, string message, string? source, int? line)
    {
        var key = $"console|{level}|{message}|{source}|{line}";
        if (IsDuplicateConsoleSignal(key)) return;

        _hub.Publish(new ConsoleEntry(DateTimeOffset.Now, level, message, source, line));
        if (level is "error" or "warning" or "warn")
        {
            var isError = level == "error";
            _hub.Publish(DiagnosticItem.Create(isError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                DiagnosticKind.Console, $"Console {(isError ? "error" : "warning")}", message, Url, source, line));
        }
    }

    private void PublishJavaScriptException(string message, string? source, int? line, int? column, string? details)
    {
        var key = $"exception|{message}|{source}|{line}";
        if (IsDuplicateConsoleSignal(key)) return;

        // An uncaught exception belongs in both places: Console for the developer's
        // execution stream and Error Centre / AI Code Review for diagnosis.
        _hub.Publish(new ConsoleEntry(DateTimeOffset.Now, "error", message, source, line));
        _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Error, DiagnosticKind.JavaScript,
            "JavaScript exception", message, Url, source, line, details: details, column: column));
    }

    private void HandleConsole(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var level = root.TryGetProperty("type", out var t) ? t.GetString() ?? "log" : "log";
            var parts = new List<string>();
            if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in args.EnumerateArray())
                {
                    if (arg.TryGetProperty("value", out var value)) parts.Add(value.ToString());
                    else if (arg.TryGetProperty("description", out var description)) parts.Add(description.GetString() ?? string.Empty);
                    else parts.Add(arg.ToString());
                }
            }

            string? source = null;
            int? line = null;
            if (root.TryGetProperty("stackTrace", out var stack) && stack.TryGetProperty("callFrames", out var frames) && frames.GetArrayLength() > 0)
            {
                var frame = frames[0];
                source = frame.TryGetProperty("url", out var u) ? u.GetString() : null;
                line = frame.TryGetProperty("lineNumber", out var l) ? l.GetInt32() + 1 : null;
            }

            PublishConsole(level, string.Join(" ", parts), source, line);
        }
        catch { }
    }

    private void HandleException(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var details = doc.RootElement.GetProperty("exceptionDetails");
            var text = details.TryGetProperty("text", out var t) ? t.GetString() ?? "JavaScript exception" : "JavaScript exception";
            var message = text;
            if (details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var description))
                message = description.GetString() ?? text;
            var source = details.TryGetProperty("url", out var url) ? url.GetString() : null;
            var line = details.TryGetProperty("lineNumber", out var lineEl) ? lineEl.GetInt32() + 1 : (int?)null;
            var column = details.TryGetProperty("columnNumber", out var columnEl) ? columnEl.GetInt32() + 1 : (int?)null;
            PublishJavaScriptException(message, source, line, column, json);
        }
        catch { }
    }

    private void HandleRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.GetProperty("requestId").GetString()!;
            var request = root.GetProperty("request");
            var entry = new NetworkEntry
            {
                RequestId = id,
                StartedAt = DateTimeOffset.Now,
                Method = request.TryGetProperty("method", out var method) ? method.GetString() ?? "GET" : "GET",
                Url = request.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                RequestHeadersJson = request.TryGetProperty("headers", out var headers) ? headers.GetRawText() : null,
                PostData = request.TryGetProperty("postData", out var postData) ? postData.GetString() : null
            };
            _network[id] = entry;
            _hub.Publish(entry);
        }
        catch { }
    }

    private void HandleResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.GetProperty("requestId").GetString()!;
            if (!_network.TryGetValue(id, out var entry)) return;
            var response = root.GetProperty("response");
            entry.Status = response.TryGetProperty("status", out var status) ? (int)Math.Round(status.GetDouble()) : null;
            entry.MimeType = response.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? string.Empty : string.Empty;
            entry.Type = root.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty;
            entry.ResponseHeadersJson = response.TryGetProperty("headers", out var headers) ? headers.GetRawText() : null;
            entry.DurationMs = Math.Max(0, (DateTimeOffset.Now - entry.StartedAt).TotalMilliseconds);
            _hub.Publish(entry);

            if (entry.Status >= 400)
            {
                _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Error, DiagnosticKind.Http,
                    $"HTTP {entry.Status}", $"{entry.Method} {entry.Url}", entry.Url,
                    details: $"Status: {entry.Status}\nType: {entry.Type}\nResponse headers: {entry.ResponseHeadersJson}", correlationId: id));
            }

            if (entry.Type.Equals("Document", StringComparison.OrdinalIgnoreCase) && headers.ValueKind == JsonValueKind.Object)
            {
                _mainDocumentHeaders.Clear();
                foreach (var property in headers.EnumerateObject()) _mainDocumentHeaders[property.Name] = property.Value.ToString();
            }
        }
        catch { }
    }

    private void HandleFinished(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("requestId").GetString()!;
            if (!_network.TryGetValue(id, out var entry)) return;
            if (doc.RootElement.TryGetProperty("encodedDataLength", out var bytes)) entry.EncodedBytes = (long)bytes.GetDouble();
            entry.DurationMs = Math.Max(0, (DateTimeOffset.Now - entry.StartedAt).TotalMilliseconds);
            _hub.Publish(entry);
            if ((entry.Status ?? 0) >= 400)
                _ = CaptureFailedResponseBodyAsync(entry);
        }
        catch { }
    }

    private async Task CaptureFailedResponseBodyAsync(NetworkEntry entry)
    {
        var generation = _diagnosticGeneration;
        try
        {
            var body = await GetResponseBodyAsync(entry.RequestId);
            if (generation != _diagnosticGeneration) return;
            if (string.IsNullOrWhiteSpace(body)) return;
            var preview = body.Length <= 8000 ? body : body[..8000] + "\n...[truncated by Synopsis]";
            entry.ResponseBodyPreview = preview;
            _hub.Publish(entry);
            _hub.Publish(DiagnosticItem.Create(
                DiagnosticSeverity.Error,
                DiagnosticKind.Http,
                $"HTTP {entry.Status} response body",
                $"{entry.Method} {entry.Url} returned an error response body.",
                entry.Url,
                details: preview,
                correlationId: entry.RequestId));
        }
        catch
        {
            // Response bodies can be unavailable after redirects/cache/process teardown.
            // The original HTTP diagnostic is still preserved.
        }
    }

    private void HandleFailed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.GetProperty("requestId").GetString()!;
            var error = root.TryGetProperty("errorText", out var errorText) ? errorText.GetString() ?? "Network request failed" : "Network request failed";
            if (_network.TryGetValue(id, out var entry))
            {
                entry.Failed = true;
                entry.ErrorText = error;
                entry.DurationMs = Math.Max(0, (DateTimeOffset.Now - entry.StartedAt).TotalMilliseconds);
                _hub.Publish(entry);
                _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Error, DiagnosticKind.Network,
                    "Network request failed", $"{entry.Method} {entry.Url}: {error}", entry.Url, details: json, correlationId: id));
            }
        }
        catch { }
    }

    private async Task RefreshTlsAsync()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        try
        {
            Security = await _tlsInspector.InspectAsync(uri, _mainDocumentHeaders);
            if (!Security.IsHttps)
            {
                _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Warning, DiagnosticKind.Security,
                    "HTTPS is not enabled", "This page is being served over plain HTTP.", Url));
            }
            else if (Security.CertificateValid == false)
            {
                _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Critical, DiagnosticKind.Ssl,
                    "Certificate validation problem", Security.CertificateStatus, Url, details: $"Issuer: {Security.Issuer}\nExpires: {Security.ValidTo}"));
            }
            if (!Security.HasCsp)
                _hub.Publish(DiagnosticItem.Create(DiagnosticSeverity.Info, DiagnosticKind.Security, "CSP header missing",
                    "No Content-Security-Policy response header was observed on the main document.", Url));
            SecurityChanged?.Invoke(this, Security);
        }
        catch (Exception ex)
        {
            Security = new SecuritySnapshot { Url = Url, IsHttps = uri.Scheme == "https", CertificateValid = null, CertificateStatus = "Inspection unavailable: " + ex.Message };
            SecurityChanged?.Invoke(this, Security);
        }
    }

    public void ResetDiagnosticSession()
    {
        _diagnosticGeneration++;
        _network.Clear();
        _mainDocumentHeaders.Clear();
        _recentConsoleSignals.Clear();
        Security = new SecuritySnapshot();
        SecurityChanged?.Invoke(this, Security);
    }

    public void Navigate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var core = View.CoreWebView2 ?? throw new InvalidOperationException("The browser engine is not initialized yet.");
        core.Navigate(uri.AbsoluteUri);
    }

    public void NavigateToHtml(string html)
    {
        var core = View.CoreWebView2 ?? throw new InvalidOperationException("The browser engine is not initialized yet.");
        core.NavigateToString(html ?? string.Empty);
    }
    public void Reload() => View.Reload();
    public void Stop() => View.CoreWebView2?.Stop();
    public void Back() { if (View.CanGoBack) View.GoBack(); }
    public void Forward() { if (View.CanGoForward) View.GoForward(); }
    public void OpenNativeDevTools() => View.CoreWebView2?.OpenDevToolsWindow();

    public async Task<string> ExecuteScriptAsync(string script) => await View.ExecuteScriptAsync(script);

    public async Task<string> GetResponseBodyAsync(string requestId)
    {
        if (View.CoreWebView2 is null) return string.Empty;
        var args = JsonSerializer.Serialize(new { requestId });
        var json = await View.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.getResponseBody", args);
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? string.Empty : string.Empty;
        var base64 = doc.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean();
        if (base64 && !string.IsNullOrEmpty(body))
        {
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body)); } catch { }
        }
        return body;
    }

    public async Task SetDeviceMetricsAsync(int width, int height, double deviceScaleFactor = 1, bool mobile = true)
    {
        if (View.CoreWebView2 is null) return;
        var args = JsonSerializer.Serialize(new
        {
            width,
            height,
            deviceScaleFactor,
            mobile,
            screenWidth = width,
            screenHeight = height
        });
        await View.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", args);
    }

    public async Task ClearDeviceMetricsAsync()
    {
        if (View.CoreWebView2 is null) return;
        await View.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
    }

    public void Dispose() => View.Dispose();
}
