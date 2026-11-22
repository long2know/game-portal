# Word Tiles rules and engine contract

Word Tiles is an independent two-seat crossword game. New deals use **Duel**: a 15×15 board, a 104-tile English
supply, and seven tiles per rack. A table waiting for its first deal starts with Duel. Saved **Quick** and **Classic**
tables keep their rules, tile IDs, draw order, and guests. The server uses
the operating system's cryptographic random generator to shuffle; a rack is not guaranteed to have a playable word.

## Turns and scoring

- **Place:** Place distinct tiles from your rack in one row or column, in empty cells. Existing tiles may bridge gaps
  between new tiles; empty cells may not. The first play must cover the center; later plays must touch an existing tile
  orthogonally. Every complete new or extended word of two or more letters must be in the lexicon, including perpendicular
  words. A single tile may form a word in either direction or in both. Unchanged words and isolated letters do not score.
  Refill the rack while tiles remain.
- **Exchange:** Draw replacements first, then return the traded tiles to the bag and shuffle. Only the count is public.
  The turn scores nothing. The profiles have different exchange limits.
- **Pass:** Play no tiles and score nothing.

### Duel (new deals, checkpoint 3)

- **Opening:** The table creator (seat `0`) plays first. The opening word must have at least two letters and cover the
  neutral center star `(7,7)`. The center does **not** double the score in Duel.
- **Scoring:** Duel uses Jade Court's original symmetric premium layout, excluding the former center double-word:
  12 triple-word, 12 double-word, 20 triple-letter and 20 double-letter cells. Apply newly covered letter premiums
  first, then multiply the word by each newly covered word premium. Apply a tile's premium to every word made
  through it on that turn; previously placed tiles score only their face value. Assign
  blanks an uppercase letter for the rest of the game. A blank stays worth zero on any square, but still activates word
  premiums. Add **35 points once** after scoring every word if seven tiles were placed. `CAB` across row 7, columns
  6–8 scores **9** with no premium. `CABINET` across row 7, columns 4–10 scores **91** (`14 × 2 × 2 + 35`),
  not Classic's 130.
- **Exchange:** Duel allows up to the number of tiles left in the bag, capped by the current rack size. It uses a
  turn and cannot draw back any of the tiles just traded. This is Jade Court's short-bag convention; the publisher
  rulebook confirms swapping costs the turn but does not specify a numeric minimum.
- **Ending:** Play ends if a seat empties its rack with an empty bag, or after **three consecutive scoreless turns**
  once either score is nonzero. Passes, exchanges and accepted zero-point plays count. A rejected submission
  does not consume a turn; a scoring play resets the streak. On rack-out, subtract the opponent's unused tile
  value and award the same amount to the seat that went out. A scoreless ending keeps the posted totals. The
  greater final score wins; equal totals return `winners: []`.

| Face value | Supply (letter × count) |
|---|---|
| 0 | Blank×2 |
| 1 | A×9, E×13, I×8, O×8, R×6, S×5, T×7 |
| 2 | D×5, L×4, N×5, U×4 |
| 3 | G×3, H×4, Y×2 |
| 4 | B×2, C×2, F×2, M×2, P×2, W×2 |
| 5 | K×1, V×2 |
| 8 | X×1 |
| 10 | J×1, Q×1, Z×1 |

Rule references: Zynga's [Classic Words With Friends rulebook](https://zyngasupport.helpshift.com/hc/en/26-words-with-friends-classic/faq/20964-classic-words-with-friends-rule-book/)
for placement, scoring and ending; its [start guide](https://zyngasupport.helpshift.com/hc/en/26-words-with-friends-classic/faq/20971-how-do-i-start-a-game/)
for creator-first play; [Hasbro's component list](https://hasbro-new.custhelp.com/app/answers/detail/a_id/177) for the
104-tile English counts; and the independent [MonkeyTactics](https://blog.monkeytactics.com/posts/wwf-tile-distribution-explained/)
and [Wordton](https://wordton.com/words-with-friends/) tables for their face values. Jade Court's
numeric premium layout, neutral center and exact short-bag and tie policies are specified here, not claims about publisher parity.

Word Tiles is not a branded edition. It validates every word against the bundled SCOWL list on submission and does not
share commercial word lists, challenge procedures, power-ups, or matchmaking. Leaving a table closes it without scored
settlement. The rendered board and tile art are original.

### Classic (already-dealt games, checkpoint 2)

- Seat `0` opened. These games use Jade Court's original 15×15 symmetric premium layout and the original
  letter distribution below, not Duel's values or neutral center.
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

### Quick (already-dealt games, checkpoint 1)

- Seat `0` opened. Six tiles per rack; center `(5,5)`; 62-tile supply. No premium cells, blanks, or rack bonus. Sum the face values
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

In the two legacy profiles, final scores may be negative; the higher adjusted total wins and any tie returns `winners: []`.
No more moves are accepted after a game ends.

## Typed library, checkpoints and wire

`src/JadeCourt.Words/JadeCourt.Words.csproj` has no runtime package dependencies. Construct `JadeCourt.Words.WordGame` and use:

- `string Start()`, `string Apply(string checkpoint, int seat, WordAction action)`: these produce opaque, versioned
  **server-only** snapshots. `Start()` deals Duel (`3`); applying turns to a Quick (`1`) or Classic (`2`) snapshot keeps its
  original format and rules. A snapshot contains both racks, draw order, board, turn, scores and the ending streak. It is not a wire
  response, signed input from a client, or a revision/token. The host must authenticate the seat and apply a move to the
  latest committed checkpoint.
- `WordView View(string checkpoint, int? viewerSeat)`: pass an authenticated seat, or `null` for a spectator. A seat sees only its own rack, even when it is not that seat's turn. A spectator sees `rack: []`. The same public fields are available to all viewers, even after the game ends. `board` is sparse (occupied cells only), sorted row then column. `turn` is `0`/`1` in `playing`, `null` in `finished`; `detail` is a terminal reason or `null`. `lastMove` is public display text, not structured game state.
- `WordAction SelectBotMove(WordView actingSeatView)`: pass the current acting seat's projection. The bot has no access to
  the opponent's rack or bag order. It uses the same spelling and scoring rules as a human, including blanks. It searches up
  to 25,000 trie visits and evaluates up to 1,000 candidate placements, chooses the highest-scoring candidate found, and
  passes if none are found or the view cannot be used. The bounds do not guarantee optimal play or finding every legal move.

Serialize the records with .NET JSON web defaults (camel case). `WordView` includes `rows`, `columns`, `turn`, `status`,
`winners`, `detail`, `board`, `rack`, `scores`, `rackSizes`, `bagCount`, and `lastMove`, plus:

- `variant`: `duel`, `classic` or `quick`.
- `rackSize`: the profile's full rack size. Actual rack sizes and scores are in seat order.
- `premiums`: `{row,column,kind}` cells in row-then-column order; each kind is `double-letter`, `triple-letter`,
  `double-word`, or `triple-word`. Quick uses `[]`. An occupied cell's premium has already been used.
- `exchangeLimit`: the number of tiles the viewer could exchange this turn, or `0` if unavailable.

Coordinates are zero-based. Board cells are `{row,column,tile,seat,recent?}`. In Duel, `recent: true` marks only the
tiles committed by the latest action if it was a placement; omitted otherwise, including after a pass or exchange.
Tiles are `{id,letter,points,blank}`. `blank` is
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
