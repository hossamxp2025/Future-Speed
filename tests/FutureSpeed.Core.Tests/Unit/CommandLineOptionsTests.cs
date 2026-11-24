using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void No_arguments_means_open_the_main_window()
    {
        var options = CommandLineOptions.Parse(Array.Empty<string>());

        Assert.Equal(CommandLineAction.None, options.Action);
        Assert.False(options.IsTransfer);
        Assert.False(options.Compact);
    }

    [Fact]
    public void Copy_with_a_destination_is_parsed()
    {
        var options = CommandLineOptions.Parse(new[]
        {
            "--copy", @"C:\data\a.txt", @"C:\data\b.txt", "--destination", @"D:\backup", "--compact"
        });

        Assert.Equal(CommandLineAction.Copy, options.Action);
        Assert.Equal(TransferMode.Copy, options.Mode);
        Assert.Equal(2, options.Sources.Count);
        Assert.Equal(@"D:\backup", options.Destination);
        Assert.True(options.Compact);
    }

    [Fact]
    public void Move_maps_to_the_move_mode()
    {
        var options = CommandLineOptions.Parse(new[] { "--move", @"C:\data\a.txt", "--destination", @"D:\x" });

        Assert.Equal(CommandLineAction.Move, options.Action);
        Assert.Equal(TransferMode.Move, options.Mode);
    }

    [Fact]
    public void The_explorer_verb_aliases_work()
    {
        Assert.Equal(CommandLineAction.Copy, CommandLineOptions.Parse(new[] { "--explorer-copy", @"C:\a" }).Action);
        Assert.Equal(CommandLineAction.Move, CommandLineOptions.Parse(new[] { "--explorer-move", @"C:\a" }).Action);
    }

    [Fact]
    public void Paths_with_spaces_survive_parsing()
    {
        var path = @"C:\Program Files\My Folder\report final.pdf";
        var options = CommandLineOptions.Parse(new[] { "--copy", path, "--destination", @"D:\My Backups" });

        Assert.Equal(path, options.Sources[0]);
        Assert.Equal(@"D:\My Backups", options.Destination);
    }

    [Fact]
    public void Arabic_paths_survive_parsing()
    {
        var source = @"D:\مشاريعي\تقرير نهائي.pdf";
        var destination = @"E:\نسخة احتياطية";

        var options = CommandLineOptions.Parse(new[] { "--copy", source, "--destination", destination });

        Assert.Equal(source, options.Sources[0]);
        Assert.Equal(destination, options.Destination);
    }

    [Fact]
    public void Quoted_paths_lose_their_quotes()
    {
        var options = CommandLineOptions.Parse(new[] { "--copy", "\"C:\\data\\a.txt\"", "--destination", @"D:\x" });
        Assert.Equal(@"C:\data\a.txt", options.Sources[0]);
    }

    [Fact]
    public void The_same_source_twice_is_collapsed_into_one()
    {
        var options = CommandLineOptions.Parse(new[]
        {
            "--copy", @"C:\data\a.txt", @"C:\DATA\A.TXT", @"C:\data\b.txt", "--destination", @"D:\x"
        });

        Assert.Equal(2, options.Sources.Count);
    }

    [Fact]
    public void Paste_to_takes_the_folder_as_destination()
    {
        var options = CommandLineOptions.Parse(new[] { "--paste-to", @"D:\target", "--compact" });

        Assert.Equal(CommandLineAction.PasteTo, options.Action);
        Assert.Equal(@"D:\target", options.Destination);
        Assert.True(options.Compact);
        Assert.True(options.IsTransfer);
    }

    [Fact]
    public void A_long_selection_can_be_passed_in_a_file_instead_of_the_command_line()
    {
        // Windows caps a command line at ~32k characters, so Explorer selections are
        // also accepted as a list file.
        var lines = new[] { @"C:\data\1.bin", @"C:\data\2.bin", "", @"""C:\data\3 with space.bin""" };

        var options = CommandLineOptions.Parse(
            new[] { "--copy", "--sources-file", @"C:\temp\list.txt", "--destination", @"D:\x" },
            _ => lines);

        Assert.Equal(3, options.Sources.Count);
        Assert.Contains(@"C:\data\3 with space.bin", options.Sources);
    }

    [Fact]
    public void A_missing_source_list_is_reported_instead_of_crashing()
    {
        var options = CommandLineOptions.Parse(
            new[] { "--copy", "--sources-file", @"C:\nope\list.txt", "--destination", @"D:\x" },
            _ => throw new FileNotFoundException("missing"));

        Assert.Equal(CommandLineAction.Invalid, options.Action);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void Copy_without_sources_is_invalid()
    {
        var options = CommandLineOptions.Parse(new[] { "--copy", "--destination", @"D:\x" });

        Assert.Equal(CommandLineAction.Invalid, options.Action);
        Assert.False(options.IsTransfer);
    }

    [Fact]
    public void Bare_paths_are_treated_as_a_copy()
    {
        var options = CommandLineOptions.Parse(new[] { @"C:\data\a.txt" });
        Assert.Equal(CommandLineAction.Copy, options.Action);
        Assert.Single(options.Sources);
    }

    [Fact]
    public void Registration_commands_are_recognised()
    {
        var register = CommandLineOptions.Parse(new[] { "--register-explorer", "--all-users" });
        Assert.Equal(CommandLineAction.RegisterExplorer, register.Action);
        Assert.True(register.AllUsers);

        var unregister = CommandLineOptions.Parse(new[] { "--unregister-explorer" });
        Assert.Equal(CommandLineAction.UnregisterExplorer, unregister.Action);
        Assert.False(unregister.AllUsers);
    }

    [Fact]
    public void An_unknown_switch_is_reported()
    {
        var options = CommandLineOptions.Parse(new[] { "--definitely-not-a-switch" });

        Assert.Equal(CommandLineAction.Invalid, options.Action);
        Assert.Contains("Unknown option", options.Error);
    }

    [Fact]
    public void Help_is_recognised()
        => Assert.Equal(CommandLineAction.ShowHelp, CommandLineOptions.Parse(new[] { "--help" }).Action);
}
