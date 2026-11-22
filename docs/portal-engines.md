# Jade Court engine boundaries

`JadeCourt.Chess` implements `JadeCourt.Games.IBoardGame`, returning only the shared board types. White is seat 0 and Black is seat 1. The `standard` variant starts from the orthodox initial position. A checkpoint is versioned (`chess-v1`) and stores the canonical UCI move history, not a library object's serialized state or a board diagram. Replaying from the start preserves castling, en passant and repetition across restarts.

The locked managed rules dependency is [Gera.Chess 1.2.0](https://www.nuget.org/packages/Gera.Chess/1.2.0), from [Geras1mleo/Chess `bc2c2b39`](https://github.com/Geras1mleo/Chess/tree/bc2c2b39debb2c647dbd39887ac5dd8ed4d9abf0), with [MIT notice](../scripts/licenses/Gera.Chess-MIT.txt). Its only runtime dependency, [Ardalis.SmartEnum 2.0.1](https://www.nuget.org/packages/Ardalis.SmartEnum/2.0.1), is pinned to its [published source commit](https://github.com/ardalis/SmartEnum/tree/aeb2e99c731b581667aa1b4957e16d55ac5467ba) and also has an [MIT notice](../scripts/licenses/Ardalis.SmartEnum-MIT.txt).

Jade Court tracks original rook rights and calculates full position repetitions rather than using the library's automatic draw rules. Repetition compares the position, side to move, castling rights, and only actually legal en passant rights. Checkmate takes precedence over draw adjudication. The portal recognizes stalemate and insufficient material (including same-square-color bishops), but does not attempt to prove arbitrary dead positions with mating material. Without a separate draw-claim UI, it automatically takes a threefold or fifty-move draw when first claimable instead of requiring a player claim under [FIDE articles 9.2 and 9.3](https://handbook.fide.com/chapter/E012023).

`Mahjong.Rules` remains a separate turn/claim engine behind its existing transport, not an implementation of the two-player board contract.

## Stockfish as an optional process

The server passes its operator-only `Portal:StockfishPath` to the chess constructor (Compose environment form: `Portal__StockfishPath=/usr/games/stockfish`). The adapter executes the absolute path directly, with no shell or user-supplied commands. It sends the replay-validated UCI history, caps the engine at one thread and 16 MiB of hash memory, bounds the request to two seconds before process cleanup, and caps stdout and stderr. Only one engine request per host process runs at a time. The returned move must be in the current authoritative managed legal list.

`ChessGame.SelectBotMove(checkpoint, difficulty)` accepts exactly these presets; the existing `IBoardGame` call uses `standard`. Difficulty is not a chess variant or part of the move history.

| Preset | Stockfish UCI skill | Search budget | Deterministic managed fallback |
| --- | --- | --- | --- |
| `easy` | 2 | 45 ms | Fourth heuristic-ranked legal move, or the last if fewer are available. |
| `standard` | Engine default (20 on Stockfish 16) | 100 ms | Existing capture, promotion, centralization, check and castle heuristic. |
| `hard` | 20 | 400 ms | Considers the 12 highest-ranked legal moves and penalizes the opponent's strongest immediate capture or promotion. |

An unconfigured path uses the matching fallback silently; a configured engine that fails, returns an illegal move, or is busy warns and uses the same fallback. These are bounded strength presets, not Elo ratings, and can pick the same move.

### Linux/amd64 release pin

Use Ubuntu 24.04 Noble's [`stockfish=16-1build1`](https://packages.ubuntu.com/noble/amd64/stockfish) from Universe, **Stockfish 16**, installed at `/usr/games/stockfish`. The [Ubuntu amd64 `.deb`](https://archive.ubuntu.com/ubuntu/pool/universe/s/stockfish/stockfish_16-1build1_amd64.deb) has SHA-256 `909bfd433b049497f06ec3f63517deaf4e821acc138c974bb2a8af2bd58b073f`.

The engine is GPL-3.0-or-later; the embedded NNUE is CC0-1.0. Preserve `/usr/share/doc/stockfish/copyright` and the full `GPL-3` and `CC0-1.0` texts from `/usr/share/common-licenses` alongside the distributed binary. Make the exact [Ubuntu corresponding source](https://packages.ubuntu.com/source/noble/stockfish) available beside any published image, including upstream source (with NNUE) and the Debian/Ubuntu packaging and patches. A link to the upstream [Stockfish 16 tag](https://github.com/official-stockfish/Stockfish/tree/sf_16) alone is not the corresponding source for the Ubuntu build.

| Corresponding source artifact | SHA-256 |
| --- | --- |
| [`stockfish_16-1build1.dsc`](https://archive.ubuntu.com/ubuntu/pool/universe/s/stockfish/stockfish_16-1build1.dsc) | `d5f19c6cc601e3205d711489ed00f9a30fce830162efd08bc705254830334eea` |
| [`stockfish_16.orig.tar.gz`](https://archive.ubuntu.com/ubuntu/pool/universe/s/stockfish/stockfish_16.orig.tar.gz) | `a928bcf1ba7894a14ff15f78f2b4081396c5ed3cf5f2aab29b0827423fd883e8` |
| [`stockfish_16-1build1.debian.tar.xz`](https://archive.ubuntu.com/ubuntu/pool/universe/s/stockfish/stockfish_16-1build1.debian.tar.xz) | `9b7264396f097bdd77e9b3f72e12fddc92939f29ef725da8f1751aadf68e9d10` |

### Qualification

On Linux/amd64 with the pinned package installed, run:

```sh
test "$(dpkg-query -W -f='${Version}' stockfish)" = 16-1build1
printf 'uci\nquit\n' | /usr/games/stockfish | grep -Fx 'id name Stockfish 16'
JADECOURT_STOCKFISH_TEST_PATH=/usr/games/stockfish \
  dotnet test tests/JadeCourt.Chess.Tests/JadeCourt.Chess.Tests.csproj \
  --filter FullyQualifiedName~PinnedStockfish
```

The test asserts that all three presets return legal moves within a bound and that no fallback warning was emitted. Qualify the **final image** separately: verify the installed version and `Stockfish 16` UCI identity, then play a bot turn through the deployed portal with no fallback warning. A host-level or extracted-binary test alone does not qualify the image.
