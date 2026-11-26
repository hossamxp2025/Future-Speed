using System.Drawing;
using System.IO;
using System.Media;
using System.Windows.Forms;
using FutureSpeed.App.Localization;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

public interface INotificationService
{
    /// <summary>Raises a Windows notification (and optional sound) for a finished job.</summary>
    /// <param name="failedFiles">Shown in the text, so the notification itself says how many files are missing.</param>
    void NotifyCompleted(string jobName, bool hadErrors, int failedFiles = 0);

    void NotifyFailed(string jobName, string? reason);

    void NotifyInfo(string message);

    /// <summary>
    /// Tells the user the app keeps working in the notification area, and wires the tray
    /// icon so a click brings the given window back.
    /// </summary>
    void ShowBackgroundHint(string message, Action restore);
}

/// <summary>
/// Windows notifications via a tray <see cref="NotifyIcon"/>. This is the dependency-free
/// way for an unpackaged desktop app to raise a real system notification: on Windows 10/11
/// a balloon tip is rendered as a toast and kept in the Action Center.
/// </summary>
public sealed class NotificationService : INotificationService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly ILogger<NotificationService> _logger;
    private NotifyIcon? _icon;
    private Action? _restore;

    public NotificationService(
        ISettingsService settings,
        ILocalizationService localization,
        ILogger<NotificationService> logger)
    {
        _settings = settings;
        _localization = localization;
        _logger = logger;
    }

    private NotifyIcon? Icon
    {
        get
        {
            if (_icon is not null) return _icon;

            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "FutureSpeed.exe");
                _icon = new NotifyIcon
                {
                    Icon = File.Exists(iconPath)
                        ? System.Drawing.Icon.ExtractAssociatedIcon(iconPath)
                        : SystemIcons.Application,
                    Visible = true,
                    Text = _localization["App_Name"]
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tray icon unavailable; notifications will be silent.");
            }

            return _icon;
        }
    }

    public void NotifyCompleted(string jobName, bool hadErrors, int failedFiles = 0)
    {
        var text = hadErrors
            ? _localization.Format("Notify_CompletedWithErrors", jobName, failedFiles)
            : _localization.Format("Notify_Completed", jobName);
        Show(text, hadErrors ? ToolTipIcon.Warning : ToolTipIcon.Info);

        if (_settings.Current.PlaySoundWhenFinished)
            PlaySound(hadErrors);
    }

    public void NotifyFailed(string jobName, string? reason)
    {
        var text = _localization.Format("Notify_Failed", jobName);
        if (!string.IsNullOrWhiteSpace(reason))
            text += " — " + reason;

        Show(text, ToolTipIcon.Error);

        if (_settings.Current.PlaySoundWhenFinished)
            PlaySound(hadErrors: true);
    }

    public void NotifyInfo(string message) => Show(message, ToolTipIcon.Info);

    public void ShowBackgroundHint(string message, Action restore)
    {
        ArgumentNullException.ThrowIfNull(restore);

        var icon = Icon;
        if (icon is null)
        {
            // No tray icon available: never strand the user with a hidden window.
            restore();
            return;
        }

        if (_restore is not null)
            icon.MouseClick -= OnTrayClick;

        _restore = restore;
        icon.MouseClick += OnTrayClick;

        try
        {
            icon.BalloonTipTitle = _localization["Notify_Title"];
            icon.BalloonTipText = message;
            icon.BalloonTipIcon = ToolTipIcon.Info;
            icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the background hint.");
        }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var restore = _restore;
        _restore = null;

        if (_icon is not null)
            _icon.MouseClick -= OnTrayClick;

        restore?.Invoke();
    }

    private void Show(string message, ToolTipIcon severity)
    {
        if (!_settings.Current.ShowWindowsNotification) return;

        try
        {
            var icon = Icon;
            if (icon is null) return;

            icon.BalloonTipTitle = _localization["Notify_Title"];
            icon.BalloonTipText = message;
            icon.BalloonTipIcon = severity;
            icon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the notification.");
        }
    }

    private void PlaySound(bool hadErrors)
    {
        try
        {
            if (hadErrors) SystemSounds.Exclamation.Play();
            else SystemSounds.Asterisk.Play();
        }
        catch
        {
            // Audio is a nicety, never a failure.
        }
    }

    public void Dispose()
    {
        if (_icon is null) return;

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // Shutting down.
        }

        _icon = null;
    }
}
