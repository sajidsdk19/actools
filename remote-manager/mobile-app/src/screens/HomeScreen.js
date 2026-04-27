import React, { useEffect, useState } from 'react';
import {
  View, Text, FlatList, TouchableOpacity,
  StyleSheet, ActivityIndicator, Alert,
} from 'react-native';
import { useSocket } from '../context/SocketContext';
import api from '../lib/api';

const STATUS_COLOR = {
  online:     '#22c55e',
  offline:    '#6b7280',
  in_session: '#facc15',
};

function DeviceItem({ device: init }) {
  const [device, setDevice]   = useState(init);
  const [timer,  setTimer]    = useState(null);
  const { socket } = useSocket();

  useEffect(() => {
    if (!socket) return;
    const onStatus = (d) => {
      if (d.deviceId === device.id) setDevice(prev => ({ ...prev, status: d.status }));
    };
    const onTimer  = (d) => {
      if (d.deviceId === device.id) setTimer(d.remainingSeconds);
    };
    const onEnd    = (d) => {
      if (d.deviceId === device.id) setTimer(null);
    };
    socket.on('device_status_changed', onStatus);
    socket.on('timer_update', onTimer);
    socket.on('session_ended', onEnd);
    return () => {
      socket.off('device_status_changed', onStatus);
      socket.off('timer_update', onTimer);
      socket.off('session_ended', onEnd);
    };
  }, [socket, device.id]);

  const fmt = (s) => {
    const m  = String(Math.floor(s / 60)).padStart(2, '0');
    const ss = String(s % 60).padStart(2, '0');
    return `${m}:${ss}`;
  };

  const startSession = async () => {
    try {
      await api.post('/sessions/start', {
        deviceId: device.id,
        carId: 'lotus_elise_sc',
        trackId: 'magione',
        durationMinutes: 30,
      });
    } catch (e) {
      Alert.alert('Error', e?.response?.data?.error || 'Failed');
    }
  };

  const forceStop = async () => {
    await api.post('/sessions/force-stop', { deviceId: device.id });
  };

  return (
    <View style={styles.card}>
      <View style={styles.cardHeader}>
        <Text style={styles.name}>{device.display_name}</Text>
        <View style={[styles.dot, { backgroundColor: STATUS_COLOR[device.status] || '#ccc' }]} />
      </View>
      <Text style={styles.statusText}>{device.status.replace('_', ' ').toUpperCase()}</Text>

      {timer !== null && (
        <Text style={styles.timer}>{fmt(timer)}</Text>
      )}

      {device.status === 'in_session' ? (
        <TouchableOpacity style={styles.btnStop} onPress={forceStop}>
          <Text style={styles.btnText}>■ Force Stop</Text>
        </TouchableOpacity>
      ) : (
        <TouchableOpacity
          style={[styles.btnStart, device.status === 'offline' && styles.disabled]}
          onPress={startSession}
          disabled={device.status === 'offline'}
        >
          <Text style={styles.btnText}>▶ Start Session</Text>
        </TouchableOpacity>
      )}
    </View>
  );
}

export default function HomeScreen() {
  const [devices, setDevices] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.get('/devices')
      .then(r => setDevices(r.data))
      .finally(() => setLoading(false));
  }, []);

  if (loading) return <ActivityIndicator style={{ flex: 1 }} size="large" />;

  return (
    <View style={styles.container}>
      <Text style={styles.title}>🏎 AC Remote</Text>
      <FlatList
        data={devices}
        keyExtractor={d => d.id}
        renderItem={({ item }) => <DeviceItem device={item} />}
        contentContainerStyle={{ paddingBottom: 32 }}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container:  { flex: 1, backgroundColor: '#f1f5f9', paddingHorizontal: 16, paddingTop: 48 },
  title:      { fontSize: 24, fontWeight: '700', color: '#1e293b', marginBottom: 20 },
  card:       { backgroundColor: '#fff', borderRadius: 16, padding: 16, marginBottom: 14,
                shadowColor: '#000', shadowOpacity: 0.06, shadowRadius: 8, elevation: 3 },
  cardHeader: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' },
  name:       { fontSize: 16, fontWeight: '600', color: '#0f172a' },
  dot:        { width: 12, height: 12, borderRadius: 6 },
  statusText: { fontSize: 11, color: '#94a3b8', marginTop: 4, marginBottom: 8, letterSpacing: 1 },
  timer:      { fontSize: 36, fontFamily: 'monospace', fontWeight: '700',
                color: '#4f46e5', textAlign: 'center', marginBottom: 12 },
  btnStart:   { backgroundColor: '#4f46e5', borderRadius: 10, padding: 12, alignItems: 'center' },
  btnStop:    { backgroundColor: '#dc2626', borderRadius: 10, padding: 12, alignItems: 'center' },
  btnText:    { color: '#fff', fontWeight: '600', fontSize: 14 },
  disabled:   { opacity: 0.4 },
});
