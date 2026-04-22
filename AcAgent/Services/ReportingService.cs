using AcAgent.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AcAgent.Services;

/// <summary>
/// Stores and queries session records.
///
/// Storage strategy:
///   • Primary  → SQLite database  (<c>sessions.db</c> next to the executable)
///   • Fallback → JSON line-delimited log (<c>sessions.jsonl</c>) if SQLite fails
///
/// The dual-write approach keeps the system resilient: if SQLite is unavailable
/// (e.g. first run, locked file) the JSON log still captures every session and
/// can be imported later.
///
/// Multi-PC design:
///   Each session carries a <see cref="Session.PcId"/> field.  The aggregation
///   queries filter / group by PcId.  A future API integration can POST the JSON
///   payload to a central server from <see cref="SaveSessionAsync"/>.
/// </summary>
public sealed class ReportingService : IAsyncDisposable
{
    private const string DbFile = "sessions.db";
    private const string JsonlFile = "sessions.jsonl";

    private readonly string _dataDir;
    private readonly ILogger<ReportingService> _logger;
    private SqliteConnection? _db;

    public ReportingService(string dataDirectory, ILogger<ReportingService> logger)
    {
        _dataDir = dataDirectory;
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens the SQLite database and ensures the schema exists.
    /// Must be called once before using any other method.
    /// </summary>
    public async Task InitialiseAsync()
    {
        var dbPath = Path.Combine(_dataDir, DbFile);
        _db = new SqliteConnection($"Data Source={dbPath}");
        await _db.OpenAsync();

        const string ddl = """
            CREATE TABLE IF NOT EXISTS sessions (
                id                       TEXT PRIMARY KEY,
                pc_id                    TEXT NOT NULL,
                car_id                   TEXT NOT NULL,
                track_id                 TEXT NOT NULL,
                mode                     TEXT NOT NULL,
                start_time_utc           TEXT NOT NULL,
                end_time_utc             TEXT,
                duration_minutes         REAL NOT NULL DEFAULT 0,
                configured_duration_min  INTEGER NOT NULL DEFAULT 0,
                timer_ended              INTEGER NOT NULL DEFAULT 0,
                player_exited_early      INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_sessions_pc   ON sessions(pc_id);
            CREATE INDEX IF NOT EXISTS idx_sessions_date ON sessions(start_time_utc);
            """;

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation("[Reporting] SQLite database ready at {Path}", dbPath);
    }

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a completed <see cref="Session"/> to SQLite and JSONL log.
    /// Errors are caught and logged so the caller is never disrupted.
    /// </summary>
    public async Task SaveSessionAsync(Session session)
    {
        // ── 1. JSONL (always) ────────────────────────────────────────────────
        try
        {
            var line = JsonConvert.SerializeObject(session, Formatting.None) + Environment.NewLine;
            var jsonlPath = Path.Combine(_dataDir, JsonlFile);
            await File.AppendAllTextAsync(jsonlPath, line);
            _logger.LogDebug("[Reporting] Session {Id} appended to JSONL log.", session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Reporting] Failed to write JSONL log for session {Id}.", session.Id);
        }

        // ── 2. SQLite ────────────────────────────────────────────────────────
        if (_db == null)
        {
            _logger.LogWarning("[Reporting] SQLite not initialised; skipping DB write for session {Id}.", session.Id);
            return;
        }

        try
        {
            const string sql = """
                INSERT OR REPLACE INTO sessions
                    (id, pc_id, car_id, track_id, mode, start_time_utc, end_time_utc,
                     duration_minutes, configured_duration_min, timer_ended, player_exited_early)
                VALUES
                    ($id, $pc, $car, $track, $mode, $start, $end,
                     $dur, $cfgDur, $timerEnded, $earlyExit);
                """;

            await using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", session.Id);
            cmd.Parameters.AddWithValue("$pc", session.PcId);
            cmd.Parameters.AddWithValue("$car", session.CarId);
            cmd.Parameters.AddWithValue("$track", session.TrackId);
            cmd.Parameters.AddWithValue("$mode", session.Mode.ToString());
            cmd.Parameters.AddWithValue("$start", session.StartTimeUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$end", (object?)session.EndTimeUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dur", session.DurationMinutes);
            cmd.Parameters.AddWithValue("$cfgDur", session.ConfiguredDurationMinutes);
            cmd.Parameters.AddWithValue("$timerEnded", session.TimerEnded ? 1 : 0);
            cmd.Parameters.AddWithValue("$earlyExit", session.PlayerExitedEarly ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
            _logger.LogDebug("[Reporting] Session {Id} saved to SQLite.", session.Id);

            // ── TODO: POST to central API when ready ─────────────────────────
            // await PostToApiAsync(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Reporting] Failed to save session {Id} to SQLite.", session.Id);
        }
    }

    // ── Read / Aggregation ───────────────────────────────────────────────────

    /// <summary>
    /// Returns total playtime in minutes per calendar day (local time).
    /// Results are ordered newest-day first.
    /// </summary>
    public async Task<IReadOnlyList<DailyPlaytime>> GetTotalPlaytimePerDayAsync()
    {
        EnsureDbReady();
        const string sql = """
            SELECT date(start_time_utc) AS day,
                   SUM(duration_minutes) AS total_minutes
            FROM   sessions
            GROUP  BY day
            ORDER  BY day DESC;
            """;

        var results = new List<DailyPlaytime>();
        await using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DailyPlaytime(
                reader.GetString(0),
                reader.GetDouble(1)));
        }
        return results;
    }

