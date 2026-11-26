import { apiCode, mountedJson } from "./api";
import { catalogPreview, dailySeal } from "./catalog-art";
import { catalog } from "./catalog";
import type { GameId } from "./catalog";
import { el, link } from "./dom";
import { mahjongSolitaireProgressKey } from "./games/mahjong-solitaire-save";

function discoverSaves(mount: string, before: HTMLElement): void {
  const json = mountedJson(mount);
  const refresh = async () => {
    let local: string | null = null;
    try { local = localStorage.getItem(mahjongSolitaireProgressKey(mount)); } catch { /* Optional storage. */ }
    const result = await json<{ saves?: unknown; unavailable?: boolean }>("api/saves").catch(error =>
      ({ saves: [], unavailable: apiCode(error) !== "not_authenticated" }));
    const saves: unknown[] = Array.isArray(result?.saves) ? result.saves : [];
    if (saves.length || local || result?.unavailable || before.previousElementSibling?.id === "continue-playing") {
      try { await (await import("./saved-games")).showSaves(mount, before, saves, local, !!result?.unavailable); }
      catch { /* Catalog links still open their game and its normal resume flow. */ }
    }
  };
  void refresh();
  window.addEventListener("pageshow", event => { if (event.persisted) void refresh(); });
}

function dailyShelf(mount: string): HTMLElement {
  const heading = el("h2", "", "Daily Court");
  heading.id = "daily-shelf-title";
  const feature = el("section", "daily-shelf", dailySeal(),
    el("div", "daily-shelf-copy", heading, el("p", "", "One Xiangqi tactic. One Mahjong Solitaire stack. New at midnight UTC.")),
    link("Open Daily Court", `${mount}daily-court/`, "button secondary"));
  feature.setAttribute("aria-labelledby", heading.id);
  return feature;
}

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
  const games = Object.keys(catalog) as GameId[];
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
      catalogPreview(game),
      el("div", "game-card-body", ...(flagship ? [seats, title, summary] : [title, seats, summary])));
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
  const daily = dailyShelf(mount);
  const root = el("div", "lobby", hero, daily, section);
  discoverSaves(mount, daily);
  return root;
}
