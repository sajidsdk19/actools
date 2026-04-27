const { pool } = require('../db/pool');
const logger   = require('../utils/logger');

/**
 * POST /sessions/start
 * Body: { deviceId, carId, trackId, trackLayout?, mode?, durationMinutes, easyAssists? }
 *
 * Flow:
 *  1. Validate device is online
 *  2. Create session record (status=pending)
 *  3. Emit START_SESSION to the device via Socket.IO
 *  4. Return the session record
 */
exports.startSession = async (req, res, next) => {
  try {
    const {
      deviceId, carId, trackId,
      trackLayout = null,
      mode = 'Practice',
      durationMinutes = 30,
      easyAssists = false,
    } = req.body;

    if (!deviceId || !carId || !trackId)
      return res.status(400).json({ error: 'deviceId, carId, trackId required' });

    // Check device exists and is online
    const { rows: devRows } = await pool.query(
      `SELECT id, status FROM devices WHERE id = $1`, [deviceId]
    );
    if (!devRows.length) return res.status(404).json({ error: 'Device not found' });
    if (devRows[0].status === 'in_session')
      return res.status(409).json({ error: 'Device already has an active session' });
    if (devRows[0].status === 'offline')
      return res.status(409).json({ error: 'Device is offline' });

    // Create session record (SQLite requires 0/1 for booleans, not true/false)
    const { rows: sessRows } = await pool.query(
      `INSERT INTO sessions
         (device_id, car_id, track_id, track_layout, mode, easy_assists, configured_duration_min, status)
       VALUES ($1,$2,$3,$4,$5,$6,$7,'pending')
       RETURNING *`,
      [deviceId, carId, trackId, trackLayout || null, mode, easyAssists ? 1 : 0, durationMinutes]
    );
    const session = sessRows[0];

    // Emit command via Socket.IO
    const io     = req.app.get('io');
    const payload = {
      sessionId:       session.id,
      carId, trackId, trackLayout,
      mode, durationMinutes, easyAssists,
    };
    io.to(`device:${deviceId}`).emit('START_SESSION', payload);
    logger.info(`[SessionCtrl] START_SESSION → device ${deviceId}, session ${session.id}`);

    // Mark device as in_session
    await pool.query(`UPDATE devices SET status='in_session' WHERE id=$1`, [deviceId]);

    // Broadcast status to dashboards
    io.to('dashboard').emit('device_status_changed', { deviceId, status: 'in_session' });
    io.to('dashboard').emit('session_started', { session });

    res.status(201).json(session);
  } catch (e) { next(e); }
};

/**
 * POST /sessions/stop
 * Body: { sessionId }
 */
exports.stopSession = async (req, res, next) => {
  try {
    const { sessionId } = req.body;
    if (!sessionId) return res.status(400).json({ error: 'sessionId required' });

    const { rows } = await pool.query(
      `SELECT s.*, d.id AS dev_id FROM sessions s JOIN devices d ON d.id = s.device_id WHERE s.id=$1`,
      [sessionId]
    );
    if (!rows.length) return res.status(404).json({ error: 'Session not found' });
    const session = rows[0];

    const io = req.app.get('io');
    io.to(`device:${session.dev_id}`).emit('STOP_SESSION', { sessionId });
    logger.info(`[SessionCtrl] STOP_SESSION → device ${session.dev_id}`);

    res.json({ ok: true, message: 'Stop command sent' });
  } catch (e) { next(e); }
};

/**
 * POST /sessions/force-stop
 * Body: { deviceId }  — kills whatever is running on that device
 */
exports.forceStop = async (req, res, next) => {
  try {
    const { deviceId } = req.body;
    if (!deviceId) return res.status(400).json({ error: 'deviceId required' });

    const io = req.app.get('io');
    io.to(`device:${deviceId}`).emit('FORCE_STOP', {});
    logger.warn(`[SessionCtrl] FORCE_STOP → device ${deviceId}`);

    res.json({ ok: true, message: 'Force stop command sent' });
  } catch (e) { next(e); }
};
