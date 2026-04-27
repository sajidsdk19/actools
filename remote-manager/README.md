# AC Remote Manager — Monorepo

Remote multi-PC session management system for Assetto Corsa.

```
remote-manager/
├── server/                     ← Node.js backend (Express + Socket.IO + PostgreSQL)
│   ├── src/
│   │   ├── index.js            ← Entry point, cron jobs
│   │   ├── db/
│   │   │   ├── pool.js         ← pg connection pool
│   │   │   └── schema.sql      ← PostgreSQL schema
│   │   ├── middleware/
│   │   │   └── auth.js         ← JWT + agent-secret guards
│   │   ├── routes/
│   │   │   ├── auth.js         ← POST /auth/login, /auth/register
│   │   │   ├── devices.js      ← GET/POST /devices
│   │   │   ├── sessions.js     ← POST /sessions/start|stop|force-stop
│   │   │   └── reports.js      ← GET /reports/daily|summary|export/csv
│   │   ├── controllers/
│   │   │   └── sessionController.js
│   │   ├── services/
│   │   │   └── reportingService.js
│   │   ├── sockets/
│   │   │   └── socketHandler.js ← All real-time WS logic
│   │   └── utils/
│   │       └── logger.js
│   ├── .env.example
│   └── package.json
│
├── client-agent/               ← Node.js agent (runs on each gaming PC)
│   ├── src/
│   │   ├── index.js            ← Register + connect + command listener
│   │   ├── gameProcess.js      ← Spawns AcAgent.exe, parses output, kills game
│   │   ├── tokenStore.js       ← Persists device token to disk
│   │   └── logger.js
│   ├── scripts/
│   │   └── installService.js   ← Installs as Windows Service (node-windows)
│   ├── .env.example
│   └── package.json
│
├── web-dashboard/              ← Next.js 14 dashboard
│   ├── src/
│   │   ├── app/
│   │   │   └── dashboard/
│   │   │       └── page.jsx    ← Device grid page
│   │   ├── components/
│   │   │   └── DeviceCard.jsx  ← Per-PC card with timer + controls
│   │   └── lib/
│   │       ├── api.js          ← Axios client
│   │       └── SocketProvider.jsx
│   └── .env.local.example
│
└── mobile-app/                 ← React Native (Expo) app
    ├── src/
    │   ├── screens/
    │   │   └── HomeScreen.js   ← Device list + timer + controls
    │   ├── context/
    │   │   └── SocketContext.js
    │   └── lib/
    │       └── api.js
    └── src/config.js           ← API_URL constant
```

---

## Architecture

```
[Gaming PC 1]          [Gaming PC 2]          [Gaming PC N]
 client-agent  ←─────── Socket.IO ─────────→  client-agent
     │                      │                      │
     └──────────────────────┼──────────────────────┘
                            ▼
                    ┌──────────────┐
                    │  Node.js     │
                    │  Server :4000│
                    │  Express +   │
                    │  Socket.IO   │
                    └──────┬───────┘
                           │ PostgreSQL
                    ┌──────▼───────┐
                    │  Dashboard   │  ← Next.js (web)
                    │  Mobile App  │  ← React Native
                    └──────────────┘
```

---

## WebSocket Events

| Direction          | Event                  | Payload                                      |
|--------------------|------------------------|----------------------------------------------|
| Server → Agent     | `START_SESSION`        | `{ sessionId, carId, trackId, durationMinutes, ... }` |
| Server → Agent     | `STOP_SESSION`         | `{ sessionId }`                              |
| Server → Agent     | `FORCE_STOP`           | `{}`                                         |
| Agent → Server     | `SESSION_STARTED`      | `{ sessionId }`                              |
| Agent → Server     | `TIMER_UPDATE`         | `{ sessionId, remainingSeconds }`            |
| Agent → Server     | `SESSION_ENDED`        | `{ sessionId, durationMinutes, timerEnded, playerExitedEarly }` |
| Agent → Server     | `SESSION_ERROR`        | `{ sessionId, error }`                       |
| Server → Dashboard | `device_connected`     | `{ deviceId, machineName, status }`          |
| Server → Dashboard | `device_disconnected`  | `{ deviceId }`                               |
| Server → Dashboard | `device_status_changed`| `{ deviceId, status }`                       |
| Server → Dashboard | `session_started`      | `{ sessionId, deviceId }`                    |
| Server → Dashboard | `session_ended`        | `{ sessionId, deviceId, durationMinutes }`   |
| Server → Dashboard | `timer_update`         | `{ sessionId, deviceId, remainingSeconds }`  |

---

## Quick Start

### 1. Server
```bash
cd server
cp .env.example .env          # fill DATABASE_URL, JWT_SECRET, AGENT_SECRET
npm install
psql -U postgres -c "CREATE DATABASE ac_manager"
psql -U postgres -d ac_manager -f src/db/schema.sql
npm run dev
```

### 2. Client Agent (each PC)
```bash
cd client-agent
cp .env.example .env          # fill SERVER_URL, AGENT_SECRET, AC_AGENT_EXE
npm install
npm start                      # runs once to register, then stays connected

# Install as Windows Service (run as Admin):
node scripts/installService.js
```

### 3. Web Dashboard
```bash
cd web-dashboard
cp .env.local.example .env.local
npm install
npm run dev                    # http://localhost:3000
```

### 4. Mobile
```bash
cd mobile-app
npm install
npx expo start
```

---

## REST API

| Method | Endpoint                | Auth          | Description              |
|--------|-------------------------|---------------|--------------------------|
| POST   | `/auth/register`        | —             | Create user              |
| POST   | `/auth/login`           | —             | Get JWT                  |
| POST   | `/devices/register`     | Agent secret  | Register PC              |
| GET    | `/devices`              | JWT           | List all devices         |
| GET    | `/devices/:id`          | JWT           | Device detail            |
| POST   | `/sessions/start`       | JWT           | Start a session          |
| POST   | `/sessions/stop`        | JWT           | Graceful stop            |
| POST   | `/sessions/force-stop`  | JWT           | Immediate kill           |
| GET    | `/sessions`             | JWT           | List sessions            |
| GET    | `/reports/daily`        | JWT           | Daily report             |
| GET    | `/reports/summary`      | JWT           | Range summary            |
| GET    | `/reports/export/csv`   | JWT           | CSV export               |

---

## Next Steps to Scale

1. **Multi-tenant** — add `arcade_id` FK to devices/sessions for SaaS
2. **Payment integration** — Stripe for per-minute billing
3. **Queue system** — Bull/BullMQ for reliable command delivery if WS drops
4. **Redis Pub/Sub** — scale to multiple server instances behind a load balancer
5. **Kubernetes** — containerise server + deploy to GKE/EKS
6. **RFID / kiosk** — client-agent reads RFID card → auto-starts session for that player
7. **Leaderboard** — track lap times from `race_out.json` per session
