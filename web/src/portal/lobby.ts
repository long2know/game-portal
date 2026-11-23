import { tableau } from "./art";
import { boardIds, catalog, expansionIds } from "./catalog";
import type { GameId } from "./catalog";
import { el, link } from "./dom";

export function lobby(mount: string): HTMLElement {
  const h1 = el("h1", "", "Find your ", el("em", "", "next move."));
  const hero = el("section", "lobby-hero", el("div", "hero-copy",
    el("p", "eyebrow", "The Jade Court collection"),
    h1,
    el("p", "intro", "Play board games, cards, and puzzles solo, against bots, or with friends."),
    el("div", "hero-actions", link("Choose a game ↓", "#games", "button primary"),
      link("Play Mahjong", `${mount}mahjong/`, "button secondary")),
  ), el("figure", "hero-plate", el("div", "hero-study"),
    el("figcaption", "plate-caption", "Go · slate and shell on kaya")));
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
    const title = el("h3", "", info.name);
    const seats = el("p", "card-meta", flagship ? `Flagship · ${info.seats}` : info.seats);
    const summary = el("p", "card-description", info.description);
    seats.id = `catalog-${game}-seats`;
    summary.id = `catalog-${game}-summary`;
    const descriptionIds = [seats.id, summary.id];
    const card = el("a", flagship ? "game-card game-card-featured" : "game-card",
      tableau(game, "index"),
      el("div", "game-card-body", ...(flagship ? [seats, title, summary] : [title, summary, seats])));
    if (flagship) {
      title.id = "catalog-mahjong-name";
      descriptionIds.unshift(title.id);
      card.append(el("span", "card-action", info.invitation));
    }
    card.href = `${mount}${game}/`;
    card.setAttribute("aria-label", info.invitation);
    card.setAttribute("aria-describedby", descriptionIds.join(" "));
    grid.append(card);
  }
  section.append(el("div", "section-title", heading,
    el("p", "section-caption", `${games.length} games · No account required`)), grid);
  return el("div", "lobby", hero, section);
}
