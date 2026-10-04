// Teammate colors, as in the editor add-on: a stable color per name.
// Sky blue is kept for "you" so it matches the page accent.
export const YOU = "#7cc4ff"
const COLORS = ["#ff8a65", "#5ee6a8", "#c3a8ff", "#ffb86b", "#ff8fa3", "#6fdcdc"]

const KNOWN: Record<string, string> = { Maya: "#6fdcdc", Alex: "#ff8a65", Sam: "#5ee6a8", Jordan: "#c3a8ff" }

export function peerColor(name: string, you?: string) {
  if (you && name === you) return YOU
  if (KNOWN[name]) return KNOWN[name]
  let h = 0
  for (const c of name) h = (h * 31 + c.charCodeAt(0)) | 0
  return COLORS[Math.abs(h) % COLORS.length]
}
