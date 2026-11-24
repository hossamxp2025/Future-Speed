using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Integration;

public enum CommandLineAction
{
    /// <summary>No arguments: start the normal application window.</summary>
    None = 0,
    Copy = 1,
    Move = 2,
    /// <summary>Paste whatever the clipboard holds into a folder.</summary>
    PasteTo = 3,
    RegisterExplorer = 4,
    UnregisterExplorer = 5,
    ShowHelp = 6,
    Invalid = 7
}

/// <summary>
/// Parses the command line used by the Explorer verbs.
///
/// Supported forms:
///   --copy  &lt;path&gt; [&lt;path&gt; ...] [--destination &lt;folder&gt;] [--compact]
///   --move  &lt;path&gt; [&lt;path&gt; ...] [--destination &lt;folder&gt;] [--compact]
///   --paste-to &lt;folder&gt; [--compact]
///   --sources-file &lt;file&gt;        (one path per line; avoids the command-line length limit)
///   --register-explorer [--all-users]
///   --unregister-explorer
///   --help
///
/// Paths with spaces arrive already unquoted by the shell, and Arabic paths need no
/// special treatment because the whole pipeline is UTF-16 end to end.
/// </summary>
public sealed class CommandLineOptions
{
    public CommandLineAction Action { get; private init; } = CommandLineAction.None;

    public IReadOnlyList<string> Sources { get; private init; } = Array.Empty<string>();

    public string? Destination { get; private init; }

    /// <summary>Show only the small transfer window, never the main window.</summary>
    public bool Compact { get; private init; }

    /// <summary>Register the Explorer verbs for every user (needs elevation).</summary>
    public bool AllUsers { get; private init; }

    public string? Error { get; private init; }

    public bool IsTransfer => Action is CommandLineAction.Copy or CommandLineAction.Move or CommandLineAction.PasteTo;

    public TransferMode Mode => Action == CommandLineAction.Move ? TransferMode.Move : TransferMode.Copy;

    public static CommandLineOptions None { get; } = new();

    public static CommandLineOptions Parse(IReadOnlyList<string>? args, Func<string, IEnumerable<string>>? readLines = null)
    {
        if (args is null || args.Count == 0)
            return None;

        readLines ??= path => File.ReadAllLines(path);

        var action = CommandLineAction.None;
        var sources = new List<string>();
        string? destination = null;
        var compact = false;
        var allUsers = false;
        string? error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i]?.Trim() ?? string.Empty;
            if (arg.Length == 0) continue;

            switch (arg.ToLowerInvariant())
            {
                case "--copy":
                case "--explorer-copy":
                    action = CommandLineAction.Copy;
                    break;

                case "--move":
                case "--explorer-move":
                    action = CommandLineAction.Move;
                    break;

                case "--paste-to":
                    action = CommandLineAction.PasteTo;
                    if (i + 1 < args.Count && !IsSwitch(args[i + 1]))
                        destination = args[++i];
                    else
                        error = "--paste-to needs a destination folder.";
                    break;

                case "--destination":
                case "--dest":
                case "--to":
                    if (i + 1 < args.Count && !IsSwitch(args[i + 1]))
                        destination = args[++i];
                    else
                        error = "--destination needs a folder.";
                    break;

                case "--sources-file":
                    if (i + 1 < args.Count && !IsSwitch(args[i + 1]))
                    {
                        var listFile = args[++i];
                        try
                        {
                            foreach (var line in readLines(listFile))
                            {
                                var path = line.Trim().Trim('"');
                                if (path.Length > 0) sources.Add(path);
                            }
                        }
                        catch (Exception ex)
                        {
                            error = $"Could not read the source list: {ex.Message}";
                        }
                    }
                    else
                    {
                        error = "--sources-file needs a file path.";
                    }

                    break;

                case "--compact":
                    compact = true;
                    break;

                case "--all-users":
                    allUsers = true;
                    break;

                case "--register-explorer":
                    action = CommandLineAction.RegisterExplorer;
                    break;

                case "--unregister-explorer":
                    action = CommandLineAction.UnregisterExplorer;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    action = CommandLineAction.ShowHelp;
                    break;

                default:
                    if (IsSwitch(arg))
                        error ??= $"Unknown option: {arg}";
                    else
                        sources.Add(arg.Trim('"'));
                    break;
            }
        }

        // Normalise and de-duplicate: Explorer can hand us the same item twice, and a
        // job must never contain one source twice.
        var cleanSources = sources
            .Select(PathUtils.Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (error is not null)
        {
            return new CommandLineOptions
            {
                Action = CommandLineAction.Invalid,
                Error = error,
                Sources = cleanSources,
                Destination = destination,
                Compact = compact
            };
        }

        if (action is CommandLineAction.Copy or CommandLineAction.Move && cleanSources.Count == 0)
        {
            return new CommandLineOptions
            {
                Action = CommandLineAction.Invalid,
                Error = "No source files were given.",
                Compact = compact
            };
        }

        if (action == CommandLineAction.None && cleanSources.Count > 0)
        {
            // Bare paths (for example a drag onto the executable) mean "copy these".
            action = CommandLineAction.Copy;
        }

        return new CommandLineOptions
        {
            Action = action,
            Sources = cleanSources,
            Destination = destination is null ? null : PathUtils.Normalize(destination),
            Compact = compact,
            AllUsers = allUsers
        };
    }

    private static bool IsSwitch(string? value)
        => value is not null && (value.StartsWith("--", StringComparison.Ordinal) || value is "-h" or "/?");

    public const string HelpText = """
        Future Speed File Transfer

          FutureSpeed.exe                                  open the main window
          FutureSpeed.exe --copy <paths...> --destination <folder> [--compact]
          FutureSpeed.exe --move <paths...> --destination <folder> [--compact]
          FutureSpeed.exe --paste-to <folder> [--compact]
          FutureSpeed.exe --sources-file <list.txt> --destination <folder>
          FutureSpeed.exe --register-explorer [--all-users]
          FutureSpeed.exe --unregister-explorer
        """;
}
