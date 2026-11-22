export const boardIds = ["chess", "go", "reversi", "checkers", "chinese-checkers"] as const;
export const expansionIds = ["holdem", "solitaire", "sudoku", "word-tiles", "farkle", "backgammon"] as const;
export type BoardId = typeof boardIds[number];
export type ExpansionId = typeof expansionIds[number];
export type GameId = BoardId | ExpansionId | "mahjong" | "lantern-run";

const jadeLettersName = "Jade Letters";

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
  holdem: {
    name: "Texas Hold’em", seats: "2–6 players",
    description: "Read the table, build a hand and play for chips. No money changes hands.",
    invitation: "Set up Texas Hold’em",
  },
  solitaire: {
    name: "Solitaire", seats: "Solo · Draw one",
    description: "Uncover the tableau and build four foundations from Ace through King.",
    invitation: "Play Solitaire",
  },
  sudoku: {
    name: "Sudoku", seats: "Solo · 3 difficulties",
    description: "Fill the grid one number at a time, with hints when you need them.",
    invitation: "Play Sudoku",
  },
  "word-tiles": {
    name: jadeLettersName, seats: "2 players",
    description: "Play crossing words with seven tiles, blanks and premium squares.",
    invitation: `Set up ${jadeLettersName}`,
  },
  farkle: {
    name: "Farkle", seats: "2–6 players",
    description: "Keep scoring dice and decide when to bank your points or risk another roll.",
    invitation: "Set up Farkle",
  },
  backgammon: {
    name: "Backgammon", seats: "2 players",
    description: "Race your checkers home, hit blots, and bear off before your opponent.",
    invitation: "Set up Backgammon",
  },
  "lantern-run": {
    name: "Lantern Run", seats: "Solo · Arcade",
    description: "Carry the light through a twilight canal town. Dodge kilns, clear rails and gather lanterns.",
    invitation: "Play Lantern Run",
  },
};

export function isBoardId(id: string): id is BoardId {
  return boardIds.some(game => game === id);
}

export function isExpansionId(id: string): id is ExpansionId {
  return expansionIds.some(game => game === id);
}
