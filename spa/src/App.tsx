import { useEffect, useState } from "react";
import { apiGet, getUser, type User } from "./bff";

export default function App() {
  // undefined: not known yet.
  const [user, setUser] = useState<User | null>();
  const [result, setResult] = useState("");

  useEffect(() => {
    getUser().then(setUser, () => setUser(null));
  }, []);

  if (user === undefined) return <p>Loading...</p>;
  if (user === null) return <a href="/bff/login">Sign in</a>;

  async function showForecast() {
    try {
      const res = await apiGet("/forecast");
      if (res.status === 401) {
        // The session has ended.
        setUser(null);
        return;
      }
      setResult(res.ok ? await res.text() : `API: ${res.status}`);
    } catch {
      setResult("The request failed. Try again.");
    }
  }

  return (
    <>
      <p>Signed in as {user.name}</p>
      <button onClick={showForecast}>Show forecast</button>
      <form method="post" action="/bff/logout">
        <button>Sign out</button>
      </form>
      <pre>{result}</pre>
    </>
  );
}
