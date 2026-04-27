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
    if (!token) return;

    const s = io(SERVER_URL, {
      auth: { type: "dashboard", jwtToken: token },
      reconnectionDelay: 2000,
    });

    s.on("connect", () => setConnected(true));
    s.on("disconnect", () => setConnected(false));

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
