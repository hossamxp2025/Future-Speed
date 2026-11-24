using System.Windows;
using Microsoft.Win32;

namespace FutureSpeed.App.Services;

public interface IThemeService
{
    AppTheme Current { get; }
    bool IsDark { get; }
    void Apply(AppTheme theme);
}

/// <summary>
/// Swaps the palette dictionary at runtime. Styles reference brushes through
/// DynamicResource, so switching repaints the open window with no reload.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string LightPalette = "/FutureSpeed;component/Themes/Palette.Light.xaml";
    private const string DarkPalette = "/FutureSpeed;component/Themes/Palette.Dark.xaml";

    public AppTheme Current { get; private set; } = AppTheme.Light;

    public bool IsDark { get; private set; }

    public void Apply(AppTheme theme)
    {
        Current = theme;
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark()
        };

        IsDark = dark;

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null || dictionaries.Count == 0) return;

        var palette = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkPalette : LightPalette, UriKind.Relative)
        };

        // Index 0 is always the palette; the style dictionary that follows is untouched.
        dictionaries[0] = palette;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // AppsUseLightTheme: 0 = dark, 1 = light.
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
