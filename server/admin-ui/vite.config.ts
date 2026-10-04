import path from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig } from "vite"
import { mockAdmin } from "./dev/mock-admin.ts"

// The build number shown next to the version: the build date, yymmdd.
const now = new Date()
const BUILD = String(now.getUTCFullYear() % 100) + String(now.getUTCMonth() + 1).padStart(2, "0") + String(now.getUTCDate()).padStart(2, "0")

// The pages served by the YHDE server: home (/), the dashboard (/app), join
// (/join) and the server admin page (/admin).
export default defineConfig({
  plugins: [react(), tailwindcss(), mockAdmin()],
  define: { __BUILD__: JSON.stringify(BUILD) },
  base: "/ui/",
  resolve: { alias: { "@": path.resolve(import.meta.dirname, "./src") } },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    chunkSizeWarningLimit: 800,
    rollupOptions: {
      input: {
        admin: path.resolve(import.meta.dirname, "admin.html"),
        join: path.resolve(import.meta.dirname, "join.html"),
        home: path.resolve(import.meta.dirname, "home.html"),
        app: path.resolve(import.meta.dirname, "app.html"),
        site: path.resolve(import.meta.dirname, "site.html"),
      },
    },
  },
})
