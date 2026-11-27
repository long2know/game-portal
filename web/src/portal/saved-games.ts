import "./saved-games.css";
import { apiCode, mountedJson, portalApi } from "./api";
import type { BotDifficulty, Table } from "./api";
import { catalog, isBoardId } from "./catalog";
import type { GameId } from "./catalog";
import { Confirmation } from "./confirm";
import { button, el, link, text } from "./dom";
import { isInviteTable, openAcceptedTable } from "./invite-handoff";
import type { InviteTable } from "./invite-handoff";

const seated = { holdem: "poker", "word-tiles": "words", farkle: "farkle", backgammon: "backgammon",
  "big-two": "big-two", "koi-koi": "koi-koi", "jade-harbors": "jade-harbors" } as const;
type Target = InviteTable | { game: "solitaire" | "sudoku"; tableId: null };
type Save = Target & { variant: string | null; players: number | null; botDifficulty?: BotDifficulty; canStartFresh: boolean };
type FreshTable = Pick<Table, "id" | "status" | "seat" | "seats">;
type Row = { game: GameId; note: string; resume: HTMLElement; fresh?: HTMLElement };
const changed = "This save changed or is no longer available. Continue to check it, or reload the list.";
const cannotOpen = "Couldn’t open the exact saved table. Allow session storage and try again.";
const uncertain = "The fresh start wasn’t confirmed. Continue or reload the list to check before trying again.";

function readSave(value: unknown): Save | undefined {
  if (!value || typeof value !== "object") return;
  const raw = value as { game?: unknown; tableId?: unknown; variant?: unknown; players?: unknown; botDifficulty?: unknown; canStartFresh?: unknown };
  let target: Target;
  if (isInviteTable(value)) target = { game: value.game, tableId: value.tableId };
  else if ((raw.game === "solitaire" || raw.game === "sudoku") && raw.tableId === null) target = { game: raw.game, tableId: null };
  else return;
  const variant = typeof raw.variant === "string" && raw.variant.length <= 64 ? raw.variant : null;
  const players = typeof raw.players === "number" && Number.isInteger(raw.players) && raw.players >= 2 && raw.players <= 6 ? raw.players : null;
  const difficulty = raw.botDifficulty;
  const botDifficulty = difficulty === "easy" || difficulty === "standard" || difficulty === "hard" ? difficulty : undefined;
  return {
    ...target, variant, players, botDifficulty,
    canStartFresh: raw.canStartFresh === true && (target.game === "mahjong" ||
      (target.tableId === null ? !!variant : !!players && (!isBoardId(target.game) || !!variant))),
  };
}

function botHost(table: FreshTable, id: string, count: number | null): boolean {
  return count !== null && table.id === id && table.status !== "closed" && table.seat === 0 && table.seats.length === count &&
    table.seats.some(seat => seat.seat === 0 && seat.occupied && !seat.bot) &&
    table.seats.filter(seat => seat.seat !== 0 && seat.occupied && seat.bot).length === count - 1;
}

