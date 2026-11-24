using System.Text;

namespace FutureSpeed.Core.Utils;

/// <summary>
/// Path helpers hardened for Windows: long paths (&gt; 260 chars), UNC shares,
/// containment checks (so nothing is ever written outside the chosen destination)
/// and safe unique-name generation.
/// </summary>
public static class PathUtils
{
    public const string ExtendedPrefix = @"\\?\";
    public const string ExtendedUncPrefix = @"\\?\UNC\";
    public const string PartialSuffix = ".fspart";

    /// <summary>
    /// Adds the \\?\ prefix so Win32 skips MAX_PATH normalization. Safe to call twice.
    /// Only applied to rooted absolute paths.
    /// </summary>
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return path;
        if (!Path.IsPathFullyQualified(path)) return path;

        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return ExtendedUncPrefix + normalized[2..];

        return ExtendedPrefix + normalized;
    }

    public static string FromExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
            return @"\\" + path[ExtendedUncPrefix.Length..];
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return path[ExtendedPrefix.Length..];
        return path;
    }

    /// <summary>Normalizes for comparison: full path, no trailing separator, case-insensitive ready.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var clean = FromExtendedPath(path.Trim()).Replace('/', '\\');
        try
        {
            clean = Path.GetFullPath(clean);
        }
        catch
        {
            // Keep the raw value; callers treat a non-normalizable path as invalid.
        }

        if (clean.Length > 3 && clean.EndsWith('\\'))
            clean = clean.TrimEnd('\\');
        return clean;
    }

    public static bool PathsEqual(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> is inside (or equal to) <paramref name="root"/>.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var r = Normalize(root);
        var c = Normalize(candidate);
        if (r.Length == 0 || c.Length == 0) return false;
        if (string.Equals(r, c, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = r.EndsWith('\\') ? r : r + '\\';
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Guards against path traversal: combines <paramref name="relative"/> onto
    /// <paramref name="root"/> and throws when the result escapes the root.
    /// </summary>
    public static string CombineSecure(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        relative ??= string.Empty;

        var cleanRelative = relative.Replace('/', '\\').TrimStart('\\');
        if (Path.IsPathRooted(cleanRelative))
            throw new UnauthorizedAccessException($"Rooted relative path rejected: {relative}");

        var combined = Normalize(Path.Combine(Normalize(root), cleanRelative));
        if (!IsInside(root, combined))
            throw new UnauthorizedAccessException($"Path escapes the destination root: {relative}");

        return combined;
    }

    /// <summary>Relative path of <paramref name="path"/> under <paramref name="root"/>, using backslashes.</summary>
    public static string GetRelativePath(string root, string path)
    {
        var r = Normalize(root);
        var p = Normalize(path);
        var rel = Path.GetRelativePath(r, p).Replace('/', '\\');
        return rel == "." ? string.Empty : rel;
    }

    /// <summary>
    /// A name that does not exist yet: "report.pdf" -> "report (2).pdf" -> "report (3).pdf".
    /// <paramref name="exists"/> is injected so this is unit-testable without a disk.
    /// </summary>
    public static string GetUniquePath(string desiredPath, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(desiredPath)) return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var i = 2; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!exists(candidate)) return candidate;
        }

        throw new IOException("Could not generate a unique file name.");
    }

    /// <summary>Temporary name used while a file is being written.</summary>
    public static string GetPartialPath(string finalPath) => finalPath + PartialSuffix;

    public static bool IsPartialPath(string path)
        => path.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects a destination that would destroy the source: identical paths, or a
    /// destination nested inside one of the sources.
    /// </summary>
    public static IReadOnlyList<string> ValidateDestinationAgainstSources(
        string destinationRoot,
        IEnumerable<string> sources)
    {
        var errors = new List<string>();
        var dest = Normalize(destinationRoot);
        if (dest.Length == 0)
        {
            errors.Add("Destination is empty.");
            return errors;
        }

        foreach (var source in sources)
        {
            var src = Normalize(source);
            if (src.Length == 0) continue;

            if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Destination is the same as the source: {src}");
                continue;
            }

            // Copying a folder into one of its own descendants would recurse forever.
            if (IsInside(src, dest))
                errors.Add($"Destination is inside the source folder: {src}");
        }

        return errors;
    }

    /// <summary>Strips characters Windows does not allow in a file name.</summary>
    public static string SanitizeFileName(string name, char replacement = '_')
    {
        if (string.IsNullOrEmpty(name)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? replacement : ch);
        return sb.ToString().Trim();
    }
}
