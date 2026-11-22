using static JadeCourt.Poker.HoldemActionKind;
using static JadeCourt.Poker.Tests.TableFixture;

namespace JadeCourt.Poker.Tests;

public sealed class BotTests
{
    [Fact]
    public void BotsChooseLegalActionsAndFinishHandsWithinTheDecisionBudget()
    {
        var random = new Random(351);
        HoldemCard[] NextDeck()
        {
            var deck = StandardDeck();
            random.Shuffle(deck);
            return deck;
        }

        var table = HoldemTable.CreateWithDeck([120, 105, 90, 75, 60, 45], 2, 4, 0, NextDeck());
        var played = new HashSet<HoldemActionKind>();
        const int hands = 8;
        for (var hand = 1; hand <= hands; hand++)
        {
            for (var step = 0; step < 120 && table.View().ActingSeat is int seat; step++)
            {
                var view = table.View(seat);
                var action = HoldemBot.Decide(view);
                played.Add(action.Kind);
                table.Act(seat, view.Revision, action);
                Conserves(table.View(), 495);
            }

            var terminal = table.View();
            Assert.Equal(hand, terminal.HandNumber);
            Assert.NotNull(terminal.Result);
            Assert.Empty(terminal.OwnCards);
            if (terminal.MatchWinnerSeat.HasValue || hand == hands)
                break;
            table.StartNextHandWithDeck(terminal.Revision, NextDeck());
        }
        Assert.Contains(Fold, played);
        Assert.Contains(Check, played);
        Assert.Contains(Call, played);
        Assert.True(played.Contains(Bet) || played.Contains(Raise));
    }
}
