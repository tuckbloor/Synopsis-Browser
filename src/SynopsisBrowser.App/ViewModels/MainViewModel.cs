using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _address = string.Empty;
    private string _pageTitle = "New Tab";
    private string _pageStatus = "READY";
    private string _httpsStatus = "TLS";
    private string _ollamaStatus = "AI OFFLINE";
    private string? _selectedModel;
    private DiagnosticItem? _selectedDiagnostic;
    private DeveloperIncident? _selectedIncident;
    private NetworkEntry? _selectedNetwork;
    private SecuritySnapshot _security = new();
    private AiRecommendation? _aiRecommendation;
    private ProjectLink? _project;
    private string _apiResponseBody = string.Empty;
    private bool _devToolsVisible;
    private string _aiPanelStatus = "Click an error in the AI Code Review list. Synopsis will gather linked source code and recent server logs, then Ollama will review the error and propose a possible fix.";
    private string _aiProjectStatus = "No linked project context for the selected error.";
    private string _aiSourceEvidence = string.Empty;
    private string? _aiSourcePath;
    private int? _aiSourceLine;
    private string _aiSourcePreview = string.Empty;
    private string _aiLogEvidence = string.Empty;
    private string _aiSourceResolution = string.Empty;

    public ObservableCollection<ConsoleEntry> ConsoleEntries { get; } = [];
    public ObservableCollection<NetworkEntry> NetworkEntries { get; } = [];
    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = [];
    public ObservableCollection<DiagnosticItem> AiDiagnostics { get; } = []; // legacy raw-error list retained for V1 compatibility
    public ObservableCollection<DeveloperIncident> AiIncidents { get; } = [];
    public ObservableCollection<string> OllamaModels { get; } = [];

    public string Address { get => _address; set => Set(ref _address, value); }
    public string PageTitle { get => _pageTitle; set => Set(ref _pageTitle, value); }
    public string PageStatus { get => _pageStatus; set => Set(ref _pageStatus, value); }
    public string HttpsStatus { get => _httpsStatus; set => Set(ref _httpsStatus, value); }
    public string OllamaStatus { get => _ollamaStatus; set => Set(ref _ollamaStatus, value); }
    public string? SelectedModel { get => _selectedModel; set => Set(ref _selectedModel, value); }
    public DiagnosticItem? SelectedDiagnostic { get => _selectedDiagnostic; set => Set(ref _selectedDiagnostic, value); }
    public DeveloperIncident? SelectedIncident { get => _selectedIncident; set => Set(ref _selectedIncident, value); }
    public NetworkEntry? SelectedNetwork { get => _selectedNetwork; set => Set(ref _selectedNetwork, value); }
    public SecuritySnapshot Security { get => _security; set { if (Set(ref _security, value)) OnPropertyChanged(nameof(SecurityScoreText)); } }
    public AiRecommendation? AiRecommendation { get => _aiRecommendation; set => Set(ref _aiRecommendation, value); }
    public ProjectLink? Project { get => _project; set => Set(ref _project, value); }
    public string ApiResponseBody { get => _apiResponseBody; set => Set(ref _apiResponseBody, value); }
    public bool DevToolsVisible { get => _devToolsVisible; set => Set(ref _devToolsVisible, value); }
    public string AiPanelStatus { get => _aiPanelStatus; set => Set(ref _aiPanelStatus, value); }
    public string AiProjectStatus { get => _aiProjectStatus; set => Set(ref _aiProjectStatus, value); }
    public string AiSourceEvidence { get => _aiSourceEvidence; set => Set(ref _aiSourceEvidence, value); }
    public string? AiSourcePath { get => _aiSourcePath; set => Set(ref _aiSourcePath, value); }
    public int? AiSourceLine { get => _aiSourceLine; set => Set(ref _aiSourceLine, value); }
    public string AiSourcePreview { get => _aiSourcePreview; set => Set(ref _aiSourcePreview, value); }
    public string AiLogEvidence { get => _aiLogEvidence; set => Set(ref _aiLogEvidence, value); }
    public string AiSourceResolution { get => _aiSourceResolution; set => Set(ref _aiSourceResolution, value); }
    public string AiErrorCountText => $"{AiIncidents.Count} INCIDENTS | {AiIncidents.Sum(x => x.SignalCount)} ERROR SIGNALS";

    public int ErrorCount => Diagnostics.Count(x => x.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical);
    public int WarningCount => Diagnostics.Count(x => x.Severity == DiagnosticSeverity.Warning);
    public string ErrorSummary => $"{ErrorCount} ERRORS";
    public string WarningSummary => $"{WarningCount} WARNINGS";
    public string SecurityScoreText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Security.Url)) return "Not inspected";
            var checks = new[] { Security.IsHttps, Security.CertificateValid == true, Security.HasCsp, Security.HasHsts, Security.HasXFrameOptions, Security.HasReferrerPolicy };
            return $"{checks.Count(x => x)} / {checks.Length} checks passed";
        }
    }

    public int RequestCount => NetworkEntries.Count;
    public int FailedRequestCount => NetworkEntries.Count(x => x.Failed || (x.Status ?? 0) >= 400);
    public string TotalTransferredText
    {
        get
        {
            var bytes = NetworkEntries.Sum(x => x.EncodedBytes ?? 0);
            return bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:F2} MB" : $"{bytes / 1024d:F1} KB";
        }
    }
    public string SlowestRequestText
    {
        get
        {
            var slowest = NetworkEntries.Where(x => x.DurationMs.HasValue).OrderByDescending(x => x.DurationMs).FirstOrDefault();
            return slowest is null ? "-" : $"{slowest.DurationMs:F0} ms | {slowest.Url}";
        }
    }

    public void NotifyNetworkMetrics()
    {
        OnPropertyChanged(nameof(RequestCount));
        OnPropertyChanged(nameof(FailedRequestCount));
        OnPropertyChanged(nameof(TotalTransferredText));
        OnPropertyChanged(nameof(SlowestRequestText));
    }

    public void NotifyCounts()
    {
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorSummary));
        OnPropertyChanged(nameof(WarningSummary));
        OnPropertyChanged(nameof(AiErrorCountText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
