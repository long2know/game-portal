import "./styles.css";
import { icon } from "../dom";
import type { Guest } from "../protocol";
import { apiCode, portalApi } from "./api";
import { catalog, isBoardId, isExpansionId } from "./catalog";
import { button, el, link } from "./dom";
import type { PortalHost, ProfileChange } from "./host";
import { isInviteGame } from "./invite-handoff";
import { lobby } from "./lobby";
import { MusicControl } from "./music";
import { onlineLobby } from "./social-entry";

const app = document.querySelector<HTMLElement>("#app")!;
const mount = app.dataset.base ?? "/";
const route = location.pathname.startsWith(mount) ? location.pathname.slice(mount.length).replace(/\/$/, "") : "";
const localPuzzle = route === "mahjong-solitaire" || route === "daily-court" || route === "nonograms";
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
  const nameChanged = !!guest && guest.displayName !== next.displayName;
  guest = next;
  if (nameChanged) document.dispatchEvent(new Event("court-display-name"));
  music?.configure(next.preferences);
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

const music = localPuzzle || route === "windward-ledger" ? null : new MusicControl(mount, enabled => changeProfile(({ preferences }) => ({
  preferences: { ...preferences, music: enabled },
}), true));
const brand = link("", mount, "brand");
brand.setAttribute("aria-label", "Jade Court · All games");
brand.append(el("span", "brand-tile"), el("span", "brand-name", "Jade Court"));
const mahjong = link("", `${mount}mahjong/`, "button lobby-mahjong-link");
mahjong.setAttribute("aria-label", "Mahjong table");
mahjong.append(el("span", "", "Mahjong", el("span", "wide-nav-label", " table")));
const socialButton = button("", "button court-lobby-trigger");
socialButton.append(icon("globe"));
const nav = el("nav", "site-nav", ...(music ? [music.button] : []), ...(localPuzzle ? [] : [socialButton]),
  localPuzzle || isBoardId(route) || isExpansionId(route) || route === "lantern-run"
  ? link("‹ All games", mount, "button")
  : mahjong);
nav.setAttribute("aria-label", "Court navigation");
const main = el("main");
main.id = "main";
main.tabIndex = -1;
const skip = link("Skip to content", "#main", "skip-link");
skip.addEventListener("click", event => {
  event.preventDefault();
  main.focus();
});
const footer = el("footer", "court-footer", el("span", "footer-mark", "◆"), el("span", "", "Jade Court"));
if (route) footer.append(el("span", "footer-note", "A place for a good game."));
app.append(skip, el("div", "shell", el("header", "header", brand, nav, ...(music ? [music.note] : [])), main, footer));

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
  farkle: () => import("./games/farkle"),
  backgammon: () => import("./games/backgammon"),
  "big-two": () => import("./games/big-two"),
  "koi-koi": () => import("./games/koi-koi"),
  "jade-harbors": () => import("./games/jade-harbors"),
  "windward-ledger": () => import("./games/windward-ledger"),
};
if (route === "daily-court") {
  void openGame("Daily Court", async () => (await import("./daily-court")).mountDailyCourt(main, mount));
} else if (route === "mahjong-solitaire" || route === "nonograms") {
  const load = route === "nonograms" ? () => import("./games/nonograms") : () => import("./games/mahjong-solitaire");
  void openGame(catalog[route].name, async () => (await load()).mount({ main, mount }));
} else {
  const onlineGame = isInviteGame(route);
  const localOnly = !onlineGame && route !== "solitaire" && route !== "sudoku";
  const social = onlineLobby(socialButton, { mount, openGuest, saveName: name => changeProfile(() => ({ displayName: name })) }, !onlineGame, localOnly);
  const host: PortalHost = { main, mount, api, openGuest, changeProfile, forgetGuest: () => { guest = undefined; }, social };
  if (isBoardId(route)) void openGame(catalog[route].name, async () => new (await import("./room")).GameRoom(route, host));
  else if (isExpansionId(route)) void openGame(catalog[route].name, async () => (await expansions[route]()).mount(host));
  else {
    if (route === "lantern-run") {
      void openGame(catalog[route].name, async () => (await import("./games/lantern-run")).mount(host));
    } else main.append(lobby(mount));
    if (music?.remembered) void openGuest().catch(() => music.preferencesUnavailable());
  }
  music?.restorePending();
}
