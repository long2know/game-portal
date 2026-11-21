export interface Variant {
  id: string;
  label: string;
}

export interface GameDefinition {
  id: string;
  name: string;
  description: string;
  playerCounts: number[];
  variants: Variant[];
}

export interface Cell {
  id: string;
  row: number;
  column: number;
  owner: number | null;
  piece: string | null;
}

export interface Move {
  id: string;
  label: string;
  from: string | null;
  to: string | null;
  promotion?: string | null;
  via?: string[] | null;
}

export interface Position {
  rows: number;
  columns: number;
  cells: Cell[];
  turn: number | null;
  status: string;
  winners: number[];
  detail: string | null;
  moves: Move[];
  lastMove: Move | null;
  scores?: number[] | null;
}

export interface Seat {
  seat: number;
  name: string | null;
  bot: boolean;
  occupied: boolean;
}

export interface Table {
  id: string;
  game: string;
  variant: string;
  revision: number;
  status: "waiting" | "playing" | "finished";
  seat: number;
  invite: string | null;
  seats: Seat[];
  position: Position | null;
}

export interface Guest {
  displayName: string;
}

export class ApiError extends Error {
  constructor(readonly code: string) { super(code); }
}

export function portalApi(mount: string) {
  async function json<T>(path: string, method = "GET", body?: object, abort?: AbortSignal): Promise<T> {
    const timeout = AbortSignal.timeout(8000);
    const response = await fetch(mount + path, {
      method, cache: "no-store", signal: abort ? AbortSignal.any([timeout, abort]) : timeout,
      ...(body ? { headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) } : {}),
    });
    if (!response.ok) {
      const result = await response.json().catch(() => null);
      throw new ApiError(result?.error?.code ?? "unavailable");
    }
    return response.json();
  }

  const tablePath = (id: string) => `api/board/tables/${encodeURIComponent(id)}`;
  return {
    guest: () => json<Guest>("api/guest", "POST", {}),
    profile: (displayName: string) => json<Guest>("api/profile", "PATCH", { displayName }),
    games: () => json<{ games: GameDefinition[] }>("api/board/games"),
    create: (game: string, variant: string, players: number, bots: boolean) =>
      json<Table>("api/board/tables", "POST", { game, variant, players, bots }),
    join: (invite: string) => json<Table>("api/board/join", "POST", { invite }),
    resume: (game: string) => json<{ tableId: string | null }>(`api/board/resume?game=${encodeURIComponent(game)}`),
    read: (id: string, signal?: AbortSignal) => json<Table>(tablePath(id), "GET", undefined, signal),
    move: (id: string, revision: number, moveId: string) => json<Table>(`${tablePath(id)}/moves`, "POST", { revision, moveId }),
  };
}
