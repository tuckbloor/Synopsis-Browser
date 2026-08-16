using System.Collections.Concurrent;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.Diagnostics;

public sealed class DiagnosticHub : IDiagnosticHub
{
    private readonly ConcurrentQueue<DiagnosticItem> _diagnostics = new();
    private readonly ConcurrentDictionary<string, NetworkEntry> _network = new(StringComparer.Ordinal);

    public event EventHandler<DiagnosticItem>? DiagnosticAdded;
    public event EventHandler<ConsoleEntry>? ConsoleAdded;
    public event EventHandler<NetworkEntry>? NetworkChanged;

    public void Publish(DiagnosticItem item)
    {
        _diagnostics.Enqueue(item);
        while (_diagnostics.Count > 2000 && _diagnostics.TryDequeue(out _)) { }
        DiagnosticAdded?.Invoke(this, item);
    }

    public void Publish(ConsoleEntry entry) => ConsoleAdded?.Invoke(this, entry);

    public void Publish(NetworkEntry entry)
    {
        _network[entry.RequestId] = entry;
        NetworkChanged?.Invoke(this, entry);
    }

    public IReadOnlyCollection<DiagnosticItem> SnapshotDiagnostics() => _diagnostics.ToArray();
    public IReadOnlyCollection<NetworkEntry> SnapshotNetwork() => _network.Values.OrderByDescending(x => x.StartedAt).ToArray();

    public void Clear()
    {
        while (_diagnostics.TryDequeue(out _)) { }
        _network.Clear();
    }
}
