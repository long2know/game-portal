using JadeCourt.Poker.Persistence;

namespace JadeCourt.Poker.Tests;

internal static class TableFixture
{
    internal static HoldemCard[] StandardDeck() => Enumerable.Range(0, 52).Select(HoldemCard.FromIndex).ToArray();

    internal static HoldemCard[] Cards(string codes) => codes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(c => new HoldemCard((HoldemRank)("23456789TJQKA".IndexOf(c[0]) + 2),
            (HoldemSuit)"cdhs".IndexOf(c[1]))).ToArray();

    internal static HoldemCard[] Rig(int button, string board, params string[] holesBySeat)
    {
        var holes = Enumerable.Range(1, holesBySeat.Length)
            .Select(offset => Cards(holesBySeat[(button + offset) % holesBySeat.Length]))
            .Where(hand => hand.Length > 0).ToArray();
        var community = Cards(board);
        var used = holes.SelectMany(cards => cards).Concat(community);
        var other = StandardDeck().Except(used).ToArray();
        return [.. holes.Select(hand => hand[0]), .. holes.Select(hand => hand[1]),
            other[0], .. community[..3], other[1], community[3], other[2], community[4], .. other[3..]];
    }

    internal static HoldemTable New(int[] stacks, int small = 5, int big = 10) =>
        HoldemTable.CreateWithDeck(stacks, small, big, 0, StandardDeck());

    internal static void Play(HoldemTable table, int seat, HoldemActionKind kind, int to = 0)
    {
        var turn = table.View(seat);
        Assert.Equal(seat, turn.ActingSeat);
        table.Act(seat, turn.Revision, new(kind, to));
    }

    internal static void CheckDown(HoldemTable table)
    {
        for (var i = 0; i < 24 && table.View().ActingSeat is int seat; i++)
            Play(table, seat, HoldemActionKind.Check);
        Assert.NotNull(table.View().Result);
    }

    internal static void Conserves(HoldemView view, int total)
    {
        Assert.All(view.Seats, s =>
        {
            Assert.True(s.Stack >= 0);
            Assert.InRange(s.StreetCommitted, 0, s.HandCommitted);
        });
        Assert.Equal(view.PotChips, view.Seats.Sum(s => s.HandCommitted));
        Assert.Equal(total, view.PotChips + view.Seats.Sum(s => s.Stack));
    }

    internal static byte[] Checkpoint(HoldemTable table)
    {
        using var stream = new MemoryStream();
        HoldemServerPersistence.WriteCheckpoint(table, stream);
        return stream.ToArray();
    }

    internal static HoldemTable Restore(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return HoldemServerPersistence.RestoreCheckpoint(stream);
    }

    internal static void Reject(HoldemRejection reason, Action attempt) =>
        Assert.Equal(reason, Assert.Throws<HoldemRejectedException>(attempt).Reason);
}
