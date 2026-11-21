export const boardIds = ["chess", "go", "reversi", "checkers", "chinese-checkers"] as const;
export type BoardId = typeof boardIds[number];
export type GameId = BoardId | "mahjong";

export const catalog: Record<GameId, { name: string; seats: string; description: string; invitation: string }> = {
  mahjong: {
    name: "Changsha Mahjong", seats: "4 players",
    description: "Draw, discard and build a winning hand at the flagship table.",
    invitation: "Take a seat",
  },
  chess: {
    name: "Chess", seats: "2 players",
    description: "Plot a few moves ahead and turn a quiet opening into checkmate.",
    invitation: "Set up Chess",
  },
  go: {
    name: "Go", seats: "2 players",
    description: "Place stones, surround territory, and find room to breathe.",
    invitation: "Set up Go",
  },
  reversi: {
    name: "Reversi", seats: "2 players",
    description: "Bracket your opponent’s discs to flip the board in your favor.",
    invitation: "Set up Reversi",
  },
  checkers: {
    name: "Checkers", seats: "2 players",
    description: "Jump, capture, and crown a piece on the far side of the board.",
    invitation: "Set up Checkers",
  },
  "chinese-checkers": {
    name: "Chinese Checkers", seats: "2, 3, 4 or 6 players",
    description: "Hop across a star and race to bring your whole crew home.",
    invitation: "Set up Chinese Checkers",
  },
};

export function isBoardId(id: string): id is BoardId {
  return boardIds.some(game => game === id);
}
