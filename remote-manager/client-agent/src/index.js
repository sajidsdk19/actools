require('dotenv').config();
const os     = require('os');
const axios  = require('axios');
const { io } = require('socket.io-client');
const logger = require('./logger');
const { launchSession, forceKillGame } = require('./gameProcess');
const { saveToken, loadToken } = require('./tokenStore');

const SERVER_URL   = process.env.SERVER_URL   || 'http://localhost:4000';
const AGENT_SECRET = process.env.AGENT_SECRET || '';
const AC_ROOT      = process.env.AC_ROOT      || 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\assettocorsa';
const MACHINE_NAME = process.env.MACHINE_NAME || os.hostname();

let socket     = null;
let deviceToken = null;

// ── Step 1: Register with server ─────────────────────────────────────────────
async function register() {
  // Try to reuse saved token
  const saved = loadToken();
  if (saved) {
    logger.info(`[Agent] Using stored device token`);
    return saved;
  }

  logger.info(`[Agent] Registering as "${MACHINE_NAME}"…`);
  const { data } = await axios.post(
    `${SERVER_URL}/devices/register`,
    { machineName: MACHINE_NAME, displayName: MACHINE_NAME, acRoot: AC_ROOT },
    { headers: { 'x-agent-secret': AGENT_SECRET } }
  );
  saveToken(data.token);
  logger.info(`[Agent] Registered — device ID: ${data.id}`);
  return data.token;
}

// ── Step 2: Connect via WebSocket ────────────────────────────────────────────
function connect(token) {
  deviceToken = token;

  socket = io(SERVER_URL, {
    auth: { type: 'agent', deviceToken: token },
    reconnection: true,
    reconnectionDelay: 3000,
    reconnectionAttempts: Infinity,
  });

  socket.on('connect', () => {
    logger.info(`[Agent] Connected to server — socket ${socket.id}`);
  });

  socket.on('disconnect', (reason) => {
    logger.warn(`[Agent] Disconnected: ${reason}`);
  });

  socket.on('connect_error', (err) => {
    logger.error(`[Agent] Connection error: ${err.message}`);
  });

  // ── Incoming Commands ───────────────────────────────────────────────────────

  socket.on('START_SESSION', async (payload) => {
    logger.info(`[Agent] START_SESSION received`, payload);
    try {
      await launchSession(payload, socket);
    } catch (err) {
      logger.error(`[Agent] launchSession error: ${err.message}`);
      socket.emit('SESSION_ERROR', { sessionId: payload.sessionId, error: err.message });
    }
  });

  socket.on('STOP_SESSION', async (payload) => {
    logger.warn(`[Agent] STOP_SESSION received`, payload);
    forceKillGame();
  });

  socket.on('FORCE_STOP', async () => {
    logger.warn(`[Agent] FORCE_STOP received — killing game immediately`);
    forceKillGame();
  });
}

// ── Boot ─────────────────────────────────────────────────────────────────────
(async () => {
  try {
    const token = await register();
    connect(token);
  } catch (err) {
    logger.error(`[Agent] Fatal startup error: ${err.message}`);
    process.exit(1);
  }
})();
