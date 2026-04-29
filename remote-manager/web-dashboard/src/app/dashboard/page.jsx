'use client';
import { useEffect, useState, useCallback } from 'react';
import api from '@/lib/api';
import DeviceCard from '@/components/DeviceCard';
import { SocketProvider, useSocket } from '@/context/SocketProvider';

// ── Inner component — has access to socket context ────────────────────────────
function DashboardContent() {
  const { socket, connected } = useSocket();
  const [devices, setDevices]   = useState([]);
  const [loading, setLoading]   = useState(false);
  const [lastRefresh, setLastRefresh] = useState(null);

  const fetchDevices = useCallback(async () => {
    setLoading(true);
    try {
      const r = await api.get('/devices');
      setDevices(r.data);
      setLastRefresh(new Date());
    } catch (err) {
      console.error('[Dashboard] Failed to fetch devices:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  // Initial load
  useEffect(() => { fetchDevices(); }, [fetchDevices]);

  // ── Auto-refresh when a gaming PC comes online / goes offline ───────────────
  useEffect(() => {
    if (!socket) return;

    // A new agent connected — re-fetch the full device list so new PCs appear
    const onConnected    = () => fetchDevices();
    const onDisconnected = () => fetchDevices();

    socket.on('device_connected',    onConnected);
    socket.on('device_disconnected', onDisconnected);

    return () => {
      socket.off('device_connected',    onConnected);
      socket.off('device_disconnected', onDisconnected);
    };
  }, [socket, fetchDevices]);

  const onlineCount  = devices.filter(d => d.status !== 'offline').length;
  const offlineCount = devices.filter(d => d.status === 'offline').length;

  return (
    <main className="min-h-screen bg-gray-950 p-8">
      {/* Header */}
      <div className="flex items-center justify-between mb-8">
        <div>
          <h1 className="text-2xl font-bold text-white tracking-tight">
            🏎 AC Remote Manager
          </h1>
          <p className="text-gray-500 text-sm mt-1">
            <span className="text-emerald-400 font-semibold">{onlineCount} online</span>
            {offlineCount > 0 && (
              <span className="ml-2 text-gray-600">{offlineCount} offline</span>
            )}
          </p>
        </div>

        <div className="flex items-center gap-3">
          {/* Socket status indicator */}
          <div className="flex items-center gap-2 bg-gray-900 border border-gray-800 rounded-xl px-3 py-2">
            <span className={`w-2 h-2 rounded-full ${connected ? 'bg-emerald-400 animate-pulse' : 'bg-red-500'}`} />
            <span className="text-xs text-gray-400">{connected ? 'Live' : 'Disconnected'}</span>
          </div>

          {/* Manual refresh button */}
          <button
            id="refresh-devices-btn"
            onClick={fetchDevices}
            disabled={loading}
            className="flex items-center gap-2 bg-gray-900 hover:bg-gray-800 border border-gray-800 hover:border-gray-700 text-gray-300 hover:text-white px-4 py-2 rounded-xl text-sm font-medium transition-all"
          >
            <svg
              className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`}
              fill="none" viewBox="0 0 24 24" stroke="currentColor"
            >
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            {loading ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>
      </div>

      {/* Last refreshed */}
      {lastRefresh && (
        <p className="text-gray-700 text-xs mb-6">
          Last updated: {lastRefresh.toLocaleTimeString()}
        </p>
      )}

      {/* No devices */}
      {!loading && devices.length === 0 && (
        <div className="flex flex-col items-center justify-center py-24 text-center">
          <div className="w-16 h-16 rounded-2xl bg-gray-900 flex items-center justify-center mb-4">
            <svg className="w-8 h-8 text-gray-700" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5}
                d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
            </svg>
          </div>
          <p className="text-gray-500 text-sm">No gaming PCs registered yet.</p>
          <p className="text-gray-700 text-xs mt-1">Run the agent on a gaming PC to see it here.</p>
        </div>
      )}

      {/* Device grid */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-6">
        {devices.map(d => (
          <DeviceCard
            key={d.id}
            device={d}
            token={null}
            onSessionChange={fetchDevices}
          />
        ))}
      </div>
    </main>
  );
}

// ── Page root — wraps content with SocketProvider ─────────────────────────────
export default function DashboardPage() {
  return (
    <SocketProvider token={null}>
      <DashboardContent />
    </SocketProvider>
  );
}
