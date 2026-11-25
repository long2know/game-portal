# Jade Court board game rules

`JadeCourt.Games` is a dependency-free .NET 10 rules library. `BoardGames.All` registers the games below. Chess and Changsha Mahjong are separate engines.

| Game and route under the portal mount | Variant IDs (default first) | Seats |
| --- | --- | --- |
| Reversi, `reversi/` | `standard` | 2 |
| English Checkers, `checkers/` | `english` | 2 |
| Chinese Checkers, `chinese-checkers/` | `standard` | 2, 3, 4, 6 |
| Go (Weiqi), `go/` | `9x9`, `19x19` | 2 |
| Xiangqi (Chinese Chess), `xiangqi/` | `standard` | 2 |
| Gomoku, `gomoku/` | `freestyle` | 2 |

## Rules and house conventions

- **Reversi.** The [World Othello Federation rules](https://www.worldothello.org/about/about-othello/othello-rules/official-rules/english) supply the opening, all eight outflank directions and mandatory pass when only the opponent can move. When neither can place, the game immediately ends; `scores` counts each seat's discs, including draws on equal counts.
- **English Checkers.** The [WCDF English rules](https://wcdf.net/rules/rules_of_checkers_english.pdf), §1.15–1.20 and §1.30–1.32, supply the 8×8 board, forward-only men, short-range kings, forced jumps and full jump chains. Any complete capture branch is legal; the longest is not compulsory. A man reaching the far row is crowned and ends its turn. No legal move is a loss even if pieces remain. Jade Court adjudicates the third occurrence of the same position (pieces and side to move), or 40 moves per side with neither a capture nor an uncrowned man advancing, automatically rather than requiring a claim to a referee.
- **Chinese Checkers.** The [Tactic rule sheet](https://tactic.net/site/rules/multi/chinesecheckers.pdf) supplies the six-point star, ten pegs per seat, symmetric starts and hops over either colour without captures. Three players start in alternating camps and four use two pairs of opposite camps. There is no balanced five-player setup here. An adjacent step or any finite chain of hops is a turn. Jade Court uses a [published house convention](https://www.mastersofgames.com/rules/chinese-checkers-rules.htm): pegs can hop through any foreign camp (including unused camps) but cannot stop there, and a peg in its goal cannot leave it. There are no swaps. First to fill the opposite camp wins immediately. A seat with no legal move must pass; if everyone is blocked on consecutive turns, the result is a draw.
- **Go (Weiqi).** The [Chinese Weiqi Association's 2002 rules (English translation)](https://home.snafu.de/jasiek/c2002.pdf) supply the capture order, prohibition of self-capture, and area counting. Jade Court enforces exact **positional** superko for all placements, including long cycles. Passes do not alter the board or add to its repetition history. Two consecutive passes end play. Score is live stones plus empty points bordered by only their colour; shared points are neutral. White gets **7.5 komi** on both boards, announced in `detail` and included in seat 1's final `scores` value. There is no dead-stone negotiation or removal step: resolve disputes by playing them out **before** both players pass. The saved position history has a service ceiling of 4,096 placements; at that ceiling, only passes are offered to finish scoring.
- **Xiangqi (Chinese Chess).** The [World Xiangqi Federation rules](https://www.wxf-xiangqi.org/images/wxf-rules/2018_World_XiangQi_Rules_English2018.pdf) supply the traditional start and piece movement. Red is seat 0 and opens at the bottom; Black is seat 1 at the top. On the 10-row, 9-column board, the palace confines the general and advisor; the river keeps elephants on their own half and adds sideways moves for crossed soldiers. Soldiers do not retreat or promote. A horse's first orthogonal point and an elephant's diagonal midpoint must be clear. Chariots and non-capturing cannons slide along a clear rank or file; a cannon capture requires exactly one intervening piece of either colour. No move can leave its general in check or the two generals facing across an empty file. Both checkmate and stalemate lose without removing the general.

  Jade Court adjudicates the third occurrence of the exact board and side to move. If exactly one seat gave check on every one of its moves between the first and third occurrences, that seat loses; otherwise it is a draw. Sixty moves per side without a capture or forward soldier move also draw; a sideways soldier move without a capture counts as quiet. No-legal-move losses take precedence. These automatic house rules do not adjudicate tournament perpetual chase. The checkpoint retains an anchor and a replay-validated trail of at most 121 plies. The deterministic bot takes immediate wins and weighs the opponent's strongest reply.

  Cell IDs use lowercase files `a`–`i` and ranks `1`–`10`: `a10` is top left (`row: 0, column: 0`) and `i1` is bottom right. Moves carry both `from` and `to`. Piece codes are `general`, `advisor`, `elephant`, `horse`, `chariot`, `cannon`, and `soldier`.
- **Gomoku.** Under [freestyle rules](https://gomokupro.com/gomoku-rules), Black (seat 0) opens and White (seat 1) replies on an empty 15×15 board. Each turn places one stone on any vacant intersection. The first unbroken horizontal, vertical, or diagonal line of at least five wins, including overlines for either colour. If the board fills without a winner, it is a draw. No passes, captures, swaps, opening restrictions, or [Renju fouls](https://gomoku.renju.net/rifrules/) apply. The checkpoint replays at most 225 placements; the deterministic bot takes a win, blocks an immediate threat, or looks for strong lines and two-ended threats.

  Gomoku uses Go's `r{row}c{column}` IDs and `stone` piece code. Move labels use files `A`–`P` skipping `I`, with ranks counted from the bottom (`J15` is `r0c8`). Placements have `from: null`; `to` is the destination cell ID.

These are casual, unranked rules. Bots use legal moves only and do not receive hidden state: Reversi prioritizes corners and restricting replies, Checkers considers the opponent's best immediate reply, Chinese Checkers races toward distinct goal holes, and Go weighs opening shape, captures, threatened groups, small pockets and passes. The bots are deterministic, not competitive engine adapters.

## Integration and display

- Persist the opaque checkpoint returned by `Start` or `Apply`. A fresh engine can read it without server or session dependencies. Only trusted server-generated checkpoints belong in storage. Illegal commands raise `ArgumentException`, and malformed or mismatched checkpoints raise `InvalidDataException`.
- `View` lists authoritative, session-neutral move IDs. Submit full IDs without parsing them. Turn advances one seat per `Apply` (including passes and complete chains). Finished games have no moves or turn; do not schedule a bot. Empty `winners` means a draw when finished.
- Cell coordinates are zero-based from the top left; an empty playable cell still has an ID, but its `owner` and `piece` are null. Checkers provides only its 32 dark squares. Chinese Checkers lists only holes on a 17×25 half-column grid: horizontal neighbours differ by two columns and diagonal neighbours by one row and one column.
- A placement has only `to`; an off-board pass has neither endpoint. A hop/jump `via` contains intermediate landing IDs in order, excluding the destination. Chinese Checkers provides one canonical shortest chain per reachable endpoint. English Checkers can offer different chains for the same endpoints, including a king returning to its origin; use full move IDs and labels to offer all choices.

## Daily Xiangqi

Red has one move to checkmate Black. Any actual checkmate succeeds. A legal
non-mating move (including a stalemate, which normally loses for Black) leaves
the original position ready for another try. Replay uses the same position
without taking away completion earned for that UTC date.

The [published corpus](../web/src/portal/games/daily-xiangqi.json) consists of
nine original compositions and their nine left–right reflections, spanning
chariot, horse, soldier, cannon-screen, capture, and discovered-check patterns.
The cycle starts on 2026-11-25 UTC and repeats every 18 days in file order.
Positions, all legal move IDs, and all checkmates are public static data loaded
only when opening the tactic. The browser uses the normal Xiangqi board but
does not implement its own rule engine. The [corpus tests](../tests/JadeCourt.Games.Tests/DailyXiangqiTests.cs)
rebuild each anchor, use `XiangqiGame.View` to verify every legal move, and
use `Apply` plus `View` to enumerate all terminal checkmates. When editing an
edition, verify its reflection too; preserve the published order within the
edition version.
