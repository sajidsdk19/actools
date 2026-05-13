const { pool }        = require('../db/pool');
const logger          = require('../utils/logger');
const acContentCache  = require('../services/acContentCache');

function now() { return new Date().toISOString(); }

function registerSocketHandlers(io) {
  const jwt = require('jsonwebtoken');

  io.on('connection', async (socket) => {
    const { type, deviceToken, jwtToken } = socket.handshake.auth;

    // ── Agent connection ──────────────────────────────────────────────────────
    if (type === 'agent') {
      const { rows } = await pool.query(
        `SELECT id, machine_name FROM devices WHERE token = $1`, [deviceToken]
      );
      if (!rows.length) {
        logger.warn('[Socket] Agent rejected — bad token');
        return socket.disconnect(true);
      }
      const device = rows[0];
      socket.deviceId = device.id;
      socket.join(`device:${device.id}`);

      await pool.query(
        `UPDATE devices SET status='online', last_seen=$1 WHERE id=$2`, [now(), device.id]
      );
      logger.info(`[Socket] Agent connected: ${device.machine_name} (${device.id})`);

      io.to('dashboard').emit('device_connected', {
        deviceId: device.id, machineName: device.machine_name, status: 'online',
      });

      // ── AC Content catalogue (emitted by agent on connect + on SCAN_AC_CONTENT) ──
      socket.on('AC_CONTENT', (data) => {
        logger.info(
          `[Socket] AC_CONTENT from ${device.machine_name}: ` +
          `${data.cars?.length ?? 0} cars, ${data.tracks?.length ?? 0} tracks`
        );
        // Cache server-side so HTTP GET /devices/:id/ac-content works for late-joining dashboards
        acContentCache.set(device.id, data);
        // Relay to all dashboard clients
        io.to('dashboard').emit('ac_content', { deviceId: device.id, ...data });
      });

      // ── Session events ──────────────────────────────────────────────────────

      socket.on('SESSION_STARTED', async (data) => {
        const { sessionId } = data;
        await pool.query(
          `UPDATE sessions SET status='running', start_time=$1 WHERE id=$2`, [now(), sessionId]
        );
        io.to('dashboard').emit('session_started', { sessionId, deviceId: device.id });
        logger.info(`[Socket] SESSION_STARTED: ${sessionId}`);
      });

      socket.on('TIMER_UPDATE', (data) => {
        const { sessionId, remainingSeconds } = data;
        io.to('dashboard').emit('timer_update', { sessionId, deviceId: device.id, remainingSeconds });
      });

      socket.on('SESSION_ENDED', async (data) => {
        const { sessionId, durationMinutes, timerEnded, playerExitedEarly } = data;
        await pool.query(
          `UPDATE sessions
           SET status='completed', end_time=$1,
               actual_duration_min=$2, timer_ended=$3, player_exited_early=$4
           WHERE id=$5`,
          [now(), durationMinutes, timerEnded ? 1 : 0, playerExitedEarly ? 1 : 0, sessionId]
        );
        await pool.query(`UPDATE devices SET status='online' WHERE id=$1`, [device.id]);
        io.to('dashboard').emit('session_ended',         { sessionId, deviceId: device.id, durationMinutes });
        io.to('dashboard').emit('device_status_changed', { deviceId: device.id, status: 'online' });
        logger.info(`[Socket] SESSION_ENDED: ${sessionId} — ${durationMinutes?.toFixed(1)} min`);
      });

      socket.on('SESSION_ERROR', async (data) => {
        const { sessionId, error } = data;
        logger.error(`[Socket] SESSION_ERROR on ${sessionId}: ${error}`);
        if (sessionId) {
          await pool.query(
            `UPDATE sessions SET status='error', end_time=$1 WHERE id=$2`, [now(), sessionId]
          );
        }
        await pool.query(`UPDATE devices SET status='online' WHERE id=$1`, [device.id]);
        io.to('dashboard').emit('session_error', { sessionId, deviceId: device.id, error });
      });

      socket.on('disconnect', async () => {
        await pool.query(
          `UPDATE devices SET status='offline', last_seen=$1 WHERE id=$2`, [now(), device.id]
        );
        io.to('dashboard').emit('device_disconnected', { deviceId: device.id });
        logger.info(`[Socket] Agent disconnected: ${device.machine_name}`);
      });

      return;
    }

    // ── Dashboard connection ──────────────────────────────────────────────────
    if (type === 'dashboard') {
      if (process.env.BYPASS_AUTH === 'true') {
        socket.join('dashboard');
        logger.info(`[Socket] Dashboard connected (bypass): ${socket.id}`);

        // ── Dashboard can request a content rescan for a specific device ────
        socket.on('SCAN_AC_CONTENT', ({ deviceId }) => {
          logger.info(`[Socket] Dashboard requesting SCAN_AC_CONTENT for device ${deviceId}`);
          io.to(`device:${deviceId}`).emit('SCAN_AC_CONTENT');
        });

        return;
      }
      try {
        jwt.verify(jwtToken, process.env.JWT_SECRET);
        socket.join('dashboard');
        logger.info(`[Socket] Dashboard connected: ${socket.id}`);

        socket.on('SCAN_AC_CONTENT', ({ deviceId }) => {
          logger.info(`[Socket] Dashboard requesting SCAN_AC_CONTENT for device ${deviceId}`);
          io.to(`device:${deviceId}`).emit('SCAN_AC_CONTENT');
        });
      } catch {
        logger.warn('[Socket] Dashboard rejected — bad JWT');
        socket.disconnect(true);
      }
      return;
    }

    socket.disconnect(true);
  });
}

module.exports = { registerSocketHandlers };
