using System.IO;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.Diagnostics;

/// <summary>
/// Groups browser/server diagnostics that are likely symptoms of the same developer incident.
/// Exact network request IDs win; otherwise Synopsis uses a deliberately short time window plus
/// URL/source affinity so unrelated errors are not aggressively merged.
/// </summary>
public sealed class IncidentCorrelator
{
    private sealed class State
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public string? ExactCorrelationId { get; set; }
        public List<DiagnosticItem> Signals { get; } = [];
    }

    private readonly List<State> _states = [];
    private readonly object _gate = new();

    public DeveloperIncident Add(DiagnosticItem item)
    {
        lock (_gate)
        {
            Prune(item.Timestamp);
            var state = FindState(item) ?? CreateState(item);
            state.Signals.Add(item);
            state.LastSeen = item.Timestamp;
            if (!string.IsNullOrWhiteSpace(item.CorrelationId)) state.ExactCorrelationId ??= item.CorrelationId;
            return Snapshot(state);
        }
    }

    public void Clear()
    {
        lock (_gate) _states.Clear();
    }

    private State? FindState(DiagnosticItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CorrelationId))
        {
            var exact = _states.LastOrDefault(x => string.Equals(x.ExactCorrelationId, item.CorrelationId, StringComparison.Ordinal));
            if (exact is not null) return exact;
        }

        var itemUrl = NormalizeUrl(item.Url);
        var itemAuthority = NormalizeAuthority(item.Url);
        var itemSource = NormalizeSource(item.Source);

        foreach (var state in _states.OrderByDescending(x => x.LastSeen))
        {
            var age = Math.Abs((item.Timestamp - state.LastSeen).TotalSeconds);
            if (age > 4.0) continue;

            var last = state.Signals.LastOrDefault();
            if (last is null) continue;

            var lastUrl = NormalizeUrl(last.Url);
            var lastAuthority = NormalizeAuthority(last.Url);
            var lastSource = NormalizeSource(last.Source);

            if (!string.IsNullOrWhiteSpace(itemUrl) && itemUrl == lastUrl) return state;
            if (age <= 1.5 && !string.IsNullOrWhiteSpace(itemAuthority) && itemAuthority == lastAuthority
                && IsBrowserFailureFamily(item.Kind) && IsBrowserFailureFamily(last.Kind)) return state;
            if (!string.IsNullOrWhiteSpace(itemSource) && itemSource == lastSource) return state;

            // HTTP and Network signals emitted from the same failed request often arrive
            // milliseconds apart even when one contains a slightly different URL shape.
            if (age <= 1.5 && IsTransportPair(item.Kind, last.Kind)) return state;

            // A linked server log entry immediately following a browser error is usually the
            // backend half of that incident. Keep this window intentionally tight.
            if (age <= 5.0 && (item.Kind == DiagnosticKind.Server || last.Kind == DiagnosticKind.Server)) return state;

            // Console error + uncaught JS exception are commonly duplicate symptoms.
            if (age <= 1.0 && IsJavaScriptPair(item.Kind, last.Kind) && TextLooksRelated(item.Message, last.Message)) return state;
        }

        return null;
    }

    private State CreateState(DiagnosticItem item)
    {
        var state = new State
        {
            FirstSeen = item.Timestamp,
            LastSeen = item.Timestamp,
            ExactCorrelationId = string.IsNullOrWhiteSpace(item.CorrelationId) ? null : item.CorrelationId
        };
        _states.Add(state);
        return state;
    }

    private static DeveloperIncident Snapshot(State state)
    {
        var signals = state.Signals.OrderBy(x => x.Timestamp).ToArray();
        var primary = signals.OrderByDescending(SignalPriority).ThenByDescending(x => x.Timestamp).First();
        var severity = signals.MaxBy(x => (int)x.Severity)?.Severity ?? primary.Severity;
        var url = signals.Select(x => x.Url).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var source = signals.Select(x => x.Source).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return new DeveloperIncident(
            state.Id,
            state.FirstSeen,
            state.LastSeen,
            severity,
            primary.Title,
            primary.Message,
            url,
            source,
            primary,
            signals);
    }

    private static int SignalPriority(DiagnosticItem item) => item.Kind switch
    {
        DiagnosticKind.Ssl => 120,
        DiagnosticKind.Server => 110,
        DiagnosticKind.JavaScript => 100,
        DiagnosticKind.Http => 90,
        DiagnosticKind.Console => 80,
        DiagnosticKind.Network => 70,
        DiagnosticKind.Project => 60,
        _ => 10
    };

    private static bool IsTransportPair(DiagnosticKind a, DiagnosticKind b)
        => (a is DiagnosticKind.Http or DiagnosticKind.Network) && (b is DiagnosticKind.Http or DiagnosticKind.Network);

    private static bool IsJavaScriptPair(DiagnosticKind a, DiagnosticKind b)
        => (a is DiagnosticKind.JavaScript or DiagnosticKind.Console) && (b is DiagnosticKind.JavaScript or DiagnosticKind.Console);

    private static bool IsBrowserFailureFamily(DiagnosticKind kind)
        => kind is DiagnosticKind.JavaScript or DiagnosticKind.Console or DiagnosticKind.Http or DiagnosticKind.Network;

    private static bool TextLooksRelated(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var left = a.Trim();
        var right = b.Trim();
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
        if (left.Length > 20 && right.Contains(left[..Math.Min(60, left.Length)], StringComparison.OrdinalIgnoreCase)) return true;
        if (right.Length > 20 && left.Contains(right[..Math.Min(60, right.Length)], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}".TrimEnd('/').ToLowerInvariant();
    }

    private static string? NormalizeAuthority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}".ToLowerInvariant();
    }

    private static string? NormalizeSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Path.GetFileName(uri.AbsolutePath).ToLowerInvariant();
            return Path.GetFileName(value).ToLowerInvariant();
        }
        catch { return value.ToLowerInvariant(); }
    }

    private void Prune(DateTimeOffset now)
        => _states.RemoveAll(x => (now - x.LastSeen).TotalMinutes > 10);
}
