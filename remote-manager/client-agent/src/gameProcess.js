const { spawn, exec } = require('child_process');
const path   = require('path');
const os     = require('os');
const fs     = require('fs');
const logger = require('./logger');

const AC_AGENT_EXE = process.env.AC_AGENT_EXE;   // path to your existing AcAgent.exe
const AC_ROOT      = process.env.AC_ROOT;

// Tracks the currently running AcAgent process
let agentProc = null;
let timerInterval = null;

// ── race.ini direct write (belt-and-braces car selection fix) ────────────────
// AC reads race.ini from Documents\Assetto Corsa\cfg\race.ini at launch.
// Steam Cloud can overwrite this file when the game starts, restoring the
// previously-played car. We write it here (from Node.js) AND lock it read-only
// so Steam cannot override our selection. Unlocked once acs.exe is running.

const RACE_INI_PATH = path.join(
  os.homedir(), 'Documents', 'Assetto Corsa', 'cfg', 'race.ini'
);

/**
 * Resolves the first available skin folder for the given car, or 'default'.
 */
function resolveCarSkin(carId) {
  try {
    const skinsDir = path.join(AC_ROOT, 'content', 'cars', carId, 'skins');
    const skins = fs.readdirSync(skinsDir, { withFileTypes: true })
      .filter(d => d.isDirectory())
      .map(d => d.name)
      .sort();
    return skins[0] || 'default';
  } catch {
    return 'default';
  }
}

/**
 * Writes race.ini with the selected car/track and locks it read-only
 * so Steam Cloud cannot restore the old cached car during launch.
 */
function writeAndLockRaceIni(carId, trackId, trackLayout, mode, durationMinutes) {
  try {
    const skin = resolveCarSkin(carId);
    const sessionType = mode === 'QuickRace' ? 3 : 1;

    const content = [
      '[RACE]',
      `MODEL=${carId}`,
      `SKIN=${skin}`,
      `TRACK=${trackId}`,
      `CONFIG_TRACK=${trackLayout || ''}`,
      'CARS=1',
      'AI_LEVEL=95',
      '',
      '[SESSION_0]',
      `NAME=${mode}`,
      `TYPE=${sessionType}`,
      `DURATION_MINUTES=${durationMinutes}`,
      'SPAWN_SET=HOTLAP_START',
      '',
      '[CAR_0]',
      `MODEL=${carId}`,
      `SKIN=${skin}`,
      'AI_LEVEL=0',
      '',
      '[WEATHER_0]',
      'GRAPHICS=Clear',
      'BASE_TEMPERATURE_AMBIENT=26',
      'BASE_TEMPERATURE_ROAD=32',
      'VARIATION_AMBIENT=0',
      'WIND_BASE_SPEED_MIN=0',
      'WIND_BASE_SPEED_MAX=0',
      'WIND_DIRECTION=0',
      'WIND_DIRECTION_VARIATION=0',
      '',
      '[DYNAMIC_TRACK]',
      'SESSION_START=95',
      'RANDOMNESS=1',
      'LAP_GAIN=2',
      'SESSION_TRANSFER=80',
    ].join('\r\n');

    // Ensure cfg dir exists
    fs.mkdirSync(path.dirname(RACE_INI_PATH), { recursive: true });

    // Remove read-only if already set (from a previous crashed session)
    try { fs.chmodSync(RACE_INI_PATH, 0o666); } catch {}

    fs.writeFileSync(RACE_INI_PATH, content, 'utf8');
    logger.info(`[GameProcess] race.ini written: car=${carId} track=${trackId} skin=${skin}`);

    // Lock read-only — blocks Steam Cloud from overwriting during launch
    fs.chmodSync(RACE_INI_PATH, 0o444);
    logger.info('[GameProcess] race.ini locked read-only (Steam Cloud protection active)');
  } catch (err) {
    logger.error(`[GameProcess] Failed to write/lock race.ini: ${err.message}`);
  }
}

/**
 * Restores race.ini to writable so the game can update it after the session.
 */
