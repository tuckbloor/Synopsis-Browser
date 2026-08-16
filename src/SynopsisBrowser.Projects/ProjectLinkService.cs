using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.Projects;

public sealed class ProjectLinkService : IProjectLinkService
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".php", ".js", ".jsx", ".ts", ".tsx", ".vue", ".cs", ".razor", ".blade.php", ".html", ".css", ".scss", ".json"
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "vendor", "bin", "obj", ".idea", ".vs", "storage\\framework", "storage/framework"
    };

    private readonly string _storageFile;
    private readonly ConcurrentDictionary<string, ProjectLink> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<DiagnosticItem>? LogDiagnostic;

    public ProjectLinkService(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _storageFile = Path.Combine(appDataDirectory, "projects.json");
        Load();
        // Refresh metadata on startup so links created by older Synopsis builds pick up
        // framework/log detection improvements automatically.
        foreach (var link in _links.Values)
        {
            RefreshLinkMetadata(link);
            StartWatcher(link);
        }
        Save();
    }

    public ProjectLink Link(string host, string path)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

        var fullPath = Path.GetFullPath(path);
        var framework = DetectFramework(fullPath);
        var logFile = DetectLogFile(fullPath, framework);
        var link = new ProjectLink
        {
            Host = host,
            Path = fullPath,
            Framework = framework,
            LogFile = logFile,
            LinkedAt = DateTimeOffset.Now
        };

        _links[host] = link;
        Save();
        StartWatcher(link);
        return link;
    }

    public ProjectLink? Find(string host) => _links.TryGetValue(host, out var link) ? link : null;
    public IReadOnlyList<ProjectLink> GetAll() => _links.Values.OrderBy(x => x.Host).ToArray();

    public ProjectDiagnosticContext BuildDiagnosticContext(ProjectLink link, DiagnosticItem diagnostic)
    {
        var summary = $"Linked project: {link.Host} -> {link.Path}; Framework: {link.Framework}";
        var evidence = new StringBuilder();
        evidence.AppendLine(summary);

        var logTail = !string.IsNullOrWhiteSpace(link.LogFile) && File.Exists(link.LogFile)
            ? ReadTail(link.LogFile, 6000)
            : string.Empty;

        var sourceMap = SourceMapResolver.TryResolve(link.Path, diagnostic);
        var match = sourceMap is not null
            ? (sourceMap.Path, sourceMap.Line)
            : FindBestSourceFile(link, diagnostic);

        if (match is null && !string.IsNullOrWhiteSpace(logTail))
        {
            // A plain HTTP 500 often contains no source filename, while the linked
            // Laravel/.NET/Node log written at the same time does. Use that recent log
            // only to resolve the referenced source file, then read the real code below.
            var augmented = diagnostic with
            {
                Details = string.Join("\n", new[] { diagnostic.Details, logTail }.Where(x => !string.IsNullOrWhiteSpace(x)))
            };
            match = FindBestSourceFile(link, augmented);
        }

        string? sourcePath = null;
        int? sourceLine = sourceMap?.Line ?? diagnostic.Line ?? match?.Line;
        var sourceExcerpt = string.Empty;
        var sourceResolution = sourceMap?.Description ?? string.Empty;

        if (match is not null)
        {
            sourcePath = match.Value.Path;
            sourceLine ??= match.Value.Line;
            sourceExcerpt = ReadSourceExcerpt(sourcePath, sourceLine);
            evidence.AppendLine();
            if (!string.IsNullOrWhiteSpace(sourceResolution)) evidence.AppendLine(sourceResolution);
            evidence.AppendLine($"Referenced source file: {sourcePath}");
            if (sourceLine is > 0) evidence.AppendLine($"Referenced line: {sourceLine}");
            evidence.AppendLine("Source excerpt:");
            evidence.AppendLine(sourceExcerpt);
        }
        else
        {
            evidence.AppendLine();
            evidence.AppendLine("No exact source file could be resolved from the browser diagnostic.");

            var related = FindRelatedSourceEvidence(link, diagnostic);
            if (!string.IsNullOrWhiteSpace(related.Evidence))
            {
                sourceExcerpt = related.Evidence;
                sourceResolution = "Bounded linked-project source search";
                evidence.AppendLine();
                evidence.AppendLine("Likely related project source matches:");
                evidence.AppendLine(related.Evidence);
                sourcePath = related.FirstPath;
                sourceLine = related.FirstLine;
            }
        }

        if (!string.IsNullOrWhiteSpace(logTail))
        {
            evidence.AppendLine();
            evidence.AppendLine($"Recent linked server log ({link.LogFile}):");
            evidence.AppendLine(logTail);
        }

        return new ProjectDiagnosticContext(
            summary,
            evidence.ToString().Trim(),
            sourcePath,
            sourceLine,
            sourceExcerpt,
            logTail,
            sourceResolution);
    }

    public ProjectDiagnosticContext BuildIncidentContext(ProjectLink link, DeveloperIncident incident)
    {
        var signals = new StringBuilder();
        signals.AppendLine($"Incident {incident.Id} with {incident.SignalCount} related signal(s):");
        foreach (var signal in incident.Signals.OrderBy(x => x.Timestamp))
        {
            signals.AppendLine($"- [{signal.Timestamp:HH:mm:ss.fff}] {signal.Kind}/{signal.Severity}: {signal.Title}");
            signals.AppendLine($"  {signal.Message}");
            if (!string.IsNullOrWhiteSpace(signal.Url)) signals.AppendLine($"  URL: {signal.Url}");
            if (!string.IsNullOrWhiteSpace(signal.Source)) signals.AppendLine($"  Source: {signal.Source}:{signal.Line}:{signal.Column}");
        }

        // Prefer the signal with the strongest direct source clue, then server/JS evidence,
        // but include every correlated signal in Details so source/log resolution can use it.
        var best = incident.Signals
            .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Source) && x.Line is > 0)
            .ThenByDescending(x => x.Kind == DiagnosticKind.Server)
            .ThenByDescending(x => x.Kind == DiagnosticKind.JavaScript)
            .ThenByDescending(x => x.Kind == DiagnosticKind.Http)
            .FirstOrDefault() ?? incident.Primary;

        var aggregate = best with
        {
            Title = incident.Title,
            Message = incident.Message,
            Details = string.Join("\n\n", new[] { best.Details, signals.ToString() }.Where(x => !string.IsNullOrWhiteSpace(x)))
        };

        var context = BuildDiagnosticContext(link, aggregate);
        var combinedEvidence = $"{signals}\n{context.Evidence}".Trim();
        return context with
        {
            Summary = $"Incident review | {context.Summary}",
            Evidence = combinedEvidence
        };
    }

    public void Remove(string host)
    {
        _links.TryRemove(host, out _);
        if (_watchers.TryRemove(host, out var watcher)) watcher.Dispose();
        _offsets.TryRemove(host, out _);
        Save();
    }

    private static (string Path, int? Line)? FindBestSourceFile(ProjectLink link, DiagnosticItem diagnostic)
    {
        var root = Path.GetFullPath(link.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var text = string.Join("\n", new[] { diagnostic.Source, diagnostic.Message, diagnostic.Details }.Where(x => !string.IsNullOrWhiteSpace(x)));

        foreach (var candidate in ExtractAbsoluteCandidates(text))
        {
            try
            {
                var full = Path.GetFullPath(candidate.Path);
                if (File.Exists(full) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return (full, candidate.Line);
            }
            catch { }
        }

        foreach (var relative in ExtractRelativeCandidates(text))
        {
            var mapped = Path.Combine(link.Path, relative.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(mapped)) return (Path.GetFullPath(mapped), relative.Line);
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.Source))
        {
            if (Uri.TryCreate(diagnostic.Source, UriKind.Absolute, out var sourceUri))
            {
                var relative = Uri.UnescapeDataString(sourceUri.AbsolutePath).TrimStart('/');
                var mapped = Path.Combine(link.Path, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(mapped)) return (Path.GetFullPath(mapped), diagnostic.Line);

                var sourceFileName = Path.GetFileName(sourceUri.AbsolutePath);
                var byName = FindByFileName(link.Path, sourceFileName);
                if (byName is not null) return (byName, diagnostic.Line);

                // Vite/webpack production assets commonly look like app-ABC123.js while
                // the linked source is resources/js/app.js. Try the unhashed base name.
                var extension = Path.GetExtension(sourceFileName);
                var stem = Path.GetFileNameWithoutExtension(sourceFileName);
                var unhashedStem = Regex.Replace(stem, @"-[A-Za-z0-9_-]{6,}$", string.Empty);
                if (!unhashedStem.Equals(stem, StringComparison.OrdinalIgnoreCase))
                {
                    byName = FindByFileName(link.Path, unhashedStem + extension);
                    // Without a source map the generated bundle line is not a trustworthy
                    // line in the original source. Resolve the likely file, but do not
                    // pretend the generated line maps directly to it.
                    if (byName is not null) return (byName, null);
                }
            }
            else if (File.Exists(diagnostic.Source))
            {
                return (Path.GetFullPath(diagnostic.Source), diagnostic.Line);
            }
            else
            {
                var byName = FindByFileName(link.Path, Path.GetFileName(diagnostic.Source));
                if (byName is not null) return (byName, diagnostic.Line);
            }
        }

        return null;
    }

    private static IEnumerable<(string Path, int? Line)> ExtractAbsoluteCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var regex = new Regex(@"(?<path>[A-Za-z]:\\[^\r\n""'<>|]+?\.(?:php|js|jsx|ts|tsx|vue|cs|razor|html))(?::(?<line>\d+))?", RegexOptions.IgnoreCase);
        foreach (Match match in regex.Matches(text))
        {
            int? line = int.TryParse(match.Groups["line"].Value, out var parsed) ? parsed : null;
            yield return (match.Groups["path"].Value.Trim(), line);
        }
    }

    private static IEnumerable<(string Path, int? Line)> ExtractRelativeCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var regex = new Regex(@"(?<path>(?:app|routes|resources|src|public|tests|config|database)[/\\][A-Za-z0-9_./\\-]+\.(?:php|js|jsx|ts|tsx|vue|cs|razor|html))(?::(?<line>\d+))?", RegexOptions.IgnoreCase);
        foreach (Match match in regex.Matches(text))
        {
            int? line = int.TryParse(match.Groups["line"].Value, out var parsed) ? parsed : null;
            yield return (match.Groups["path"].Value, line);
        }
    }


    private static (string Evidence, string? FirstPath, int? FirstLine) FindRelatedSourceEvidence(ProjectLink link, DiagnosticItem diagnostic)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(diagnostic.Url) && Uri.TryCreate(diagnostic.Url, UriKind.Absolute, out var uri))
        {
            foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = Regex.Replace(Uri.UnescapeDataString(segment), @"[^A-Za-z0-9_-]", string.Empty);
                if (token.Length >= 4 && !int.TryParse(token, out _) && !token.Equals("api", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(token);
            }
        }

        var genericTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "error", "failed", "request", "network", "exception", "undefined", "cannot", "server", "navigation", "connection", "aborted"
        };
        foreach (Match match in Regex.Matches($"{diagnostic.Title} {diagnostic.Message}", @"[A-Za-z_][A-Za-z0-9_]{4,}"))
        {
            var token = match.Value;
            if (!genericTokens.Contains(token)) tokens.Add(token);
            if (tokens.Count >= 8) break;
        }

        if (tokens.Count == 0) return (string.Empty, null, null);

        var searchRoots = new List<string>();
        void AddRoot(params string[] parts)
        {
            var dir = Path.Combine(new[] { link.Path }.Concat(parts).ToArray());
            if (Directory.Exists(dir)) searchRoots.Add(dir);
        }

        if (link.Framework.Equals("Laravel", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(link.Path, "artisan")))
        {
            AddRoot("routes");
            AddRoot("app", "Http");
            AddRoot("resources", "js");
            AddRoot("resources", "views");
        }
        else
        {
            AddRoot("src");
            AddRoot("routes");
            AddRoot("resources");
            AddRoot("app");
        }
        if (searchRoots.Count == 0) searchRoots.Add(link.Path);

        var output = new StringBuilder();
        string? firstPath = null;
        int? firstLine = null;
        var matchesFound = 0;
        var filesSeen = 0;

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in EnumerateSourceFilesBounded(root, 1400))
            {
                if (++filesSeen > 1800 || matchesFound >= 3) break;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 800_000) continue;
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!tokens.Any(t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                        firstPath ??= file;
                        firstLine ??= i + 1;
                        output.AppendLine($"File: {file}");
                        output.AppendLine($"Match near line {i + 1}:");
                        var start = Math.Max(0, i - 3);
                        var end = Math.Min(lines.Length - 1, i + 5);
                        for (var n = start; n <= end; n++)
                            output.AppendLine($"{(n == i ? ">>" : "  ")} {n + 1,5}: {lines[n]}");
                        output.AppendLine();
                        matchesFound++;
                        break;
                    }
                }
                catch { }
            }
            if (filesSeen > 1800 || matchesFound >= 3) break;
        }

        return (output.ToString().Trim(), firstPath, firstLine);
    }

    private static IEnumerable<string> EnumerateSourceFilesBounded(string root, int maxDirectories)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < maxDirectories)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToArray();
                dirs = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch { continue; }

            foreach (var file in files)
                if (IsSourceFile(file)) yield return file;

            foreach (var sub in dirs)
            {
                var relative = Path.GetRelativePath(root, sub);
                if (!ShouldIgnoreDirectory(relative, Path.GetFileName(sub))) stack.Push(sub);
            }
        }
    }

    private static void RefreshLinkMetadata(ProjectLink link)
    {
        try
        {
            if (!Directory.Exists(link.Path)) return;
            link.Framework = DetectFramework(link.Path);
            link.LogFile = DetectLogFile(link.Path, link.Framework);
        }
        catch { }
    }

    private static string? FindByFileName(string root, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var visited = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && visited < 6000)
        {
            var dir = stack.Pop();
            visited++;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase) && IsSourceFile(file))
                        return file;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var relative = Path.GetRelativePath(root, sub);
                    if (ShouldIgnoreDirectory(relative, Path.GetFileName(sub))) continue;
                    stack.Push(sub);
                }
            }
            catch { }
        }
        return null;
    }

    private static bool IsSourceFile(string path)
    {
        var lower = path.ToLowerInvariant();
        return SourceExtensions.Any(ext => lower.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIgnoreDirectory(string relative, string name)
        => IgnoredDirectories.Contains(name) || IgnoredDirectories.Contains(relative.Replace(Path.DirectorySeparatorChar, '/'));

    private static string ReadSourceExcerpt(string path, int? line)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0) return "(file is empty)";
            var target = Math.Clamp((line ?? 1) - 1, 0, lines.Length - 1);
            var start = Math.Max(0, target - 14);
            var end = Math.Min(lines.Length - 1, target + 15);
            var builder = new StringBuilder();
            for (var i = start; i <= end; i++)
            {
                var marker = i == target ? ">>" : "  ";
                builder.AppendLine($"{marker} {i + 1,5}: {lines[i]}");
            }
            return builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"(Synopsis could not read source excerpt: {ex.Message})";
        }
    }

    private static string ReadTail(string path, int maxChars)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - Math.Max(maxChars * 2L, 8192));
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return text.Length <= maxChars ? text.Trim() : text[^maxChars..].Trim();
        }
        catch { return string.Empty; }
    }

    private static string DetectFramework(string path)
    {
        if (File.Exists(Path.Combine(path, "artisan"))) return "Laravel";
        if (File.Exists(Path.Combine(path, "package.json")))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "package.json")));
                var text = doc.RootElement.ToString();
                if (text.Contains("next", StringComparison.OrdinalIgnoreCase)) return "Next.js";
                if (text.Contains("vite", StringComparison.OrdinalIgnoreCase)) return "Vite";
                if (text.Contains("react", StringComparison.OrdinalIgnoreCase)) return "React/Node";
                if (text.Contains("vue", StringComparison.OrdinalIgnoreCase)) return "Vue/Node";
                if (text.Contains("express", StringComparison.OrdinalIgnoreCase)) return "Express/Node";
            }
            catch { }
            return "Node";
        }
        if (Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).Any()) return ".NET";
        if (File.Exists(Path.Combine(path, "composer.json"))) return "PHP/Composer";
        return "Unknown";
    }

    private static string? DetectLogFile(string path, string framework)
    {
        if (framework == "Laravel")
        {
            var preferred = Path.Combine(path, "storage", "logs", "laravel.log");
            if (File.Exists(preferred)) return preferred;
            var logsDir = Path.Combine(path, "storage", "logs");
            if (Directory.Exists(logsDir))
                return Directory.EnumerateFiles(logsDir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return Directory.EnumerateFiles(path, "*.log", SearchOption.TopDirectoryOnly).FirstOrDefault();
    }

    private void StartWatcher(ProjectLink link)
    {
        if (string.IsNullOrWhiteSpace(link.LogFile) || !File.Exists(link.LogFile)) return;
        if (_watchers.TryRemove(link.Host, out var old)) old.Dispose();

        var file = link.LogFile;
        _offsets[link.Host] = new FileInfo(file).Length;
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Changed += (_, _) => ReadNewLogContent(link);
        _watchers[link.Host] = watcher;
    }

    private void ReadNewLogContent(ProjectLink link)
    {
        if (link.LogFile is null) return;
        try
        {
            var offset = _offsets.GetValueOrDefault(link.Host, 0);
            using var stream = new FileStream(link.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset) offset = 0;
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            _offsets[link.Host] = stream.Position;
            if (string.IsNullOrWhiteSpace(content)) return;

            var tail = content.Length > 12000 ? content[^12000..] : content;
            var severity = tail.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || tail.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticSeverity.Error : DiagnosticSeverity.Info;
            LogDiagnostic?.Invoke(this, DiagnosticItem.Create(severity, DiagnosticKind.Server,
                $"{link.Framework} server log", tail.Trim(), source: link.LogFile, details: tail.Trim()));
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storageFile)) return;
            var items = JsonSerializer.Deserialize<List<ProjectLink>>(File.ReadAllText(_storageFile)) ?? [];
            foreach (var link in items) _links[link.Host] = link;
        }
        catch { }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_links.Values.OrderBy(x => x.Host), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storageFile, json);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
}
