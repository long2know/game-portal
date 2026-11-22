# Word Tiles rules and engine contract

Word Tiles is an original two-seat game on an unmarked 11×11 board. Seat `0` opens; coordinates are zero-based, with the center at `(5,5)`. Each seat starts with six tiles from the same 62-tile bag. The dealer and exchange shuffles use the operating system's cryptographic random generator. There is no guarantee that a given rack has a legal play.

## Turns and scoring

- **Place:** Place one or more distinct tiles from your rack in one row or column, in empty cells. Existing tiles may bridge gaps between new tiles; empty cells may not. The first play must cross the center. Later plays must touch an existing tile orthogonally. Each complete new or extended word of two or more letters must be in the lexicon, including every perpendicular word. A single tile may form a word in either direction or in both. Unchanged words and isolated letters do not score.
- **Score:** Sum the face values in each complete word, including its existing letters, and add two points for each word after the first made in that turn. There are no premium cells, blanks or rack bonuses. For example, with `CAT` across, play `PI` directly above its `AT` to form `PI` (3), `PA` (3), and `IT` (2), plus two links (4): **12 points**. Refill your rack to six while tiles remain.
- **Exchange:** Trade 1–3 distinct tiles from your rack, provided the bag contains at least that many. Draw replacements first, then return the traded tiles to the bag and shuffle. The exchange scores nothing; only its count is public.
- **Pass:** Play no tiles and score nothing.

Play ends when the bag is empty **and** a seat empties its rack, or after four consecutive turns without a placement (passes and exchanges both count). Each seat subtracts the face value of its own remaining tiles, with no transfer. Final scores may be negative; the greater score wins. On a tie `winners` is `[]`. No more moves are accepted after the game ends.

| Face value | Supply (letter × count) |
|---|---|
| 1 | A×5, E×6, I×4, L×3, N×3, O×4, R×4, S×4, T×4, U×2 |
| 2 | B×2, C×2, D×2, G×1, H×2, M×2, P×2, Y×2 |
| 3 | F×1, K×1, V×1, W×1 |
| 4 | J×1, X×1 |
| 5 | Q×1, Z×1 |

## Typed library, checkpoints and wire

`src/JadeCourt.Words/JadeCourt.Words.csproj` has no runtime package dependencies. Construct `JadeCourt.Words.WordGame` and use:

- `string Start()`, `string Apply(string checkpoint, int seat, WordAction action)`: these produce opaque, versioned **server-only** snapshots. A snapshot contains both racks, draw order, board, turn, scores and the current ending streak. It is not a wire response, signed input from a client, or a revision/token. The host owns authentication, persistence and stale-revision checks; require the latest committed checkpoint before applying a move.
- `WordView View(string checkpoint, int? viewerSeat)`: pass an authenticated seat, or `null` for a spectator. A seat sees only its own rack, even when it is not that seat's turn. A spectator sees `rack: []`. The same public fields are available to all viewers, even after the game ends. `board` is sparse (occupied cells only), sorted row then column. `turn` is `0`/`1` in `playing`, `null` in `finished`; `detail` is a terminal reason or `null`. `lastMove` is public display text, not structured game state.
- `WordAction SelectBotMove(WordView actingSeatView)`: pass the current acting seat's projection. The bot uses only board tiles and its own rack, with the same spelling and scoring rules as a human. It searches up to 25,000 trie visits and evaluates up to 1,000 candidate placements, chooses the highest-scoring candidate found, and passes if none are found. That bound is not a guarantee of optimal play or of finding every legal move.

Serialize the records with .NET JSON web defaults (camel case). The `WordView` fields are `rows`, `columns`, `turn`, `status`, `winners`, `detail`, `board`, `rack`, `scores`, `rackSizes`, `bagCount`, `lastMove`. Board cells are `{row,column,tile,seat}`; rack and board tiles are `{id,letter,points}` (`letter` is uppercase). Arrays for per-seat scores and sizes are in seat order. No bag order, other rack, or checkpoint is returned.

Actions use exact lowercase kinds and only the relevant optional field:

```json
{"kind":"place","placements":[{"row":5,"column":5,"tileId":4},{"row":5,"column":6,"tileId":51}]}
{"kind":"exchange","exchange":[4,51]}
{"kind":"pass"}
```

Coordinates and tile IDs above are examples; take tile IDs from the current `rack`. Illegality, wrong turn or a finished game throws `InvalidOperationException`. Out-of-range seats throw `ArgumentOutOfRangeException`; malformed or unsupported checkpoint data throws `InvalidDataException`. The private checkpoint format is version `1`.

## Lexicon source and redistribution

The embedded lexicon is a modified subset of [SCOWL (Spell Checker Oriented Word Lists) 2020.12.07](https://wordlist.aspell.net/),
compiled by Kevin Atkinson. The collective work is Copyright 2000–2018 by Kevin Atkinson; the
[verbatim copyright and redistribution grant](../src/JadeCourt.Words/Lexicon/SCOWL-Copyright.txt) include
the upstream component notices and disclaimers. The grant permits using, copying, modifying and distributing
these lists and their outputs under its notice requirements. This is a general English spell-check lexicon,
not a commercial game word list or ENABLE (which SCOWL draws on in higher tiers than those used here).

Use the [2020.12.07 source archive](https://downloads.sourceforge.net/project/wordlist/SCOWL/2020.12.07/scowl-2020.12.07.tar.gz).
Take only `final/english-words.{10,20,35,40}` and `final/american-words.{10,20,35,40}`. Keep lines matching
`[a-z]{2,11}` byte-for-byte before converting to uppercase, deduplicate, then sort bytewise and write
one word per LF line with a final LF. This excludes separate proper-name, abbreviation, jargon and higher-tier
files and rejects accented and punctuated forms; membership in the resulting 40,368-entry list is definitive.
No network fetch or permissive fallback occurs at runtime. Missing, empty or malformed data fails during library initialization.

| File | SHA-256 |
|---|---|
| `scowl-2020.12.07.tar.gz` | `5587667caa20c4891390c2d42dbb4d5c4c3f41bee77af1457ece3ba23fb859cc` |
| `src/JadeCourt.Words/Lexicon/words.txt` | `f4f2e4387356837b76ddc3d2d00cbfbd321f064ea7fcc85b20749f5eaf3999c1` |
| `src/JadeCourt.Words/Lexicon/SCOWL-Copyright.txt` (archive `Copyright`) | `283326a422e29c510e2ba6805518c418ce3c35ed1fadc3eec83d2da4f6c5a055` |

The lexicon and full copyright notice are embedded in `JadeCourt.Words.dll`; the notice is also
copied as `licenses/SCOWL-2020.12.07.txt` for published and packed builds. Retain the notice
and identify the lexicon as modified when redistributing.
