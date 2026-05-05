const express    = require('express');
const { v4: uuidv4 } = require('uuid');
const { pool }   = require('../db/pool');
const { requireAuth, requireAgentSecret } = require('../middleware/auth');
const logger     = require('../utils/logger');

const router = express.Router();

// POST /devices/register  — called by the client agent on first boot
router.post('/register', requireAgentSecret, async (req, res, next) => {
  try {
    const { machineName, displayName, acRoot, existingToken } = req.body;
    if (!machineName) return res.status(400).json({ error: 'machineName required' });

    const token = uuidv4();
    const now   = new Date().toISOString();
    const dname = displayName || machineName;

    // ── 1. Match by saved token (handles MACHINE_NAME renames) ────────────────
    if (existingToken) {
      const { rows: byToken } = await pool.query(
        `SELECT id, machine_name, display_name, token, status FROM devices WHERE token = $1`,
        [existingToken]
      );
      if (byToken.length) {
        await pool.query(
          `UPDATE devices SET machine_name=$1, display_name=$2, ac_root=$3, last_seen=$4 WHERE token=$5`,
          [machineName, dname, acRoot || null, now, existingToken]
        );
        logger.info(`[Devices] Updated (name change): ${byToken[0].machine_name} → ${machineName}`);
        return res.status(201).json({ ...byToken[0], machine_name: machineName, display_name: dname });
      }
    }

    // ── 2. Match by machine_name (same name, different token) ─────────────────
    const { rows: existing } = await pool.query(
      `SELECT id, machine_name, display_name, token, status FROM devices WHERE machine_name = $1`,
      [machineName]
    );
    if (existing.length) {
      await pool.query(
        `UPDATE devices SET display_name=$1, last_seen=$2 WHERE machine_name=$3`,
        [dname, now, machineName]
      );
      logger.info(`[Devices] Re-registered: ${machineName}`);
      return res.status(201).json({ ...existing[0], display_name: dname });
    }

    // ── 3. Brand new device ───────────────────────────────────────────────────
    const { rows } = await pool.query(
      `INSERT INTO devices (machine_name, display_name, ac_root, token)
       VALUES ($1, $2, $3, $4)
       RETURNING id, machine_name, display_name, token, status`,
      [machineName, dname, acRoot || null, token]
    );
    logger.info(`[Devices] Registered new: ${machineName}`);
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
