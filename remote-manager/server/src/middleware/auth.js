const jwt    = require('jsonwebtoken');
const logger = require('../utils/logger');

// ── JWT auth (dashboard/mobile users) ────────────────────────────────────────
// If BYPASS_AUTH=true, all requests are treated as authenticated admin.
function requireAuth(req, res, next) {
  if (process.env.BYPASS_AUTH === 'true') {
    req.user = { id: 'admin', email: 'admin@local', role: 'admin' };
    return next();
  }

  const header = req.headers['authorization'] || '';
  const token  = header.startsWith('Bearer ') ? header.slice(7) : null;
  if (!token) return res.status(401).json({ error: 'Missing token' });

  try {
    req.user = jwt.verify(token, process.env.JWT_SECRET);
    next();
  } catch {
    return res.status(401).json({ error: 'Invalid or expired token' });
  }
}

// ── Agent secret (WebSocket auth header + REST fallback) ─────────────────────
function requireAgentSecret(req, res, next) {
  const secret = req.headers['x-agent-secret'];
  if (!secret || secret !== process.env.AGENT_SECRET) {
    logger.warn('[Auth] Bad agent secret from', req.ip);
    return res.status(403).json({ error: 'Forbidden' });
  }
  next();
}

// ── Socket auth bypass ───────────────────────────────────────────────────────
// Returns a static token string for use when BYPASS_AUTH=true.
function getBypassToken() {
  if (process.env.BYPASS_AUTH !== 'true') return null;
  return jwt.sign(
    { id: 'admin', email: 'admin@local', role: 'admin' },
    process.env.JWT_SECRET || 'bypass-secret-key',
    { expiresIn: '365d' }
  );
}

module.exports = { requireAuth, requireAgentSecret, getBypassToken };
