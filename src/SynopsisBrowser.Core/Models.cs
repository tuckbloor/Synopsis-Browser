using System.Collections.ObjectModel;

namespace SynopsisBrowser.Core;

public enum DiagnosticSeverity { Info, Warning, Error, Critical }
public enum DiagnosticKind { Browser, JavaScript, Console, Http, Network, Security, Ssl, Server, Performance, Project, Ai }

public sealed record DiagnosticItem(
    Guid Id,
    DateTimeOffset Timestamp,
    DiagnosticSeverity Severity,
    DiagnosticKind Kind,
    string Title,
    string Message,
    string? Url = null,
    string? Source = null,
    int? Line = null,
    string? Details = null,
    string? CorrelationId = null,
    int? Column = null)
{
    public static DiagnosticItem Create(DiagnosticSeverity severity, DiagnosticKind kind, string title, string message,
        string? url = null, string? source = null, int? line = null, string? details = null, string? correlationId = null, int? column = null)
        => new(Guid.NewGuid(), DateTimeOffset.Now, severity, kind, title, message, url, source, line, details, correlationId, column);
}



public sealed record DeveloperIncident(
    Guid Id,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DiagnosticSeverity Severity,
    string Title,
    string Message,
    string? Url,
    string? Source,
    DiagnosticItem Primary,
    IReadOnlyList<DiagnosticItem> Signals)
{
    public int SignalCount => Signals.Count;
    public string SignalSummary => SignalCount == 1 ? "1 signal" : $"{SignalCount} related signals";
    public string KindSummary => string.Join(" + ", Signals.Select(x => x.Kind).Distinct().Take(4));
    public string TimeText => LastSeen.ToLocalTime().ToString("HH:mm:ss");
}

public sealed record ConsoleEntry(DateTimeOffset Timestamp, string Level, string Message, string? Source = null, int? Line = null);

public sealed class NetworkEntry
{
    public required string RequestId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = string.Empty;
    public int? Status { get; set; }
    public string Type { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public double? DurationMs { get; set; }
    public long? EncodedBytes { get; set; }
    public bool Failed { get; set; }
    public string? ErrorText { get; set; }
    public string? RequestHeadersJson { get; set; }
    public string? ResponseHeadersJson { get; set; }
    public string? PostData { get; set; }
    public string? ResponseBodyPreview { get; set; }
}

public sealed class SecuritySnapshot
{
    public string Url { get; set; } = string.Empty;
    public bool IsHttps { get; set; }
    public bool? CertificateValid { get; set; }
    public string CertificateStatus { get; set; } = "Not inspected";
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public int? DaysRemaining { get; set; }
    public string? Protocol { get; set; }
    public string? CipherSuite { get; set; }
    public bool? ChainValid { get; set; }
    public bool HasHsts { get; set; }
    public bool HasCsp { get; set; }
    public bool HasXFrameOptions { get; set; }
    public bool HasReferrerPolicy { get; set; }
    public bool MixedContentObserved { get; set; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record OllamaStatus(bool Available, string? Version, IReadOnlyList<string> Models, string? Error = null);

public sealed class AiRecommendation
{
    public string RootCause { get; set; } = string.Empty;
    public string Confidence { get; set; } = "Unknown";
    public string Explanation { get; set; } = string.Empty;
    public List<string> InvestigationSteps { get; set; } = [];
    public string SuggestedFix { get; set; } = string.Empty;
    public string SuggestedCode { get; set; } = string.Empty;
    public List<string> RelatedSignals { get; set; } = [];
}

public sealed class ProjectLink
{
    public string Host { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Framework { get; set; } = "Unknown";
    public string? LogFile { get; set; }
    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record ProjectDiagnosticContext(
    string Summary,
    string Evidence,
    string? SourcePath = null,
    int? SourceLine = null,
    string SourceExcerpt = "",
    string LogEvidence = "",
    string SourceResolution = "");


public sealed class AiSettings
{
    public string Provider { get; set; } = "Disabled";
    public string BaseUrl { get; set; } = "http://localhost:11434/";
    public int TimeoutSeconds { get; set; } = 180;
    public bool IncludeProjectContext { get; set; } = true;
    public bool AutoAnalyzeErrors { get; set; } = false;
    public string? PreferredModel { get; set; }
}

public sealed record OllamaConnectionOptions(string BaseUrl, string? ApiKey, int TimeoutSeconds = 90);

public sealed class BrowserSettings
{
    public string HomePage { get; set; } = "https://www.google.com";
    public string SearchEngineTemplate { get; set; } = "https://www.google.com/search?q={query}";
    public bool RestoreSession { get; set; } = true;
    public bool DevToolsOpenByDefault { get; set; }
    public string DevToolsDock { get; set; } = "Bottom";
    public double DevToolsSize { get; set; } = 330;
    public AiSettings Ai { get; set; } = new();
    public List<string> Bookmarks { get; set; } = [];
    public List<string> RecentUrls { get; set; } = [];
    public List<ProjectLink> ProjectLinks { get; set; } = [];
}
