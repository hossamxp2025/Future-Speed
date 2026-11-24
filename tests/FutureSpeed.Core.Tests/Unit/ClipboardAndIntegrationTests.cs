using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class ClipboardPayloadTests
{
    private static bool AlwaysExists(string _) => true;

    [Fact]
    public void A_clipboard_without_files_produces_nothing()
    {
        var payload = ClipboardPayloadFactory.Create(null, null);

        Assert.False(payload.HasFiles);
        Assert.Equal(ClipboardOperation.None, payload.Operation);
    }

    [Fact]
    public void An_empty_file_list_produces_nothing()
    {
        var payload = ClipboardPayloadFactory.Create(Array.Empty<string>(), ClipboardPayloadFactory.DropEffectCopy, AlwaysExists);
        Assert.False(payload.HasFiles);
    }

    [Fact]
    public void Paths_that_no_longer_exist_are_dropped()
    {
        var payload = ClipboardPayloadFactory.Create(
            new[] { @"C:\gone\a.txt", @"C:\here\b.txt" },
            ClipboardPayloadFactory.DropEffectCopy,
            p => p.Contains(@"\here\", StringComparison.OrdinalIgnoreCase));

        Assert.True(payload.HasFiles);
        Assert.Single(payload.Files);
    }

    [Fact]
    public void A_cut_is_recognised_as_a_move()
    {
        var payload = ClipboardPayloadFactory.Create(
            new[] { @"C:\a.txt" }, ClipboardPayloadFactory.DropEffectMove, AlwaysExists);

        Assert.Equal(ClipboardOperation.Move, payload.Operation);
        Assert.Equal(TransferMode.Move, payload.Mode);
    }

    [Fact]
    public void A_copy_is_recognised_as_a_copy()
    {
        var payload = ClipboardPayloadFactory.Create(
            new[] { @"C:\a.txt" }, ClipboardPayloadFactory.DropEffectCopy, AlwaysExists);

        Assert.Equal(ClipboardOperation.Copy, payload.Operation);
        Assert.Equal(TransferMode.Copy, payload.Mode);
    }

    [Fact]
    public void A_missing_drop_effect_defaults_to_copy()
    {
        // Explorer omits the flag for a plain copy; assuming "move" here would delete
        // the user's originals.
        Assert.Equal(ClipboardOperation.Copy, ClipboardPayloadFactory.FromDropEffect(null));
    }

    [Theory]
    [InlineData(5, ClipboardOperation.Copy)]   // COPY | LINK
    [InlineData(6, ClipboardOperation.Move)]   // MOVE | LINK
    public void Combined_drop_effect_flags_are_resolved(int effect, ClipboardOperation expected)
        => Assert.Equal(expected, ClipboardPayloadFactory.FromDropEffect(effect));

    [Fact]
    public void Duplicates_in_the_clipboard_are_collapsed()
    {
        var payload = ClipboardPayloadFactory.Create(
            new[] { @"C:\a.txt", @"C:\A.TXT" }, ClipboardPayloadFactory.DropEffectCopy, AlwaysExists);

        Assert.Single(payload.Files);
    }
}

/// <summary>An in-memory registry, so integration install/remove is fully testable.</summary>
internal sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistryStore(IntegrationScope scope = IntegrationScope.CurrentUser) => Scope = scope;

    public IntegrationScope Scope { get; }

    public IReadOnlyCollection<string> AllKeys => _keys.Keys;

    public void SetValue(string keyPath, string? valueName, string value)
    {
        if (!_keys.TryGetValue(keyPath, out var values))
            _keys[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        values[valueName ?? string.Empty] = value;
    }

    public string? GetValue(string keyPath, string? valueName)
        => _keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName ?? string.Empty, out var v)
            ? v
            : null;

    public bool KeyExists(string keyPath) => _keys.ContainsKey(keyPath);

    public void DeleteKeyTree(string keyPath)
    {
        foreach (var key in _keys.Keys
                     .Where(k => k.Equals(keyPath, StringComparison.OrdinalIgnoreCase) ||
                                 k.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            _keys.Remove(key);
        }
    }
}

public sealed class ExplorerIntegrationTests
{
    private const string Exe = @"C:\Program Files\Future Speed\FutureSpeed.exe";

    [Fact]
    public void The_plan_covers_files_folders_and_folder_background()
    {
        var plan = ExplorerIntegrationService.BuildPlan(Exe, IntegrationLabels.English, IntegrationScope.CurrentUser);
        var keys = plan.Select(e => e.KeyPath).Distinct().ToList();

        Assert.Contains(keys, k => k.Contains(@"\*\shell\FutureSpeed.Copy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(keys, k => k.Contains(@"\Directory\shell\FutureSpeed.Move", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(keys, k => k.Contains(@"\Directory\Background\shell\FutureSpeed.Paste", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_verb_asks_explorer_for_one_process_per_selection()
    {
        var plan = ExplorerIntegrationService.BuildPlan(Exe, IntegrationLabels.English, IntegrationScope.CurrentUser);

        var verbKeys = plan.Where(e => e.ValueName == "MultiSelectModel").ToList();

        Assert.Equal(6, verbKeys.Count);
        Assert.All(verbKeys, e => Assert.Equal("Player", e.Value));
    }

    [Fact]
    public void The_commands_quote_the_executable_so_spaces_are_safe()
    {
        var plan = ExplorerIntegrationService.BuildPlan(Exe, IntegrationLabels.English, IntegrationScope.CurrentUser);
        var commands = plan.Where(e => e.KeyPath.EndsWith(@"\command", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Equal(6, commands.Count);
        Assert.All(commands, c => Assert.StartsWith($"\"{Exe}\"", c.Value));
    }

    [Fact]
    public void The_paste_verbs_pass_the_folder_and_ask_for_the_compact_window()
    {
        var plan = ExplorerIntegrationService.BuildPlan(Exe, IntegrationLabels.Arabic, IntegrationScope.CurrentUser);

        var pasteCommands = plan
            .Where(e => e.KeyPath.Contains("FutureSpeed.Paste", StringComparison.OrdinalIgnoreCase) &&
                        e.KeyPath.EndsWith(@"\command", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(2, pasteCommands.Count);
        Assert.All(pasteCommands, c => Assert.Contains("--paste-to", c.Value));
        Assert.All(pasteCommands, c => Assert.Contains("--compact", c.Value));
        Assert.Contains(pasteCommands, c => c.Value.Contains("\"%V\""));
    }

    [Fact]
    public void Arabic_labels_are_written_to_the_menu()
    {
        var plan = ExplorerIntegrationService.BuildPlan(Exe, IntegrationLabels.Arabic, IntegrationScope.CurrentUser);
        var labels = plan.Where(e => e.ValueName == "MUIVerb").Select(e => e.Value).Distinct().ToList();

        Assert.Contains("نسخ بواسطة Future Speed", labels);
        Assert.Contains("نقل بواسطة Future Speed", labels);
        Assert.Contains("لصق بواسطة Future Speed", labels);
    }

    [Fact]
    public void Install_then_status_reports_installed()
    {
        var store = new FakeRegistryStore();
        var service = new ExplorerIntegrationService(store);

        service.Install(Exe, IntegrationLabels.English);
        var status = service.GetStatus(Exe);

        Assert.True(status.IsInstalled);
        Assert.Equal(Exe, status.RegisteredExecutable);
    }

    [Fact]
    public void Uninstall_leaves_no_key_behind()
    {
        var store = new FakeRegistryStore();
        var service = new ExplorerIntegrationService(store);

        service.Install(Exe, IntegrationLabels.English);
        Assert.NotEmpty(store.AllKeys);

        service.Uninstall();

        Assert.Empty(store.AllKeys);
        Assert.Equal(IntegrationState.NotInstalled, service.GetStatus(Exe).State);
    }

    [Fact]
    public void Uninstalling_twice_is_harmless()
    {
        var store = new FakeRegistryStore();
        var service = new ExplorerIntegrationService(store);

        service.Install(Exe, IntegrationLabels.English);
        service.Uninstall();
        service.Uninstall();

        Assert.Empty(store.AllKeys);
    }

    [Fact]
    public void Installing_twice_does_not_duplicate_or_orphan_keys()
    {
        var store = new FakeRegistryStore();
        var service = new ExplorerIntegrationService(store);

        service.Install(Exe, IntegrationLabels.English);
        var afterFirst = store.AllKeys.Count;

        service.Install(Exe, IntegrationLabels.Arabic);

        Assert.Equal(afterFirst, store.AllKeys.Count);
        Assert.True(service.GetStatus(Exe).IsInstalled);
    }

    [Fact]
    public void A_moved_executable_is_reported_as_stale()
    {
        var store = new FakeRegistryStore();
        var service = new ExplorerIntegrationService(store);

        service.Install(@"C:\old\FutureSpeed.exe", IntegrationLabels.English);
        var status = service.GetStatus(@"C:\new\FutureSpeed.exe");

        Assert.Equal(IntegrationState.Stale, status.State);
        Assert.False(status.IsInstalled);
    }

    [Fact]
    public void A_half_removed_installation_is_reported_as_stale()
    {
        var store = new FakeRegistryStore();
        var service = new ExplorerIntegrationService(store);

        service.Install(Exe, IntegrationLabels.English);
        store.DeleteKeyTree(ExplorerIntegrationService.VerbKeys(IntegrationScope.CurrentUser)[^1]);

        Assert.Equal(IntegrationState.Stale, service.GetStatus(Exe).State);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\a.exe\" --copy \"%1\"", @"C:\Program Files\App\a.exe")]
    [InlineData("C:\\app\\a.exe --copy", @"C:\app\a.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_registered_executable_is_extracted_from_the_command(string? command, string? expected)
        => Assert.Equal(expected, ExplorerIntegrationService.ExtractExecutable(command));

    [Fact]
    public void A_registry_failure_never_escapes_as_an_unhandled_crash()
    {
        // The manager swallows store failures; here the service itself is expected to
        // surface them so the caller can log and carry on.
        var service = new ExplorerIntegrationService(new ThrowingStore());

        Assert.Throws<UnauthorizedAccessException>(() => service.Install(Exe, IntegrationLabels.English));
    }

    private sealed class ThrowingStore : IRegistryStore
    {
        public IntegrationScope Scope => IntegrationScope.AllUsers;
        public void SetValue(string keyPath, string? valueName, string value) => throw new UnauthorizedAccessException();
        public string? GetValue(string keyPath, string? valueName) => null;
        public bool KeyExists(string keyPath) => false;
        public void DeleteKeyTree(string keyPath) { }
    }
}
