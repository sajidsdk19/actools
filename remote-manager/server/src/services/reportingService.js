const { pool } = require('../db/pool');
const logger   = require('../utils/logger');

async function generateDailyReport(dateStr) {
  const date = dateStr || new Date().toISOString().slice(0, 10);

  const { rows } = await pool.query(
    `SELECT d.id AS device_id, d.display_name,
            COUNT(s.id)                                    AS sessions,
            ROUND(COALESCE(SUM(s.actual_duration_min),0), 2) AS minutes
     FROM sessions s
     JOIN devices d ON d.id = s.device_id
     WHERE date(COALESCE(s.start_time, s.created_at)) = $1 AND s.status = 'completed'
     GROUP BY d.id, d.display_name`,
    [date]
  );

  const perDevice = {};
  let totalSessions = 0;
  let totalMinutes  = 0;

  for (const r of rows) {
    perDevice[r.device_id] = {
      display_name: r.display_name,
      sessions: r.sessions,
      minutes: parseFloat(r.minutes),
    };
    totalSessions += Number(r.sessions);
    totalMinutes  += parseFloat(r.minutes);
  }

  const perDeviceJson = JSON.stringify(perDevice);

  // Upsert: delete old record for this date if exists, then insert fresh
  await pool.query(`DELETE FROM daily_reports WHERE report_date = $1`, [date]);
  const { rows: saved } = await pool.query(
    `INSERT INTO daily_reports (report_date, total_sessions, total_minutes, per_device)
     VALUES ($1, $2, $3, $4)
     RETURNING *`,
    [date, totalSessions, totalMinutes, perDeviceJson]
  );

  logger.info(`[Reporting] Daily report for ${date}: ${totalSessions} sessions, ${totalMinutes} min`);
  return saved[0];
}

module.exports = { generateDailyReport };
