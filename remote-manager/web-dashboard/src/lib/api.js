// Native fetch-based API client — no axios dependency needed
const SERVER_URL = process.env.NEXT_PUBLIC_SERVER_URL || 'http://localhost:4000';

function getToken() {
  if (typeof window !== 'undefined') return localStorage.getItem('ac_token');
  return null;
}

const api = {
  get: async (path) => {
    const res = await fetch(`${SERVER_URL}${path}`, {
      headers: { Authorization: `Bearer ${getToken()}` },
    });
    const data = await res.json();
    return { data };
  },
  post: async (path, body) => {
    const res = await fetch(`${SERVER_URL}${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${getToken()}` },
      body: JSON.stringify(body),
    });
    const data = await res.json();
    return { data };
  },
};

export default api;
