"use client";

import { useState, useEffect, useCallback } from "react";
import { SocketProvider, useSocket } from "@/context/SocketProvider";
import LoginPage from "@/components/LoginPage";
import DeviceCard from "@/components/DeviceCard";

const SERVER_URL = process.env.NEXT_PUBLIC_SERVER_URL || "http://localhost:4000";

// ── Inner dashboard (needs socket context) ────────────────────────────────────
function Dashboard({ token, user, onLogout }) {
  const { connected } = useSocket();
  const [devices, setDevices] = useState([]);
  const [sessions, setSessions] = useState([]);
  const [report, setReport] = useState(null);
  const [tab, setTab] = useState("devices"); // devices | sessions | reports
  const [loading, setLoading] = useState(true);

  const authHeaders = { Authorization: `Bearer ${token}` };

  const fetchAll = useCallback(async () => {
    try {
      const [devRes, sessRes] = await Promise.all([
        fetch(`${SERVER_URL}/devices`, { headers: authHeaders }),
        fetch(`${SERVER_URL}/sessions`, { headers: authHeaders }),
      ]);
      setDevices(await devRes.json());
      const s = await sessRes.json();
      setSessions(Array.isArray(s) ? s : [s].filter(Boolean));
    } catch {}
    setLoading(false);
  }, [token]);

  const fetchReport = useCallback(async () => {
    const today = new Date().toISOString().slice(0, 10);
    try {
      const r = await fetch(`${SERVER_URL}/reports/daily?date=${today}`, { headers: authHeaders });
      setReport(await r.json());
    } catch {}
  }, [token]);

  useEffect(() => { fetchAll(); fetchReport(); }, [fetchAll, fetchReport]);

  const onlineCount   = devices.filter(d => d.status !== "offline").length;
  const activeCount   = devices.filter(d => d.status === "in_session").length;
  const completedToday = report?.total_sessions ?? 0;
  const minutesToday   = report?.total_minutes  ?? 0;

  return (
    <div className="min-h-screen bg-gray-950 text-white">
      {/* Navbar */}
      <header className="border-b border-gray-800 bg-gray-950/80 backdrop-blur sticky top-0 z-50">
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
            <div className="flex items-center gap-1.5">
              <span className={`w-2 h-2 rounded-full ${connected ? "bg-emerald-400 animate-pulse" : "bg-gray-600"}`}/>
              <span className="text-xs text-gray-400">{connected ? "Live" : "Offline"}</span>
            </div>
            <span className="text-xs text-gray-500">{user?.email}</span>
            <button onClick={onLogout} className="text-xs text-gray-500 hover:text-white transition">Sign out</button>
          </div>
        </div>
      </header>

      <main className="max-w-7xl mx-auto px-4 py-8">
        {/* Stats Row */}
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-8">
          {[
            { label: "PCs Online",    value: onlineCount,     color: "text-emerald-400" },
            { label: "In Session",    value: activeCount,     color: "text-amber-400"   },
            { label: "Sessions Today",value: completedToday,  color: "text-blue-400"    },
            { label: "Minutes Today", value: minutesToday.toFixed(1), color: "text-purple-400" },
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
            <button key={t} onClick={() => setTab(t)}
              className={`px-4 py-1.5 rounded-lg text-sm font-medium capitalize transition-colors
                ${tab === t ? "bg-red-600 text-white" : "text-gray-400 hover:text-white"}`}>
              {t}
            </button>
          ))}
        </div>

        {/* ── Devices tab ──────────────────────────────────────────────────── */}
        {tab === "devices" && (
          <div>
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">Devices <span className="text-gray-500 text-sm font-normal">({devices.length})</span></h2>
              <button onClick={fetchAll} className="text-xs text-gray-500 hover:text-white transition">↺ Refresh</button>
            </div>
            {loading ? (
              <div className="text-center py-12 text-gray-600">Loading…</div>
            ) : devices.length === 0 ? (
              <div className="text-center py-12 text-gray-600">No devices registered yet.</div>
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                {devices.map(d => (
                  <DeviceCard key={d.id} device={d} token={token} onSessionChange={() => { fetchAll(); fetchReport(); }} />
                ))}
              </div>
            )}
          </div>
        )}

        {/* ── Sessions tab ──────────────────────────────────────────────────── */}
        {tab === "sessions" && (
          <div>
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">Sessions <span className="text-gray-500 text-sm font-normal">({sessions.length})</span></h2>
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
                      <td className="px-4 py-3 text-gray-400">{s.actual_duration_min ? `${parseFloat(s.actual_duration_min).toFixed(1)} min` : "—"}</td>
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

        {/* ── Reports tab ──────────────────────────────────────────────────── */}
        {tab === "reports" && (
          <div className="space-y-6">
            <div className="flex items-center justify-between">
              <h2 className="text-lg font-semibold">Daily Report — {new Date().toLocaleDateString()}</h2>
              <a href={`${SERVER_URL}/reports/export/csv?from=2026-01-01&to=2099-01-01`}
                className="text-xs bg-gray-800 hover:bg-gray-700 border border-gray-700 text-gray-300 px-3 py-1.5 rounded-lg transition-colors">
                ↓ Export CSV
              </a>
            </div>

            <div className="grid grid-cols-2 gap-4">
              <div className="bg-gray-900 border border-gray-800 rounded-2xl px-6 py-5">
                <p className="text-gray-500 text-xs mb-2">Total Sessions</p>
                <p className="text-4xl font-bold text-blue-400">{report?.total_sessions ?? "—"}</p>
              </div>
              <div className="bg-gray-900 border border-gray-800 rounded-2xl px-6 py-5">
                <p className="text-gray-500 text-xs mb-2">Total Minutes</p>
                <p className="text-4xl font-bold text-purple-400">{report?.total_minutes ? parseFloat(report.total_minutes).toFixed(1) : "—"}</p>
              </div>
            </div>

            {report?.per_device && (() => {
              let pd = {};
              try { pd = JSON.parse(report.per_device); } catch {}
              const entries = Object.entries(pd);
              if (!entries.length) return <p className="text-gray-600 text-sm">No completed sessions today.</p>;
              return (
                <div className="bg-gray-900 border border-gray-800 rounded-2xl overflow-hidden">
                  <div className="px-5 py-3 border-b border-gray-800">
                    <p className="text-sm font-medium">Per Device Breakdown</p>
                  </div>
                  <div className="divide-y divide-gray-800">
                    {entries.map(([id, info]) => (
                      <div key={id} className="px-5 py-4 flex items-center justify-between">
                        <span className="text-white text-sm">{info.display_name}</span>
                        <div className="flex gap-6 text-sm">
                          <span className="text-gray-400">{info.sessions} sessions</span>
                          <span className="text-purple-400 font-medium">{parseFloat(info.minutes).toFixed(1)} min</span>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              );
            })()}
          </div>
        )}
      </main>
    </div>
  );
}

// ── Root page (auth gate) ─────────────────────────────────────────────────────
export default function Page() {
  const [token, setToken] = useState(null);
  const [user, setUser] = useState(null);

  useEffect(() => {
    const t = localStorage.getItem("ac_token");
    const u = localStorage.getItem("ac_user");
    if (t && u) { setToken(t); setUser(JSON.parse(u)); }
  }, []);

  function handleLogin(t, u) {
    localStorage.setItem("ac_token", t);
    localStorage.setItem("ac_user", JSON.stringify(u));
    setToken(t);
    setUser(u);
  }

  function handleLogout() {
    localStorage.removeItem("ac_token");
    localStorage.removeItem("ac_user");
    setToken(null);
    setUser(null);
  }

  if (!token) return <LoginPage onLogin={handleLogin} />;

  return (
    <SocketProvider token={token}>
      <Dashboard token={token} user={user} onLogout={handleLogout} />
    </SocketProvider>
  );
}
