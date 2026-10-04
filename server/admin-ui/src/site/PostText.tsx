import type { ReactNode } from "react"

// Post bodies are plain text with a few marks, written on the admin page:
//   ## Heading          a heading
//   - item              a list item
//   **bold**, `code`, [text](https://… or /page)
//   ![description](/media/…)   a picture uploaded on the admin page
// Blank lines separate paragraphs. Everything is built as React elements, so
// nothing in a post can inject HTML.

export const FORMAT_HELP = "## Heading · - list item · **bold** · `code` · [link text](https://…) · ![picture](/media/…) · empty line = new paragraph"

// Only pictures served by this site (uploaded on the admin page).
const PICTURE = /^!\[([^\]]*)\]\((\/media\/[0-9a-f]{64})\)$/

function inline(text: string, key: string): ReactNode[] {
  const out: ReactNode[] = []
  const re = /\*\*([^*]+)\*\*|`([^`]+)`|\[([^\]]+)\]\(((?:https:\/\/|\/)[^)\s]*)\)/g
  let last = 0
  let m: RegExpExecArray | null
  let i = 0
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(text.slice(last, m.index))
    const k = `${key}-${i++}`
    if (m[1]) out.push(<strong key={k}>{m[1]}</strong>)
    else if (m[2]) out.push(<code key={k}>{m[2]}</code>)
    else out.push(
      <a key={k} href={m[4]} rel={m[4].startsWith("/") ? undefined : "noreferrer"}>
        {m[3]}
      </a>,
    )
    last = m.index + m[0].length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

export function PostImage({ src, alt, className }: { src: string; alt: string; className?: string }) {
  return (
    <figure className={className ?? "mt-6"}>
      <img src={src} alt={alt} loading="lazy" decoding="async" className="w-full rounded-2xl bg-card-2" />
      {alt ? <figcaption className="mt-2 text-center text-sm text-dim">{alt}</figcaption> : null}
    </figure>
  )
}

export function PostText({ body }: { body: string }) {
  const blocks: ReactNode[] = []
  let list: string[] = []
  let para: string[] = []
  const flush = () => {
    if (para.length) blocks.push(<p key={`p${blocks.length}`}>{inline(para.join(" "), `p${blocks.length}`)}</p>)
    if (list.length)
      blocks.push(
        <ul key={`u${blocks.length}`}>
          {list.map((l, i) => (
            <li key={i}>{inline(l, `l${blocks.length}-${i}`)}</li>
          ))}
        </ul>,
      )
    para = []
    list = []
  }
  for (const raw of body.replace(/\r\n/g, "\n").split("\n")) {
    const line = raw.trim()
    if (!line) {
      flush()
      continue
    }
    const pic = PICTURE.exec(line)
    if (pic) {
      flush()
      blocks.push(<PostImage key={`i${blocks.length}`} src={pic[2]} alt={pic[1]} />)
    } else if (line.startsWith("## ")) {
      flush()
      blocks.push(<h3 key={`h${blocks.length}`}>{inline(line.slice(3), `h${blocks.length}`)}</h3>)
    } else if (line.startsWith("- ")) {
      if (para.length) {
        const keep = list
        list = []
        flush()
        list = keep
      }
      list.push(line.slice(2))
    } else {
      if (list.length) flush()
      para.push(line)
    }
  }
  flush()
  return <>{blocks}</>
}
