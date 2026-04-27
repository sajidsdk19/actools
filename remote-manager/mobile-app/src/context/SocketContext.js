import { createContext, useContext, useEffect, useRef, useState } from 'react';
import { io } from 'socket.io-client';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { API_URL } from '../config';

const SocketContext = createContext(null);

export function SocketProvider({ children }) {
  const socketRef = useRef(null);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    (async () => {
      const token = await AsyncStorage.getItem('ac_token');
      if (!token) return;

      const socket = io(API_URL, {
        auth: { type: 'dashboard', jwtToken: token },
        transports: ['websocket'],
      });
      socket.on('connect',    () => setConnected(true));
      socket.on('disconnect', () => setConnected(false));
      socketRef.current = socket;
    })();
    return () => socketRef.current?.disconnect();
  }, []);

  return (
    <SocketContext.Provider value={{ socket: socketRef.current, connected }}>
      {children}
    </SocketContext.Provider>
  );
}

export const useSocket = () => useContext(SocketContext);
