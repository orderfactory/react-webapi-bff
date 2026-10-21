// The BFF refuses API calls without this header.
const headers = { "X-CSRF": "1" };

export type User = { name: string };

// null: no session.
export async function getUser(): Promise<User | null> {
  const res = await fetch("/bff/user", { headers });
  if (res.status === 401) return null;
  if (!res.ok) throw new Error(`BFF: ${res.status}`);
  return res.json();
}

export function apiGet(path: string) {
  return fetch(`/api${path}`, { headers });
}
