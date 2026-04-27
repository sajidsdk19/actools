"use client";

import { useState, useEffect, useCallback } from "react";
import { useSocket } from "@/context/SocketProvider";

const SERVER_URL = process.env.NEXT_PUBLIC_SERVER_URL || "http://localhost:4000";

const STATUS_COLOR = {
  online:     "bg-emerald-500",
  offline:    "bg-gray-600",
  in_session: "bg-amber-500",
};

const STATUS_LABEL = {
  online:     "Online",
  offline:    "Offline",
  in_session: "In Session",
};

function fmt(seconds) {
  if (!seconds && seconds !== 0) return "--:--";
  const m = Math.floor(seconds / 60).toString().padStart(2, "0");
  const s = Math.floor(seconds % 60).toString().padStart(2, "0");
  return `${m}:${s}`;
}

export default function DeviceCard({ device, token, onSessionChange }) {
  const { socket } = useSocket();
  const [status, setStatus] = useState(device.status);
  const [remaining, setRemaining] = useState(null);
  const [activeSession, setActiveSession] = useState(null);

  // Form state
  const [car, setCar] = useState("lotus_elise_sc");
  const [track, setTrack] = useState("magione");
  const [duration, setDuration] = useState(30);
  const [mode, setMode] = useState("Practice");
  const [easy, setEasy] = useState(false);
  const [launching, setLaunching] = useState(false);
  const [error, setError] = useState("");
  const [showForm, setShowForm] = useState(false);

  // Real-time socket events
  useEffect(() => {
    if (!socket) return;

    const onStatusChange = ({ deviceId, status: s }) => {
      if (deviceId === device.id) setStatus(s);
    };
    const onConnected = ({ deviceId, status: s }) => {
      if (deviceId === device.id) setStatus(s);
    };
    const onDisconnected = ({ deviceId }) => {
      if (deviceId === device.id) { setStatus("offline"); setRemaining(null); }
    };
    const onTimerUpdate = ({ deviceId, sessionId, remainingSeconds }) => {
      if (deviceId === device.id) setRemaining(remainingSeconds);
    };
    const onSessionStarted = ({ deviceId }) => {
      if (deviceId === device.id) setStatus("in_session");
    };
    const onSessionEnded = ({ deviceId }) => {
      if (deviceId === device.id) { setStatus("online"); setRemaining(null); setActiveSession(null); setLaunching(false); onSessionChange?.(); }
    };
    const onSessionError = ({ deviceId, error: err }) => {
      if (deviceId === device.id) { setStatus("online"); setLaunching(false); setError(`Session error: ${err}`); setRemaining(null); onSessionChange?.(); }
    };

    socket.on("device_status_changed", onStatusChange);
    socket.on("device_connected", onConnected);
    socket.on("device_disconnected", onDisconnected);
    socket.on("timer_update", onTimerUpdate);
    socket.on("session_started", onSessionStarted);
    socket.on("session_ended", onSessionEnded);
    socket.on("session_error", onSessionError);

    return () => {
      socket.off("device_status_changed", onStatusChange);
      socket.off("device_connected", onConnected);
      socket.off("device_disconnected", onDisconnected);
      socket.off("timer_update", onTimerUpdate);
      socket.off("session_started", onSessionStarted);
      socket.off("session_ended", onSessionEnded);
      socket.off("session_error", onSessionError);
    };
  }, [socket, device.id]);

  const startSession = useCallback(async (e) => {
    e.preventDefault();
    setLaunching(true);
    setError("");
    try {
      const res = await fetch(`${SERVER_URL}/sessions/start`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({ deviceId: device.id, carId: car, trackId: track, mode, durationMinutes: Number(duration), easyAssists: easy }),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error);
      setActiveSession(data.id);
      setShowForm(false);
      onSessionChange?.();
    } catch (e) {
      setError(e.message);
      setLaunching(false);
    }
  }, [device.id, token, car, track, mode, duration, easy]);

  const forceStop = useCallback(async () => {
    await fetch(`${SERVER_URL}/sessions/force-stop`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify({ deviceId: device.id }),
    });
  }, [device.id, token]);

  return (
    <div className="bg-gray-900 border border-gray-800 rounded-2xl overflow-hidden transition-all hover:border-gray-700">
      {/* Header */}
      <div className="px-5 py-4 flex items-center justify-between border-b border-gray-800">
        <div className="flex items-center gap-3">
          <div className="w-9 h-9 rounded-xl bg-gray-800 flex items-center justify-center">
            <svg className="w-5 h-5 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
            </svg>
          </div>
          <div>
            <p className="text-white font-semibold text-sm">{device.display_name}</p>
            <p className="text-gray-500 text-xs">{device.machine_name}</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <span className={`w-2 h-2 rounded-full ${STATUS_COLOR[status] || "bg-gray-600"} ${status === "in_session" ? "animate-pulse" : ""}`} />
          <span className="text-xs text-gray-400">{STATUS_LABEL[status] || status}</span>
        </div>
      </div>

      {/* Timer (session active) */}
      {status === "in_session" && (
        <div className="px-5 py-4 bg-amber-500/5 border-b border-amber-500/20">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-amber-400 text-xs font-medium uppercase tracking-wide mb-1">Time Remaining</p>
              <p className="text-3xl font-mono font-bold text-white">{fmt(remaining)}</p>
            </div>
            <button
              onClick={forceStop}
              className="bg-red-600/20 hover:bg-red-600/40 border border-red-500/30 text-red-400 px-3 py-2 rounded-xl text-xs font-semibold transition-colors"
            >
              Force Stop
            </button>
          </div>
        </div>
      )}

      {/* Error */}
      {error && (
        <div className="px-5 py-3 bg-red-500/10 border-b border-red-500/20">
          <p className="text-red-400 text-xs">{error}</p>
        </div>
      )}

      {/* Actions */}
      <div className="px-5 py-4">
        {status === "online" && !launching && (
          <>
            <button
              onClick={() => setShowForm(f => !f)}
              className="w-full bg-red-600 hover:bg-red-500 text-white text-sm font-semibold py-2.5 rounded-xl transition-colors"
            >
              {showForm ? "Cancel" : "Launch Session"}
            </button>

            {showForm && (
              <form onSubmit={startSession} className="mt-4 space-y-3">
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="text-gray-400 text-xs mb-1 block">Car ID</label>
                    <input value={car} onChange={e => setCar(e.target.value)}
                      className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white text-xs focus:outline-none focus:border-red-500 transition"
                      placeholder="lotus_elise_sc" required />
                  </div>
                  <div>
                    <label className="text-gray-400 text-xs mb-1 block">Track ID</label>
                    <input value={track} onChange={e => setTrack(e.target.value)}
                      className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white text-xs focus:outline-none focus:border-red-500 transition"
                      placeholder="magione" required />
                  </div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="text-gray-400 text-xs mb-1 block">Mode</label>
                    <select value={mode} onChange={e => setMode(e.target.value)}
                      className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white text-xs focus:outline-none focus:border-red-500 transition">
                      {["Practice","Hotlap","Race","Drift","TimeAttack"].map(m => <option key={m}>{m}</option>)}
                    </select>
                  </div>
                  <div>
                    <label className="text-gray-400 text-xs mb-1 block">Duration (min)</label>
                    <input type="number" value={duration} onChange={e => setDuration(e.target.value)} min={1} max={180}
                      className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white text-xs focus:outline-none focus:border-red-500 transition" required />
                  </div>
                </div>
                <label className="flex items-center gap-2 text-gray-400 text-xs cursor-pointer">
                  <input type="checkbox" checked={easy} onChange={e => setEasy(e.target.checked)} className="accent-red-500" />
                  Easy Assists
                </label>
                <button type="submit"
                  className="w-full bg-red-600 hover:bg-red-500 text-white text-xs font-semibold py-2.5 rounded-xl transition-colors">
                  Start Session →
                </button>
              </form>
            )}
          </>
        )}

        {launching && (
          <div className="flex items-center gap-2 text-amber-400 text-sm">
            <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"/>
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
            </svg>
            Launching…
          </div>
        )}

        {status === "offline" && (
          <p className="text-gray-600 text-xs text-center py-1">Device offline</p>
        )}
      </div>
    </div>
  );
}
