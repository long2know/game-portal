const games = ["mahjong", "chess", "go", "reversi", "checkers", "chinese-checkers", "holdem", "word-tiles", "farkle", "backgammon",
  "xiangqi", "gomoku", "big-two"] as const;
export type InviteGame = typeof games[number];
export type InviteTable = { game: InviteGame; tableId: string };

export const isInviteGame = (value: unknown): value is InviteGame => games.some(game => game === value);
const validTable = (game: InviteGame, id: unknown): id is string =>
  typeof id === "string" && (game === "mahjong" ? /^[A-Za-z0-9_-]{22}$/ : /^[a-f0-9]{32}$/).test(id);
export const isInviteTable = (value: unknown): value is InviteTable => {
  const table = value as Partial<InviteTable> | null;
  return !!table && isInviteGame(table.game) && validTable(table.game, table.tableId);
};

const storageKey = (mount: string) => `${mount}court-accepted-table`;
type Entry = InviteTable & { mount: string; accepted: boolean };

export function openAcceptedTable(mount: string, table: InviteTable): void {
  const destination = new URL(`${mount}${table.game}/`, location.origin);
  if (!isInviteTable(table) || destination.origin !== location.origin) throw new Error("Invalid table");
  sessionStorage.setItem(storageKey(mount), JSON.stringify({ ...table, until: Date.now() + 120_000 }));
  location.assign(destination);
}

export function rememberTable(mount: string, game: InviteGame, tableId: string | null, accepted = false): void {
  const entry: Entry | null = tableId === null ? null : { mount, game, tableId, accepted };
  history.replaceState({ ...history.state, courtTable: entry }, "");
}

export function tableEntry(mount: string, game: InviteGame): Entry | undefined {
  const key = storageKey(mount);
  const prior = history.state?.courtTable as Entry | undefined;
  const saved = prior?.mount === mount && prior.game === game && isInviteTable(prior) ? prior : undefined;
  let pending: (InviteTable & { until: number }) | undefined;
  try { pending = JSON.parse(sessionStorage.getItem(key) ?? "null") ?? undefined; } catch { /* Optional storage. */ }
  const manual = new URLSearchParams(location.hash.slice(1)).has("invite");
  if (pending?.game === game && isInviteTable(pending)) {
    if (!manual && pending.until > Date.now()) {
      rememberTable(mount, game, pending.tableId, true);
      sessionStorage.removeItem(key);
      return history.state.courtTable;
    }
    sessionStorage.removeItem(key);
  }
  if (manual && saved) rememberTable(mount, game, null);
  return manual ? undefined : saved;
}
