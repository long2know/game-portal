import "./styles.css";
import type { Guest } from "../protocol";
import { apiCode, portalApi } from "./api";
import { catalog, isBoardId, isExpansionId } from "./catalog";
import { button, el, link } from "./dom";
import type { PortalHost, ProfileChange } from "./host";
import { lobby } from "./lobby";
import { MusicControl } from "./music";

const app = document.querySelector<HTMLElement>("#app")!;
const mount = app.dataset.base ?? "/";
const route = location.pathname.startsWith(mount) ? location.pathname.slice(mount.length).replace(/\/$/, "") : "";
const api = portalApi(mount);
let guest: Guest | undefined;
let opening: Promise<Guest> | undefined;
let profileWrites: Promise<unknown> = Promise.resolve();

function rememberGuest(next: Guest): Guest {
  const reduced = String(next.preferences.reducedMotion);
  if (document.documentElement.dataset.reducedMotion !== reduced) {
    document.documentElement.dataset.reducedMotion = reduced;
    document.dispatchEvent(new Event("court-motion-preference"));
  }
  guest = next;
  music.configure(next.preferences);
  return next;
}

function openGuest(refresh = false): Promise<Guest> {
  return guest && !refresh ? Promise.resolve(guest) : opening ??= api.guest().then(rememberGuest)
    .finally(() => { opening = undefined; });
}

function changeProfile(change: ProfileChange, refresh = false): Promise<Guest> {
  const readLatest = refresh && !!guest;
  const write = profileWrites.then(async () => {
    try { return rememberGuest(await api.profile(change(await openGuest(readLatest)))); }
    catch (error) {
      if (apiCode(error) === "not_authenticated") guest = undefined;
      throw error;
    }
  });
  profileWrites = write.catch(() => {});
  return write;
}

const music = new MusicControl(mount, enabled => changeProfile(({ preferences }) => ({
  preferences: { ...preferences, music: enabled },
}), true));
const brand = link("", mount, "brand");
brand.setAttribute("aria-label", "Jade Court · All games");
brand.append(el("span", "brand-tile"), el("span", "brand-name", "Jade Court"));
const nav = el("nav", "site-nav", music.button, isBoardId(route) || isExpansionId(route)
  ? link("‹ All games", mount, "button")
  : link("Mahjong table ↗", `${mount}mahjong/`, "button"));
nav.setAttribute("aria-label", "Court navigation");
const main = el("main");
main.id = "main";
main.tabIndex = -1;
const skip = link("Skip to content", "#main", "skip-link");
skip.addEventListener("click", event => {
  event.preventDefault();
  main.focus();
});
const footer = el("footer", "court-footer",
  el("span", "footer-mark", "◆"), el("span", "", "Jade Court"), el("span", "", "A place for a good game."));
app.append(skip, el("div", "shell", el("header", "header", brand, nav, music.note), main, footer));

const host: PortalHost = { main, mount, api, openGuest, changeProfile, forgetGuest: () => { guest = undefined; } };
async function openGame(name: string, render: () => Promise<unknown>): Promise<void> {
  document.title = `${name} · Jade Court`;
  const progress = el("p", "muted", `Opening ${name}…`);
  progress.setAttribute("role", "status");
  main.append(progress);
  try { await render(); }
  catch {
    progress.textContent = "Couldn’t load the game. Check your connection and try again.";
    main.append(button("Reload game", "button secondary", () => location.reload()));
    return;
  }
  progress.remove();
}

const expansions = {
  holdem: () => import("./games/holdem"),
  solitaire: () => import("./games/solitaire"),
  sudoku: () => import("./games/sudoku"),
  "word-tiles": () => import("./games/words"),
};
if (isBoardId(route)) void openGame(catalog[route].name, async () => new (await import("./room")).GameRoom(route, host));
else if (isExpansionId(route)) void openGame(catalog[route].name, async () => (await expansions[route]()).mount(host));
else {
  main.append(lobby(mount));
  if (music.remembered) void openGuest().catch(() => music.preferencesUnavailable());
}
music.restorePending();