export async function showSaves(mount: string, before: HTMLElement, values: unknown[], local: string | null, unavailable: boolean) {
  const json = mountedJson(mount);
  const api = portalApi(mount);
  const rows: Row[] = [];
  const confirmations = new Confirmation();
  const notice = el("p", "save-notice");
  notice.setAttribute("role", "status");
  let busy = false;
  const blocked = new Set<GameId>();
  const freshControls = new Map<GameId, HTMLButtonElement>();

  function navigate(target: InviteTable) {
    if (busy) return;
    try { openAcceptedTable(mount, target); }
    catch { text(notice, cannotOpen); }
  }

  async function start(save: Save) {
    if (busy || blocked.has(save.game)) return;
    if (save.tableId !== null) {
      try {
        const probe = `${mount}court-handoff-available`;
        sessionStorage.setItem(probe, "");
        sessionStorage.removeItem(probe);
      } catch { text(notice, cannotOpen); return; }
    }
    busy = true;
    for (const control of freshControls.values()) control.disabled = true;
    text(notice, `Starting ${catalog[save.game].name}…`);
    let writing = false;
    let destination: InviteTable | undefined;
    try {
      if (save.tableId === null) {
        writing = true;
        await json(`api/solo/${save.game}`, "POST", { variant: save.variant });
        location.assign(`${mount}${save.game}/`);
        return;
      }
      if (save.game === "mahjong") {
        const { freshMahjong } = await import("./mahjong-fresh");
        const issue = await freshMahjong(mount, save.tableId);
        if (issue) {
          if (issue.uncertain) blocked.add(save.game);
          text(notice, issue.message);
          return;
        }
        destination = save;
      } else if (isBoardId(save.game)) {
        const prior = await api.read(save.tableId);
        if (!botHost(prior, save.tableId, save.players) || prior.game !== save.game || prior.variant !== save.variant) {
          text(notice, changed);
          return;
        }
        writing = true;
        const fresh = await api.create(save.game, prior.variant, prior.seats.length, true,
          save.game === "chess" ? prior.botDifficulty ?? save.botDifficulty : undefined);
        if (fresh.game !== save.game || fresh.id === prior.id || !botHost(fresh, fresh.id, save.players)) throw new Error();
        destination = { game: save.game, tableId: fresh.id };
      } else {
        const endpoint = `api/${seated[save.game]}/tables`;
        const prior = await json<FreshTable>(`${endpoint}/${encodeURIComponent(save.tableId)}`);
        if (prior.status !== "playing" || !botHost(prior, save.tableId, save.players)) {
          text(notice, changed);
          return;
        }
        writing = true;
        const fresh = await json<FreshTable>(endpoint, "POST", { players: prior.seats.length, bots: true });
        if (fresh.id === prior.id || !botHost(fresh, fresh.id, save.players)) throw new Error();
        destination = { game: save.game, tableId: fresh.id };
      }
      openAcceptedTable(mount, destination);
    } catch (error) {
      const code = apiCode(error);
      if (destination || writing && (!code || code === "unavailable")) blocked.add(save.game);
      text(notice, blocked.has(save.game) ? uncertain : code === "busy" ? "The court is busy. Wait a moment and try again." : changed);
    } finally {
      busy = false;
      for (const [game, control] of freshControls) control.disabled = blocked.has(game);
    }
  }

  for (const value of values) {
    const save = readSave(value);
    if (!save || rows.some(row => row.game === save.game)) continue;
    const title = catalog[save.game].name;
    const resume = save.tableId !== null ? button("Continue", "button primary", () => navigate(save)) :
      link("Continue", `${mount}${save.game}/`, "button primary");
    resume.setAttribute("aria-label", `Continue ${title}`);
    const note = save.tableId !== null ? save.canStartFresh ? "Bot table" : "Saved seat" : "Saved solo game";
    let fresh: HTMLButtonElement | undefined;
    if (save.canStartFresh) {
      const summary = save.game === "mahjong" ?
        "This keeps the same bots, settings and table total, but resets the match score. If the seat is open in another tab, you’ll take it over." :
        save.tableId === null ? `Starting again replaces your saved ${title} game.` :
          "Create another bot table with the same settings. Your previous table won’t be closed or changed.";
      fresh = button("Start fresh", "button secondary", () => {
        if (!busy) confirmations.show("Start fresh", summary, fresh!, () => void start(save));
      });
      fresh.setAttribute("aria-label", `Start fresh: ${title}`);
      freshControls.set(save.game, fresh);
    }
    rows.push({ game: save.game, note, resume, fresh });
  }
  if (local) {
    const { MahjongStack } = await import("./games/mahjong-stack");
    const stack = MahjongStack.restore(local);
    if (stack) {
      const path = `${mount}mahjong-solitaire/`;
      const resume = link("Continue", path, "button primary");
      const fresh = link("Start fresh", `${path}?fresh=1`, "button secondary");
      resume.setAttribute("aria-label", "Continue Mahjong Solitaire");
      fresh.setAttribute("aria-label", "Start fresh: Mahjong Solitaire");
      rows.push({ game: "mahjong-solitaire", note: "On this device", resume, fresh });
    }
  }

  if (before.previousElementSibling?.id === "continue-playing") before.previousElementSibling.remove();
  if (!rows.length && !unavailable) return;
  if (unavailable) text(notice, "Couldn’t check for server saves. You can still open a game from the catalog.");
  const heading = el("h2", "", "Continue playing");
  heading.id = "continue-heading";
  const list = el("ul", "save-list", ...rows.map(({ game, note, resume, fresh }) =>
    el("li", "save-row", el("div", "", el("h3", "", catalog[game].name), el("p", "muted", note)),
      el("div", "save-actions", resume, ...(fresh ? [fresh] : [])))));
  const shelf = el("section", "save-shelf", heading, ...(rows.length ? [list] : []), notice, confirmations.root);
  shelf.id = "continue-playing";
  shelf.setAttribute("aria-labelledby", heading.id);
  before.before(shelf);
}
