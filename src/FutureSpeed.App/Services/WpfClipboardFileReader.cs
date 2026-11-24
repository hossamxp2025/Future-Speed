using System.IO;
using System.Windows;
using FutureSpeed.Core.Integration;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

/// <summary>
/// Reads the Windows clipboard, and only ever its file list.
///
/// Two formats are involved: CF_HDROP (the file paths) and the shell's
/// "Preferred DropEffect" stream, whose first byte distinguishes a cut from a copy.
/// Text, images and every other clipboard format are ignored and never logged.
/// </summary>
public sealed class WpfClipboardFileReader : IClipboardFileReader
{
    private const string PreferredDropEffect = "Preferred DropEffect";

    private readonly ILogger<WpfClipboardFileReader> _logger;

    public WpfClipboardFileReader(ILogger<WpfClipboardFileReader> logger) => _logger = logger;

    public ClipboardFilePayload Read()
    {
        try
        {
            // No file list: this clipboard is not ours to touch.
            if (!Clipboard.ContainsFileDropList())
                return ClipboardFilePayload.Empty;

            var paths = Clipboard.GetFileDropList().Cast<string?>().Where(p => p is not null).Select(p => p!).ToList();
            var effect = ReadDropEffect();

            var payload = ClipboardPayloadFactory.Create(paths, effect);

            // Log the count only: clipboard contents are the user's business.
            _logger.LogInformation(
                "Clipboard holds {Count} file system item(s), operation {Operation}.",
                payload.Files.Count, payload.Operation);

            return payload;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the clipboard file list.");
            return ClipboardFilePayload.Empty;
        }
    }

    public bool Write(IReadOnlyList<string> files, ClipboardOperation operation)
    {
        if (files.Count == 0) return false;

        try
        {
            var collection = new System.Collections.Specialized.StringCollection();
            foreach (var file in files) collection.Add(file);

            var data = new DataObject();
            data.SetFileDropList(collection);

            var effect = operation == ClipboardOperation.Move
                ? ClipboardPayloadFactory.DropEffectMove
                : ClipboardPayloadFactory.DropEffectCopy;

            data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(effect)));

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not place the file list on the clipboard.");
            return false;
        }
    }

    private static int? ReadDropEffect()
    {
        try
        {
            if (Clipboard.GetData(PreferredDropEffect) is not MemoryStream stream) return null;

            var buffer = new byte[4];
            var read = stream.Read(buffer, 0, 4);
            return read == 4 ? BitConverter.ToInt32(buffer, 0) : null;
        }
        catch
        {
            // The flag is optional; its absence means "copy".
            return null;
        }
    }
}
