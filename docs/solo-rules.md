# Jade Court solo engines

`JadeCourt.Solo` exposes `KlondikeGame`, `SudokuGame`, and `WindwardGame`. Each takes a variant in
`Start`, a saved checkpoint and a `SoloAction` in `Apply`, and returns a typed
position from `View`. Only the host stores the opaque, versioned checkpoint;
it contains private game state and must never go to clients or logs. The
engines don't own autosave, concurrency revisions, or retries. Unsupported
variants throw `ArgumentException`, invalid plays throw `InvalidOperationException`,
and invalid checkpoints throw `InvalidDataException`.

## Klondike (`draw-one`)

- Deal seven columns with one face-up card each; the rest of the 52-card deck
  goes to stock. `draw` moves one stock card to the top of waste. With empty
  stock, it recycles the remaining waste in order without revealing the next
  card. Passes are unlimited. `StockCount` and `WasteCount` distinguish draw,
  recycle, and empty.
- `move` takes `from`, `to`, and `count`. Piles are `waste`, `t0`–`t6`, and
  `f0`–`f3` (clubs, diamonds, hearts, spades). Only the waste or foundation top,
  or a face-up tableau tail, can move. Tableau builds descend in alternating
  colors; only Kings (alone or leading a tail) fill an empty column. Exposing a
  covered top flips it automatically. Each foundation builds in suit, Ace
  through King; a top foundation card may return to tableau.
- `LegalMoves` lists currently legal transfers. The projection shows only the
  top of waste and each foundation, and gives covered cards a null rank and
  suit. `Finished` means all four foundations are complete. No undo or
  guarantee that every shuffled deal is winnable.

## Sudoku (`easy`, `standard`, `hard`)

- Each 9×9 puzzle has a unique solution with rotationally symmetric clues.
  The variants keep 45, 37, and 31 clues respectively; this is a clue-count
  distinction, not a human-solving difficulty rating. Boards are row-major
  arrays of 81 numbers, with zero meaning blank.
- `set` takes `index` (0–80) and `value` (0–9; zero clears). Givens are immutable.
  Guesses are accepted; `Conflicts` lists every cell participating in a repeated
  digit in its row, column, or box. A wrong digit with no duplicate does not
  count as a conflict.
- `hint` fills or corrects one editable cell, defaulting to the first blank or
  incorrect editable cell in row-major order. Hints do not lock cells and no
  solution field is projected. A set or targeted hint that would leave the
  cell unchanged is rejected. Klondike and Sudoku count successful actions as `Moves`,
  including hints and recycles. Completed games reject further actions.

## Windward Ledger (`soundings`)

See the [voyage rules and controls](windward-ledger-rules.md). The server stores
an opaque `windward-ledger-v1` replay of accepted orders, bounded by the season and
action limits. The engine replays against the original fixed chart and rejects
unknown versions, malformed journals, and illegal histories. The chart and
stock shown in the guest view are reconstructed, never accepted from the client.

## Mahjong Solitaire (`mahjong-solitaire/`)

- Clear the 78-tile, four-tier Lantern Stack by matching two identical suited faces.
  At selection time, each tile needs one open horizontal side and no overlap
  from any higher tier. Partial overlap also covers a tile; front and back edges
  do not restrict removal.
- Seeds assign faces to a full, legal removal sequence, so every initial deal
  can be solved. Other matching choices can run out of pairs. **Hint** marks an
  available pair without removing it; it does not promise a way out after every
  choice. **Undo** restores the last pair, **Restart deal** resets the same seed,
  and confirmed **New game** starts a different deal.
- This game creates no guest and has no server save. Its browser-local progress
  key is `${mount}court-mahjong-solitaire-v1` (including the mount's trailing
  slash), exported by [`mahjong-solitaire-save.ts`](../web/src/portal/games/mahjong-solitaire-save.ts)
  without loading the game. The versioned save stores the seed and replays
  validated matches on load. The regular route resumes it; adding `?fresh=1`
  opens the **New game** confirmation without replacing progress on cancel.
  Without browser storage the deal remains playable until navigation or reload.

### Daily Mahjong Solitaire

Daily Court uses the same 78-tile stack, rule engine, faces, Hint and Undo.
The day’s deal seed is the number of UTC days since 1970-01-01, interpreted
as an unsigned 32-bit integer. The generator assigns equal faces to the
pairs of a legal removal sequence, so the initial deal has a solution.
Clearing the stack earns completion for that date. **Restart today’s deal**
resets that exact seed without removing an earned completion; there is no
new random deal in Daily Court. Its dated progress is [stored separately](portal-api.md#daily-court)
and never changes the practice deal.

## Ink Grids (`nonograms/`)

- Each number is the length of an uninterrupted ink run. Read the row clues
  left to right and the column clues top to bottom; separate adjacent runs by
  at least one unpainted square. A **0** means no ink. The twelve original
  plates (four each at 5×5, 8×8 and 10×10) have exactly one solution apiece and
  can all be solved by line deduction without guessing. The
  [Ink Grids spec](../web/e2e/nonograms.spec.ts) independently enumerates the
  solutions and plays the released controls at either portal mount.
- Select **Ink**, **X Empty**, or **Erase**, then tap a square. An X rules a
  square out; the undecided dot is not a ruling. Tap a clue to highlight that
  line. With the grid focused, arrows move among squares and their clue
  headers; Enter or Space uses the selected tool on a square. **I**, **X** and
  **E** apply the corresponding marks directly. **Enlarge grid** gives each
  square a 52 px target; swipe inside the paper to see more, or use **Fit grid**
  for the overview. The top and left clues stay in view as you scroll.
- **Hint** marks a forced square without changing it. It filters the possible
  placements in each line using your marks, passes forced deductions between
  lines, and reports a contradiction if no placement remains. It does not
  check against an answer bitmap or silently remove your guesses. A print is
  earned only when all ink runs match their row and column clues. Remaining
  unpainted squares need not be crossed out. **Undo** can reopen a finished
  plate; **Reset puzzle** confirms before clearing only that plate.
- The selected plate, marks and up to 32 recent edits per plate stay in this
  browser under `${mount}court-ink-grids-v1` (mount includes the trailing
  slash). No guest, API, shared save or leaderboard is involved. If browser
  storage is unavailable, marks survive plate changes but not navigation or
  reload. Browser tabs do not synchronize their edits.
- The silhouettes, paper fibers and print-room artwork are original. The
  [Conceptis rules](https://www.conceptispuzzles.com/index.aspx?uri=puzzle/pic-a-pix/rules)
  were consulted for run separation, and the
  [Pattern manual](https://www.chiark.greenend.org.uk/~sgtatham/puzzles/doc/pattern.html)
  for three-state and keyboard conventions. No outside puzzles, artwork,
  fonts or code ship with Ink Grids.
