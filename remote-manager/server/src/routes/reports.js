const express  = require('express');
const { pool } = require('../db/pool');
const { requireAuth } = require('../middleware/auth');
const reportingService = require('../services/reportingService');

const router = express.Router();

// GET /reports/daily?date=YYYY-MM-DD  (default: today)
router.get('/daily', requireAuth, async (req, res, next) => {
  try {
    const date = req.query.date || new Date().toISOString().slice(0, 10);
    const { rows } = await pool.query(
      `SELECT * FROM daily_reports WHERE report_date = $1`, [date]
    );
    if (rows.length) return res.json(rows[0]);

    // Generate on-the-fly if not yet saved
    const report = await reportingService.generateDailyReport(date);
    res.json(report);
  } catch (e) { next(e); }
});

// GET /reports/summary?from=&to=
router.get('/summary', requireAuth, async (req, res, next) => {
  try {
    const from = req.query.from || '1970-01-01';
    const to   = req.query.to   || new Date().toISOString().slice(0, 10);
    const { rows } = await pool.query(
      `SELECT d.display_name, d.machine_name,
              COUNT(s.id)                               AS session_count,
              ROUND(COALESCE(SUM(s.actual_duration_min),0), 2) AS total_minutes
       FROM sessions s
       JOIN devices d ON d.id = s.device_id
       WHERE date(COALESCE(s.start_time, s.created_at)) >= $1
         AND date(COALESCE(s.start_time, s.created_at)) <= $2
         AND s.status = 'completed'
       GROUP BY d.id, d.display_name, d.machine_name
       ORDER BY total_minutes DESC`,
      [from, to]
    );
    res.json({ from, to, devices: rows });
  } catch (e) { next(e); }
});

// GET /reports/export/csv?from=&to=
router.get('/export/csv', requireAuth, async (req, res, next) => {
  try {
    const from = req.query.from || '1970-01-01';
    const to   = req.query.to   || new Date().toISOString().slice(0, 10);
    const { rows } = await pool.query(
      `SELECT s.id, d.display_name AS device, s.car_id, s.track_id, s.mode,
              s.start_time, s.end_time, s.actual_duration_min,
              s.timer_ended, s.player_exited_early
       FROM sessions s JOIN devices d ON d.id = s.device_id
        WHERE date(COALESCE(s.start_time, s.created_at)) >= $1
          AND date(COALESCE(s.start_time, s.created_at)) <= $2
       ORDER BY s.start_time`,
      [from, to]
    );
    const header = Object.keys(rows[0] || {}).join(',');
    const csvRows = rows.map(r => Object.values(r).map(v =>
      v === null ? '' : String(v).includes(',') ? `"${v}"` : v
    ).join(','));
    res.setHeader('Content-Type', 'text/csv');
    res.setHeader('Content-Disposition', `attachment; filename="sessions_${from}_${to}.csv"`);
    res.send([header, ...csvRows].join('\n'));
  } catch (e) { next(e); }
});

module.exports = router;
