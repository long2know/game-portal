import { tableau } from "./art";
import { boardIds, catalog } from "./catalog";
import type { GameId } from "./catalog";
import { el, link } from "./dom";

export function lobby(mount: string): HTMLElement {
  const h1 = el("h1", "", "Find your ", el("em", "", "next move."));
  const hero = el("section", "lobby-hero", el("div", "hero-copy",
    el("p", "eyebrow", "The board game parlor"),
    h1,
    el("p", "intro", "Six classic games, one quiet court. Play a bot at your own pace, or invite a friend to a private table."),
    el("div", "hero-actions", link("Choose a game ↓", "#games", "button primary"),
      link("Play Mahjong", `${mount}mahjong/`, "button secondary")),
  ), el("div", "hero-study"));
  hero.setAttribute("aria-label", "Welcome to Jade Court");
  const section = el("section", "catalog");
  section.id = "games";
  const heading = el("h2", "", "Choose a game");
  heading.id = "catalog-heading";
  section.setAttribute("aria-labelledby", heading.id);
  const grid = el("div", "catalog-grid");
  const games: GameId[] = ["mahjong", ...boardIds];
  for (const game of games) {
    const info = catalog[game];
    const card = el("a", "game-card", tableau(game), el("div", "game-card-body",
      el("p", "card-meta", game === "mahjong" ? "Flagship · 4 players" : info.seats),
      el("h3", "", info.name),
      el("p", "card-description", info.description),
      el("span", "card-action", game === "mahjong" ? "Take a seat" : "Open game", el("span", "arrow", "↗")),
    ));
    card.href = `${mount}${game}/`;
    card.setAttribute("aria-label", info.invitation);
    grid.append(card);
  }
  section.append(el("div", "section-title", el("div", "", el("p", "eyebrow", "The collection"), heading),
    el("p", "section-caption", "Six games, no account required.")), grid);
  return el("div", "lobby", hero, section);
}
