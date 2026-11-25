# Jade Court solo engines

`JadeCourt.Solo` exposes `KlondikeGame` and `SudokuGame`. Each takes a variant in
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
  cell unchanged is rejected. Both games count successful actions as `Moves`,
  including hints and recycles. Completed games reject further actions.

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
