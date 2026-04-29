using AcAgent.Infrastructure;
using AcAgent.Models;
using AcAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Color      = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

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

        // Reset countdown clock the moment acs.exe is confirmed running
        _launcher.OnGameStarted = () =>
            Dispatcher.Invoke(() =>
            {
                _sessionStartTime = DateTime.UtcNow;
            });

        PcLabel.Text       = $"PC: {Environment.MachineName}";
        DriverNameBox.Text = Environment.UserName;

        Loaded += MainWindow_Loaded;
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadContent();

        // ── Auto-launch: triggered when Node.js client-agent passes CLI args ──────
        // After content (cars/tracks) is loaded we pre-fill the form and
        // programmatically click Launch Race so no human interaction is needed.
        var al = App.AutoLaunch;
        if (al != null)
        {
            ApplyAutoLaunchConfig(al);
        }
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

            // Populate the Game Directory box with the current AC root
            var acTools = App.Services.GetRequiredService<AcToolsIntegration>();
            AcRootBox.Text = acTools.AcRoot;
            ValidateAndShowAcRoot(acTools.AcRoot);

            SetStatus("Ready — configure session and click Launch Race", isOk: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading content: {ex.Message}", isOk: false);
        }
    }

    /// <summary>
    /// Pre-fills the UI with the CLI-provided settings and triggers Launch Race.
    /// Called only when AcAgent is started by the remote Node.js agent.
    /// </summary>
    private void ApplyAutoLaunchConfig(AutoLaunchConfig al)
    {
        try
        {
            // ── Apply duration ─────────────────────────────────────────────
            DurationSlider.Value = Math.Clamp(al.Duration, (int)DurationSlider.Minimum, (int)DurationSlider.Maximum);

            // ── Apply mode ───────────────────────────────────────────────
            if (al.Mode != null)
            {
                foreach (ComboBoxItem item in ModeCombo.Items)
                {
                    if (string.Equals(item.Tag?.ToString(), al.Mode, StringComparison.OrdinalIgnoreCase))
                    {
                        ModeCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            // ── Apply assists ───────────────────────────────────────────
            if (al.EasyAssists)
            {
                foreach (ComboBoxItem item in AssistsCombo.Items)
                {
                    if (string.Equals(item.Tag?.ToString(), "Easy", StringComparison.OrdinalIgnoreCase))
                    {
                        AssistsCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            // ── Select car ────────────────────────────────────────────────
            if (al.Car != null && _allCars.Contains(al.Car, StringComparer.OrdinalIgnoreCase))
            {
                CarList.SelectedItem = _allCars.FirstOrDefault(
                    c => c.Equals(al.Car, StringComparison.OrdinalIgnoreCase));
            }

            // ── Select track ──────────────────────────────────────────────
            if (al.Track != null && _allTracks.Contains(al.Track, StringComparer.OrdinalIgnoreCase))
            {
                TrackList.SelectedItem = _allTracks.FirstOrDefault(
                    t => t.Equals(al.Track, StringComparison.OrdinalIgnoreCase));
            }

            // ── Status message ──────────────────────────────────────────
            SetStatus($"Auto-launching: {al.Car ?? "(default car)"} @ {al.Track ?? "(default track)"} — {al.Duration} min", isOk: true);

            // ── Trigger Launch Race ──────────────────────────────────────
            // Small delay so UI renders first (makes debugging easier if something goes wrong)
            Dispatcher.InvokeAsync(() =>
            {
                LaunchBtn_Click(this, new RoutedEventArgs());
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            SetStatus($"Auto-launch error: {ex.Message}", isOk: false);
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

        var car   = CarList.SelectedItem   as string ?? (_allCars.Count   > 0 ? _allCars[0]   : null);
        var track = TrackList.SelectedItem as string ?? (_allTracks.Count > 0 ? _allTracks[0] : null);

        SelectedCarLabel.Text   = CarList.SelectedItem   != null ? $"Car:    {car}"
                                : car != null                    ? $"Car:    {car}  (default)"
                                : "Car:    — no cars found —";

        SelectedTrackLabel.Text = TrackList.SelectedItem != null ? $"Track:  {track}"
                                : track != null                  ? $"Track:  {track}  (default)"
                                : "Track:  — no tracks found —";

        var mode = GetSelectedMode();
        var mins = (int)DurationSlider.Value;
        SelectedSummaryLabel.Text = $"{mode}  ·  {mins} minutes";

        // Launch is always enabled — AC root validity is the only real gate
        LaunchBtn.IsEnabled = true;
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
        // Use selected car/track, or fall back to first in list (optional selection)
        var carId   = (CarList.SelectedItem   as string) ?? (_allCars.Count   > 0 ? _allCars[0]   : null);
        var trackId = (TrackList.SelectedItem as string) ?? (_allTracks.Count > 0 ? _allTracks[0] : null);

        if (carId == null || trackId == null)
        {
            var missing = carId == null && trackId == null ? "car and track"
                        : carId == null ? "car" : "track";
            MessageBox.Show(
                $"No {missing} found in the Assetto Corsa content folder.\n" +
                $"Make sure Game Directory is set correctly.",
                "AcAgent — Nothing to launch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Apply any pending AC root change before launching
        var newRoot = AcRootBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(newRoot))
            ApplyNewAcRoot(newRoot);

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

    // ── Game Directory helpers ─────────────────────────────────────────────────

    private void BrowseAcRootBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "Select your Assetto Corsa installation folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        // Pre-select current path if it exists
        if (Directory.Exists(AcRootBox.Text))
            dialog.InitialDirectory = AcRootBox.Text;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            AcRootBox.Text = dialog.SelectedPath;
            ApplyNewAcRoot(dialog.SelectedPath);
        }
    }

    private void AcRootBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AcRootBox == null || AcRootStatusLabel == null) return;
        ValidateAndShowAcRoot(AcRootBox.Text?.Trim() ?? string.Empty);
    }

    /// <summary>Shows a green ✓ or red ⚠ next to the path box.</summary>
    private void ValidateAndShowAcRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AcRootStatusLabel.Text       = "No path set";
            AcRootStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x47, 0x57));
            return;
        }

        bool valid = File.Exists(Path.Combine(path, "acs.exe")) &&
                     Directory.Exists(Path.Combine(path, "content", "cars"));

        AcRootStatusLabel.Text       = valid ? "✓  Valid AC directory" : "⚠  acs.exe / content/cars not found";
        AcRootStatusLabel.Foreground = valid
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0xD5, 0x73))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
    }

    /// <summary>
    /// Saves the new AC root to appsettings.json and reloads cars/tracks
    /// without restarting the whole app.
    /// </summary>
    private void ApplyNewAcRoot(string newRoot)
    {
        if (string.IsNullOrWhiteSpace(newRoot) || !Directory.Exists(newRoot)) return;

        // Persist to appsettings.json so next launch remembers it
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var json = File.Exists(settingsPath)
                ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath))
                    .RootElement.Clone().ToString()
                : "{}";

            // Simple replace — works for the small appsettings.json we have
            var obj = System.Text.Json.Nodes.JsonObject.Parse(json)!.AsObject();
            obj["AcRoot"] = newRoot;
            File.WriteAllText(settingsPath,
                obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-critical */ }

        // Update AcToolsIntegration in-place
        try
        {
            var acTools = App.Services.GetRequiredService<AcToolsIntegration>();
            acTools.SetAcRoot(newRoot);
            LoadContent();
            SetStatus($"Game directory updated: {newRoot}", isOk: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not apply new AC root: {ex.Message}", isOk: false);
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
