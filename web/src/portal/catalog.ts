export const boardIds = ["chess", "go", "reversi", "checkers", "chinese-checkers"] as const;
export const expansionIds = ["holdem", "solitaire", "sudoku", "word-tiles", "farkle", "backgammon"] as const;
export type BoardId = typeof boardIds[number];
export type ExpansionId = typeof expansionIds[number];
export type GameId = BoardId | ExpansionId | "mahjong" | "lantern-run";

const jadeLettersName = "Jade Letters";

export const catalog: Record<GameId, { name: string; seats: string; description: string; invitation: string }> = {
  mahjong: {
    name: "Changsha Mahjong", seats: "4 players",
    description: "Draw and discard, claim melds, and complete a winning hand.",
    invitation: "Take a seat",
  },
  chess: {
    name: "Chess", seats: "2 players",
    description: "Control the center, develop your pieces, and trap the king.",
    invitation: "Set up Chess",
  },
  go: {
    name: "Go", seats: "2 players",
    description: "Surround territory and capture stones on a 19×19 grid.",
    invitation: "Set up Go",
  },
  reversi: {
    name: "Reversi", seats: "2 players",
    description: "Trap opposing discs between your own to flip them.",
    invitation: "Set up Reversi",
  },
  checkers: {
    name: "Checkers", seats: "2 players",
    description: "Jump to capture; reach the far rank to crown your checker.",
    invitation: "Set up Checkers",
  },
  "chinese-checkers": {
    name: "Chinese Checkers", seats: "2, 3, 4 or 6 players",
    description: "Chain jumps to move all your pegs to the opposite point.",
    invitation: "Set up Chinese Checkers",
  },
  holdem: {
    name: "Texas Hold’em", seats: "2–6 players",
    description: "Read the board, manage your stack, and play the odds with play chips only.",
    invitation: "Set up Texas Hold’em",
  },
  solitaire: {
    name: "Solitaire", seats: "Solo · Draw one",
    description: "Build four suited piles from ace to king; alternate colors in the tableau.",
    invitation: "Play Solitaire",
  },
  sudoku: {
    name: "Sudoku", seats: "Solo · 3 difficulties",
    description: "Use each digit from 1 to 9 once per row, column, and box.",
    invitation: "Play Sudoku",
  },
  "word-tiles": {
    name: jadeLettersName, seats: "2 players",
    description: "Cross words with letter tiles; use premiums to score more.",
    invitation: `Set up ${jadeLettersName}`,
  },
  farkle: {
    name: "Farkle", seats: "2–6 players",
    description: "Keep scoring dice; bank your points or risk losing them on another roll.",
    invitation: "Set up Farkle",
  },
  backgammon: {
    name: "Backgammon", seats: "2 players",
    description: "Hit blots to send them to the bar, then race your checkers home and bear off.",
    invitation: "Set up Backgammon",
  },
  "lantern-run": {
    name: "Lantern Run", seats: "Solo · Arcade",
    description: "Dodge kilns and rails, and collect lanterns along the canal.",
    invitation: "Play Lantern Run",
  },
};

export function isBoardId(id: string): id is BoardId {
  return boardIds.some(game => game === id);
}

export function isExpansionId(id: string): id is ExpansionId {
  return expansionIds.some(game => game === id);
}
