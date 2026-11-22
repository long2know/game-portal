import type { Guest } from "../protocol";

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

export type BotDifficulty = "easy" | "standard" | "hard";

export interface Table {
  id: string;
  game: string;
  variant: string;
  revision: number;
  status: "waiting" | "playing" | "finished" | "closed";
  seat: number;
  invite: string | null;
  seats: Seat[];
  position: Position | null;
  botDifficulty?: BotDifficulty | null;
  endReason?: string | null;
  endedBySeat?: number | null;
  result?: { cause: "resignation"; loserSeat: number; winnerSeat: number } | null;
}

export const isLive = ({ status }: Pick<Table, "status">) => status === "waiting" || status === "playing";

export interface MotionStep {
  move: Move;
  changes: Cell[];
}

export interface TurnMotion {
  fromRevision: number;
  steps: MotionStep[];
}

export type MoveReply = Table & { turnMotion?: TurnMotion };

export class ApiError extends Error {
  constructor(readonly code: string) { super(code); }
}

export const apiCode = (error: unknown) => error instanceof ApiError ? error.code : "";

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
    profile: (change: Partial<Pick<Guest, "displayName" | "preferences">>) => json<Guest>("api/profile", "PATCH", change),
    games: () => json<{ games: GameDefinition[] }>("api/board/games"),
    create: (game: string, variant: string, players: number, bots: boolean, botDifficulty?: BotDifficulty) =>
      json<Table>("api/board/tables", "POST", { game, variant, players, bots, botDifficulty }),
    join: (invite: string) => json<Table>("api/board/join", "POST", { invite }),
    resume: (game: string) => json<{ tableId: string | null }>(`api/board/resume?game=${encodeURIComponent(game)}`),
    read: (id: string, signal?: AbortSignal) => json<Table>(tablePath(id), "GET", undefined, signal),
    move: (id: string, revision: number, moveId: string) => json<MoveReply>(`${tablePath(id)}/moves`, "POST", { revision, moveId }),
    end: (id: string, revision: number, action: "leave" | "resign") =>
      json<Table>(`${tablePath(id)}/${action}`, "POST", { revision }),
  };
}
