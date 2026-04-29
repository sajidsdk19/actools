const express  = require('express');
const bcrypt   = require('bcryptjs');
const jwt      = require('jsonwebtoken');
const { pool } = require('../db/pool');
const { getBypassToken } = require('../middleware/auth');

const router = express.Router();

// GET /auth/bypass-token  — returns a long-lived token when BYPASS_AUTH=true
// Used by the Electron/desktop dashboard to auto-authenticate without a login screen.
router.get('/bypass-token', (req, res) => {
  const token = getBypassToken();
  if (!token) return res.status(403).json({ error: 'Bypass auth is not enabled on this server.' });
  res.json({ token, user: { id: 'admin', email: 'Admin', role: 'admin' } });
});

// POST /auth/register  (first-time setup; disable in production after use)
router.post('/register', async (req, res, next) => {
  try {
    const { email, password, role = 'operator' } = req.body;
    if (!email || !password) return res.status(400).json({ error: 'email & password required' });

    const hash = await bcrypt.hash(password, 12);
    const { rows } = await pool.query(
      `INSERT INTO users (email, password_hash, role)
       VALUES ($1, $2, $3)
       ON CONFLICT (email) DO NOTHING
       RETURNING id, email, role`,
      [email, hash, role]
    );
    if (!rows.length) return res.status(409).json({ error: 'Email already exists' });
    res.status(201).json(rows[0]);
  } catch (e) { next(e); }
});

// POST /auth/login
router.post('/login', async (req, res, next) => {
  try {
    const { email, password } = req.body;
    const { rows } = await pool.query('SELECT * FROM users WHERE email=$1', [email]);
    const user = rows[0];
    if (!user || !(await bcrypt.compare(password, user.password_hash)))
      return res.status(401).json({ error: 'Invalid credentials' });

    const token = jwt.sign(
      { id: user.id, email: user.email, role: user.role },
      process.env.JWT_SECRET,
      { expiresIn: process.env.JWT_EXPIRES_IN || '7d' }
    );
    res.json({ token, user: { id: user.id, email: user.email, role: user.role } });
  } catch (e) { next(e); }
});

module.exports = router;
