using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.AI;

/// <summary>
/// Ollama API client. V1.4.2 deliberately uses a tolerant section-based code-review
/// protocol rather than strict JSON. Small local models can truncate or slightly
/// malformed JSON even after producing a useful diagnosis; Synopsis should show the
/// useful review instead of turning that into another browser error.
/// </summary>
public sealed class OllamaClient : IOllamaClient
{
    private readonly ISecretRedactor _redactor;
    private readonly OllamaConnectionOptions _options;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public OllamaClient(ISecretRedactor redactor, OllamaConnectionOptions? options = null)
    {
        _redactor = redactor;
        _options = options ?? new OllamaConnectionOptions("http://localhost:11434/", null, 180);
    }

    public async Task<OllamaStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = CreateClient();
            string? version = null;
            try
            {
                var versionJson = await http.GetFromJsonAsync<JsonElement>("api/version", cancellationToken);
                version = versionJson.TryGetProperty("version", out var v) ? v.GetString() : null;
            }
            catch
            {
                // Some Ollama-compatible remote endpoints do not expose /api/version.
            }

            var tags = await http.GetFromJsonAsync<JsonElement>("api/tags", cancellationToken);
            var models = new List<string>();
            if (tags.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                        models.Add(name.GetString()!);
                }
            }

            return new OllamaStatus(true, version, models.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex)
        {
            return new OllamaStatus(false, null, Array.Empty<string>(), FriendlyError(ex));
        }
    }

    public async Task<AiRecommendation> AnalyzeAsync(DiagnosticItem diagnostic, string? model, string? projectContext = null,
        bool fastMode = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("No Ollama model selected.");

        var evidence = _redactor.Redact($"""
        ERROR TO REVIEW
        Type: {diagnostic.Kind}
        Severity: {diagnostic.Severity}
        Title: {Clip(diagnostic.Title, 300)}
        Message: {Clip(diagnostic.Message, fastMode ? 1400 : 2800)}
        URL: {Clip(diagnostic.Url, 600)}
        Browser source: {Clip(diagnostic.Source, 700)}
        Browser line: {diagnostic.Line}
        Browser details: {Clip(diagnostic.Details, fastMode ? 1800 : 4500)}

        LINKED PROJECT / SOURCE / LOG EVIDENCE
        {Clip(projectContext, fastMode ? 2800 : 10000)}
        """);

        var instructions = """
        You are the code-review engine inside Synopsis, a developer-only web browser.
        Review the reported error and propose the smallest likely code fix supported by the evidence.

        Rules:
        - Treat linked project source code and server logs as the strongest evidence when present.
        - If a real source file and line are supplied, review that code specifically.
        - Separate the root cause from downstream browser/network symptoms.
        - Never invent files, routes, variables, frameworks, line numbers, or values that are not in the evidence.
        - If there is not enough evidence for a safe code change, say what additional evidence is needed instead of fabricating code.
        - Keep the answer concise and practical.

        Return plain text using these headings exactly. DO NOT return JSON.

        ROOT CAUSE:
        <most likely cause>

        CONFIDENCE:
        <Low, Medium, or High>

        EXPLANATION:
        <brief explanation, preferably naming the real file/line if supplied>

        POSSIBLE FIX:
        <smallest safe fix>

        SUGGESTED CODE:
        <code only when justified; otherwise write NONE>

        INVESTIGATION:
        - <step>
        - <step>

        RELATED SIGNALS:
        - <signal>
        """;

        try
        {
            return await SendReviewAsync(model, evidence, instructions, fastMode, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Ollama code review timed out after {_options.TimeoutSeconds} seconds. Increase the AI timeout or select a smaller local model, then retry.", ex);
        }
    }

    public Task<AiRecommendation> AnalyzeIncidentAsync(DeveloperIncident incident, string? model, string? projectContext = null,
        bool fastMode = false, CancellationToken cancellationToken = default)
    {
        var signals = string.Join("\n", incident.Signals.OrderBy(x => x.Timestamp).Select(x =>
            $"[{x.Timestamp:HH:mm:ss.fff}] {x.Kind}/{x.Severity}: {x.Title} | {x.Message} | URL={x.Url} | Source={x.Source}:{x.Line}:{x.Column}"));
        var synthetic = incident.Primary with
        {
            Title = $"Incident: {incident.Title}",
            Message = incident.Message,
            Details = string.Join("\n\n", new[]
            {
                incident.Primary.Details,
                $"CORRELATED INCIDENT SIGNALS ({incident.SignalCount})\n{signals}"
            }.Where(x => !string.IsNullOrWhiteSpace(x)))
        };
        return AnalyzeAsync(synthetic, model, projectContext, fastMode, cancellationToken);
    }

    private async Task<AiRecommendation> SendReviewAsync(string model, string evidence, string instructions,
        bool fastMode, CancellationToken cancellationToken)
    {
        object generationOptions = fastMode
            ? new { temperature = 0.0, num_predict = 360, num_ctx = 3072, top_p = 0.8 }
            : new { temperature = 0.1, num_predict = 720, num_ctx = 4096, top_p = 0.9 };

        var payload = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "10m",
            options = generationOptions,
            messages = new object[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = evidence }
            }
        };

        using var http = CreateClient();
        using var response = await http.PostAsJsonAsync("api/chat", payload, _json, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {ExtractServerError(body)}");

        using var root = JsonDocument.Parse(body);
        if (!root.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
            throw new InvalidOperationException("Ollama returned a successful response without message.content.");

        var raw = content.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Ollama returned an empty code review.");

        return ParseCodeReview(raw);
    }

    private static AiRecommendation ParseCodeReview(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Trim();
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var originalLine in text.Split('\n'))
        {
            var line = originalLine.TrimEnd();
            var heading = NormalizeHeading(line);
            string inlineValue = string.Empty;

            if (heading is null)
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    heading = NormalizeHeading(line[..(colon + 1)]);
                    if (heading is not null) inlineValue = line[(colon + 1)..].Trim();
                }
            }

            if (heading is not null)
            {
                current = heading;
                if (!sections.ContainsKey(current)) sections[current] = [];
                if (!string.IsNullOrWhiteSpace(inlineValue)) sections[current].Add(inlineValue);
                continue;
            }

            if (current is not null)
                sections[current].Add(line);
        }

        string Get(string key) => sections.TryGetValue(key, out var lines)
            ? CleanSection(string.Join("\n", lines))
            : string.Empty;

        List<string> GetList(string key)
        {
            var value = Get(key);
            if (string.IsNullOrWhiteSpace(value)) return [];
            return value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().TrimStart('-', '*', '•').Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(5)
                .ToList();
        }

        var rootCause = Get("ROOT CAUSE");
        var confidence = Get("CONFIDENCE");
        var explanation = Get("EXPLANATION");
        var suggestedFix = Get("POSSIBLE FIX");
        var suggestedCode = Get("SUGGESTED CODE");

        // The section protocol is intentionally tolerant. A useful Ollama response must
        // never be discarded just because a small model missed one heading.
        if (string.IsNullOrWhiteSpace(rootCause))
        {
            rootCause = FirstUsefulLine(text) ?? "Ollama code review";
            if (string.IsNullOrWhiteSpace(explanation)) explanation = text;
        }
        if (string.IsNullOrWhiteSpace(explanation)) explanation = rootCause;
        if (string.IsNullOrWhiteSpace(confidence)) confidence = "Unknown";
        if (suggestedCode.Equals("NONE", StringComparison.OrdinalIgnoreCase)) suggestedCode = string.Empty;

        return new AiRecommendation
        {
            RootCause = rootCause,
            Confidence = confidence,
            Explanation = explanation,
            InvestigationSteps = GetList("INVESTIGATION"),
            SuggestedFix = suggestedFix,
            SuggestedCode = StripCodeFence(suggestedCode),
            RelatedSignals = GetList("RELATED SIGNALS")
        };
    }

    private static string? NormalizeHeading(string line)
    {
        var normalized = line.Trim().Trim('#', '*', ' ', '\t').TrimEnd(':').Trim().ToUpperInvariant();
        return normalized switch
        {
            "ROOT CAUSE" => "ROOT CAUSE",
            "CONFIDENCE" => "CONFIDENCE",
            "EXPLANATION" => "EXPLANATION",
            "POSSIBLE FIX" or "SUGGESTED FIX" or "FIX" => "POSSIBLE FIX",
            "SUGGESTED CODE" or "CODE" => "SUGGESTED CODE",
            "INVESTIGATION" or "INVESTIGATION STEPS" => "INVESTIGATION",
            "RELATED SIGNALS" => "RELATED SIGNALS",
            _ => null
        };
    }

    private static string CleanSection(string value)
        => value.Trim();

    private static string StripCodeFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n');
        if (firstNewline >= 0) text = text[(firstNewline + 1)..];
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) text = text[..lastFence];
        return text.Trim();
    }

    private static string? FirstUsefulLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().Trim('#', '*', '-', ' '))
            .FirstOrDefault(x => x.Length > 2 && NormalizeHeading(x) is null);

    private HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            BaseAddress = NormalizeBaseAddress(_options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 10, 900))
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

        return http;
    }

    private static Uri NormalizeBaseAddress(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "http://localhost:11434" : value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        text = text.TrimEnd('/');
        if (text.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        return new Uri(text + "/", UriKind.Absolute);
    }

    private static string Clip(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + "\n...[truncated by Synopsis]";
    }

    private static string ExtractServerError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "No error body was returned.";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.String ? error.GetString() ?? body : error.GetRawText();
        }
        catch { }
        var compact = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 800 ? compact : compact[..800] + "...";
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is TaskCanceledException) return "Connection timed out.";
        if (ex is HttpRequestException httpEx && httpEx.StatusCode is not null)
            return $"HTTP {(int)httpEx.StatusCode} ({httpEx.StatusCode}). Check the endpoint and API key.";
        return ex.Message;
    }
}
