import React, { useState, useEffect, useCallback, useRef } from "react";
import {
  View, Text, ScrollView, TouchableOpacity, TextInput,
  StyleSheet, StatusBar, Switch, ActivityIndicator, Alert,
} from "react-native";
import { io } from "socket.io-client";

const SERVER_URL = "http://localhost:4000"; // change to your server IP for real device

// ── Colours ────────────────────────────────────────────────────────────────────
const C = {
  bg:        "#0A0A0F",
  surface:   "#111118",
  border:    "#1E1E2A",
  red:       "#DC2626",
  redDim:    "#7F1D1D",
  amber:     "#F59E0B",
  green:     "#10B981",
  muted:     "#6B7280",
  text:      "#F9FAFB",
  textDim:   "#9CA3AF",
};

function fmt(s) {
  if (s == null) return "--:--";
  const m = Math.floor(s / 60).toString().padStart(2, "0");
  const sc = Math.floor(s % 60).toString().padStart(2, "0");
  return `${m}:${sc}`;
}

// ── Status badge ─────────────────────────────────────────────────────────────
function Badge({ status }) {
  const color = status === "online" ? C.green : status === "in_session" ? C.amber : C.muted;
  const label = status === "in_session" ? "In Session" : status === "online" ? "Online" : "Offline";
  return (
    <View style={[styles.badge, { borderColor: color + "40", backgroundColor: color + "18" }]}>
      <View style={[styles.dot, { backgroundColor: color }]} />
      <Text style={[styles.badgeText, { color }]}>{label}</Text>
    </View>
  );
}

// ── Device card ──────────────────────────────────────────────────────────────
function DeviceCard({ device, token, socket }) {
  const [status, setStatus] = useState(device.status);
  const [remaining, setRemaining] = useState(null);
  const [showForm, setShowForm] = useState(false);
  const [car, setCar] = useState("lotus_elise_sc");
  const [track, setTrack] = useState("magione");
  const [duration, setDuration] = useState("30");
  const [easy, setEasy] = useState(false);
  const [launching, setLaunching] = useState(false);

  useEffect(() => {
    if (!socket) return;
    const onStatus = ({ deviceId, status: s }) => { if (deviceId === device.id) setStatus(s); };
    const onConn   = ({ deviceId }) => { if (deviceId === device.id) setStatus("online"); };
    const onDisc   = ({ deviceId }) => { if (deviceId === device.id) { setStatus("offline"); setRemaining(null); } };
    const onTimer  = ({ deviceId, remainingSeconds: r }) => { if (deviceId === device.id) setRemaining(r); };
    const onEnded  = ({ deviceId }) => { if (deviceId === device.id) { setStatus("online"); setRemaining(null); setLaunching(false); } };
    const onError  = ({ deviceId, error: e }) => { if (deviceId === device.id) { setStatus("online"); setLaunching(false); Alert.alert("Session Error", e); } };

    socket.on("device_status_changed", onStatus);
    socket.on("device_connected", onConn);
    socket.on("device_disconnected", onDisc);
    socket.on("timer_update", onTimer);
    socket.on("session_ended", onEnded);
    socket.on("session_error", onError);
    return () => {
      socket.off("device_status_changed", onStatus);
      socket.off("device_connected", onConn);
      socket.off("device_disconnected", onDisc);
      socket.off("timer_update", onTimer);
      socket.off("session_ended", onEnded);
      socket.off("session_error", onError);
    };
  }, [socket, device.id]);

  const startSession = useCallback(async () => {
    setLaunching(true);
    setShowForm(false);
    try {
      const res = await fetch(`${SERVER_URL}/sessions/start`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({ deviceId: device.id, carId: car, trackId: track, durationMinutes: Number(duration), easyAssists: easy }),
      });
      const data = await res.json();
      if (!res.ok) { Alert.alert("Error", data.error); setLaunching(false); }
    } catch (e) { Alert.alert("Error", e.message); setLaunching(false); }
  }, [device.id, token, car, track, duration, easy]);

  const forceStop = useCallback(async () => {
    await fetch(`${SERVER_URL}/sessions/force-stop`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify({ deviceId: device.id }),
    });
  }, [device.id, token]);

  return (
    <View style={styles.card}>
      {/* Header */}
      <View style={styles.cardHeader}>
        <View>
          <Text style={styles.cardTitle}>{device.display_name}</Text>
          <Text style={styles.cardSub}>{device.machine_name}</Text>
        </View>
        <Badge status={status} />
      </View>

      {/* Timer */}
      {status === "in_session" && (
        <View style={styles.timerBox}>
          <View>
            <Text style={styles.timerLabel}>TIME REMAINING</Text>
            <Text style={styles.timerText}>{fmt(remaining)}</Text>
          </View>
          <TouchableOpacity style={styles.stopBtn} onPress={forceStop}>
            <Text style={styles.stopBtnText}>Force Stop</Text>
          </TouchableOpacity>
        </View>
      )}

      {/* Actions */}
      {status === "online" && !launching && (
        <View>
          <TouchableOpacity style={styles.launchBtn} onPress={() => setShowForm(f => !f)}>
            <Text style={styles.launchBtnText}>{showForm ? "Cancel" : "Launch Session"}</Text>
          </TouchableOpacity>
          {showForm && (
            <View style={styles.form}>
              {[
                { label: "Car ID", val: car, set: setCar, ph: "lotus_elise_sc" },
                { label: "Track ID", val: track, set: setTrack, ph: "magione" },
                { label: "Duration (min)", val: duration, set: setDuration, ph: "30", num: true },
              ].map(f => (
                <View key={f.label} style={styles.formRow}>
                  <Text style={styles.formLabel}>{f.label}</Text>
                  <TextInput
                    value={f.val} onChangeText={f.set} placeholder={f.ph}
                    keyboardType={f.num ? "numeric" : "default"}
                    placeholderTextColor={C.muted}
                    style={styles.input}
                  />
                </View>
              ))}
              <View style={styles.formRow}>
                <Text style={styles.formLabel}>Easy Assists</Text>
                <Switch value={easy} onValueChange={setEasy} trackColor={{ true: C.red }} />
              </View>
              <TouchableOpacity style={styles.launchBtn} onPress={startSession}>
                <Text style={styles.launchBtnText}>Start →</Text>
              </TouchableOpacity>
            </View>
          )}
        </View>
      )}

      {launching && (
        <View style={styles.launching}>
          <ActivityIndicator color={C.amber} size="small" />
          <Text style={[styles.textDim, { marginLeft: 8 }]}>Launching…</Text>
        </View>
      )}

      {status === "offline" && (
        <Text style={[styles.textDim, { textAlign: "center", paddingVertical: 8 }]}>Device offline</Text>
      )}
    </View>
  );
}