    /// <summary>
    /// Returns total playtime in minutes per PC (machine ID).
    /// Results are ordered by total playtime descending.
    /// </summary>
    public async Task<IReadOnlyList<PcPlaytime>> GetTotalPlaytimePerPcAsync()
    {
        EnsureDbReady();
        const string sql = """
            SELECT pc_id,
                   SUM(duration_minutes) AS total_minutes,
                   COUNT(*)              AS session_count
            FROM   sessions
            GROUP  BY pc_id
            ORDER  BY total_minutes DESC;
            """;

        var results = new List<PcPlaytime>();
        await using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new PcPlaytime(
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetInt32(2)));
        }
        return results;
    }

    /// <summary>
    /// Returns all raw sessions for the specified PC, ordered newest-first.
    /// </summary>
    public async Task<IReadOnlyList<Session>> GetSessionsByPcAsync(string pcId)
    {
        EnsureDbReady();
        const string sql = """
            SELECT id, pc_id, car_id, track_id, mode,
                   start_time_utc, end_time_utc,
                   configured_duration_min, timer_ended, player_exited_early
            FROM   sessions
            WHERE  pc_id = $pc
            ORDER  BY start_time_utc DESC;
            """;

        var results = new List<Session>();
        await using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$pc", pcId);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            // Session uses init-only properties; build via the private DTO then
            // construct a correctly-populated Session through JSON round-trip.
            var dto = new SessionDto
            {
                Id                      = reader.GetString(0),
                PcId                    = reader.GetString(1),
                CarId                   = reader.GetString(2),
                TrackId                 = reader.GetString(3),
                Mode                    = reader.GetString(4),
                StartTimeUtc            = reader.GetString(5),
                EndTimeUtc              = reader.IsDBNull(6) ? null : reader.GetString(6),
                ConfiguredDurationMinutes = reader.GetInt32(7),
                TimerEnded              = reader.GetInt32(8) == 1,
                PlayerExitedEarly       = reader.GetInt32(9) == 1,
            };

            var session = dto.ToSession();
            results.Add(session);
        }

        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnsureDbReady()
    {
        if (_db == null)
            throw new InvalidOperationException(
                "ReportingService has not been initialised. Call InitialiseAsync() first.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_db != null)
        {
            await _db.CloseAsync();
            await _db.DisposeAsync();
        }
    }

    // ── Excel Export ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns every session row stored in the database, ordered newest-first.
    /// </summary>
    public async Task<IReadOnlyList<Session>> GetAllSessionsAsync()
    {
        EnsureDbReady();
        const string sql = """
            SELECT id, pc_id, car_id, track_id, mode,
                   start_time_utc, end_time_utc,
                   configured_duration_min, timer_ended, player_exited_early
            FROM   sessions
            ORDER  BY start_time_utc DESC;
            """;

        var results = new List<Session>();
        await using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var dto = new SessionDto
            {
                Id                        = reader.GetString(0),
                PcId                      = reader.GetString(1),
                CarId                     = reader.GetString(2),
                TrackId                   = reader.GetString(3),
                Mode                      = reader.GetString(4),
                StartTimeUtc              = reader.GetString(5),
                EndTimeUtc                = reader.IsDBNull(6) ? null : reader.GetString(6),
                ConfiguredDurationMinutes = reader.GetInt32(7),
                TimerEnded                = reader.GetInt32(8) == 1,
                PlayerExitedEarly         = reader.GetInt32(9) == 1,
            };
            results.Add(dto.ToSession());
        }
        return results;
    }

    /// <summary>
    /// Generates an Excel workbook at <paramref name="filePath"/> containing:
    /// <list type="bullet">
    ///   <item><b>Summary</b> – total playtime in minutes per PC, with a bar chart indicator column.</item>
    ///   <item><b>Daily Breakdown</b> – minutes played per day per PC (pivot style).</item>
    ///   <item><b>All Sessions</b> – full raw session log.</item>
    /// </list>
    /// </summary>
    public async Task ExportToExcelAsync(string filePath)
    {
        var allSessions = await GetAllSessionsAsync();
        var perDay      = await GetTotalPlaytimePerDayAsync();
        var perPc       = await GetTotalPlaytimePerPcAsync();

        await Task.Run(() =>
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();

            // ── Sheet 1: Per-PC Summary ──────────────────────────────────────
            var wsSummary = wb.Worksheets.Add("Summary");
            WriteHeader(wsSummary, 1, new[]
            {
                "PC / Machine", "Total Minutes", "Total Hours", "Sessions", "Avg Duration (min)"
            });

            int row = 2;
            foreach (var pc in perPc)
            {
                wsSummary.Cell(row, 1).Value = pc.PcId;
                wsSummary.Cell(row, 2).Value = Math.Round(pc.TotalMinutes, 1);
                wsSummary.Cell(row, 3).Value = Math.Round(pc.TotalMinutes / 60.0, 2);
                wsSummary.Cell(row, 4).Value = pc.SessionCount;
                wsSummary.Cell(row, 5).Value = pc.SessionCount > 0
                    ? Math.Round(pc.TotalMinutes / pc.SessionCount, 1) : 0;
                row++;
            }
            AutoFitAndStyle(wsSummary, perPc.Count + 1);

            // ── Sheet 2: Daily Breakdown ─────────────────────────────────────
            var wsDaily = wb.Worksheets.Add("Daily Breakdown");
            WriteHeader(wsDaily, 1, new[] { "Date", "Minutes Played" });

            row = 2;
            foreach (var d in perDay)
            {
                wsDaily.Cell(row, 1).Value = d.Day;
                wsDaily.Cell(row, 2).Value = Math.Round(d.TotalMinutes, 1);
                row++;
            }
            AutoFitAndStyle(wsDaily, perDay.Count + 1);

            // ── Sheet 3: All Sessions ────────────────────────────────────────
            var wsSessions = wb.Worksheets.Add("All Sessions");
            WriteHeader(wsSessions, 1, new[]
            {
                "Session ID", "PC", "Car", "Track", "Mode",
                "Start (UTC)", "End (UTC)", "Duration (min)",
                "Configured (min)", "Timer Ended", "Player Exited Early"
            });

            row = 2;
            foreach (var s in allSessions)
            {
                wsSessions.Cell(row, 1).Value  = s.Id;
                wsSessions.Cell(row, 2).Value  = s.PcId;
                wsSessions.Cell(row, 3).Value  = s.CarId;
                wsSessions.Cell(row, 4).Value  = s.TrackId;
                wsSessions.Cell(row, 5).Value  = s.Mode.ToString();
                wsSessions.Cell(row, 6).Value  = s.StartTimeUtc.ToString("yyyy-MM-dd HH:mm:ss");
                wsSessions.Cell(row, 7).Value  = s.EndTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                wsSessions.Cell(row, 8).Value  = Math.Round(s.DurationMinutes, 2);
                wsSessions.Cell(row, 9).Value  = s.ConfiguredDurationMinutes;
                wsSessions.Cell(row, 10).Value = s.TimerEnded       ? "Yes" : "No";
                wsSessions.Cell(row, 11).Value = s.PlayerExitedEarly ? "Yes" : "No";
                row++;
            }
            AutoFitAndStyle(wsSessions, allSessions.Count + 1);

            // ── Freeze header rows and set first sheet active ─────────────────
            foreach (var ws in wb.Worksheets)
                ws.SheetView.FreezeRows(1);

            wb.Worksheets.First().SetTabActive();
            wb.SaveAs(filePath);
        });

        _logger.LogInformation("[Reporting] Excel report exported to {Path}", filePath);
    }

    // ── Private Excel helpers ─────────────────────────────────────────────────

    // Excel-friendly light-mode palette
    private static readonly ClosedXML.Excel.XLColor _headerBg   = ClosedXML.Excel.XLColor.FromHtml("#C0392B"); // dark red
    private static readonly ClosedXML.Excel.XLColor _headerFg   = ClosedXML.Excel.XLColor.White;
    private static readonly ClosedXML.Excel.XLColor _altRowBg   = ClosedXML.Excel.XLColor.FromHtml("#FDECEA"); // very light red-tint
    private static readonly ClosedXML.Excel.XLColor _normalBg   = ClosedXML.Excel.XLColor.White;
    private static readonly ClosedXML.Excel.XLColor _normalFg   = ClosedXML.Excel.XLColor.FromHtml("#1A1A2E"); // near-black
    private static readonly ClosedXML.Excel.XLColor _borderColor = ClosedXML.Excel.XLColor.FromHtml("#D5D5D5");

    private static void WriteHeader(
        ClosedXML.Excel.IXLWorksheet ws, int row, string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(row, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold           = true;
            cell.Style.Font.FontSize       = 11;
            cell.Style.Font.FontColor       = _headerFg;
            cell.Style.Fill.BackgroundColor = _headerBg;
            cell.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical   = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
            cell.Style.Border.BottomBorder  = ClosedXML.Excel.XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = ClosedXML.Excel.XLColor.FromHtml("#922B21");
        }
    }

    private static void AutoFitAndStyle(ClosedXML.Excel.IXLWorksheet ws, int lastDataRow)
    {
        // Set row height for header
        ws.Row(1).Height = 22;

        int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;

        // Style each data row
        for (int r = 2; r <= lastDataRow; r++)
        {
            var bg = (r % 2 == 0) ? _altRowBg : _normalBg;
            ws.Row(r).Height = 18;

            for (int c = 1; c <= lastCol; c++)
            {
                var cell = ws.Cell(r, c);
                cell.Style.Fill.BackgroundColor = bg;
                cell.Style.Font.FontColor        = _normalFg;
                cell.Style.Font.FontSize         = 10;
                cell.Style.Border.BottomBorder   = ClosedXML.Excel.XLBorderStyleValues.Hair;
                cell.Style.Border.BottomBorderColor = _borderColor;
            }
        }

        // Auto-fit after styling so widths include content
        ws.Columns(1, lastCol).AdjustToContents();

        // Minimum column width for readability
        for (int c = 1; c <= lastCol; c++)
        {
            if (ws.Column(c).Width < 12)
                ws.Column(c).Width = 12;
        }
    }
}


// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>Playtime in minutes for a single calendar day.</summary>
public sealed record DailyPlaytime(string Day, double TotalMinutes);

/// <summary>Aggregated playtime for a single PC.</summary>
public sealed record PcPlaytime(string PcId, double TotalMinutes, int SessionCount);

/// <summary>
/// Intermediate DB-read DTO used to reconstruct a <see cref="Session"/> from
/// SQLite without violating the init-only property constraints.
///
/// Because <see cref="Session"/> uses <c>init</c> accessors on identity fields
/// (Id, PcId, CarId, TrackId, Mode, ConfiguredDurationMinutes), we cannot set
/// them after construction.  We read into this flat, mutable DTO and then
/// convert to a Session via the <see cref="ToSession"/> method.
/// </summary>
internal sealed class SessionDto
{
    public string Id { get; set; } = string.Empty;
    public string PcId { get; set; } = string.Empty;
    public string CarId { get; set; } = string.Empty;
    public string TrackId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string StartTimeUtc { get; set; } = string.Empty;
    public string? EndTimeUtc { get; set; }
    public int ConfiguredDurationMinutes { get; set; }
    public bool TimerEnded { get; set; }
    public bool PlayerExitedEarly { get; set; }

    /// <summary>
    /// Converts the DTO into the domain <see cref="Session"/> model.
    /// init-only fields are set here inside the object initialiser.
    /// </summary>
    public Session ToSession() => new Session
    {
        // --- init-only fields (must be set at construction time) ---
        Id = Id,
        PcId = PcId,
        CarId = CarId,
        TrackId = TrackId,
        Mode = Enum.TryParse<DriveMode>(Mode, out var m) ? m : DriveMode.Practice,
        ConfiguredDurationMinutes = ConfiguredDurationMinutes,

        // --- regular settable fields ---
        StartTimeUtc = DateTime.TryParse(StartTimeUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var start)
            ? start : DateTime.UtcNow,
        EndTimeUtc = EndTimeUtc != null && DateTime.TryParse(EndTimeUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var end)
            ? end : null,
        TimerEnded = TimerEnded,
        PlayerExitedEarly = PlayerExitedEarly,
    };
}
