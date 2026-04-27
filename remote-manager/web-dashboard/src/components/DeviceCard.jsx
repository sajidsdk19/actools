'use client';
import { useEffect, useState } from 'react';
import { useSocket } from '@/lib/SocketProvider';
import api from '@/lib/api';

const STATUS_COLOR = {
  online:     'bg-green-500',
  offline:    'bg-gray-500',
  in_session: 'bg-yellow-400',
};

export default function DeviceCard({ device: initialDevice }) {
  const [device, setDevice]   = useState(initialDevice);
  const [timer, setTimer]     = useState(null); // remaining seconds
  const [launching, setLaunching] = useState(false);
  const { socket } = useSocket();

  // Form state
  const [carId,  setCarId]  = useState('lotus_elise_sc');
  const [trackId, setTrackId] = useState('magione');
  const [duration, setDuration] = useState(30);

  useEffect(() => {
    if (!socket) return;

    socket.on('device_status_changed', (data) => {
      if (data.deviceId === device.id) setDevice(d => ({ ...d, status: data.status }));
    });

    socket.on('timer_update', (data) => {
      if (data.deviceId === device.id) setTimer(data.remainingSeconds);
    });

    socket.on('session_ended', (data) => {
      if (data.deviceId === device.id) setTimer(null);
    });

    return () => {
      socket.off('device_status_changed');
      socket.off('timer_update');
      socket.off('session_ended');
    };
  }, [socket, device.id]);

  const startSession = async () => {
    setLaunching(true);
    try {
      await api.post('/sessions/start', {
        deviceId: device.id, carId, trackId, durationMinutes: duration,
      });
    } catch (e) {
      alert(e?.response?.data?.error || 'Failed to start session');
    } finally {
      setLaunching(false);
    }
  };

  const forceStop = async () => {
    await api.post('/sessions/force-stop', { deviceId: device.id });
  };

  const fmtTime = (s) => {
    const m = Math.floor(s / 60).toString().padStart(2, '0');
    const ss = (s % 60).toString().padStart(2, '0');
    return `${m}:${ss}`;
  };

  return (
    <div className="rounded-2xl bg-white shadow-md p-5 flex flex-col gap-3 border border-gray-100">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold text-gray-800">{device.display_name}</h3>
        <span className={`inline-block w-3 h-3 rounded-full ${STATUS_COLOR[device.status] || 'bg-gray-300'}`} />
      </div>

      <p className="text-xs text-gray-400 uppercase tracking-wide">{device.status}</p>

      {timer !== null && (
        <div className="text-3xl font-mono font-bold text-center text-indigo-600">
          {fmtTime(timer)}
        </div>
      )}

      {device.status !== 'in_session' && (
        <div className="flex flex-col gap-2">
          <input className="border rounded px-2 py-1 text-sm" placeholder="Car ID"
            value={carId} onChange={e => setCarId(e.target.value)} />
          <input className="border rounded px-2 py-1 text-sm" placeholder="Track ID"
            value={trackId} onChange={e => setTrackId(e.target.value)} />
          <input className="border rounded px-2 py-1 text-sm" type="number" placeholder="Duration (min)"
            value={duration} onChange={e => setDuration(Number(e.target.value))} />
          <button onClick={startSession} disabled={launching || device.status === 'offline'}
            className="bg-indigo-600 text-white rounded px-3 py-2 text-sm font-semibold
                       hover:bg-indigo-700 disabled:opacity-50 transition">
            {launching ? 'Launching…' : '▶ Start Session'}
          </button>
        </div>
      )}

      {device.status === 'in_session' && (
        <button onClick={forceStop}
          className="bg-red-600 text-white rounded px-3 py-2 text-sm font-semibold
                     hover:bg-red-700 transition">
          ■ Force Stop
        </button>
      )}
    </div>
  );
}
