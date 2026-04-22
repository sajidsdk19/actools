using AcAgent.Models;
using AcAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AcAgent;

/// <summary>
/// Code-behind for the main launcher window.
///
/// UI states:
///   Idle     → MainPanel visible, SessionOverlay + CompleteOverlay hidden
///   Running  → SessionOverlay visible (countdown timer active)
///   Complete → CompleteOverlay visible (session stats + export button)
/// </summary>
public partial class MainWindow : Window
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly GameLauncherService _launcher;
    private readonly ReportingService    _reporting;

    // ── State ─────────────────────────────────────────────────────────────────
    private List<string> _allCars   = new();
    private List<string> _allTracks = new();
    private Session?     _lastSession;

    // ── Countdown timer ───────────────────────────────────────────────────────
    private DispatcherTimer? _countdownTimer;
    private DateTime         _sessionStartTime;
    private int              _sessionDurationMinutes;

    // ── Cancellation ─────────────────────────────────────────────────────────
    private CancellationTokenSource? _sessionCts;

    public MainWindow()
    {
        InitializeComponent();
        _launcher  = App.Services.GetRequiredService<GameLauncherService>();
        _reporting = App.Services.GetRequiredService<ReportingService>();

        PcLabel.Text     = $"PC: {Environment.MachineName}";
        DriverNameBox.Text = Environment.UserName;

        Loaded += MainWindow_Loaded;
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadContent();
    }

    private void LoadContent()
    {
        try
        {
            _allCars   = _launcher.ListCars().ToList();
            _allTracks = _launcher.ListTracks().ToList();

            RefreshCarList(string.Empty);
            RefreshTrackList(string.Empty);

            CarCountLabel.Text   = $"{_allCars.Count} cars installed";
            TrackCountLabel.Text = $"{_allTracks.Count} tracks installed";

            SetStatus("Ready — select a car and track to begin", isOk: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading content: {ex.Message}", isOk: false);
        }
    }

    private void RefreshCarList(string filter)
    {
        var items = string.IsNullOrWhiteSpace(filter)
            ? _allCars
            : _allCars.Where(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        CarList.ItemsSource = items;
    }

    private void RefreshTrackList(string filter)
    {
        var items = string.IsNullOrWhiteSpace(filter)
            ? _allTracks
            : _allTracks.Where(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        TrackList.ItemsSource = items;
    }

    // ── Event Handlers ────────────────────────────────────────────────────────

    private void CarSearch_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshCarList(CarSearch.Text);

    private void TrackSearch_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshTrackList(TrackSearch.Text);

    private void CarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectionPreview();

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Show available layouts for multi-layout tracks
        if (TrackList.SelectedItem is string trackId)
        {
            var layouts = _launcher.ListTrackLayouts(trackId);
            LayoutLabel.Text = layouts.Count > 0
                ? $"Layouts: {string.Join(", ", layouts)}"
                : string.Empty;
        }
        else
        {
            LayoutLabel.Text = string.Empty;
        }
        UpdateSelectionPreview();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectionPreview();

    private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DurationLabel == null) return;
        int val = (int)e.NewValue;
        DurationLabel.Text = val.ToString();
        if (SelectedSummaryLabel != null) UpdateSelectionPreview();
    }

    private void UpdateSelectionPreview()
    {
        // Guard: WPF fires SelectionChanged during XAML load before all controls exist
        if (SelectedCarLabel == null || SelectedTrackLabel == null ||
            SelectedSummaryLabel == null || LaunchBtn == null ||
            DurationSlider == null || ModeCombo == null)
            return;

        var car   = CarList.SelectedItem   as string;
        var track = TrackList.SelectedItem as string;

        SelectedCarLabel.Text   = car   != null ? $"Car:    {car}"   : "Car:    — none selected —";
        SelectedTrackLabel.Text = track != null ? $"Track:  {track}" : "Track:  — none selected —";

        if (car != null && track != null)
        {
            var mode = GetSelectedMode();
            var mins = (int)DurationSlider.Value;
            SelectedSummaryLabel.Text = $"{mode}  ·  {mins} minutes";
            LaunchBtn.IsEnabled = true;
        }
        else
        {
            SelectedSummaryLabel.Text = string.Empty;
            LaunchBtn.IsEnabled = false;
        }
    }


    private string GetSelectedMode()
    {
        if (ModeCombo.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? "Practice";
        return "Practice";
    }

    // ── Launch ────────────────────────────────────────────────────────────────

    private async void LaunchBtn_Click(object sender, RoutedEventArgs e)
    {
        var carId   = CarList.SelectedItem   as string;
        var trackId = TrackList.SelectedItem as string;

        if (carId == null || trackId == null) return;

        var mode        = GetSelectedMode();
        var duration    = (int)DurationSlider.Value;
        var driverName  = string.IsNullOrWhiteSpace(DriverNameBox.Text)
                            ? Environment.UserName
                            : DriverNameBox.Text.Trim();
        var easyAssists = (AssistsCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Easy";

        var config = new GameConfig
        {
            CarId            = carId,
            TrackId          = trackId,
            Mode             = Enum.TryParse<DriveMode>(mode, out var dm) ? dm : DriveMode.Practice,
            DurationMinutes  = duration,
            DriverName       = driverName,
            PcId             = Environment.MachineName,
            EasyAssists      = easyAssists,
        };

        // Show session overlay
        _sessionDurationMinutes = duration;
        _sessionStartTime       = DateTime.UtcNow;
        _sessionCts             = new CancellationTokenSource();

        ShowOverlay(SessionOverlay);
        SessionInfoLabel.Text = $"{carId}  @  {trackId}";
        SessionModeLabel.Text = mode;
        StartCountdownTimer(duration);
        SetStatus("Game is running…", isOk: true);

        try
        {
            _lastSession = await Task.Run(
                () => _launcher.LaunchAsync(config, _sessionCts.Token));
        }
        catch (OperationCanceledException)
        {
            // Cancelled by End Session button — normal flow
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Session error:\n{ex.Message}", "AcAgent",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            StopCountdownTimer();
            ShowCompletePanel();
        }
    }

    // ── Session countdown timer ───────────────────────────────────────────────

    private void StartCountdownTimer(int totalMinutes)
    {
        SessionProgress.Maximum = totalMinutes * 60;
        SessionProgress.Value   = 0;

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
        _countdownTimer.Start();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed   = (DateTime.UtcNow - _sessionStartTime).TotalSeconds;
        var totalSecs = _sessionDurationMinutes * 60.0;
        var remaining = Math.Max(0, totalSecs - elapsed);

        var ts = TimeSpan.FromSeconds(remaining);
        CountdownLabel.Text    = $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        CountdownSubLabel.Text = "remaining";
        SessionProgress.Value  = elapsed;

        // Pulse accent color when under 60 seconds
        if (remaining <= 60)
            CountdownLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x47, 0x57));
    }

    private void StopCountdownTimer()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
    }

    // ── End session early ─────────────────────────────────────────────────────

    private void EndSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        _sessionCts?.Cancel();
        EndSessionBtn.IsEnabled = false;
        CountdownSubLabel.Text  = "Ending session…";
    }

    // ── Complete panel ────────────────────────────────────────────────────────

    private void ShowCompletePanel()
    {
        if (_lastSession == null)
        {
            ShowOverlay(MainPanel);
            return;
        }

        var s = _lastSession;
        CompleteCarTrack.Text = $"{s.CarId}  @  {s.TrackId}";
        StatDuration.Text     = $"{s.DurationMinutes:F1}";
        StatMode.Text         = s.Mode.ToString();
        StatEnded.Text        = s.TimerEnded ? "Timer" : "Player";
        CompleteBadge.Text    = s.PlayerExitedEarly
            ? "⚠  PLAYER EXITED EARLY"
            : "✓  SESSION COMPLETE";
        ExportStatusLabel.Text = string.Empty;
        ExportExcelBtn.IsEnabled = true;
        EndSessionBtn.IsEnabled  = true;

        ShowOverlay(CompleteOverlay);
        SetStatus($"Session complete — {s.DurationMinutes:F1} min played", isOk: true);
    }

    // ── Export to Excel ───────────────────────────────────────────────────────

    private async void ExportExcelBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Playtime Report",
            Filter     = "Excel Files (*.xlsx)|*.xlsx",
            FileName   = $"AcAgent_Report_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            DefaultExt = ".xlsx",
        };

        if (dialog.ShowDialog() != true) return;

        ExportExcelBtn.IsEnabled = false;
        ExportStatusLabel.Text   = "Exporting…";

        try
        {
            await _reporting.ExportToExcelAsync(dialog.FileName);
            ExportStatusLabel.Text      = $"✓  Saved: {Path.GetFileName(dialog.FileName)}";
            ExportStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0xD5, 0x73));

            // Offer to open the file
            var result = MessageBox.Show(
                $"Report saved to:\n{dialog.FileName}\n\nOpen it now?",
                "AcAgent — Export Complete",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(dialog.FileName)
                    { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ExportStatusLabel.Text      = $"Export failed: {ex.Message}";
            ExportStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x47, 0x57));
        }
        finally
        {
            ExportExcelBtn.IsEnabled = true;
        }
    }

    // ── Reports button (from toolbar) ─────────────────────────────────────────

    private async void ReportsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Full Playtime Report",
            Filter     = "Excel Files (*.xlsx)|*.xlsx",
            FileName   = $"AcAgent_FullReport_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            DefaultExt = ".xlsx",
        };

        if (dialog.ShowDialog() != true) return;

        ReportsBtn.IsEnabled = false;
        SetStatus("Generating Excel report…", isOk: true);

        try
        {
            await _reporting.ExportToExcelAsync(dialog.FileName);
            SetStatus($"Report exported: {Path.GetFileName(dialog.FileName)}", isOk: true);

            var result = MessageBox.Show(
                $"Report saved to:\n{dialog.FileName}\n\nOpen it now?",
                "AcAgent — Export Complete",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(dialog.FileName)
                    { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", isOk: false);
            MessageBox.Show(ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ReportsBtn.IsEnabled = true;
        }
    }

    // ── New Session ───────────────────────────────────────────────────────────

    private void NewSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        _lastSession = null;
        ShowOverlay(MainPanel);
        SetStatus("Ready — select a car and track to begin", isOk: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ShowOverlay(UIElement visible)
    {
        MainPanel.Visibility      = visible == MainPanel      ? Visibility.Visible : Visibility.Collapsed;
        SessionOverlay.Visibility = visible == SessionOverlay ? Visibility.Visible : Visibility.Collapsed;
        CompleteOverlay.Visibility = visible == CompleteOverlay ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool isOk)
    {
        StatusLabel.Text  = text;
        StatusDot.Fill    = new SolidColorBrush(isOk
            ? Color.FromRgb(0x2E, 0xD5, 0x73)   // green
            : Color.FromRgb(0xFF, 0x47, 0x57));  // red
    }
}
