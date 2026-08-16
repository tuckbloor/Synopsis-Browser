using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.Projects;

internal sealed record SourceMapMatch(string Path, int? Line, int? Column, string Description);

/// <summary>
/// Minimal Source Map v3 resolver for browser exception locations. It decodes only as far as
/// needed for one generated line/column and never executes project code.
/// </summary>
internal static class SourceMapResolver
{
    private const string Base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    public static SourceMapMatch? TryResolve(string projectRoot, DiagnosticItem diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.Source) || diagnostic.Line is not > 0) return null;
        if (!Uri.TryCreate(diagnostic.Source, UriKind.Absolute, out var sourceUri)) return null;

        var generated = FindGeneratedAsset(projectRoot, sourceUri);
        if (generated is null) return null;
        var mapFile = FindMapFile(generated);
        if (mapFile is null) return null;

        try
        {
            using var map = JsonDocument.Parse(File.ReadAllText(mapFile));
            var root = map.RootElement;
            if (!root.TryGetProperty("mappings", out var mappingsEl) || mappingsEl.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("sources", out var sourcesEl) || sourcesEl.ValueKind != JsonValueKind.Array) return null;

            var mappings = mappingsEl.GetString() ?? string.Empty;
            var targetGeneratedLine = diagnostic.Line.Value - 1;
            var targetGeneratedColumn = Math.Max(0, (diagnostic.Column ?? 1) - 1);
            var mapped = DecodeToOriginal(mappings, targetGeneratedLine, targetGeneratedColumn);
            if (mapped is null || mapped.Value.SourceIndex < 0 || mapped.Value.SourceIndex >= sourcesEl.GetArrayLength()) return null;

            var sourceValue = sourcesEl[mapped.Value.SourceIndex].GetString();
            if (string.IsNullOrWhiteSpace(sourceValue)) return null;
            var sourceRoot = root.TryGetProperty("sourceRoot", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;
            var originalPath = ResolveOriginalPath(projectRoot, mapFile, sourceRoot, sourceValue);
            if (originalPath is null || !File.Exists(originalPath)) return null;

            return new SourceMapMatch(
                originalPath,
                mapped.Value.OriginalLine + 1,
                mapped.Value.OriginalColumn + 1,
                $"Source map: {Path.GetFileName(generated)}:{diagnostic.Line}:{diagnostic.Column ?? 1} -> {Path.GetRelativePath(projectRoot, originalPath)}:{mapped.Value.OriginalLine + 1}:{mapped.Value.OriginalColumn + 1}");
        }
        catch
        {
            return null;
        }
    }

    private static string? FindGeneratedAsset(string root, Uri uri)
    {
        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        foreach (var candidate in new[]
        {
            Path.Combine(root, relative),
            Path.Combine(root, "public", relative)
        })
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        var name = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var dir in new[] { Path.Combine(root, "public", "build", "assets"), Path.Combine(root, "dist", "assets"), Path.Combine(root, "build", "assets") })
        {
            if (!Directory.Exists(dir)) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string? FindMapFile(string generated)
    {
        var adjacent = generated + ".map";
        if (File.Exists(adjacent)) return adjacent;
        try
        {
            using var stream = new FileStream(generated, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(8192, stream.Length);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            _ = stream.Read(buffer, 0, length);
            var tail = System.Text.Encoding.UTF8.GetString(buffer);
            var match = Regex.Match(tail, @"sourceMappingURL=(?<url>[^\s*]+)");
            if (!match.Success) return null;
            var value = match.Groups["url"].Value.Trim();
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            var mapped = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(generated)!, Uri.UnescapeDataString(value)));
            return File.Exists(mapped) ? mapped : null;
        }
        catch { return null; }
    }

    private static string? ResolveOriginalPath(string root, string mapFile, string? sourceRoot, string source)
    {
        var cleaned = source.Replace("webpack:///", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("vite:///", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("file:///", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimStart('/', '\\');
        var combinedSource = string.IsNullOrWhiteSpace(sourceRoot) ? cleaned : Path.Combine(sourceRoot!, cleaned);

        foreach (var candidate in new[]
        {
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mapFile)!, combinedSource.Replace('/', Path.DirectorySeparatorChar))),
            Path.GetFullPath(Path.Combine(root, combinedSource.Replace('/', Path.DirectorySeparatorChar))),
            Path.GetFullPath(Path.Combine(root, cleaned.Replace('/', Path.DirectorySeparatorChar)))
        })
        {
            if (File.Exists(candidate) && IsInside(root, candidate)) return candidate;
        }

        // Source-map paths can carry extra ../ segments relative to build tooling. Search by
        // a useful suffix while staying inside the linked project.
        var segments = cleaned.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(4, segments.Length); take >= 1; take--)
        {
            var suffix = Path.Combine(segments[^take..]);
            foreach (var top in new[] { "resources", "src", "app", "frontend" })
            {
                var baseDir = Path.Combine(root, top);
                if (!Directory.Exists(baseDir)) continue;
                try
                {
                    var found = Directory.EnumerateFiles(baseDir, Path.GetFileName(suffix), SearchOption.AllDirectories)
                        .FirstOrDefault(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                    if (found is not null) return Path.GetFullPath(found);
                }
                catch { }
            }
        }
        return null;
    }

    private static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static (int SourceIndex, int OriginalLine, int OriginalColumn)? DecodeToOriginal(string mappings, int targetLine, int targetColumn)
    {
        var sourceIndex = 0;
        var originalLine = 0;
        var originalColumn = 0;
        var lines = mappings.Split(';');
        var maxLine = Math.Min(targetLine, lines.Length - 1);
        (int SourceIndex, int OriginalLine, int OriginalColumn)? best = null;

        for (var line = 0; line <= maxLine; line++)
        {
            var generatedColumn = 0;
            var segments = lines[line].Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                var values = DecodeSegment(segment);
                if (values.Count == 0) continue;
                generatedColumn += values[0];
                if (values.Count >= 4)
                {
                    sourceIndex += values[1];
                    originalLine += values[2];
                    originalColumn += values[3];
                    if (line == targetLine && generatedColumn <= targetColumn)
                        best = (sourceIndex, originalLine, originalColumn);
                }
            }
        }
        return best;
    }

    private static List<int> DecodeSegment(string segment)
    {
        var result = new List<int>(5);
        var value = 0;
        var shift = 0;
        foreach (var ch in segment)
        {
            var digit = Base64Chars.IndexOf(ch);
            if (digit < 0) continue;
            var continuation = (digit & 32) != 0;
            digit &= 31;
            value += digit << shift;
            if (continuation)
            {
                shift += 5;
                continue;
            }
            var negative = (value & 1) == 1;
            var decoded = value >> 1;
            result.Add(negative ? -decoded : decoded);
            value = 0;
            shift = 0;
        }
        return result;
    }
}
