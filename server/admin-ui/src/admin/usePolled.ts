import { useEffect, useState } from "react"
import { api } from "./api"

// Data one page shows, fetched when the page opens and again every `every`
// milliseconds while the tab is visible. Null until the first answer; a
// failed refresh keeps the last data (the shell says when the server is gone).
export function usePolled<T>(path: string, every: number): T | null {
  const [data, setData] = useState<T | null>(null)
  useEffect(() => {
    let live = true
    const load = () =>
      api<T>("GET", path).then(
        (d) => live && setData(d),
        () => {},
      )
    load()
    const t = setInterval(() => !document.hidden && load(), every)
    const onShow = () => !document.hidden && load()
    document.addEventListener("visibilitychange", onShow)
    return () => {
      live = false
      clearInterval(t)
      document.removeEventListener("visibilitychange", onShow)
    }
  }, [path, every])
  return data
}
