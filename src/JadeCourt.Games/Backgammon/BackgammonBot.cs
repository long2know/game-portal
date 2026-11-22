namespace JadeCourt.Games.Backgammon;

internal static class BackgammonBot
{
    public static int Score(BackgammonState position, int seat)
    {
        if (position.Off[seat] == 15) return 100_000;
        var contact = position.Bar.Any(count => count != 0)
            || Array.FindLastIndex(position.Points, count => count > 0) > Array.FindIndex(position.Points, count => count < 0);
        return Evaluate(seat) - Evaluate(1 - seat);

        int Evaluate(int player)
        {
            var value = position.Off[player] * 14 - position.Bar[player] * 92;
            var run = 0;
            for (var distance = 1; distance <= 24; distance++)
            {
                var count = Relative(distance, player);
                if (count <= 0)
                {
                    run = 0;
                    continue;
                }
                value -= count * distance * 3;
                if (!contact)
                {
                    value -= Math.Max(0, count - 2) * Math.Max(0, 6 - distance);
                    continue;
                }
                if (count >= 2)
                {
                    run++;
                    value += (distance <= 6 ? 12 : distance >= 19 ? 8 : 6) + Math.Min(run - 1, 5) * 4;
                }
                else
                {
                    run = 0;
                    var shots = 0;
                    if (position.Bar[1 - player] != 0)
                        shots = distance <= 6 ? 1 : 0;
                    else
                        for (var back = Math.Max(1, distance - 6); back < distance; back++)
                            if (Relative(back, player) < 0) shots++;
                    if (shots > 0) value -= (30 - distance) + Math.Min(shots - 1, 3) * 4;
                }
            }
            return value;
        }

        int Relative(int distance, int player) => player == 0
            ? position.Points[distance - 1]
            : -position.Points[24 - distance];
    }
}
