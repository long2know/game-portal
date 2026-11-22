using JadeCourt.Games.Farkle;
using static JadeCourt.Games.Farkle.FarkleActionKind;

namespace JadeCourt.Games.Tests;

public sealed class FarkleTests
{
    [Theory]
    [InlineData("152346", 0b000001, 100)]
    [InlineData("152346", 0b000010, 50)]
    [InlineData("152346", 0b000011, 150)]
    [InlineData("222153", 0b011111, 350)]
    [InlineData("111234", 0b000111, 300)]
    [InlineData("666415", 0b000111, 600)]
    [InlineData("222235", 0b001111, 1000)]
    [InlineData("333335", 0b111111, 2050)]
    [InlineData("444444", 0b111111, 3000)]
    [InlineData("624315", 0b111111, 1500)]
    [InlineData("423243", 0b111111, 1500)]
    [InlineData("464644", 0b111111, 1500)]
    [InlineData("125152", 0b111111, 1500)]
    [InlineData("464646", 0b111111, 2500)]
    public void EverySelectedDieContributesToTheDeclaredScore(string faces, int mask, int points)
    {
        var game = Rolled(FarkleGame.Start(2), faces.Select(face => face - '0').ToArray());
        Assert.Equal(points, Assert.Single(game.Choices(), choice => choice.Mask == mask).Points);
        Assert.Equal(points, Decide(game, Keep, mask).TurnPoints);
    }

    [Fact]
    public void RejectsWrongSeatWrongPhaseAndAnyUnscoredDieInAKeep()
    {
        var game = FarkleGame.Start(2);
        Assert.Throws<InvalidOperationException>(() => game.Apply(1, new(Roll), NoRoll));
        Assert.Throws<InvalidOperationException>(() => Decide(game, Bank));
        Assert.Throws<InvalidOperationException>(() => Decide(game, Keep, 1));

        var rolled = Rolled(game, 2, 2, 2, 6, 3, 5);
        Assert.Throws<InvalidOperationException>(() => Decide(rolled, Roll));
        Assert.Throws<InvalidOperationException>(() => Decide(rolled, Bank));
        Assert.DoesNotContain(rolled.Choices(), choice => choice.Mask == 0b001111);
        Assert.Throws<InvalidOperationException>(() => Decide(rolled, Keep, 0b001111));
        Assert.Throws<InvalidOperationException>(() => Decide(rolled, Keep, 0b000011));
        Assert.Throws<InvalidOperationException>(() => Decide(rolled, Keep, 0));
        Assert.Throws<InvalidOperationException>(() => Decide(rolled, Keep, 64));
        var kept = Decide(rolled, Keep, 0b000111);
        Assert.Throws<InvalidOperationException>(() => Decide(kept, Keep, 0b000111));
        Assert.Equal(200, kept.TurnPoints);
        Assert.Equal(3, kept.DiceToRoll);
    }

    [Fact]
    public void ScoringDoesNotCombineOldDiceWithANewRollAndFarkleLosesOnlyTheTurn()
    {
        var game = Decide(Rolled(FarkleGame.Start(2), 5, 2, 3, 4, 6, 2), Keep, 0b1);
        var nextRoll = Rolled(game, 5, 5, 2, 3, 4);
        Assert.Throws<InvalidOperationException>(() => Decide(nextRoll, Keep, 0b100001));
        var kept = Decide(nextRoll, Keep, 0b11);
        Assert.Equal(150, kept.TurnPoints); // Three fives would be 500 only in a single roll.
        Assert.False(kept.CanBank);
        var farkle = Rolled(kept, 2, 3, 4);
        Assert.Equal(1, farkle.Turn);
        Assert.Equal(0, farkle.TurnPoints);
        Assert.Equal(new[] { 0, 0 }, farkle.Scores);
        var lost = Assert.Single(farkle.RecentTurns());
        Assert.Equal((0, false, 150), (lost.Seat, lost.Banked, lost.Points));
        Assert.Equal(new[] { 2, 3, 4 }, lost.Dice);
    }

    [Fact]
    public void ExactEntryThresholdAndLaterSmallBanksAreLegalAndSurviveFarkle()
    {
        var game = Decide(Rolled(FarkleGame.Start(2), 5, 2, 3, 4, 6, 2), Keep, 1);
        Assert.False(game.CanBank);
        Assert.Throws<InvalidOperationException>(() => Decide(game, Bank));
        game = Rolled(game, 2, 2, 3, 3, 6);
        game = Rolled(game, 2, 2, 3, 3, 4, 6);
        game = Decide(Rolled(game, 5, 5, 5, 2, 3, 6), Keep, 0b111);
        Assert.True(game.CanBank);
        game = Decide(game, Bank);
        Assert.Equal(new[] { 500, 0 }, game.Scores);
        game = Rolled(game, 2, 2, 3, 3, 4, 6);
        game = Decide(Rolled(game, 5, 2, 3, 4, 6, 6), Keep, 1);
        Assert.True(game.CanBank);
        game = Decide(game, Bank);
        game = Rolled(game, 2, 2, 3, 3, 4, 6);
        game = Rolled(game, 2, 2, 3, 3, 4, 6);
        Assert.Equal(new[] { 550, 0 }, game.Scores);
    }

