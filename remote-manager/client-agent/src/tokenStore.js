const fs   = require('fs');
const path = require('path');

const TOKEN_FILE = path.join(__dirname, '..', '.device_token');

function saveToken(token) {
  fs.writeFileSync(TOKEN_FILE, token, 'utf8');
}

function loadToken() {
  if (!fs.existsSync(TOKEN_FILE)) return null;
  const t = fs.readFileSync(TOKEN_FILE, 'utf8').trim();
  return t || null;
}

module.exports = { saveToken, loadToken };
