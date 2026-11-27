#if CLIENT_TRANSPORT
using SabberStoneCore.Model;
using SabberStoneCore.Enums;

// Exercises the local transport and save contract with existing starter decks. The installed
// encounter has its own admission proof and is never substituted at the live picker.
internal static class AdventureTransportProof
{
    [GroupProof("adventure-transport")]
    internal static void Run()
    {
        string root = Directory.CreateTempSubdirectory("hs-adventure-transport-").FullName;
        string path = Path.Combine(root, "profile.xml");
        var player = LocalDecks.PlayerCoreStarterDecks[0];
        var opponent = LocalDecks.PlayerCoreStarterDecks[1];
        try
        {
            string firstId;
            using (var profile = new LocalProfile(path))
            {
                profile.GrantAdventureWingAccess();
                firstId = profile.BeginAdventureAttempt(player.Id, player, 42, 1);
                int history = 0;
                var match = new NativeMatch((id, _) => { if (id == 19) history++; }, player,
                    Cards.FromId(opponent.Hero), opponent.Build(), 273, 42, null) { MatchId = firstId };
                try { match.SettleAdventure(profile, firstId); throw new Exception("Accepted an unstarted match."); }
                catch (InvalidOperationException) { }
                match.Start();
                Core2026Proof.Check(history > 0 && match.Game.State == State.RUNNING
                    && match.Game.Player1.Hero.Card.Id == player.Hero && match.Game.Player2.Hero.Card.Id == opponent.Hero
                    && match.Game.Player2.DeckCards.Select(c => c.Id).Order().SequenceEqual(opponent.CardIds.Order()),
                    "adventure transport uses the selected human and supplied boss deck");
                match.ConcedeNpcForTest();
                try { match.SettleAdventure(profile, Guid.NewGuid().ToString()); throw new Exception("Accepted a foreign result."); }
                catch (InvalidOperationException) { }
                Core2026Proof.Check(match.SettleAdventure(profile, firstId).RewardId == 127
                    && match.SettleAdventure(profile, firstId).RewardId == 127, "terminal transport result is idempotent");
            }
            using (var reloaded = new LocalProfile(path))
            {
                Core2026Proof.Check(reloaded.PendingAdventureReward.Id == firstId && reloaded.AdventureWing.Progress == 2
                    && reloaded.OwnedCount(LocalAdventureReward.HauntedCreeper) == 2 && reloaded.History.Count == 0,
                    "adventure win restores first-clear progress, collection and pending presentation");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
#endif
