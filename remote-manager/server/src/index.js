require('dotenv').config();
const express    = require('express');
const http       = require('http');
const helmet     = require('helmet');
const cors       = require('cors');
const rateLimit  = require('express-rate-limit');
const { Server } = require('socket.io');
const cron       = require('node-cron');

const logger          = require('./utils/logger');
const { connectDb }   = require('./db/pool');
const deviceRoutes    = require('./routes/devices');
const sessionRoutes   = require('./routes/sessions');
const reportRoutes    = require('./routes/reports');
const authRoutes      = require('./routes/auth');
const { registerSocketHandlers } = require('./sockets/socketHandler');
const reportingService = require('./services/reportingService');

// ─── App ─────────────────────────────────────────────────────────────────────
const app    = express();
const server = http.createServer(app);

// ─── Socket.IO ───────────────────────────────────────────────────────────────
const io = new Server(server, {
  cors: { origin: '*', methods: ['GET', 'POST'] },
  pingInterval: 10000,
  pingTimeout: 5000,
});

// Expose io globally so controllers can emit events
app.set('io', io);

// ─── Middleware ───────────────────────────────────────────────────────────────
app.use(helmet());
app.use(cors());
app.use(express.json());
app.use(rateLimit({ windowMs: 60_000, max: 200 }));

// ─── Routes ───────────────────────────────────────────────────────────────────
app.use('/auth',     authRoutes);
app.use('/devices',  deviceRoutes);
app.use('/sessions', sessionRoutes);
app.use('/reports',  reportRoutes);

app.get('/health', (_req, res) => res.json({ status: 'ok', ts: new Date() }));

// ─── Error handler ────────────────────────────────────────────────────────────
app.use((err, _req, res, _next) => {
  logger.error(err.message, { stack: err.stack });
  res.status(err.status || 500).json({ error: err.message || 'Internal Server Error' });
});

// ─── Socket handlers ──────────────────────────────────────────────────────────
registerSocketHandlers(io);

// ─── Cron: daily report ───────────────────────────────────────────────────────
cron.schedule(process.env.REPORT_CRON || '0 0 * * *', async () => {
  logger.info('[Cron] Generating daily report...');
  try {
    await reportingService.generateDailyReport();
    logger.info('[Cron] Daily report saved.');
  } catch (e) {
    logger.error('[Cron] Report failed:', e.message);
  }
});

// ─── Boot ─────────────────────────────────────────────────────────────────────
const PORT = process.env.PORT || 4000;
(async () => {
  await connectDb();
  server.listen(PORT, () => logger.info(`Server listening on :${PORT}`));
})();
