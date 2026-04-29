# AC Remote Manager — System Overview & Multi-PC Setup Guide

---

## How the System Works

This is a **remote arcade management system** for Assetto Corsa. It lets one "boss PC" (the server) control and monitor multiple gaming PCs over a local WiFi network.

### The 4 Pieces

```
┌──────────────────────────────────────────────────────────────────┐
│                      SERVER PC (your "boss" machine)             │
│                                                                  │
│  ┌─────────────────────┐    ┌──────────────────────────────────┐ │
│  │  Node.js Server     │    │  Web Dashboard (Next.js)         │ │
│  │  Port :4000         │◄───│  http://localhost:3000           │ │
│  │  Express + Socket.IO│    │  → Operator sees all PCs here    │ │
│  │  + PostgreSQL DB    │    └──────────────────────────────────┘ │
│  └────────┬────────────┘                                         │
└───────────┼──────────────────────────────────────────────────────┘
            │  WebSocket (Socket.IO) over LAN
            │
   ┌─────────▼─────────────────────────────────┐
   │              LAN / WiFi Network            │
   └──────┬────────────────────────┬────────────┘
          │                        │
   ┌──────▼──────────┐     ┌───────▼─────────┐
   │  Gaming PC 1    │     │  Gaming PC 2    │  (+ more)
   │                 │     │                 │
   │  client-agent   │     │  client-agent   │
   │  (Node.js)      │     │  (Node.js)      │
   │       │         │     │       │         │
   │  AcAgent.exe    │     │  AcAgent.exe    │
   │  (C# launcher)  │     │  (C# launcher)  │
   │       │         │     │       │         │
   │  Assetto Corsa  │     │  Assetto Corsa  │
   └─────────────────┘     └─────────────────┘
```

### What Each Piece Does

| Component | Where It Runs | What It Does |
|---|---|---|
| **Node.js Server** | Server PC | Central brain — stores sessions in PostgreSQL, routes commands via Socket.IO |
| **Web Dashboard** | Server PC (browser) | Operator UI — see all PCs, start/stop sessions, view reports |
| **client-agent** | Every Gaming PC | Listens for commands from server, launches/kills AcAgent.exe |
| **AcAgent.exe** | Every Gaming PC | Actually launches Assetto Corsa, tracks time, kills game when session ends |

### How a Session Flow Works

```
Operator clicks "Start Session" on Dashboard
    → Server sends START_SESSION via WebSocket to the target PC's client-agent
    → client-agent receives command and launches AcAgent.exe
    → AcAgent.exe launches Assetto Corsa game
    → AcAgent.exe sends TIMER_UPDATE every second back to server
    → Dashboard shows live countdown timer
    → When time expires → AcAgent.exe kills the game process
    → client-agent sends SESSION_ENDED back to server
    → Server logs the session to PostgreSQL database
```

---

## Step-by-Step: Running on Multiple Laptops Under Same WiFi

### Prerequisites

- All PCs (server + gaming laptops) on the **same WiFi router**
- Server PC needs: **Node.js 18+**, **PostgreSQL 14+**
- Each gaming laptop needs: **Node.js 18+**, **Assetto Corsa installed**

---

### STEP 1 — Find Your Server PC's Local IP

On the server PC, open PowerShell and run:
```powershell
ipconfig
```
Look for `IPv4 Address` under your WiFi adapter. It will look like:
```
192.168.1.100
```
**Write this down — all gaming PCs will use this address.**

---

### STEP 2 — Set Up the Server (on the "boss" PC only)

#### 2a. Install PostgreSQL
Download from https://www.postgresql.org/download/windows/ and install.

#### 2b. Create the database
Open **pgAdmin** or a terminal and run:
```sql
CREATE DATABASE ac_manager;
```

#### 2c. Configure the server
```bash
cd c:\Users\sajid\Desktop\Freelacing\actools\remote-manager\server
copy .env.example .env
```

Open `.env` and fill in:
```env
PORT=4000
DATABASE_URL=postgresql://postgres:YOUR_PASSWORD@localhost:5432/ac_manager
JWT_SECRET=some_long_random_string_minimum_32_chars
AGENT_SECRET=MySharedSecret123          ← remember this value!
```

