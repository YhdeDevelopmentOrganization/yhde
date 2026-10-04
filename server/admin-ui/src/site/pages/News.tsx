import { useEffect, useState } from "react"
import { Newspaper } from "lucide-react"
import { Skeleton } from "@/components/ui/skeleton"
import { longDate } from "@/lib/dates"
import { livePosts, type Post } from "../api"
import { PageTop, Prose } from "../parts"
import { PostImage, PostText } from "../PostText"

const TEXT = {
  title: "News from",
  lead: "Announcements, plans and behind-the-scenes notes from the team.",
  failed: "News couldn't be loaded right now. Try again in a moment.",
  empty: "No news yet",
  emptyBody: "The first post is on its way.",
}

export function News() {
  const t = TEXT
  const [posts, setPosts] = useState<Post[] | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    livePosts("news").then(setPosts, () => setFailed(true))
  }, [])

  return (
    <>
      <PageTop title={t.title} accent="YHDE" lead={t.lead} />
      <div className="mx-auto grid max-w-4xl gap-5 px-5 pb-16 sm:px-7">
        {failed ? (
          <p className="rounded-[1.5rem] bg-card p-7 text-dim">{t.failed}</p>
        ) : !posts ? (
          [0, 1].map((i) => <Skeleton key={i} className="h-48 rounded-[1.5rem] bg-card" />)
        ) : posts.length === 0 ? (
          <div className="grid justify-items-center gap-3 rounded-[1.5rem] bg-card px-7 py-14 text-center">
            <span className="grid size-12 place-items-center rounded-2xl bg-tint-blue text-sky">
              <Newspaper className="size-6" aria-hidden="true" />
            </span>
            <p className="text-lg font-semibold text-text">{t.empty}</p>
            <p className="max-w-[30rem] text-dim">{t.emptyBody}</p>
          </div>
        ) : (
          posts.map((p) => (
            <article key={p.id} id={p.id} className="scroll-mt-24 overflow-hidden rounded-[1.5rem] bg-card p-7 sm:p-9">
              {p.cover ? <PostImage src={p.cover} alt="" className="-mx-7 -mt-7 mb-7 sm:-mx-9 sm:-mt-9 sm:mb-8 [&_img]:aspect-[2/1] [&_img]:rounded-none [&_img]:object-cover" /> : null}
              <p className="text-sm text-dim">
                <time dateTime={p.publishAt ?? p.created}>{longDate(p.publishAt ?? p.created)}</time>
              </p>
              <h2 className="mt-2 text-[1.75rem] leading-tight font-bold tracking-[-0.02em] text-text">{p.title}</h2>
              {p.summary ? <p className="mt-3 text-lg leading-relaxed text-dim">{p.summary}</p> : null}
              {p.body ? (
                <Prose className="mt-2">
                  <PostText body={p.body} />
                </Prose>
              ) : null}
            </article>
          ))
        )}
      </div>
    </>
  )
}
