const express  = require('express');
const { pool } = require('../db/pool');
const { requireAuth } = require('../middleware/auth');
const sessionController = require('../controllers/sessionController');

const router = express.Router();

// POST /sessions/start
router.post('/start', requireAuth, sessionController.startSession);

// POST /sessions/stop
router.post('/stop', requireAuth, sessionController.stopSession);

// POST /sessions/force-stop
router.post('/force-stop', requireAuth, sessionController.forceStop);

// GET /sessions  — list with optional ?deviceId=&status=
router.get('/', requireAuth, async (req, res, next) => {
  try {
    const { deviceId, status } = req.query;
    let q = `SELECT s.*, d.display_name AS device_name
             FROM sessions s JOIN devices d ON d.id = s.device_id
             WHERE 1=1`;
    const params = [];
    if (deviceId) { params.push(deviceId); q += ` AND s.device_id = $${params.length}`; }
    if (status)   { params.push(status);   q += ` AND s.status = $${params.length}`; }
    q += ' ORDER BY s.created_at DESC LIMIT 200';
    const { rows } = await pool.query(q, params);
    res.json(rows);
  } catch (e) { next(e); }
});

// GET /sessions/:id
router.get('/:id', requireAuth, async (req, res, next) => {
  try {
    const { rows } = await pool.query(
      `SELECT s.*, d.display_name AS device_name
       FROM sessions s JOIN devices d ON d.id = s.device_id
       WHERE s.id = $1`,
      [req.params.id]
    );
    if (!rows.length) return res.status(404).json({ error: 'Session not found' });
    res.json(rows[0]);
  } catch (e) { next(e); }
});

module.exports = router;
