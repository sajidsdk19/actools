"use client";

import { createContext, useContext, useEffect, useState, useRef } from "react";
import { io } from "socket.io-client";

const SocketContext = createContext(null);

const SERVER_URL = process.env.NEXT_PUBLIC_SERVER_URL || "http://localhost:4000";

export function SocketProvider({ token, children }) {
  const [socket, setSocket] = useState(null);
  const [connected, setConnected] = useState(false);
  const socketRef = useRef(null);

  useEffect(() => {
    // Connect with whatever token is given (even null/bypass — server allows it via BYPASS_AUTH)
    const authPayload = token
      ? { type: "dashboard", jwtToken: token }
      : { type: "dashboard", jwtToken: "bypass" };

    const s = io(SERVER_URL, {
      auth: authPayload,
      reconnection: true,
      reconnectionDelay: 2000,
      reconnectionAttempts: Infinity,
    });

    s.on("connect", () => setConnected(true));
    s.on("disconnect", () => setConnected(false));
    s.on("connect_error", (err) => {
      console.warn("[Socket] Connection error:", err.message);
    });

    socketRef.current = s;
    setSocket(s);

    return () => s.disconnect();
  }, [token]);

  return (
    <SocketContext.Provider value={{ socket, connected }}>
      {children}
    </SocketContext.Provider>
  );
}

export const useSocket = () => useContext(SocketContext);
