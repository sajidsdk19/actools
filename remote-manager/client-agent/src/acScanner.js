/**
 * acScanner.js
 * ------------
 * Scans the local Assetto Corsa installation and returns the list of available
 * cars and tracks in the same shape the dashboard expects:
 *
 *   cars:   [{ id, name }]
 *   tracks: [{ id, name, layouts: [{ id, name }] }]
 *
 * Car-scanning logic mirrors CarsManager.cs from the AC Tools codebase:
 *  - Skips folders that start with "__cm_tmp_"  (temp/work-in-progress folders)
 *  - Kunos official cars (id starts with "ks_") MUST have ui/ui_car.json to be valid
 *  - Reads the car's display name from ui/ui_car.json → "name" field
 *  - Falls back to the folder id if the JSON is missing or malformed
 */

const fs     = require('fs');
const path   = require('path');
const logger = require('./logger');

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Safely parse JSON; returns null on error. */
function tryParseJson(filePath) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch {
    return null;
  }
}

/** Return an array of immediate child folder names inside `dir`. */
function listFolders(dir) {
  try {
    return fs.readdirSync(dir, { withFileTypes: true })
      .filter(d => d.isDirectory())
      .map(d => d.name);
  } catch {
    return [];
  }
}

// ---------------------------------------------------------------------------
// Car scanning  (mirrors CarsManager.cs)
// ---------------------------------------------------------------------------

/**
 * Scan `<acRoot>/cars` and return a sorted list of car objects.
 * @param {string} acRoot  - Path to the Assetto Corsa root directory
 * @returns {{ id: string, name: string }[]}
 */
function scanCars(acRoot) {
  const carsDir = path.join(acRoot, 'content', 'cars');
  const folders  = listFolders(carsDir);
  const result   = [];

  for (const id of folders) {
    // ── Mirror: Filter() in CarsManager.cs ────────────────────────────────
    // 1. Skip CM temp folders
    if (id.startsWith('__cm_tmp_')) continue;

    const carDir    = path.join(carsDir, id);
    const uiCarJson = path.join(carDir, 'ui', 'ui_car.json');

    // 2. Kunos cars (ks_*) must have ui_car.json — same check as C#
    if (id.startsWith('ks_') && !fs.existsSync(uiCarJson)) continue;

    // ── Read display name ──────────────────────────────────────────────────
    let name = id; // fallback
    const meta = tryParseJson(uiCarJson);
    if (meta?.name && typeof meta.name === 'string' && meta.name.trim()) {
      name = meta.name.trim();
    }

    result.push({ id, name });
  }

  // Sort alphabetically by display name
  result.sort((a, b) => a.name.localeCompare(b.name));
  logger.info(`[AcScanner] Found ${result.length} cars in ${carsDir}`);
  return result;
}

// ---------------------------------------------------------------------------
// Track scanning
// ---------------------------------------------------------------------------

/**
 * Scan `<acRoot>/content/tracks` and return a sorted list of track objects.
 * Each track may have multiple layouts (sub-folders that contain ui_track.json).
 *
 * @param {string} acRoot
 * @returns {{ id: string, name: string, layouts: { id: string, name: string }[] }[]}
 */
function scanTracks(acRoot) {
  const tracksDir = path.join(acRoot, 'content', 'tracks');
  const folders   = listFolders(tracksDir);
  const result    = [];

  for (const id of folders) {
    if (id.startsWith('__cm_tmp_')) continue;

    const trackDir   = path.join(tracksDir, id);
    const uiDir      = path.join(trackDir, 'ui');
    const uiJson     = path.join(uiDir, 'ui_track.json');

    // Detect multi-layout track: if ui/ contains sub-folders with their own
    // ui_track.json, each sub-folder is a layout.
    const layoutFolders = listFolders(uiDir).filter(lf =>
      fs.existsSync(path.join(uiDir, lf, 'ui_track.json'))
    );

    let trackName = id;
    const layouts = [];

    if (layoutFolders.length > 0) {
      // Multi-layout track — read track name from first layout's JSON
      const firstMeta = tryParseJson(path.join(uiDir, layoutFolders[0], 'ui_track.json'));
      if (firstMeta?.name) trackName = firstMeta.name.trim().replace(/\s*[-–]\s*\w.*$/, '').trim() || id;

      for (const lf of layoutFolders) {
        const lMeta = tryParseJson(path.join(uiDir, lf, 'ui_track.json'));
        const lName = lMeta?.name?.trim() || lf;
        layouts.push({ id: lf, name: lName });
      }
      layouts.sort((a, b) => a.name.localeCompare(b.name));

    } else if (fs.existsSync(uiJson)) {
      // Single-layout track
      const meta = tryParseJson(uiJson);
      if (meta?.name) trackName = meta.name.trim() || id;
      // No sub-layouts — leave layouts array empty; dashboard won't show layout picker

    } else {
      // No ui_track.json at all — skip (likely invalid / work-in-progress)
      continue;
    }

    result.push({ id, name: trackName, layouts });
  }

  result.sort((a, b) => a.name.localeCompare(b.name));
  logger.info(`[AcScanner] Found ${result.length} tracks in ${tracksDir}`);
  return result;
}

// ---------------------------------------------------------------------------
// Main export
// ---------------------------------------------------------------------------

/**
 * Scan the AC installation at `acRoot` and return the full content catalogue.
 *
 * @param {string} acRoot
 * @returns {{ cars: object[], tracks: object[], scannedAt: string }}
 */
function scanAcContent(acRoot) {
  if (!acRoot || !fs.existsSync(acRoot)) {
    logger.warn(`[AcScanner] AC_ROOT not found or not set: "${acRoot}"`);
    return { cars: [], tracks: [], scannedAt: new Date().toISOString(), error: 'AC_ROOT not found' };
  }

  logger.info(`[AcScanner] Scanning AC content at: ${acRoot}`);
  const cars      = scanCars(acRoot);
  const tracks    = scanTracks(acRoot);
  const scannedAt = new Date().toISOString();

  logger.info(`[AcScanner] Scan complete — ${cars.length} cars, ${tracks.length} tracks`);
  return { cars, tracks, scannedAt };
}

module.exports = { scanAcContent };
