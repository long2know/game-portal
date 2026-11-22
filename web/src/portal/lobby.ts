import { tableau } from "./art";
import { boardIds, catalog, expansionIds } from "./catalog";
import type { GameId } from "./catalog";
import { el, link } from "./dom";

export function lobby(mount: string): HTMLElement {
  const h1 = el("h1", "", "Find your ", el("em", "", "next move."));
  const hero = el("section", "lobby-hero", el("div", "hero-copy",
    el("p", "eyebrow", "The Jade Court collection"),
    h1,
    el("p", "intro", "Board games, cards and puzzles, with an arcade run after dusk. Play solo or bring a friend to the table."),
    el("div", "hero-actions", link("Choose a game ↓", "#games", "button primary"),
      link("Play Mahjong", `${mount}mahjong/`, "button secondary")),
  ), el("figure", "hero-plate", el("div", "hero-study"),
    el("figcaption", "plate-caption", el("span", "", "Table study 01"), el("span", "", "Go · stones on wood"))));
  hero.setAttribute("aria-label", "Welcome to Jade Court");
  const section = el("section", "catalog");
  section.id = "games";
  const heading = el("h2", "", "Choose a game");
  heading.id = "catalog-heading";
  section.setAttribute("aria-labelledby", heading.id);
  const grid = el("div", "catalog-grid");
  const games: GameId[] = ["mahjong", ...boardIds, ...expansionIds, "lantern-run"];
  for (const game of games) {
    const info = catalog[game];
    const flagship = game === "mahjong";
    const card = el("a", flagship ? "game-card game-card-featured" : "game-card",
      tableau(game, "index"),
      el("div", "game-card-body", el("p", "card-meta", flagship ? `Flagship · ${info.seats}` : info.seats),
        el("h3", "", info.name), el("p", "card-description", info.description)),
      el("span", "card-action", flagship ? "Take a seat" : "", el("span", "arrow", "↗")));
    card.href = `${mount}${game}/`;
    card.setAttribute("aria-label", info.invitation);
    grid.append(card);
  }
  section.append(el("div", "section-title", heading,
    el("p", "section-caption", `${games.length} games · No account required`)), grid);
  return el("div", "lobby", hero, section);
}
