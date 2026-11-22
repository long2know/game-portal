# Word Tiles rules and engine contract

Word Tiles is a two-seat crossword game. New deals use **Classic**: a 15×15 board with an original symmetric premium layout,
seven tiles per rack, and an original 100-tile supply including two blanks. Already-dealt **Quick** games keep their 11×11
board and original rules, table, and guests after an update. A table waiting for its first deal starts with Classic.
Seat `0` opens. The dealer and exchange shuffles use the operating system's cryptographic random generator.
A given rack is not guaranteed to have a legal play.

## Turns and scoring

- **Place:** Place distinct tiles from your rack in one row or column, in empty cells. Existing tiles may bridge gaps
  between new tiles; empty cells may not. The first play must cover the center; later plays must touch an existing tile
  orthogonally. Every complete new or extended word of two or more letters must be in the lexicon, including perpendicular
  words. A single tile may form a word in either direction or in both. Unchanged words and isolated letters do not score.
  Refill the rack while tiles remain.
- **Exchange:** Draw replacements first, then return the traded tiles to the bag and shuffle. Only the count is public.
  The turn scores nothing. The two profiles have different exchange limits.
- **Pass:** Play no tiles and score nothing.

### Classic (new deals)

- The center `(7,7)` is a double-word square. A letter premium doubles or triples the new tile's face value before the
  word total is multiplied by any new word premiums. Apply each covered premium to every word made through that square
  **on that turn only**. Previously placed tiles contribute their face values with no premiums, even if they extend a word.
- Assign a blank any uppercase `A`–`Z` letter when placing it. It keeps that letter on the board and is always worth
  zero, including on letter premiums. A blank can still activate a word premium for the other letters. A zero-point word
  is legal.
- Add **50 points** after scoring all the words when you place seven tiles in one turn. For example, playing `CABINET`
  along row `7` from column `4` through `10` on an empty board gives **130 points**: 10 face points, three double-word
  squares, and the 50-point bonus.
- Exchange one to seven of your current tiles only when the bag has **at least seven**.
- Play ends after **six consecutive scoreless turns** (passes, exchanges, or legal plays worth zero) or when a seat
  empties its rack with nothing left in the bag. Subtract the value of each seat's unused tiles from its score. If a
  seat went out, add the opponent's deduction to the seat that emptied its rack.

| Face value | Supply (letter × count) |
|---|---|
| 0 | Blank×2 |
| 1 | A×9, E×11, I×8, L×4, N×6, O×7, R×6, S×4, T×6 |
| 2 | C×3, D×4, G×3, M×3, U×4 |
| 3 | B×2, H×3, P×2, Y×2 |
| 4 | F×2, W×2 |
| 5 | K×1, V×2 |
| 7 | J×1, X×1 |
| 9 | Q×1, Z×1 |

### Quick (already-dealt games)

- Six tiles per rack; center `(5,5)`; 62-tile supply. No premium cells, blanks, or rack bonus. Sum the face values
  in each complete word, including its existing letters, and add two points for each word after the first made on that
  turn. For example, with `CAT` across, play `PI` directly above its `AT` to form `PI` (3), `PA` (3), and `IT` (2),
  plus two links (4): **12 points**.
- Exchange one to three tiles if the bag contains at least that many.
- Play ends after four consecutive turns without a placement (passes or exchanges), or when a seat empties its rack
  with nothing left in the bag. Deduct each seat's unused tile points with no transfer.

| Face value | Supply (letter × count) |
|---|---|
| 1 | A×5, E×6, I×4, L×3, N×3, O×4, R×4, S×4, T×4, U×2 |
| 2 | B×2, C×2, D×2, G×1, H×2, M×2, P×2, Y×2 |
| 3 | F×1, K×1, V×1, W×1 |
| 4 | J×1, X×1 |
| 5 | Q×1, Z×1 |

In both profiles, final scores may be negative; the greater score wins. On a tie `winners` is `[]`.
No more moves are accepted after the game ends.

## Typed library, checkpoints and wire

`src/JadeCourt.Words/JadeCourt.Words.csproj` has no runtime package dependencies. Construct `JadeCourt.Words.WordGame` and use:

- `string Start()`, `string Apply(string checkpoint, int seat, WordAction action)`: these produce opaque, versioned
  **server-only** snapshots. `Start()` deals Classic (`2`); applying turns to a Quick (`1`) snapshot keeps its original
  format and rules. A snapshot contains both racks, draw order, board, turn, scores and the ending streak. It is not a wire
  response, signed input from a client, or a revision/token. The host must authenticate the seat and apply a move to the
  latest committed checkpoint.