function unlockRaceIni() {
  try {
    fs.chmodSync(RACE_INI_PATH, 0o666);
    logger.info('[GameProcess] race.ini unlocked (game can now write to it)');
  } catch {}
}

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

  // Write race.ini from Node.js BEFORE spawning AcAgentCli.exe.
  // This guarantees the correct car is written even if the C# binary
  // has a stale build, and locks it so Steam Cloud can't override it.
  writeAndLockRaceIni(carId, trackId, trackLayout, mode, durationMinutes);

  const startTime = Date.now();

  agentProc = spawn(AC_AGENT_EXE, args, {
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: false,
  });

  let gameStarted     = false;
  let spawnFailed      = false;
  let gameClockTimer   = null;  // fallback if SPACE key is never sent

  // ── Handle spawn failure (e.g. EXE not found) ──────────────────────────────
  agentProc.on('error', (err) => {
    spawnFailed = true;
    if (gameClockTimer) { clearTimeout(gameClockTimer); gameClockTimer = null; }
    logger.error(`[GameProcess] Failed to spawn AcAgent.exe: ${err.message}`);
    stopTimerUpdates();
    unlockRaceIni(); // release lock on spawn failure
    socket.emit('SESSION_ERROR', { sessionId, error: `Spawn failed: ${err.message}` });
    agentProc = null;
  });

  // Helper: start dashboard timer once game is actually playable
  function markGameStarted() {
    if (gameStarted) return;
    gameStarted = true;
    if (gameClockTimer) { clearTimeout(gameClockTimer); gameClockTimer = null; }
    const safeDuration = Math.max(1, durationMinutes || 30);
    socket.emit('SESSION_STARTED', { sessionId });
    startTimerUpdates(socket, sessionId, safeDuration);
  }

  // ── Parse stdout ─────────────────────────────────────────────────────────────
  agentProc.stdout.on('data', (data) => {
    const text = data.toString();
    // Log every line individually at INFO level so TrickyStarter errors are always visible
    for (const line of text.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed) continue;

      // Promote error/warning lines so they stand out
      if (trimmed.includes('FAILED') || trimmed.includes('CRITICAL') || trimmed.includes('Error')) {
        logger.error(`[AcAgent] ${trimmed}`);
      } else {
        logger.info(`[AcAgent] ${trimmed}`);
      }
    }

    const line = text.trim();

    // When acs.exe is confirmed running, arm a 3-min fallback
    // (in case AutoStart is disabled / SPACE is never sent)
    if (line.includes('Game clock started') && !gameClockTimer && !gameStarted) {
      logger.info('[GameProcess] Game clock detected — will start dashboard timer on SPACE or in 3 min.');
      // Game is running and has loaded race.ini — safe to unlock it now
      unlockRaceIni();
      gameClockTimer = setTimeout(() => {
        logger.warn('[GameProcess] SPACE key not detected after 3 min — starting timer as fallback.');
        markGameStarted();
      }, 3 * 60 * 1000);
    }

    // Primary trigger: start timer once the engine SPACE key has been sent
    // i.e. game is fully loaded and car is in the pit lane ready to drive
    if (!gameStarted && line.includes('SPACE sent')) {
      logger.info('[GameProcess] SPACE key detected — starting dashboard timer now.');
      markGameStarted();
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
      unlockRaceIni(); // always release lock when process ends

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
  let killFired = false;

  timerInterval = setInterval(() => {
    const remaining = Math.max(0, Math.round((endMs - Date.now()) / 1000));
    socket.emit('TIMER_UPDATE', { sessionId, remainingSeconds: remaining });

    if (remaining <= 0 && !killFired) {
      killFired = true;
      stopTimerUpdates();

      // Belt-and-braces: AcAgentCli.exe has its own internal timer that should
      // kill the game, but we also force-kill here to guarantee the session ends.
      // Killing agentProc causes its 'close' handler to fire → SESSION_ENDED emitted.
      logger.info('[GameProcess] Timer expired — forcing game kill from client agent.');
      forceKillGame();
    }
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
