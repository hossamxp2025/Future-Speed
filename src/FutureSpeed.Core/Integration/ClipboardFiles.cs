using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Integration;

/// <summary>What the clipboard was asked to do with the files it holds.</summary>
public enum ClipboardOperation
{
    /// <summary>The clipboard holds no files at all (text, an image, nothing).</summary>
    None = 0,
    Copy = 1,
    Move = 2
}

public sealed record ClipboardFilePayload(ClipboardOperation Operation, IReadOnlyList<string> Files)
{
    public static ClipboardFilePayload Empty { get; } = new(ClipboardOperation.None, Array.Empty<string>());

    public bool HasFiles => Operation != ClipboardOperation.None && Files.Count > 0;

    public TransferMode Mode => Operation == ClipboardOperation.Move ? TransferMode.Move : TransferMode.Copy;
}

/// <summary>
/// Reads the file list from the clipboard. Deliberately narrow: the app only ever looks
/// at CF_HDROP (a file drop list) plus the "Preferred DropEffect" flag, and never at
/// text, images or any other clipboard content.
/// </summary>
public interface IClipboardFileReader
{
    ClipboardFilePayload Read();

    /// <summary>Places a file list on the clipboard with the given effect (for the copy/cut verbs).</summary>
    bool Write(IReadOnlyList<string> files, ClipboardOperation operation);
}

/// <summary>
/// Turns a raw clipboard reading into a validated payload. Kept free of Windows APIs so
/// the rules (ignore non-file clipboards, drop missing paths, de-duplicate) are testable.
/// </summary>
public static class ClipboardPayloadFactory
{
    /// <summary>DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2; Explorer sets this when cutting.</summary>
    public const int DropEffectCopy = 1;
    public const int DropEffectMove = 2;

    public static ClipboardOperation FromDropEffect(int? dropEffect)
        => dropEffect switch
        {
            null => ClipboardOperation.Copy,              // no flag at all: Explorer means copy
            var e when (e & DropEffectMove) == DropEffectMove => ClipboardOperation.Move,
            var e when (e & DropEffectCopy) == DropEffectCopy => ClipboardOperation.Copy,
            _ => ClipboardOperation.Copy
        };

    public static ClipboardFilePayload Create(
        IEnumerable<string>? rawPaths,
        int? dropEffect,
        Func<string, bool>? exists = null)
    {
        if (rawPaths is null) return ClipboardFilePayload.Empty;

        exists ??= p => File.Exists(PathUtils.ToExtendedPath(p)) || Directory.Exists(PathUtils.ToExtendedPath(p));

        var files = rawPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathUtils.Normalize)
            .Where(p => p.Length > 0 && exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return files.Count == 0
            ? ClipboardFilePayload.Empty
            : new ClipboardFilePayload(FromDropEffect(dropEffect), files);
    }
}
