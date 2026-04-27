const { spawn, exec } = require('child_process');
const path   = require('path');
const logger = require('./logger');

const AC_AGENT_EXE = process.env.AC_AGENT_EXE;   // path to your existing AcAgent.exe
const AC_ROOT      = process.env.AC_ROOT;

// Tracks the currently running AcAgent process
let agentProc = null;
let timerInterval = null;

/**
 * Launches Assetto Corsa via the existing AcAgent.exe (your C# binary).
 * Emits SESSION_STARTED, TIMER_UPDATE, SESSION_ENDED back on the socket.
 *
 * @param {object} payload  - from START_SESSION event
 * @param {Socket} socket   - Socket.IO client socket for emitting updates
 */
async function launchSession(payload, socket) {
  const {
    sessionId,
    carId,
    trackId,
    trackLayout,
    mode = 'Practice',
    durationMinutes = 30,
    easyAssists = false,
  } = payload;

  if (agentProc) {
    logger.warn('[GameProcess] Session already running — ignoring START_SESSION');
    return;
  }

  // Build CLI args for your existing AcAgent.exe
  const args = [
    '--car',      carId,
    '--track',    trackId,
    '--mode',     mode,
    '--duration', String(durationMinutes),
    '--ac-root',  AC_ROOT,
  ];
  if (trackLayout) args.push('--layout', trackLayout);
  if (easyAssists) args.push('--easy-assists');

  logger.info(`[GameProcess] Launching: ${AC_AGENT_EXE} ${args.join(' ')}`);

  const startTime = Date.now();

  agentProc = spawn(AC_AGENT_EXE, args, {
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: false,
  });

  let gameStarted  = false;
  let spawnFailed  = false;

  // ── Handle spawn failure (e.g. EXE not found) ──────────────────────────────
  agentProc.on('error', (err) => {
    spawnFailed = true;
    logger.error(`[GameProcess] Failed to spawn AcAgent.exe: ${err.message}`);
    stopTimerUpdates();
    socket.emit('SESSION_ERROR', { sessionId, error: `Spawn failed: ${err.message}` });
    agentProc = null;
  });

  // Parse stdout to detect when game actually started

  agentProc.stdout.on('data', (data) => {
    const line = data.toString().trim();
    logger.debug(`[AcAgent] ${line}`);

    // AcAgent logs this when acs.exe is confirmed running
    if (!gameStarted && line.includes('Game clock started')) {
      gameStarted = true;
      socket.emit('SESSION_STARTED', { sessionId });
      startTimerUpdates(socket, sessionId, durationMinutes);
    }

    // Session complete summary line
    if (line.includes('Session Complete')) {
      logger.info('[GameProcess] Session complete detected from stdout');
    }
  });

  agentProc.stderr.on('data', (data) => {
    logger.error(`[AcAgent stderr] ${data.toString().trim()}`);
  });

  return new Promise((resolve) => {
    agentProc.on('close', (code) => {
      stopTimerUpdates();

      // Skip SESSION_ENDED if spawn already failed — SESSION_ERROR was already sent.
      if (spawnFailed) {
        agentProc = null;
        return resolve();
      }

      const durationActual    = (Date.now() - startTime) / 1000 / 60;
      const timerEnded        = code === 0;
      const playerExitedEarly = code !== 0 && !timerEnded;

      logger.info(`[GameProcess] AcAgent exited code=${code}, duration=${durationActual.toFixed(1)}min`);

      socket.emit('SESSION_ENDED', {
        sessionId,
        durationMinutes: parseFloat(durationActual.toFixed(2)),
        timerEnded,
        playerExitedEarly,
      });

      agentProc = null;
      resolve();
    });
  });
}

function startTimerUpdates(socket, sessionId, durationMinutes) {
  const endMs = Date.now() + durationMinutes * 60 * 1000;

  timerInterval = setInterval(() => {
    const remaining = Math.max(0, Math.round((endMs - Date.now()) / 1000));
    socket.emit('TIMER_UPDATE', { sessionId, remainingSeconds: remaining });
    if (remaining <= 0) stopTimerUpdates();
  }, 1000);
}

function stopTimerUpdates() {
  if (timerInterval) {
    clearInterval(timerInterval);
    timerInterval = null;
  }
}

/**
 * Forcefully kills acs.exe, acs_x86.exe, AssettoCorsa.exe
 * Used by STOP_SESSION and FORCE_STOP commands.
 */
function forceKillGame() {
  if (agentProc) {
    try { agentProc.kill('SIGTERM'); } catch {}
  }
  // Belt-and-braces: also kill by name (same as C# KillGame helper)
  for (const name of ['acs', 'acs_x86', 'AssettoCorsa']) {
    exec(`taskkill /F /IM "${name}.exe" /T`, (err) => {
      if (!err) logger.info(`[GameProcess] Killed ${name}.exe`);
    });
  }
  stopTimerUpdates();
  agentProc = null;
}

module.exports = { launchSession, forceKillGame };
