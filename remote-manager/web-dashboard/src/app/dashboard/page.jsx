'use client';
import { useEffect, useState } from 'react';
import api from '@/lib/api';
import DeviceCard from '@/components/DeviceCard';
import { SocketProvider } from '@/lib/SocketProvider';

export default function DashboardPage() {
  const [devices, setDevices] = useState([]);

  useEffect(() => {
    api.get('/devices').then(r => setDevices(r.data)).catch(console.error);
  }, []);

  return (
    <SocketProvider>
      <main className="min-h-screen bg-gray-50 p-8">
        <h1 className="text-2xl font-bold text-gray-900 mb-6">
          🏎 AC Remote Manager
        </h1>

        {devices.length === 0 && (
          <p className="text-gray-500">No devices registered yet.</p>
        )}

        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-6">
          {devices.map(d => <DeviceCard key={d.id} device={d} />)}
        </div>
      </main>
    </SocketProvider>
  );
}
