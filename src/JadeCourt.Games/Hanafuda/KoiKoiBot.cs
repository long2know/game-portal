using static JadeCourt.Games.Hanafuda.HanafudaCards;

namespace JadeCourt.Games.Hanafuda;

internal static class KoiKoiBot
{
    public static KoiKoiAction Choose(KoiKoiView view)
    {
        var seat = view.TurnSeat ?? throw new InvalidDataException("Koi-Koi bot has no active turn.");
        var own = view.Seats[seat];
        var options = view.LegalActions;
        var subtotal = own.Yaku.Sum(yaku => yaku.Points);
        if (options.CanScore)
        {
            var press = options.CanContinue && subtotal < 7 && own.HandCount >= 3 && view.Seats[1 - seat].Yaku.Length == 0;
            return new(press ? KoiKoiActionKind.Continue : KoiKoiActionKind.Score);
        }

        if (view.DrawnCard is { } drawn && options.DrawTargets.Length == 2)
            return new(KoiKoiActionKind.TakeDraw, TargetId: BestTarget(drawn.Id, options.DrawTargets));

        var plays = options.Plays.Select(play =>
        {
            var target = play.TargetIds.Length == 2 ? BestTarget(play.CardId, play.TargetIds) : (int?)null;
            var targets = target is int choice ? [choice] : play.TargetIds;
            var value = targets.Length == 0 ? -Interest(play.CardId) : CaptureValue(play.CardId, targets);
            return (Value: value, Action: new KoiKoiAction(KoiKoiActionKind.Play, play.CardId, target));
        }).ToArray();
        if (plays.Length == 0) throw new InvalidDataException("Koi-Koi bot has no legal action.");
        return plays.MaxBy(play => play.Value).Action;

        int BestTarget(int source, int[] choices) => choices.MaxBy(target => CaptureValue(source, [target]));

        int CaptureValue(int source, int[] targets)
        {
            int[] newCards = [source, .. targets];
            var gain = Yaku(own.Captured.Select(card => card.Id).Concat(newCards)).Sum(yaku => yaku.Points) - subtotal;
            return 32 * gain + newCards.Sum(Interest);
        }
    }

    private static int Interest(int id) => id switch
    {
        Cup => 9,
        CherryCurtain or Moon => 8,
        Butterfly or Boar or Deer => 5,
        _ => Card(id).Kind switch { Bright => 5, Animal or Ribbon => 3, _ => 1 }
    };
}
