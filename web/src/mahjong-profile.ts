import type { RulesVersion, Seat, Snapshot } from "./protocol";

export const classicRules = "changsha-v1";
export const twoTileKongRules = "changsha-two-tile-kong-v1";
export const rulesProfiles = {
  [classicRules]: { label: "One bird (classic)", birds: 1 },
  "changsha-two-bird-v1": { label: "Two birds (house rules)", birds: 2 },
  [twoTileKongRules]: { label: "Two-tile Kong · Two birds", birds: 2 },
} as const satisfies Record<RulesVersion, { label: string; birds: 1 | 2 }>;

export function rulesProfile(version: RulesVersion = classicRules) {
  return rulesProfiles[version];
}

export function tableScore(state: Snapshot, seat: Seat): number {
  return state.room.tableTotals?.[seat] ??
    state.game?.settlement?.seatResults.find(row => row.seat === seat)?.total ??
    state.game?.seats.find(row => row.seat === seat)?.total ?? 0;
}

export function canManageMatch(state: Snapshot | null): boolean {
  return !!state?.game && state.room.hostSeat === state.self.seat &&
    state.room.seats.every(seat => seat.seat === state.self.seat || seat.kind === "bot");
}