    [Fact]
    public void UsingEveryAvailableDieMakesHotDiceButDoesNotBankThem()
    {
        var game = Decide(Rolled(FarkleGame.Start(3), 2, 2, 2, 2, 1, 5), Keep, 0b11111);
        Assert.Equal(1, game.DiceToRoll);
        game = Decide(Rolled(game, 5), Keep, 1);
        Assert.Equal(1150, game.TurnPoints);
        Assert.Equal(6, game.DiceToRoll);
        Assert.True(game.CanBank);
        game = Rolled(game, 2, 2, 3, 3, 4, 6);
        Assert.Equal(new[] { 0, 0, 0 }, game.Scores);
        Assert.Equal(1150, Assert.Single(game.RecentTurns()).Points);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossingGoalGivesEachOpponentOneMoreTurnAndRecognizesAllWinners(bool outright)
    {
        var game = FarkleGame.Start(3);
        for (var i = 0; i < 9; i++)
            game = BankRoll(game, 0b111111, 2, 2, 2, 2, 2, 2);
        game = BankRoll(game, 0b111, 5, 5, 5, 2, 3, 6);
        game = BankRoll(game, 0b1111, 2, 2, 2, 2, 3, 6);
        Assert.Equal(new[] { 9500, 10000, 9000 }, game.Scores);
        Assert.Equal(1, game.FinalStarter);
        Assert.Equal(2, game.Turn);
        Assert.Empty(game.Winners());

        game = outright ? BankRoll(game, 0b111111, 6, 2, 5, 3, 1, 4) :
            BankRoll(game, 0b1111, 6, 6, 6, 6, 2, 3);
        Assert.Equal(0, game.Turn);
        Assert.Empty(game.Winners());
        game = BankRoll(game, 0b111, 5, 5, 5, 2, 3, 6);
        Assert.Null(game.Turn);
        Assert.Equal(outright ? new[] { 2 } : new[] { 0, 1, 2 }, game.Winners());
        Assert.Equal(new[] { 0, 2, 1 }, game.RecentTurns().Select(result => result.Seat));
        Assert.Throws<InvalidOperationException>(() => game.Apply(1, new(Roll), NoRoll));
        Assert.Equal(game.Winners(), FarkleGame.Restore(game.Save()).Winners());
    }

    [Fact]
    public void RollLimitRemainsEnforcedAfterRestoringTheHotDiceTurn()
    {
        var game = FarkleGame.Start(2);
        for (var i = 0; i < FarkleGame.MaxRolls; i++)
            game = Decide(Rolled(game, 1, 2, 3, 4, 5, 6), Keep, 0b111111);
        game = FarkleGame.Restore(game.Save());
        Assert.Equal(36_000, game.TurnPoints);
        Assert.False(game.CanRoll);
        Assert.Throws<InvalidOperationException>(() => Decide(game, Roll));
        Assert.Equal(new[] { 36_000, 0 }, Decide(game, Bank).Scores);
    }

    [Fact]
    public void RestorePreservesPreciseKeepChoicesAndRejectsCorruptCheckpoints()
    {
        var game = Rolled(FarkleGame.Start(4), 1, 5, 2, 2, 2, 6);
        var saved = game.Save();
        var restored = FarkleGame.Restore(saved);
        Assert.Equal(game.Choices(), restored.Choices());
        Assert.Equal(Decide(game, Keep, 0b011111).Save(), Decide(restored, Keep, 0b011111).Save());
        Assert.Throws<InvalidDataException>(() => FarkleGame.Restore(saved.Replace("\"Version\":1", "\"Version\":2")));
        Assert.Throws<InvalidDataException>(() => FarkleGame.Restore(saved.Replace("\"Dice\":[1,", "\"Dice\":[7,")));
        Assert.Throws<InvalidDataException>(() => FarkleGame.Restore(saved.Replace("\"RollCount\":1", "\"RollCount\":25")));
        Assert.Throws<InvalidDataException>(() => FarkleGame.Restore(new string(' ', 4097)));
    }

    [Theory]
    [InlineData(2, 571)]
    [InlineData(6, 611)]
    public void BotsCompleteRepeatableMatchesAcrossCheckpointRestores(int players, int seed)
    {
        var unbroken = BotMatch(players, seed, restore: false);
        var resumed = BotMatch(players, seed, restore: true);
        Assert.True(resumed.Scores.Max() >= FarkleGame.Goal);
        Assert.NotEmpty(resumed.Winners());
        Assert.Equal(unbroken.Save(), resumed.Save());
    }

    private static FarkleGame BotMatch(int players, int seed, bool restore)
    {
        var game = FarkleGame.Start(players);
        var random = new Random(seed);
        for (var action = 0; action < 2000 && game.Turn is { } seat; action++)
        {
            game = game.Apply(seat, game.BotAction(), () => random.Next(1, 7));
            if (restore) game = FarkleGame.Restore(game.Save());
        }
        Assert.Equal(FarklePhase.Finished, game.Phase);
        return game;
    }

    private static FarkleGame BankRoll(FarkleGame game, int mask, params int[] dice) =>
        Decide(Decide(Rolled(game, dice), Keep, mask), Bank);

    private static FarkleGame Rolled(FarkleGame game, params int[] faces)
    {
        var dice = new Queue<int>(faces);
        var next = game.Apply(game.Turn!.Value, new(Roll), dice.Dequeue);
        Assert.Empty(dice);
        return next;
    }

    private static FarkleGame Decide(FarkleGame game, FarkleActionKind kind, int mask = 0) =>
        game.Apply(game.Turn!.Value, new(kind, mask), NoRoll);

    private static int NoRoll() => throw new InvalidDataException("Unexpected die roll.");
}
