import type { Seat, Snapshot } from "./protocol";

export const birdProfiles = {
  "changsha-v1": "One bird (classic)",
  "changsha-two-bird-v1": "Two birds (house rules)",
} as const;

export function tableScore(state: Snapshot, seat: Seat): number {
  return state.room.tableTotals?.[seat] ??
    state.game?.settlement?.seatResults.find(row => row.seat === seat)?.total ??
    state.game?.seats.find(row => row.seat === seat)?.total ?? 0;
}

export function canManageMatch(state: Snapshot | null): boolean {
  return !!state?.game && state.room.hostSeat === state.self.seat &&
    state.room.seats.every(seat => seat.seat === state.self.seat || seat.kind === "bot");
}
