using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static JadeCourt.Poker.HoldemActionKind;
using static JadeCourt.Poker.Tests.TableFixture;

namespace JadeCourt.Poker.Tests;

public sealed class ProjectionAndReplayTests
{
    [Fact]
    public void PlayerAndSpectatorProjectionsAreUnchangedByUnseenCards()
    {
        const string board = "2c 3c 7h 9s Jc";
        var firstDeck = Rig(0, board, "As Kd", "Qc Jd", "Ts Td");
        var secondDeck = Rig(0, board, "As Kd", "5d 6d", "8d 9d");
        var first = HoldemTable.CreateWithDeck([80, 80, 80], 5, 10, 0, firstDeck);
        var second = HoldemTable.CreateWithDeck([80, 80, 80], 5, 10, 0, secondDeck);
        Assert.Equal(Json(first.View(0)), Json(second.View(0)));
        Assert.Equal(Json(first.View()), Json(second.View()));
        Assert.Equal(HoldemBot.Decide(first.View(0)), HoldemBot.Decide(second.View(0)));
        Assert.Throws<ArgumentException>(() => HoldemBot.Decide(first.View()));
        Assert.Throws<ArgumentException>(() => HoldemBot.Decide(first.View(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.View(3));
        Assert.Equal("{}", JsonSerializer.Serialize(first));

        foreach (var table in new[] { first, second })
        {
            Assert.Empty(table.View().OwnCards);
            Assert.Null(table.View().LegalActions);
            Assert.Null(table.View().Result);
            Assert.Equal(Cards("As Kd"), table.View(0).OwnCards.AsEnumerable());
            Assert.NotNull(table.View(0).LegalActions);
            Assert.Null(table.View(1).LegalActions);
            Assert.Null(table.View(2).LegalActions);
            Play(table, 0, Call);
            Play(table, 1, Call);
            Play(table, 2, Check);
        }
        Assert.Equal(Cards("Qc Jd"), first.View(1).OwnCards.AsEnumerable());
        Assert.Equal(Cards("Ts Td"), first.View(2).OwnCards.AsEnumerable());
        Assert.Equal(Cards("5d 6d"), second.View(1).OwnCards.AsEnumerable());
        Assert.Equal(Cards("8d 9d"), second.View(2).OwnCards.AsEnumerable());
        Assert.Equal(Cards("2c 3c 7h"), first.View().Board.AsEnumerable());
        Assert.Equal(Json(first.View(0)), Json(second.View(0)));
        Assert.Equal(Json(first.View()), Json(second.View()));
    }

    [Fact]
    public void CheckpointReplaysAnActiveHandAndContinuesIdenticallyAcrossHands()
    {
        var deck = Rig(0, "2c 4d 7h 9s Jc", "As Ad", "Kd Qd", "Kh Ks", "3h 8h");
        var table = HoldemTable.CreateWithDeck([100, 100, 24, 100], 5, 10, 0, deck);
        Play(table, 3, Raise, 20);
        Play(table, 0, Call);
        Play(table, 1, Call);
        Play(table, 2, Raise, 24);
        var snapshot = Checkpoint(table);
        var revision = table.View().Revision;
        Reject(HoldemRejection.IllegalAction, () => table.Act(3, revision, new(Raise, 34)));
        Assert.Equal(snapshot, Checkpoint(table));

        var restored = Restore(snapshot);
        Assert.Equal(snapshot, Checkpoint(restored));
        MatchViews(table, restored);
        Assert.Null(restored.View(3).LegalActions!.Raise);
        foreach (var current in new[] { table, restored })
        {
            Play(current, 3, Call);
            Play(current, 0, Call);
            Play(current, 1, Call);
            Play(current, 1, Bet, 10);
            Play(current, 3, Fold);
            Play(current, 0, Raise, 20);
            Play(current, 1, Call);
            CheckDown(current);
        }
        var done = restored.View();
        Assert.Equal([96, 40], done.Result!.Pots.Select(p => p.Chips));
        Assert.Equal([192, 56, 0, 76], done.Seats.Select(s => s.Stack));
        MatchViews(table, restored);
        MatchViews(restored, Restore(Checkpoint(restored)));

        var nextDeck = StandardDeck().Reverse().ToArray();
        table.StartNextHandWithDeck(done.Revision, nextDeck);
        restored.StartNextHandWithDeck(done.Revision, nextDeck);
        Assert.Equal((1, 3, 0), (restored.View().ButtonSeat, restored.View().SmallBlindSeat,
            restored.View().BigBlindSeat));
        Assert.Equal(2, restored.View().HandNumber);
        Assert.Equal(done.Revision + 1, restored.View().Revision);
        Assert.Empty(restored.View(2).OwnCards);
        MatchViews(table, restored);
        MatchViews(restored, Restore(Checkpoint(restored)));
    }

    [Fact]
    public void InvalidCheckpointFrameCannotBeRestored()
    {
        var bytes = Checkpoint(New([100, 100]));
        Assert.Throws<InvalidDataException>(() => Restore(bytes[..^1]));
        var frame = JsonNode.Parse(bytes)!.AsObject();
        frame["Version"] = 2;
        Assert.Throws<InvalidDataException>(() => Restore(Encode(frame)));
        frame["Version"] = 1;
        frame["Digest"] = Convert.ToBase64String(new byte[32]);
        Assert.Throws<InvalidDataException>(() => Restore(Encode(frame)));
    }

    [Theory]
    [InlineData("invalid-balance")]
    [InlineData("duplicate-card")]
    [InlineData("bad-revision")]
    [InlineData("out-of-turn-action")]
    [InlineData("invalid-action")]
    [InlineData("missing-action-field")]
    public void ReplayValidatesCheckpointContentsEvenWithAValidChecksum(string corruption)
    {
        var table = New([100, 100]);
        Play(table, 0, Call);
        var frame = JsonNode.Parse(Checkpoint(table))!.AsObject();
        var payload = JsonNode.Parse(Convert.FromBase64String(frame["Payload"]!.GetValue<string>()))!.AsObject();
        switch (corruption)
        {
            case "invalid-balance":
                payload["Stacks"]![0] = -1;
                break;
            case "duplicate-card":
                var deck = Convert.FromBase64String(payload["Deck"]!.GetValue<string>());
                deck[1] = deck[0];
                payload["Deck"] = Convert.ToBase64String(deck);
                break;
            case "bad-revision":
                payload["Revision"] = 100;
                break;
            case "out-of-turn-action":
                payload["Actions"]![0]!["Seat"] = 1;
                break;
            case "invalid-action":
                payload["Actions"]![0]!["To"] = 1;
                break;
            case "missing-action-field":
                Assert.True(payload["Actions"]![0]!.AsObject().Remove("Seat"));
                break;
        }
        var forgedPayload = Encode(payload);
        frame["Payload"] = Convert.ToBase64String(forgedPayload);
        frame["Digest"] = Convert.ToBase64String(SHA256.HashData(forgedPayload));
        Assert.Throws<InvalidDataException>(() => Restore(Encode(frame)));
    }

    private static void MatchViews(HoldemTable expected, HoldemTable actual)
    {
        var spectator = expected.View();
        Assert.Equal(Json(spectator), Json(actual.View()));
        foreach (var player in spectator.Seats)
            Assert.Equal(Json(expected.View(player.Seat)), Json(actual.View(player.Seat)));
    }

    private static string Json(HoldemView view) => JsonSerializer.Serialize(view);
    private static byte[] Encode(JsonObject json) => Encoding.UTF8.GetBytes(json.ToJsonString());
}
