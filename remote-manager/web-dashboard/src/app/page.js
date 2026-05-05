"use client";

import { useState, useEffect, useCallback } from "react";
import { SocketProvider, useSocket } from "@/context/SocketProvider";
import DeviceCard from "@/components/DeviceCard";

const SERVER_URL = process.env.NEXT_PUBLIC_SERVER_URL || "http://localhost:4000";
const PROTECTED_PASSWORD = "Corsa@2026";

// Local date string YYYY-MM-DD (browser timezone)
function localDateStr(d = new Date()) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

// ── Password Modal ─────────────────────────────────────────────────────────────
function PasswordModal({ targetTab, onSuccess, onCancel }) {
  const [value, setValue] = useState("");
  const [error, setError] = useState(false);
  const [shake, setShake] = useState(false);

  const submit = (e) => {
    e.preventDefault();
    if (value === PROTECTED_PASSWORD) {
      onSuccess();
    } else {
      setError(true);
      setShake(true);
      setValue("");
      setTimeout(() => setShake(false), 500);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm">
      <div
        style={shake ? { animation: "shake 0.4s ease" } : {}}
        className={`bg-gray-900 border ${error ? "border-red-500/60" : "border-gray-700"} rounded-2xl p-8 w-full max-w-sm shadow-2xl`}
      >
        <div className="flex flex-col items-center gap-2 mb-6">
          <div className="w-12 h-12 rounded-xl bg-red-600/20 border border-red-500/30 flex items-center justify-center">
            <svg className="w-6 h-6 text-red-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
            </svg>
          </div>
          <h2 className="text-lg font-bold text-white capitalize">{targetTab}</h2>
          <p className="text-gray-400 text-sm text-center">This section is password protected</p>
        </div>

        <form onSubmit={submit} className="space-y-4">
          <input
            id="tab-password-input"
            type="password"
            autoFocus
            placeholder="Enter password"
            value={value}
            onChange={e => { setValue(e.target.value); setError(false); }}
            className={`w-full bg-gray-800 border ${error ? "border-red-500" : "border-gray-700"} rounded-xl px-4 py-3 text-white placeholder-gray-500 focus:outline-none focus:border-red-500 transition-colors`}
          />
          {error && <p className="text-red-400 text-xs text-center">Incorrect password. Try again.</p>}
          <div className="flex gap-3">
            <button type="button" onClick={onCancel}
              className="flex-1 py-2.5 rounded-xl border border-gray-700 text-gray-400 hover:text-white text-sm font-medium transition-colors">
              Cancel
            </button>
            <button type="submit"
              className="flex-1 py-2.5 rounded-xl bg-red-600 hover:bg-red-500 text-white text-sm font-semibold transition-colors">
              Unlock
            </button>
          </div>
        </form>
      </div>
      <style>{`
        @keyframes shake {
          0%,100%{transform:translateX(0)}
          20%{transform:translateX(-8px)}
          40%{transform:translateX(8px)}
          60%{transform:translateX(-6px)}
          80%{transform:translateX(6px)}
        }
      `}</style>
    </div>
  );
}

// ── Inner dashboard ────────────────────────────────────────────────────────────
function Dashboard({ token }) {
  const { connected, socket } = useSocket();
  const [devices, setDevices]       = useState([]);
  const [sessions, setSessions]     = useState([]);
  const [report, setReport]         = useState(null);
  const [reportDate, setReportDate] = useState(localDateStr());
  const [tab, setTab]               = useState("devices");
  const [loading, setLoading]       = useState(true);

  // Password protection
  const [unlocked, setUnlocked]     = useState(false);
  const [pendingTab, setPendingTab] = useState(null);
  const PROTECTED_TABS = ["sessions", "reports"];

  const authHeaders = { Authorization: `Bearer ${token}` };

  const fetchAll = useCallback(async () => {
    try {
      const [devRes, sessRes] = await Promise.all([
        fetch(`${SERVER_URL}/devices`,  { headers: authHeaders }),
        fetch(`${SERVER_URL}/sessions`, { headers: authHeaders }),
      ]);
      setDevices(await devRes.json());
      const s = await sessRes.json();
      setSessions(Array.isArray(s) ? s : [s].filter(Boolean));
    } catch {}
    setLoading(false);
  }, [token]);

  const fetchReport = useCallback(async (date) => {
    const d = date || reportDate;
    try {
      const r = await fetch(`${SERVER_URL}/reports/daily?date=${d}`, { headers: authHeaders });
      setReport(await r.json());
    } catch {}
  }, [token, reportDate]);

  useEffect(() => { fetchAll(); fetchReport(); }, [fetchAll, fetchReport]);

  // Refresh report when date picker changes
  useEffect(() => { fetchReport(reportDate); }, [reportDate]);

  // Refresh device list when any agent connects / disconnects
  useEffect(() => {
    if (!socket) return;
    const refresh = () => fetchAll();
    socket.on("device_connected",    refresh);
    socket.on("device_disconnected", refresh);
    return () => {
      socket.off("device_connected",    refresh);
      socket.off("device_disconnected", refresh);
    };
  }, [socket, fetchAll]);

  const onlineCount    = devices.filter(d => d.status !== "offline").length;
  const activeCount    = devices.filter(d => d.status === "in_session").length;
  const completedToday = report?.total_sessions ?? 0;
  const minutesToday   = Number(report?.total_minutes ?? 0);

  // Tab click — show password modal for protected tabs if not unlocked
  const handleTabClick = (t) => {
    if (PROTECTED_TABS.includes(t) && !unlocked) {
      setPendingTab(t);
    } else {
      setTab(t);
    }
  };

  const handleUnlockSuccess = () => {
    setUnlocked(true);
    if (pendingTab) { setTab(pendingTab); setPendingTab(null); }
  };

  // Per-device breakdown from report
  let perDevice = {};
  try { if (report?.per_device) perDevice = JSON.parse(report.per_device); } catch {}
  const perDeviceEntries = Object.entries(perDevice);

  return (
    <div className="min-h-screen bg-gray-950 text-white">

      {/* Password modal */}
      {pendingTab && (
        <PasswordModal
          targetTab={pendingTab}
          onSuccess={handleUnlockSuccess}
          onCancel={() => setPendingTab(null)}
        />
      )}

      {/* Navbar */}
      <header className="border-b border-gray-800 bg-gray-950/80 backdrop-blur sticky top-0 z-40">
        <div className="max-w-7xl mx-auto px-4 h-14 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="w-7 h-7 rounded-lg bg-red-600 flex items-center justify-center">
              <svg className="w-4 h-4 text-white" fill="currentColor" viewBox="0 0 24 24">
                <path d="M13 10V3L4 14h7v7l9-11h-7z"/>
              </svg>
            </div>
            <span className="font-bold text-sm">AC Remote Manager</span>
          </div>
          <div className="flex items-center gap-4">
            {unlocked && (
              <button onClick={() => { setUnlocked(false); setTab("devices"); }}
                className="text-xs text-gray-500 hover:text-red-400 transition flex items-center gap-1">
                <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                    d="M8 11V7a4 4 0 118 0m-4 8v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2z"/>
                </svg>
                Lock
              </button>
            )}
            <div className="flex items-center gap-1.5">
              <span className={`w-2 h-2 rounded-full ${connected ? "bg-emerald-400 animate-pulse" : "bg-gray-600"}`}/>
              <span className="text-xs text-gray-400">{connected ? "Live" : "Offline"}</span>
            </div>
          </div>
        </div>
      </header>

      <main className="max-w-7xl mx-auto px-4 py-8">

        {/* Stats Row */}
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-8">
          {[
            { label: "PCs Online",     value: onlineCount,           color: "text-emerald-400" },
            { label: "In Session",     value: activeCount,           color: "text-amber-400"   },
            { label: "Sessions Today", value: completedToday,        color: "text-blue-400"    },
            { label: "Minutes Today",  value: minutesToday.toFixed(1), color: "text-purple-400" },
          ].map(s => (
            <div key={s.label} className="bg-gray-900 border border-gray-800 rounded-2xl px-5 py-4">
              <p className="text-gray-500 text-xs mb-1">{s.label}</p>
              <p className={`text-3xl font-bold ${s.color}`}>{s.value}</p>
            </div>
          ))}
        </div>

        {/* Tabs */}
        <div className="flex gap-1 mb-6 bg-gray-900 border border-gray-800 rounded-xl p-1 w-fit">
          {["devices", "sessions", "reports"].map(t => (
            <button key={t} id={`tab-btn-${t}`} onClick={() => handleTabClick(t)}
              className={`px-4 py-1.5 rounded-lg text-sm font-medium capitalize transition-colors flex items-center gap-1.5
                ${tab === t ? "bg-red-600 text-white" : "text-gray-400 hover:text-white"}`}>
              {t}
              {PROTECTED_TABS.includes(t) && !unlocked && (
                <svg className="w-3 h-3 opacity-50" fill="currentColor" viewBox="0 0 20 20">
                  <path fillRule="evenodd" clipRule="evenodd"
                    d="M5 9V7a5 5 0 0110 0v2a2 2 0 012 2v5a2 2 0 01-2 2H5a2 2 0 01-2-2v-5a2 2 0 012-2zm8-2v2H7V7a3 3 0 016 0z"/>
                </svg>
              )}
            </button>
          ))}
        </div>

        {/* ── Devices tab ──────────────────────────────────────────────────────── */}
        {tab === "devices" && (
          <div>
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">
                Devices <span className="text-gray-500 text-sm font-normal">({devices.length})</span>
              </h2>
              <button onClick={fetchAll} className="text-xs text-gray-500 hover:text-white transition">↺ Refresh</button>
            </div>
            {loading ? (
              <div className="text-center py-12 text-gray-600">Loading…</div>
            ) : devices.length === 0 ? (
              <div className="text-center py-12 text-gray-600">No devices registered yet.</div>
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                {devices.map(d => (
                  <DeviceCard key={d.id} device={d} token={token}
                    onSessionChange={() => { fetchAll(); fetchReport(reportDate); }} />
                ))}
              </div>
            )}
          </div>
        )}

        {/* ── Sessions tab ─────────────────────────────────────────────────────── */}
        {tab === "sessions" && (
          <div>
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">
                Sessions <span className="text-gray-500 text-sm font-normal">({sessions.length})</span>
              </h2>
              <button onClick={fetchAll} className="text-xs text-gray-500 hover:text-white transition">↺ Refresh</button>
            </div>
            <div className="bg-gray-900 border border-gray-800 rounded-2xl overflow-hidden">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-gray-800">
                    {["Device","Car","Track","Mode","Duration","Status","Timer End"].map(h => (
                      <th key={h} className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-800">
                  {sessions.length === 0 && (
                    <tr><td colSpan={7} className="text-center py-8 text-gray-600">No sessions yet</td></tr>
                  )}
                  {sessions.map(s => (
                    <tr key={s.id} className="hover:bg-gray-800/50 transition-colors">
                      <td className="px-4 py-3 text-white">{s.device_name || "—"}</td>
                      <td className="px-4 py-3 text-gray-300">{s.car_id}</td>
                      <td className="px-4 py-3 text-gray-300">{s.track_id}</td>
                      <td className="px-4 py-3 text-gray-400">{s.mode}</td>
                      <td className="px-4 py-3 text-gray-400">
                        {s.actual_duration_min ? `${parseFloat(s.actual_duration_min).toFixed(1)} min` : "—"}
                      </td>
                      <td className="px-4 py-3">
                        <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium
                          ${s.status === "completed" ? "bg-emerald-500/15 text-emerald-400" :
                            s.status === "running"   ? "bg-amber-500/15 text-amber-400" :
                            s.status === "error"     ? "bg-red-500/15 text-red-400" :
                            "bg-gray-700 text-gray-400"}`}>
                          {s.status}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-gray-500">{s.timer_ended ? "✓" : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {/* ── Reports tab ──────────────────────────────────────────────────────── */}
        {tab === "reports" && (
          <div className="space-y-6">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="text-lg font-semibold">Daily Report</h2>
              <div className="flex items-center gap-3">
                <div className="flex items-center gap-2">
                  <label className="text-xs text-gray-500">Date</label>
                  <input
                    id="report-date-picker"
                    type="date"
                    value={reportDate}
                    max={localDateStr()}
                    onChange={e => setReportDate(e.target.value)}
                    className="bg-gray-800 border border-gray-700 rounded-lg px-3 py-1.5 text-sm text-white focus:outline-none focus:border-red-500"
                  />
                </div>
                <button onClick={() => fetchReport(reportDate)}
                  className="text-xs text-gray-500 hover:text-white transition">↺ Refresh</button>
                <a href={`${SERVER_URL}/reports/export/csv?from=2026-01-01&to=2099-01-01`}
                  className="text-xs bg-gray-800 hover:bg-gray-700 border border-gray-700 text-gray-300 px-3 py-1.5 rounded-lg transition-colors">
                  ↓ Export CSV
                </a>
              </div>
            </div>

            {/* Summary cards */}
            <div className="grid grid-cols-2 gap-4">
              <div className="bg-gray-900 border border-gray-800 rounded-2xl px-6 py-5">
                <p className="text-gray-500 text-xs mb-2 uppercase tracking-wide">Total Sessions</p>
                <p className="text-4xl font-bold text-blue-400">{report?.total_sessions ?? "—"}</p>
                <p className="text-gray-600 text-xs mt-1">{reportDate}</p>
              </div>
              <div className="bg-gray-900 border border-gray-800 rounded-2xl px-6 py-5">
                <p className="text-gray-500 text-xs mb-2 uppercase tracking-wide">Total Minutes</p>
                <p className="text-4xl font-bold text-purple-400">
                  {report?.total_minutes ? Number(report.total_minutes).toFixed(1) : "—"}
                </p>
                <p className="text-gray-600 text-xs mt-1">
                  {report?.total_minutes ? `≈ ${(Number(report.total_minutes)/60).toFixed(1)} hrs` : ""}
                </p>
              </div>
            </div>

            {/* Per-device breakdown */}
            {perDeviceEntries.length > 0 ? (
              <div className="bg-gray-900 border border-gray-800 rounded-2xl overflow-hidden">
                <div className="px-5 py-3 border-b border-gray-800 flex items-center justify-between">
                  <p className="text-sm font-medium">Per Device Breakdown</p>
                  <span className="text-xs text-gray-500">{perDeviceEntries.length} device{perDeviceEntries.length !== 1 ? "s" : ""}</span>
                </div>
                <div className="divide-y divide-gray-800">
                  {perDeviceEntries.map(([id, info]) => (
                    <div key={id} className="px-5 py-4 flex items-center justify-between hover:bg-gray-800/40 transition-colors">
                      <span className="text-white text-sm font-medium">{info.display_name || id}</span>
                      <div className="flex gap-6 text-sm">
                        <span className="text-gray-400">{info.sessions} session{info.sessions !== 1 ? "s" : ""}</span>
                        <span className="text-purple-400 font-semibold">{Number(info.minutes).toFixed(1)} min</span>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            ) : (
              <div className="bg-gray-900 border border-gray-800 rounded-2xl px-6 py-10 text-center">
                <p className="text-3xl mb-2">📊</p>
                <p className="text-gray-400 text-sm">No completed sessions on {reportDate}</p>
                <p className="text-gray-600 text-xs mt-1">Try selecting a different date</p>
              </div>
            )}

            {/* All completed sessions for context */}
            {sessions.filter(s => s.status === "completed").length > 0 && (
              <div className="bg-gray-900 border border-gray-800 rounded-2xl overflow-hidden">
                <div className="px-5 py-3 border-b border-gray-800">
                  <p className="text-sm font-medium">All Completed Sessions</p>
                </div>
                <div className="overflow-x-auto">
                  <table className="w-full text-xs">
                    <thead>
                      <tr className="border-b border-gray-800">
                        {["Device","Car","Track","Duration","Result"].map(h => (
                          <th key={h} className="px-4 py-2.5 text-left font-medium text-gray-500 uppercase tracking-wider">{h}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-800">
                      {sessions.filter(s => s.status === "completed").map(s => (
                        <tr key={s.id} className="hover:bg-gray-800/40 transition-colors">
                          <td className="px-4 py-3 text-white">{s.device_name || "—"}</td>
                          <td className="px-4 py-3 text-gray-300">{s.car_id}</td>
                          <td className="px-4 py-3 text-gray-300">{s.track_id}</td>
                          <td className="px-4 py-3 text-purple-400 font-medium">
                            {s.actual_duration_min ? `${Number(s.actual_duration_min).toFixed(1)} min` : "—"}
                          </td>
                          <td className="px-4 py-3 text-emerald-400">{s.timer_ended ? "✓ Timer" : "Manual"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
        )}
      </main>
    </div>
  );
}

// ── Root page ──────────────────────────────────────────────────────────────────
export default function Page() {
  const [token, setToken] = useState(null);
  const [error, setError] = useState(null);

  useEffect(() => {
    const SERVER = process.env.NEXT_PUBLIC_SERVER_URL || "http://localhost:4000";
    fetch(`${SERVER}/auth/bypass-token`)
      .then(r => r.json())
      .then(d => {
        if (d.token) setToken(d.token);
        else setError("Server bypass auth not enabled. Check BYPASS_AUTH=true in server .env");
      })
      .catch(() => setError("Cannot reach server at " + SERVER));
  }, []);

  if (error) return (
    <div className="min-h-screen bg-gray-950 text-white flex items-center justify-center">
      <div className="text-center space-y-3">
        <div className="text-4xl">⚡</div>
        <p className="text-red-400 text-sm">{error}</p>
        <button onClick={() => window.location.reload()}
          className="text-xs text-gray-500 hover:text-white border border-gray-700 px-3 py-1 rounded-lg">
          Retry
        </button>
      </div>
    </div>
  );

  if (!token) return (
    <div className="min-h-screen bg-gray-950 text-white flex items-center justify-center">
      <div className="text-center space-y-3">
        <div className="text-4xl animate-pulse">⚡</div>
        <p className="text-gray-500 text-sm">Connecting to server…</p>
      </div>
    </div>
  );

  return (
    <SocketProvider token={token}>
      <Dashboard token={token} />
    </SocketProvider>
  );
}
