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
