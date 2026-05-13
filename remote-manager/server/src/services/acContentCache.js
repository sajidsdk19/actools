/**
 * acContentCache.js
 * -----------------
 * Tiny in-memory store that keeps the last scanned AC content
 * (cars + tracks) per device.
 *
 * Shared between socketHandler (writes on AC_CONTENT event)
 * and devices route (reads on GET /devices/:id/ac-content).
 */

const cache = new Map(); // deviceId (string) → { cars, tracks, scannedAt }

module.exports = {
  /**
   * Store content for a device.
   * @param {string} deviceId
   * @param {{ cars: object[], tracks: object[], scannedAt: string }} content
   */
  set(deviceId, content) {
    cache.set(deviceId, { ...content, cachedAt: new Date().toISOString() });
  },

  /**
   * Retrieve cached content for a device, or null if not present.
   * @param {string} deviceId
   * @returns {{ cars, tracks, scannedAt, cachedAt } | null}
   */
  get(deviceId) {
    return cache.get(deviceId) || null;
  },

  /** Remove entry (e.g. on device disconnect). */
  delete(deviceId) {
    cache.delete(deviceId);
  },

  /** For debugging — dump all entries. */
  all() {
    return Object.fromEntries(cache.entries());
  },
};
