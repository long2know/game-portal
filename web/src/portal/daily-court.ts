import "./daily-court.css";
import { DailyProgress, utcEdition } from "./daily-edition";
import type { Challenge } from "./daily-edition";
import { button, el, link } from "./dom";

const games = {
  xiangqi: { title: "Xiangqi", goal: "Red to move. Checkmate Black in one move." },
  "mahjong-solitaire": { title: "Mahjong Solitaire", goal: "Clear today’s four-tier stack by matching all 78 tiles." },
};
const isChallenge = (hash: string): hash is Challenge => hash === "xiangqi" || hash === "mahjong-solitaire";

export function mountDailyCourt(main: HTMLElement, mount: string): void {
  const progress = new DailyProgress(mount);
  const page = el("div", "daily-page");
  main.append(page);
  let edition = utcEdition(new Date());
  let rendered = 0;
  let timer: number;

  function checkDate(): boolean {
    const now = new Date();
    const next = utcEdition(now);
    if (next.date === edition.date) return true;
    edition = next;
    void render(page.contains(document.activeElement), true);
    schedule();
    return false;
  }

  function schedule(): void {
    clearTimeout(timer);
    timer = window.setTimeout(() => {
      if (checkDate()) schedule();
    }, Math.max(1, edition.nextAt - Date.now()));
  }

  async function render(focus: boolean, rolledOver = false): Promise<void> {
    const revision = ++rendered;
    const { date, seed } = edition;
    const current = () => revision === rendered && checkDate();
    const hash = location.hash.slice(1);
    const challenge = isChallenge(hash) ? hash : null;
    const dateLabel = el("time", "daily-date", `${date} UTC`);
    dateLabel.dateTime = date;
    const top = el("div", "daily-top",
      challenge ? link("‹ Daily Court", "#daily", "button secondary") : el("span", "eyebrow", "The daily edition"), dateLabel);
    const rollover = el("p", "daily-rollover", rolledOver ? "A new UTC edition is ready." : "");
    rollover.setAttribute("role", "status");
    const content = el("div", "daily-content");
    page.replaceChildren(top, rollover, content);
    document.title = challenge ? `${games[challenge].title} · Daily Court` : "Daily Court · Jade Court";

    if (!challenge) {
      const title = el("h1", "", "Daily Court");
      const deck = el("div", "daily-deck");
      for (const id of Object.keys(games) as Challenge[]) {
        const { title, goal } = games[id];
        const attempt = progress.read(id, date);
        const status = el("p", "daily-status", attempt?.completed ? "Completed today" : attempt ? "In progress" : "Ready to play");
        status.toggleAttribute("data-completed", attempt?.completed === true);
        const play = link(attempt && !attempt.completed ? `Continue ${title}` : `Play ${title}`, `#${id}`, "button secondary");
        deck.append(el("section", "daily-card", el("h2", "", title), el("p", "muted", goal), status, play));
      }
      content.append(el("header", "daily-heading", title,
        el("p", "intro", "Two challenges for everyone. A new edition starts at 00:00 UTC.")), deck,
      el("p", "daily-local muted", "Progress stays in this browser. No account needed."));
    } else {
      const loading = el("p", "muted", `Opening ${games[challenge].title}…`);
      loading.setAttribute("role", "status");
      content.append(loading);
      try {
        const saved = progress.read(challenge, date);
        if (challenge === "xiangqi") {
          const { mountDailyXiangqi } = await import("./games/daily-xiangqi");
          if (!current()) return;
          mountDailyXiangqi(content, edition, saved?.value, current, move => {
            progress.save(challenge, date, move, true);
            return progress.available;
          }, progress.available);
        } else {
          const { mount: mountStack } = await import("./games/mahjong-solitaire");
          if (!current()) return;
          mountStack({ main: content, mount }, {
            seed, saved: saved?.value ?? null, current, storageAvailable: progress.available,
            save: (snapshot, won) => {
              progress.save(challenge, date, snapshot, won);
              return progress.available;
            },
          });
        }
        loading.remove();
        const practice = link(challenge === "xiangqi" ? "Open regular Xiangqi" : "Open practice stack", `${mount}${challenge}/`);
        content.append(el("p", "daily-local", practice));
      } catch {
        if (!current()) return;
        loading.textContent = "Couldn’t load this challenge. Check your connection and try again.";
        content.append(button("Reload Daily Court", "button secondary", () => location.reload()));
      }
    }
    if (focus) {
      const heading = content.querySelector("h1");
      if (heading) {
        heading.tabIndex = -1;
        heading.focus();
      }
    }
  }

  window.addEventListener("hashchange", () => { if (checkDate()) void render(true); });
  window.addEventListener("pageshow", checkDate);
  document.addEventListener("visibilitychange", () => { if (!document.hidden) checkDate(); });
  schedule();
  void render(false);
}
