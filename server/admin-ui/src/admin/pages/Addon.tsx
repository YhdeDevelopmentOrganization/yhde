import { useRef, useState } from "react"
import { Copy, Upload } from "lucide-react"
import { toast } from "sonner"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Progress } from "@/components/ui/progress"
import type { Data } from "../App"
import { api, upload, type Addon } from "../api"
import { ago, bytes } from "../format"
import { Section } from "../parts"

const platforms = (a: Addon) => a.platforms.map((p) => ({ windows: "Windows", linux: "Linux", macos: "macOS" })[p] ?? p).join(", ")

export function AddonPage({ data, refresh }: { data: Data; refresh: () => Promise<void> }) {
  const { addon, addonPending } = data.overview
  const [progress, setProgress] = useState<number | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const joinUrl = location.origin + "/join"

  const act = async (path: string, done: string) => {
    try {
      await api("POST", path)
      toast.success(done)
      await refresh()
    } catch (e) {
      toast.error((e as Error).message)
    }
  }

  return (
    <div className="grid gap-6">
      <Section
        title="Editor add-on"
        description={addon ? `Released: ${addon.version} · ${platforms(addon)} · ${bytes(addon.size)} · ${ago(addon.uploaded)}` : "Nothing released yet"}
        action={
          <>
            <input
              ref={input}
              type="file"
              accept=".zip,application/zip"
              hidden
              onChange={async (e) => {
                const file = e.target.files?.[0]
                e.target.value = ""
                if (!file) return
                setProgress(0)
                try {
                  await upload("/addon", file, setProgress)
                  toast.success("Uploaded")
                  await refresh()
                } catch (err) {
                  toast.error((err as Error).message)
                } finally {
                  setProgress(null)
                }
              }}
            />
            <Button variant="outline" size="sm" disabled={progress !== null} onClick={() => input.current?.click()}>
              <Upload /> Upload zip
            </Button>
          </>
        }
      >
        <div className="grid gap-4">
          {progress !== null ? <Progress value={progress * 100} /> : null}
          {addonPending ? (
            <Alert>
              <AlertTitle>
                {addonPending.version} is uploaded but not released
              </AlertTitle>
              <AlertDescription>
                <div className="mt-2 flex flex-wrap items-center gap-2">
                  <Button size="sm" onClick={() => act("/addon/release", `${addonPending.version} released`)}>
                    Release
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => act("/addon/discard", "Discarded")}>
                    Discard
                  </Button>
                  <span className="text-xs text-muted-foreground">
                    {platforms(addonPending)} · {bytes(addonPending.size)}
                  </span>
                </div>
              </AlertDescription>
            </Alert>
          ) : !addon ? (
            <p className="text-sm text-muted-foreground">Download links need a released add-on.</p>
          ) : null}
        </div>
      </Section>
      <Section title="Join page" description="Works with a typed invite code">
        <div className="flex max-w-md gap-2">
          <Input readOnly value={joinUrl} onFocus={(e) => e.target.select()} />
          <Button
            variant="outline"
            size="icon"
            aria-label="Copy"
            onClick={() => navigator.clipboard?.writeText(joinUrl).then(() => toast.success("Copied"), () => toast.error("Could not copy"))}
          >
            <Copy />
          </Button>
        </div>
      </Section>
    </div>
  )
}
