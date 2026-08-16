namespace SynopsisBrowser.Core;

public interface IDiagnosticHub
{
    event EventHandler<DiagnosticItem>? DiagnosticAdded;
    event EventHandler<ConsoleEntry>? ConsoleAdded;
    event EventHandler<NetworkEntry>? NetworkChanged;
    void Publish(DiagnosticItem item);
    void Publish(ConsoleEntry entry);
    void Publish(NetworkEntry entry);
    IReadOnlyCollection<DiagnosticItem> SnapshotDiagnostics();
    IReadOnlyCollection<NetworkEntry> SnapshotNetwork();
    void Clear();
}

public interface IOllamaClient
{
    Task<OllamaStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AiRecommendation> AnalyzeAsync(DiagnosticItem diagnostic, string? model, string? projectContext = null,
        bool fastMode = false, CancellationToken cancellationToken = default);
    Task<AiRecommendation> AnalyzeIncidentAsync(DeveloperIncident incident, string? model, string? projectContext = null,
        bool fastMode = false, CancellationToken cancellationToken = default);
}

public interface ISecretRedactor
{
    string Redact(string input);
}

public interface ITlsInspector
{
    Task<SecuritySnapshot> InspectAsync(Uri uri, IReadOnlyDictionary<string, string>? responseHeaders = null,
        CancellationToken cancellationToken = default);
}

public interface IProjectLinkService : IDisposable
{
    event EventHandler<DiagnosticItem>? LogDiagnostic;
    ProjectLink Link(string host, string path);
    ProjectLink? Find(string host);
    IReadOnlyList<ProjectLink> GetAll();
    ProjectDiagnosticContext BuildDiagnosticContext(ProjectLink link, DiagnosticItem diagnostic);
    ProjectDiagnosticContext BuildIncidentContext(ProjectLink link, DeveloperIncident incident);
    void Remove(string host);
}

public interface ISettingsStore
{
    BrowserSettings Load();
    void Save(BrowserSettings settings);
}

public interface ISecretStore
{
    string? GetSecret(string name);
    void SetSecret(string name, string value);
    void DeleteSecret(string name);
}
