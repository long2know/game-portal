# Play-Chip No-Limit Texas Hold’em

`src/JadeCourt.Poker` is a standalone .NET 10 rules library. It is not connected to the portal,
lobby, or browser. Chips are integers used only within one match; they have no monetary value.
There are no deposits, rebuys, payouts outside a hand, or transfers between tables.

## Host interface

- `HoldemTable.Create(startingStacks, smallBlind, bigBlind, firstButtonSeat: 0)` deals hand 1.
  Stack indexes are fixed, zero-based seat IDs in clockwise order. There are 2–6 seats, at least
  two funded, with a funded initial button. Zero-stack seats sit out. The table total fits in
  `Int32`; blinds are fixed and satisfy `0 < smallBlind <= bigBlind`.
- `table.View(seat)` is a detached, immutable projection. Use the seat authorized by the host;
  never select it from an unverified client field. `table.View()` is for spectators. Both show
  positions, stacks, commitments, the current board and turn, and public settlement. The seat view
  also shows that seat’s own cards, even after it folds. Only the acting seat receives legal actions.
- `table.Act(seat, view.Revision, action)` requires the current revision and actor. Use the numeric
  `CallAmount` for display; a `Call` never takes an amount. For `Bet` and `Raise` the `To` value is
  **total committed on the current street**, not a raise increment or cumulative hand amount.
  It must fit the applicable inclusive range. `To` is zero for `Fold`, `Check`, and `Call`.
  Rejected commands throw `HoldemRejectedException` with a `Reason` and do not advance the table.
- `table.StartNextHand(view.Revision)` is an explicit host command after settlement, while at least
  two seats have chips. `Result` indicates hand completion; `MatchWinnerSeat` indicates the sole
  funded seat. Each accepted command advances the revision once, even if it runs out the board.
  Serialize all access to a table instance on the host; the revision is not a synchronization lock.
- `HoldemBot.Decide(actingSeatView)` proposes one legal action based on the seat’s cards, visible
  board, pot, and options. Its deterministic hand-strength and pot-odds heuristics do not simulate
  unseen cards or run a background loop. `HoldemHandEvaluator.Evaluate` ranks 5–7 distinct cards;
  its result compares category and then decisive ranks in tiebreaker order, without a suit tiebreaker.

## Rules and house conventions

- The server shuffles a fresh 52-card deck for each hand using the platform cryptographic RNG.
  It deals two rounds clockwise left of the button and burns before each community street.
  Deterministic complete decks are available only to the engine’s test assembly.
- With three or more funded seats the live button advances clockwise, skipping busted seats.
  Preflop starts after the big blind; later streets start left of the button. Heads-up, the
  button posts the small blind and acts first preflop and last afterward. The next heads-up big
  blind advances from the previous big blind, so a surviving big blind doesn’t post it twice in
  a row at the transition.
- Blinds can be short. The nominal preflop bring-in is the big blind while two or more live stacks
  can act. If only one live stack can act, it only faces actual opposing commitments on that street
  and cannot bet or raise into a dry pot. The big blind keeps its preflop option when action reaches
  it with other live stacks. Folding is offered only when facing a wager.
- A normal opening bet is at least one big blind. A full raise adds at least the last full bet or
  raise increment (initially one big blind), even over a short opening all-in. A smaller increase
  is legal only for the player’s entire remaining stack. Short increases do not change the full
  raise increment. A player who has acted, including by checking, cannot raise again until the
  cumulative increase since **their** last action reaches that increment. Short stacks can call
  with all their remaining chips.
- A sole contender wins without a runout or a reveal. Otherwise, when nobody can make a further
  betting decision, remaining streets are dealt automatically. At a contested showdown all
  non-folded hands are shown; folded hands, burns, and undealt cards are never shown to others.
  There is no voluntary show or muck option.
- Unmatched chips are returned at street closure. Only eligible, non-folded contributors can win a
  pot. Dead folded wagers are included without creating separate pots for their own commitment
  levels. Each pot is awarded separately to the best five-card hand(s), with odd chips awarded
  clockwise from left of the button among that pot’s tied winners. Seats use zero, one, or both
  hole cards and the ace can play low in `A–2–3–4–5`. After settlement commitments clear and
  `Result.Pots` and `UncalledReturns` show how the hand paid out.

This is a fixed-blind casual variant, not a full implementation of live tournament procedures.
There are no antes, straddles, blind schedule, rake, timeouts, table changes, or multiple runouts.
For background on heads-up position, minimum raises, and reopening, see the
[Poker Tournament Directors Association rules](https://www.pokertda.com/poker-tda-rules/).

## Private storage and verification

`JadeCourt.Poker.Persistence.HoldemServerPersistence` writes and restores a versioned **trusted
server checkpoint** through streams. It is not a public DTO, and must never be exposed to or
accepted from a client. It contains the private deck and current hand’s replay inputs: opening
stacks, positions, revisions, and accepted actions. Restore validates the version, a corruption
checksum, deck, and command sequence before returning a table. Invalid data raises
`InvalidDataException`. The checksum neither authenticates nor encrypts the data; hosts are
responsible for private, atomic storage and access control. Replay reproduces the saved hand and
its projections, not future random deals. Advancing a hand replaces its replay log.

Run the independently locked tests from the repository root:

```sh
dotnet test tests/JadeCourt.Poker.Tests/JadeCourt.Poker.Tests.csproj -c Release -p:RestoreLockedMode=true
```