// ── Main App ──────────────────────────────────────────────────────────────────
export default function App() {
  const [token, setToken] = useState(null);
  const [email, setEmail] = useState("admin@ac.local");
  const [password, setPassword] = useState("");
  const [loginErr, setLoginErr] = useState("");
  const [loginLoading, setLoginLoading] = useState(false);

  const [socket, setSocket] = useState(null);
  const [connected, setConnected] = useState(false);
  const [devices, setDevices] = useState([]);
  const [stats, setStats] = useState({ sessions: 0, minutes: 0 });
  const [tab, setTab] = useState("devices");

  // Socket setup
  useEffect(() => {
    if (!token) return;
    const s = io(SERVER_URL, { auth: { type: "dashboard", jwtToken: token } });
    s.on("connect", () => setConnected(true));
    s.on("disconnect", () => setConnected(false));
    setSocket(s);

    fetch(`${SERVER_URL}/devices`, { headers: { Authorization: `Bearer ${token}` } })
      .then(r => r.json()).then(d => setDevices(Array.isArray(d) ? d : [d].filter(Boolean)));

    const today = new Date().toISOString().slice(0, 10);
    fetch(`${SERVER_URL}/reports/daily?date=${today}`, { headers: { Authorization: `Bearer ${token}` } })
      .then(r => r.json()).then(r => setStats({ sessions: r.total_sessions, minutes: r.total_minutes }));

    return () => s.disconnect();
  }, [token]);

  const login = useCallback(async () => {
    setLoginLoading(true); setLoginErr("");
    try {
      const res = await fetch(`${SERVER_URL}/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, password }),
      });
      const d = await res.json();
      if (!res.ok) throw new Error(d.error);
      setToken(d.token);
    } catch (e) { setLoginErr(e.message); }
    finally { setLoginLoading(false); }
  }, [email, password]);

  // ── Login screen ────────────────────────────────────────────────────────────
  if (!token) return (
    <View style={styles.loginScreen}>
      <StatusBar barStyle="light-content" backgroundColor={C.bg} />
      <View style={styles.logoBox}>
        <Text style={styles.logoIcon}>⚡</Text>
      </View>
      <Text style={styles.loginTitle}>AC Remote Manager</Text>
      <Text style={styles.loginSub}>Assetto Corsa Session Control</Text>

      <View style={styles.loginCard}>
        <Text style={styles.formLabel}>Email</Text>
        <TextInput value={email} onChangeText={setEmail} keyboardType="email-address" autoCapitalize="none"
          style={[styles.input, { marginBottom: 12 }]} placeholderTextColor={C.muted} />
        <Text style={styles.formLabel}>Password</Text>
        <TextInput value={password} onChangeText={setPassword} secureTextEntry placeholder="••••••••"
          style={[styles.input, { marginBottom: 16 }]} placeholderTextColor={C.muted} />
        {loginErr ? <Text style={styles.error}>{loginErr}</Text> : null}
        <TouchableOpacity style={styles.launchBtn} onPress={login} disabled={loginLoading}>
          {loginLoading ? <ActivityIndicator color="#fff" /> : <Text style={styles.launchBtnText}>Sign In</Text>}
        </TouchableOpacity>
      </View>
    </View>
  );

  // ── Main app ────────────────────────────────────────────────────────────────
  return (
    <View style={styles.root}>
      <StatusBar barStyle="light-content" backgroundColor={C.bg} />

      {/* Header */}
      <View style={styles.header}>
        <Text style={styles.headerTitle}>⚡ AC Remote</Text>
        <View style={{ flexDirection: "row", alignItems: "center", gap: 6 }}>
          <View style={[styles.dot, { backgroundColor: connected ? C.green : C.muted }]} />
          <Text style={styles.textDim}>{connected ? "Live" : "Offline"}</Text>
        </View>
      </View>

      {/* Stats */}
      <View style={styles.statsRow}>
        {[
          { label: "Online",   val: devices.filter(d => d.status !== "offline").length, color: C.green   },
          { label: "Sessions", val: devices.filter(d => d.status === "in_session").length, color: C.amber },
          { label: "Today",    val: stats.sessions, color: "#818CF8" },
          { label: "Minutes",  val: parseFloat(stats.minutes || 0).toFixed(0), color: "#A78BFA" },
        ].map(s => (
          <View key={s.label} style={styles.statCard}>
            <Text style={[styles.statVal, { color: s.color }]}>{s.val}</Text>
            <Text style={styles.statLabel}>{s.label}</Text>
          </View>
        ))}
      </View>

      {/* Tabs */}
      <View style={styles.tabs}>
        {["devices", "logout"].map(t => (
          <TouchableOpacity key={t} style={[styles.tab, tab === t && styles.tabActive]}
            onPress={() => t === "logout" ? setToken(null) : setTab(t)}>
            <Text style={[styles.tabText, tab === t && styles.tabTextActive]}>
              {t === "logout" ? "Sign Out" : "Devices"}
            </Text>
          </TouchableOpacity>
        ))}
      </View>

      {/* Devices list */}
      <ScrollView contentContainerStyle={{ padding: 16, gap: 12 }}>
        {devices.length === 0
          ? <Text style={[styles.textDim, { textAlign: "center", marginTop: 40 }]}>No devices registered.</Text>
          : devices.map(d => <DeviceCard key={d.id} device={d} token={token} socket={socket} />)
        }
      </ScrollView>
    </View>
  );
}

// ── Styles ─────────────────────────────────────────────────────────────────────
const styles = StyleSheet.create({
  root:        { flex: 1, backgroundColor: C.bg },
  loginScreen: { flex: 1, backgroundColor: C.bg, alignItems: "center", justifyContent: "center", padding: 24 },
  logoBox:     { width: 64, height: 64, borderRadius: 18, backgroundColor: C.red, alignItems: "center", justifyContent: "center", marginBottom: 12, shadowColor: C.red, shadowOpacity: 0.5, shadowRadius: 20 },
  logoIcon:    { fontSize: 30 },
  loginTitle:  { color: C.text, fontSize: 22, fontWeight: "700", marginBottom: 4 },
  loginSub:    { color: C.muted, fontSize: 13, marginBottom: 28 },
  loginCard:   { width: "100%", backgroundColor: C.surface, borderRadius: 20, padding: 20, borderWidth: 1, borderColor: C.border },
  error:       { color: "#F87171", fontSize: 12, marginBottom: 10 },

  header:      { flexDirection: "row", justifyContent: "space-between", alignItems: "center", paddingHorizontal: 16, paddingTop: 52, paddingBottom: 12, backgroundColor: C.surface, borderBottomWidth: 1, borderBottomColor: C.border },
  headerTitle: { color: C.text, fontSize: 17, fontWeight: "700" },

  statsRow:   { flexDirection: "row", padding: 12, gap: 8 },
  statCard:   { flex: 1, backgroundColor: C.surface, borderRadius: 14, borderWidth: 1, borderColor: C.border, alignItems: "center", paddingVertical: 10 },
  statVal:    { fontSize: 22, fontWeight: "800" },
  statLabel:  { color: C.muted, fontSize: 10, marginTop: 2 },

  tabs:        { flexDirection: "row", marginHorizontal: 16, marginBottom: 4, backgroundColor: C.surface, borderRadius: 12, borderWidth: 1, borderColor: C.border, padding: 3 },
  tab:         { flex: 1, paddingVertical: 7, borderRadius: 10, alignItems: "center" },
  tabActive:   { backgroundColor: C.red },
  tabText:     { color: C.muted, fontSize: 13, fontWeight: "600" },
  tabTextActive:{ color: C.text },

  card:        { backgroundColor: C.surface, borderRadius: 18, borderWidth: 1, borderColor: C.border, overflow: "hidden" },
  cardHeader:  { flexDirection: "row", justifyContent: "space-between", alignItems: "center", padding: 16 },
  cardTitle:   { color: C.text, fontWeight: "700", fontSize: 15 },
  cardSub:     { color: C.muted, fontSize: 12, marginTop: 2 },

  badge:       { flexDirection: "row", alignItems: "center", gap: 5, borderRadius: 20, borderWidth: 1, paddingHorizontal: 8, paddingVertical: 4 },
  dot:         { width: 6, height: 6, borderRadius: 3 },
  badgeText:   { fontSize: 11, fontWeight: "600" },

  timerBox:    { flexDirection: "row", justifyContent: "space-between", alignItems: "center", backgroundColor: "#F59E0B10", borderTopWidth: 1, borderTopColor: "#F59E0B30", paddingHorizontal: 16, paddingVertical: 14 },
  timerLabel:  { color: C.amber, fontSize: 10, fontWeight: "600", letterSpacing: 1, marginBottom: 4 },
  timerText:   { color: C.text, fontSize: 36, fontWeight: "800", fontVariant: ["tabular-nums"] },
  stopBtn:     { backgroundColor: "#7F1D1D30", borderWidth: 1, borderColor: "#DC262640", borderRadius: 10, paddingHorizontal: 12, paddingVertical: 8 },
  stopBtnText: { color: "#FCA5A5", fontSize: 12, fontWeight: "600" },

  launchBtn:   { backgroundColor: C.red, marginHorizontal: 16, marginBottom: 14, borderRadius: 12, paddingVertical: 12, alignItems: "center" },
  launchBtnText:{ color: "#fff", fontWeight: "700", fontSize: 14 },

  form:        { paddingHorizontal: 16, paddingBottom: 4 },
  formRow:     { flexDirection: "row", justifyContent: "space-between", alignItems: "center", marginBottom: 10 },
  formLabel:   { color: C.textDim, fontSize: 12, fontWeight: "500", marginBottom: 4 },
  input:       { flex: 1, backgroundColor: "#1A1A24", borderWidth: 1, borderColor: C.border, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 8, color: C.text, fontSize: 13, marginLeft: 12 },

  launching:   { flexDirection: "row", alignItems: "center", paddingHorizontal: 16, paddingBottom: 14 },
  textDim:     { color: C.muted, fontSize: 13 },
});
