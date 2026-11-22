using System.Diagnostics;
using JadeCourt.Games;

namespace JadeCourt.Chess.Tests;

public sealed class StockfishTests
{
    private readonly ChessGame builtIn = new();

    [Fact]
    public void UnconfiguredBotIsLegalDeterministicAndSilent()
    {
        var warnings = new List<string>();
        var game = new ChessGame(warn: warnings.Add);
        var checkpoint = SicilianOpening(game);
        var move = game.SelectBotMove(checkpoint);
        Assert.Contains(game.View(checkpoint).Moves, m => m.Id == move);
        Assert.Equal(move, new ChessGame().SelectBotMove(checkpoint));
        Assert.Empty(warnings);
    }

    [Fact]
    public void PresetsUseDistinctDeterministicFallbacksInATacticalPosition()
    {
        var game = new ChessGame();
        var checkpoint = "e2e4 e7e5 d1h5 b8c6".Split(' ')
            .Aggregate(game.Start("standard", 2), game.Apply);
        var standard = ((IBoardGame)game).SelectBotMove(checkpoint);
        Assert.Equal("h5e5", standard);
        Assert.Equal(standard, game.SelectBotMove(checkpoint, "standard"));
        var easy = game.SelectBotMove(checkpoint, "easy");
        var hard = game.SelectBotMove(checkpoint, "hard");
        Assert.NotEqual(standard, easy);
        Assert.NotEqual(standard, hard);
        Assert.Contains(game.View(game.Apply(checkpoint, standard)).Moves, move => move.Id == "c6e5");
        var afterHard = game.View(game.Apply(checkpoint, hard));
        var queen = Assert.Single(afterHard.Cells, cell => cell.Owner == 0 && cell.Piece == "queen");
        Assert.DoesNotContain(afterHard.Moves, move => move.To == queen.Id);
        foreach (var (difficulty, move) in new[] { ("easy", easy), ("hard", hard) })
        {
            Assert.Equal(move, new ChessGame().SelectBotMove(checkpoint, difficulty));
            Assert.Equal(move, game.View(game.Apply(checkpoint, move)).LastMove?.Id);
        }

        var opening = game.Start("standard", 2);
        Assert.NotEqual(game.SelectBotMove(opening, "easy"), game.SelectBotMove(opening, "hard"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Easy")]
    [InlineData("hard\nquit")]
    [InlineData("expert")]
    public void InvalidPresetIsRejectedBeforeAttemptingStockfish(string? difficulty)
    {
        var warnings = new List<string>();
        var game = new ChessGame("not-an-absolute-path", warnings.Add);
        var checkpoint = game.Start("standard", 2);
        Assert.Equal("difficulty",
            Assert.Throws<ArgumentException>(() => game.SelectBotMove(checkpoint, difficulty!)).ParamName);
        Assert.Empty(warnings);
    }

    [Fact]
    public void MissingConfiguredExecutableWarnsAndFallsBackToTheBuiltInMove()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"missing-uci-{Guid.NewGuid():N}");
        var checkpoint = SicilianOpening(builtIn);
        AssertFallback(path, checkpoint, "could not be run");
    }

    [LinuxUciFact]
    public void AdapterSendsFixedCommandsAndAcceptsOnlyLegalUciIds()
    {
        var checkpoint = SicilianOpening(builtIn);
        using var good = new UciExecutable("""
            IFS= read -r line && [ "$line" = uci ] || exit 11
            printf 'id name Test Engine\r\nuciok\r\n'
            IFS= read -r line && [ "$line" = 'setoption name Threads value 1' ] || exit 12
            IFS= read -r line && [ "$line" = 'setoption name Hash value 16' ] || exit 13
            IFS= read -r line && [ "$line" = isready ] || exit 14
            printf 'readyok\n'
            IFS= read -r line && [ "$line" = 'position startpos moves e2e4 c7c5' ] || exit 15
            IFS= read -r line && [ "$line" = 'go movetime 100' ] || exit 16
            printf 'info depth 1\nbestmove a2a3 ponder b8c6\n'
            IFS= read -r line && [ "$line" = quit ]
            """);
        var warnings = new List<string>();
        Assert.NotEqual("a2a3", builtIn.SelectBotMove(checkpoint));
        Assert.Equal("a2a3", new ChessGame(good.Path, warnings.Add).SelectBotMove(checkpoint));
        Assert.Empty(warnings);

        using var illegal = new UciExecutable("""
            while IFS= read -r line; do
              case "$line" in
                uci) printf 'uciok\n' ;;
                isready) printf 'readyok\n' ;;
                'go movetime 100') printf 'bestmove a1a8\n' ;;
                quit) exit ;;
              esac
            done
            """);
        AssertFallback(illegal.Path, checkpoint, "not legal");
    }

    [LinuxUciFact]
    public void NewPresetsConfigureStockfishAndSendDistinctBoundedSearches()
    {
        foreach (var (difficulty, skill, budget) in new[] { ("easy", 2, 45), ("hard", 20, 400) })
        {
            using var engine = new UciExecutable($$"""
                IFS= read -r line && [ "$line" = uci ] || exit 11
                printf 'uciok\n'
                IFS= read -r line && [ "$line" = 'setoption name Threads value 1' ] || exit 12
                IFS= read -r line && [ "$line" = 'setoption name Hash value 16' ] || exit 13
                IFS= read -r line && [ "$line" = 'setoption name Skill Level value {{skill}}' ] || exit 14
                IFS= read -r line && [ "$line" = isready ] || exit 15
                printf 'readyok\n'
                IFS= read -r line && [ "$line" = 'position startpos moves e2e4 c7c5' ] || exit 16
                IFS= read -r line && [ "$line" = 'go movetime {{budget}}' ] || exit 17
                printf 'bestmove a2a3\n'
                IFS= read -r line && [ "$line" = quit ]
                """);
            var warnings = new List<string>();
            var game = new ChessGame(engine.Path, warnings.Add);
            Assert.Equal("a2a3", game.SelectBotMove(SicilianOpening(game), difficulty));
            Assert.Empty(warnings);
        }
    }

    [LinuxUciFact]
    public void BrokenPipeLeavesAPlayableFallback()
    {
        using var closed = new UciExecutable("exit 0");
        AssertFallback(closed.Path, builtIn.Start("standard", 2), "pipe closed", "hard");
    }

    [LinuxUciFact]
    public async Task BusyProcessSlotFallsBackWithoutLaunchingAnotherEngine()
    {
        using var hung = new UciExecutable("""
            printf '%s\n' "$$" >> "$0.pid"
            while IFS= read -r line; do :; done
            """);
        var checkpoint = builtIn.Start("standard", 2);
        var first = Task.Run(() => AssertFallback(hung.Path, checkpoint, "timed out"));
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(hung.Path + ".pid") && timer.Elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(10);
            Assert.True(File.Exists(hung.Path + ".pid"));
            AssertFallback(hung.Path, checkpoint, "busy", "easy");
        }
        finally
        {
            await first;
        }
        Assert.Single(File.ReadAllLines(hung.Path + ".pid"));
    }