- `WordView View(string checkpoint, int? viewerSeat)`: pass an authenticated seat, or `null` for a spectator. A seat sees only its own rack, even when it is not that seat's turn. A spectator sees `rack: []`. The same public fields are available to all viewers, even after the game ends. `board` is sparse (occupied cells only), sorted row then column. `turn` is `0`/`1` in `playing`, `null` in `finished`; `detail` is a terminal reason or `null`. `lastMove` is public display text, not structured game state.
- `WordAction SelectBotMove(WordView actingSeatView)`: pass the current acting seat's projection. The bot has no access to
  the opponent's rack or bag order. It uses the same spelling and scoring rules as a human, including blanks. It searches up
  to 25,000 trie visits and evaluates up to 1,000 candidate placements, chooses the highest-scoring candidate found, and
  passes if none are found or the view cannot be used. The bounds do not guarantee optimal play or finding every legal move.

Serialize the records with .NET JSON web defaults (camel case). `WordView` includes `rows`, `columns`, `turn`, `status`,
`winners`, `detail`, `board`, `rack`, `scores`, `rackSizes`, `bagCount`, and `lastMove`, plus:

- `variant`: `classic` or `quick`.
- `rackSize`: the profile's full rack size. Actual rack sizes and scores are in seat order.
- `premiums`: `{row,column,kind}` cells in row-then-column order; each kind is `double-letter`, `triple-letter`,
  `double-word`, or `triple-word`. Quick uses `[]`. An occupied cell's premium has already been used.
- `exchangeLimit`: the number of tiles the viewer could exchange this turn, or `0` if unavailable.

Coordinates are zero-based. Board cells are `{row,column,tile,seat}`. Tiles are `{id,letter,points,blank}`. `blank` is
`false` for ordinary tiles; for a blank it is `true`, `points` is `0`, and `letter` is `?` in a rack or its assigned
uppercase letter on the board. No bag order, other rack, or checkpoint is returned.

Actions use exact lowercase kinds and only the relevant optional field:

```json
{"kind":"place","placements":[{"row":7,"column":7,"tileId":99,"letter":"A"},{"row":7,"column":8,"tileId":81}]}
{"kind":"exchange","exchange":[4,51]}
{"kind":"pass"}
```

Use IDs from the current `rack`. `letter` must be a single uppercase `A`–`Z` for a blank placement; omit it for an ordinary
tile. An illegal action, wrong turn, or finished game throws `InvalidOperationException`. Out-of-range seats throw
`ArgumentOutOfRangeException`; malformed or unsupported checkpoint data throws `InvalidDataException`.

## Lexicon source and redistribution

The embedded lexicon is a modified subset of [SCOWL (Spell Checker Oriented Word Lists) 2020.12.07](https://wordlist.aspell.net/),
compiled by Kevin Atkinson. The collective work is Copyright 2000–2018 by Kevin Atkinson; the
[verbatim copyright and redistribution grant](../src/JadeCourt.Words/Lexicon/SCOWL-Copyright.txt) include
the upstream component notices and disclaimers. The grant permits using, copying, modifying and distributing
these lists and their outputs under its notice requirements. This is a general English spell-check lexicon,
not a commercial game word list or ENABLE (which SCOWL draws on in higher tiers than those used here).

Use the [2020.12.07 source archive](https://downloads.sourceforge.net/project/wordlist/SCOWL/2020.12.07/scowl-2020.12.07.tar.gz).
Take only `final/english-words.{10,20,35,40}` and `final/american-words.{10,20,35,40}`. Keep lines matching
`[a-z]{2,15}` byte-for-byte before converting to uppercase, deduplicate, then sort bytewise and write
one word per LF line with a final LF. This excludes separate proper-name, abbreviation, jargon and higher-tier
files and rejects accented and punctuated forms; membership in the resulting 43,736-entry list is definitive.
All 40,368 entries of length 2–11 match the previous list exactly, so Quick keeps its original allowed words.
No network fetch or permissive fallback occurs at runtime. Missing, empty or malformed data fails during library initialization.

| File | SHA-256 |
|---|---|
| `scowl-2020.12.07.tar.gz` | `5587667caa20c4891390c2d42dbb4d5c4c3f41bee77af1457ece3ba23fb859cc` |
| `src/JadeCourt.Words/Lexicon/words.txt` | `c0ffd5c58b00722bf065be37a9f3fd3f12f358367fa36bcc8b5414e97b08c7e6` |
| `src/JadeCourt.Words/Lexicon/SCOWL-Copyright.txt` (archive `Copyright`) | `283326a422e29c510e2ba6805518c418ce3c35ed1fadc3eec83d2da4f6c5a055` |

The lexicon and full copyright notice are embedded in `JadeCourt.Words.dll`; the notice is also
copied as `licenses/SCOWL-2020.12.07.txt` for published and packed builds. Retain the notice
and identify the lexicon as modified when redistributing.
