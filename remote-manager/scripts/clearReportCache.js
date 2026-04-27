// Clears all cached daily_reports so they regenerate fresh
const { DatabaseSync } = require('node:sqlite');
const path = require('path');
const db = new DatabaseSync(path.join(__dirname, '../server/data/ac_manager.db'));
const result = db.prepare('DELETE FROM daily_reports').run();
console.log('Cleared', result.changes, 'cached daily reports');
db.close();