    [LinuxUciFact]
    public void TimeoutKillsAndReapsTheUnresponsiveProcess()
    {
        using var hung = new UciExecutable("""
            printf '%s\n' "$$" > "$0.pid"
            while IFS= read -r line; do :; done
            """);
        var timer = Stopwatch.StartNew();
        AssertFallback(hung.Path, builtIn.Start("standard", 2), "timed out");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Took {timer.Elapsed}.");
        var pid = int.Parse(File.ReadAllText(hung.Path + ".pid"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(Directory.Exists($"/proc/{pid}"), $"Unresponsive child {pid} was not reaped.");
    }

    [LinuxUciFact]
    public void BothOutputPipesHaveLimitsEvenWithoutNewlines()
    {
        using var stdout = new UciExecutable("while :; do printf '0123456789abcdef'; done");
        using var stderr = new UciExecutable("while :; do printf '0123456789abcdef' >&2; done");
        var checkpoint = builtIn.Start("standard", 2);
        AssertFallback(stdout.Path, checkpoint, "output limit");
        AssertFallback(stderr.Path, checkpoint, "output limit");
    }

    [StockfishQualificationFact]
    public void PinnedStockfishReturnsAnAuthoritativeLegalMoveWithoutFallback()
    {
        var warnings = new List<string>();
        var path = Environment.GetEnvironmentVariable(StockfishQualificationFactAttribute.Variable);
        var game = new ChessGame(path, warnings.Add);
        var checkpoint = SicilianOpening(game);
        foreach (var difficulty in new[] { "easy", "standard", "hard" })
        {
            var timer = Stopwatch.StartNew();
            var move = game.SelectBotMove(checkpoint, difficulty);
            Assert.Empty(warnings);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"{difficulty} took {timer.Elapsed}.");
            Assert.Contains(game.View(checkpoint).Moves, m => m.Id == move);
            Assert.Equal(move, game.View(game.Apply(checkpoint, move)).LastMove?.Id);
        }
    }

    private void AssertFallback(string path, string checkpoint, string reason, string difficulty = "standard")
    {
        var warnings = new List<string>();
        var game = new ChessGame(path, warnings.Add);
        Assert.Equal(builtIn.SelectBotMove(checkpoint, difficulty), game.SelectBotMove(checkpoint, difficulty));
        Assert.Contains(reason, Assert.Single(warnings), StringComparison.Ordinal);
    }

    private static string SicilianOpening(ChessGame game) =>
        game.Apply(game.Apply(game.Start("standard", 2), "e2e4"), "c7c5");

    private sealed class UciExecutable : IDisposable
    {
        internal UciExecutable(string body)
        {
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, $"uci-{Guid.NewGuid():N}.sh");
            File.WriteAllText(Path, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal string Path { get; }

        public void Dispose()
        {
            File.Delete(Path);
            File.Delete(Path + ".pid");
        }
    }
}

public sealed class LinuxUciFactAttribute : FactAttribute
{
    public LinuxUciFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Uses a Linux UCI executable fixture.";
    }
}

public sealed class StockfishQualificationFactAttribute : FactAttribute
{
    public const string Variable = "JADECOURT_STOCKFISH_TEST_PATH";

    public StockfishQualificationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to an absolute path to pinned Stockfish for qualification.";
    }
}