#### 2d. Initialize the database schema
```bash
psql -U postgres -d ac_manager -f src/db/schema.sql
```

#### 2e. Install dependencies and start the server
```bash
npm install
npm run dev
```

You should see: `Server running on port 4000`

> **Firewall**: Allow port 4000 inbound on the server PC's Windows Firewall.
> `netsh advfirewall firewall add rule name="AcServer" dir=in action=allow protocol=TCP localport=4000`

---

### STEP 3 — Set Up the Web Dashboard (on the "boss" PC only)

```bash
cd c:\Users\sajid\Desktop\Freelacing\actools\remote-manager\web-dashboard
copy .env.local.example .env.local
```

Open `.env.local` and set:
```env
NEXT_PUBLIC_API_URL=http://localhost:4000
NEXT_PUBLIC_SOCKET_URL=http://localhost:4000
```

Then start:
```bash
npm install
npm run dev
```

Open browser at `http://localhost:3000` — this is your control panel.

---

### STEP 4 — Deploy the Agent on EACH Gaming Laptop

Do this on **each** gaming PC separately.

#### 4a. Copy the deployment folder
Copy the entire `pc-agent-deploy` folder to each gaming laptop at:
```
C:\AcRemoteAgent\
```
(You can do this via USB drive or shared network folder)

#### 4b. Run the setup script
Double-click `1-setup.bat` — it will:
- Check Node.js is installed
- Install npm dependencies
- Open `.env` in Notepad for you to fill in

#### 4c. Fill in the .env (CRITICAL — different for each PC)

```env
SERVER_URL=http://192.168.1.100:4000     ← server PC's LAN IP from Step 1
AGENT_SECRET=MySharedSecret123           ← EXACT same as server's AGENT_SECRET
MACHINE_NAME=Gaming-PC-1                 ← UNIQUE name per PC (PC-1, PC-2, etc.)
AC_ROOT=C:\Program Files (x86)\Steam\steamapps\common\assettocorsa
AC_AGENT_EXE=C:\AcRemoteAgent\AcAgent\AcAgent.exe
```

> ⚠️ **Every gaming PC MUST have a different `MACHINE_NAME`**

#### 4d. Test it (optional)
Double-click `2-start-agent.bat`. You should see:
```
[Agent] Registering as "Gaming-PC-1"...
[Agent] Registered — device ID: xxxxxxxx-...
[Agent] Connected to server
```
And in the web dashboard, a new PC card will appear!

#### 4e. Install as Windows Service (for production)
Right-click `3-install-service.bat` → **Run as Administrator**

The agent will now auto-start on boot and reconnect automatically.

---

### STEP 5 — Register an Operator Account

Once the server is running, create your admin account via the API (one time only):
```bash
curl -X POST http://localhost:4000/auth/register \
  -H "Content-Type: application/json" \
  -d "{\"username\":\"admin\",\"password\":\"yourpassword\"}"
```
Then log in on the web dashboard.

---

## Network Summary

```
WiFi Router: 192.168.1.1
│
├── Server PC:        192.168.1.100  ← runs Node.js :4000 + Next.js :3000
├── Gaming Laptop 1:  192.168.1.101  ← runs client-agent → connects to .100:4000
├── Gaming Laptop 2:  192.168.1.102  ← runs client-agent → connects to .100:4000
└── Gaming Laptop 3:  192.168.1.103  ← runs client-agent → connects to .100:4000
```

All communication flows **from gaming PCs → server** using WebSockets.
The operator controls everything from the **dashboard on the server PC**.

---

## Quick Troubleshooting

| Problem | Fix |
|---|---|
| Gaming PC can't connect to server | Check `SERVER_URL` IP is correct; check firewall allows port 4000 |
| "Unauthorized" error on agent | `AGENT_SECRET` on PC must match `AGENT_SECRET` on server exactly |
| Two PCs show same name in dashboard | Set different `MACHINE_NAME` in each PC's `.env` |
| Game doesn't launch | Check `AC_AGENT_EXE` path; check `AC_ROOT` path |
| Agent connects then immediately drops | Check server logs: `remote-manager/server/logs/` |
