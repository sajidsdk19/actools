const express    = require('express');
const { v4: uuidv4 } = require('uuid');
const { pool }   = require('../db/pool');
const { requireAuth, requireAgentSecret } = require('../middleware/auth');
const logger     = require('../utils/logger');

const router = express.Router();

// POST /devices/register  — called by the client agent on first boot
router.post('/register', requireAgentSecret, async (req, res, next) => {
  try {
    const { machineName, displayName, acRoot } = req.body;
    if (!machineName) return res.status(400).json({ error: 'machineName required' });

    const token = uuidv4();
    const now   = new Date().toISOString();

    // Check if already registered
    const { rows: existing } = await pool.query(
      `SELECT id, machine_name, display_name, token, status FROM devices WHERE machine_name = $1`,
      [machineName]
    );

    if (existing.length) {
      // Update existing record, keep its token
      await pool.query(
        `UPDATE devices SET display_name=$1, last_seen=$2 WHERE machine_name=$3`,
        [displayName || machineName, now, machineName]
      );
      logger.info(`[Devices] Re-registered: ${machineName}`);
      return res.status(201).json(existing[0]);
    }

    const { rows } = await pool.query(
      `INSERT INTO devices (machine_name, display_name, ac_root, token)
       VALUES ($1, $2, $3, $4)
       RETURNING id, machine_name, display_name, token, status`,
      [machineName, displayName || machineName, acRoot || null, token]
    );
    logger.info(`[Devices] Registered: ${machineName}`);
    res.status(201).json(rows[0]);
  } catch (e) { next(e); }
});

// GET /devices  — list all devices (dashboard/mobile)
router.get('/', requireAuth, async (_req, res, next) => {
  try {
    const { rows } = await pool.query(
      `SELECT id, machine_name, display_name, status, last_seen, registered_at
       FROM devices ORDER BY display_name`
    );
    res.json(rows);
  } catch (e) { next(e); }
});

// GET /devices/:id
router.get('/:id', requireAuth, async (req, res, next) => {
  try {
    const { rows } = await pool.query(
      `SELECT id, machine_name, display_name, status, last_seen, ac_root, registered_at
       FROM devices WHERE id=$1`,
      [req.params.id]
    );
    if (!rows.length) return res.status(404).json({ error: 'Device not found' });
    res.json(rows[0]);
  } catch (e) { next(e); }
});

module.exports = router;
