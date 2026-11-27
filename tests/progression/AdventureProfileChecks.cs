using System.Xml.Serialization;

internal static class AdventureProfileChecks
{
    private const string Card = "FP1_002";
    private static readonly LocalDeck Starter = LocalDecks.PlayerCoreStarterDecks.First();
    private static string Begin(LocalProfile profile, int seed = 42, int version = 1) =>
        profile.BeginAdventureAttempt(Starter.Id, Starter, seed, version);

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException || error is IOException || error is ArgumentException) { return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static void Write(string path, LocalProfileData data)
    {
        using var file = File.Create(path);
        new XmlSerializer(typeof(LocalProfileData)).Serialize(file, data);
    }

    internal static void Run(string root)
    {
        GrantAndReplay(root, 0); GrantAndReplay(root, 1); GrantAndReplay(root, 2);
        OtherOutcomes(root);
        FailedWrites(root);
        Migration(root);
        PersonalDeck(root);
        Console.WriteLine("PASS adventure profile: access, deck binding, reward provenance, simultaneous settlement, recovery and V17 migration");
    }

    private static void GrantAndReplay(string root, int before)
    {
        string path = Path.Combine(root, "copies-" + before + ".xml");
        string first;
        using (var profile = new LocalProfile(path)) {
            Check(!profile.AdventureWing.Owned && profile.AdventureWing.Progress == 0, "new wing must be locked");
            Reject(() => Begin(profile), "no wing access");
            Check(!File.Exists(path), "a refused start created a profile");
            profile.GrantAdventureWingAccess();
            profile.CommitChange(data => {
                if (before > 0) data.Collection.Add(new LocalCardCount { Id = Card, Count = before, Acquired = before });
                data.PremiumCollection.Add(new LocalCardCount { Id = Card, Premium = 1, Count = 1, Acquired = 1 });
                return true;
            });
            first = Begin(profile);
            Check(first == Begin(profile), "identical repeated start");
            Reject(() => Begin(profile, 43), "second seed while pending");
            Reject(() => profile.Begin("mage", "priest", 5), "ranked while adventure pending");
            int gold = profile.Gold;
            var results = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => profile.SettleAdventureAttempt(first, "Won"))))
                .GetAwaiter().GetResult();
            Check(results.All(row => row.Id == first && row.RewardId == 127), "winner callbacks disagree");
            Reject(() => profile.SettleAdventureAttempt(first, "Lost"), "conflicting outcome");
            Check(profile.Gold == gold && profile.History.Count == 0 && profile.OwnedCount(Card) == before + 2
                && profile.StoredOwnedCount(Card, 1) == 1 && profile.Acquired[Card] == 2, "reward affected the wrong ledger");
        }
        using (var profile = new LocalProfile(path)) {
            var receipt = profile.PendingAdventureReward;
            Check(receipt?.Id == first && receipt.Copies == 2 && receipt.CountBefore == before
                && profile.PendingAdventureAttempt == null && profile.AdventureWing.Progress == 2 && profile.AdventureWing.Ack == 1,
                "persisted win and pending presentation");
            string second = Begin(profile, 43, 2);
            Check(profile.SettleAdventureAttempt(second, "Won").RewardId == 0 && profile.OwnedCount(Card) == before + 2,
                "replay with another version granted twice");
            Check(profile.AcknowledgeAdventureWing(2) && !profile.AcknowledgeAdventureWing(1)
                && !profile.AcknowledgeAdventureWing(3) && profile.PendingAdventureReward.Id == first, "wing ack consumed the presentation");
            Check(!profile.AcknowledgeAdventureReward(second) && profile.AcknowledgeAdventureReward(first)
                && !profile.AcknowledgeAdventureReward(first), "reward acknowledgement not idempotent");
        }
        using (var profile = new LocalProfile(path)) {
            Check(profile.PendingAdventureReward == null && profile.AdventureWing.Ack == 2 && profile.OwnedCount(Card) == before + 2
                && profile.SettleAdventureAttempt(first, "Won").RewardId == 127, "ack or idempotent receipt lost across restart");
            // Lifetime ownership and the reward receipt must survive the physical cards leaving the collection.
            profile.CommitChange(data => data.Collection.Single(row => row.Id == Card).Count = 0);
            Check(profile.SettleAdventureAttempt(Begin(profile, 44), "Won").RewardId == 0 && profile.OwnedCount(Card) == 0, "replay followed stock");
        }
        var forged = LocalProfile.ReadFile(path);
        forged.Collection.Single(row => row.Id == Card).Count = before + 3;
        Write(path, forged);
        Reject(() => { using var invalid = new LocalProfile(path); }, "unexplained extras");
    }

    private static void OtherOutcomes(string root)
    {
        string path = Path.Combine(root, "losses.xml");
        using var other = new LocalProfile(Path.Combine(root, "other.xml"));
        other.GrantAdventureWingAccess();
        string otherId = Begin(other);
        using (var profile = new LocalProfile(path)) {
            profile.GrantAdventureWingAccess();
            foreach (var outcome in new[] { "Lost", "Tied", "Conceded" }) {
                string id = Begin(profile);
                Reject(() => profile.SettleAdventureAttempt(otherId, "Won"), "foreign callback");
                Check(profile.SettleAdventureAttempt(id, outcome).RewardId == 0, outcome + " granted a reward");
            }
            Begin(profile);
        }
        using (var profile = new LocalProfile(path)) {
            string abandoned = profile.PendingAdventureAttempt.Id;
            profile.RecoverInterrupted();
            Check(profile.AdventureRecovered && profile.PendingAdventureAttempt == null && profile.SettleAdventureAttempt(abandoned, "Interrupted").RewardId == 0
                && profile.History.Count == 0 && profile.AdventureWing.Progress == 1 && profile.OwnedCount(Card) == 0
                && Begin(profile, 45) != abandoned, "interrupted adventure or replay polluted the ladder");
        }
        Check(other.PendingAdventureAttempt.Id == otherId && other.OwnedCount(Card) == 0, "foreign profile mutated");
    }

    private static void FailedWrites(string root)
    {
        string path = Path.Combine(root, "failed.xml");
        using var profile = new LocalProfile(path);
        profile.GrantAdventureWingAccess();
        string id = Begin(profile);
        byte[] before = File.ReadAllBytes(path);
        foreach (var phase in new[] { "write", "flush", "replace" }) {
            profile.CommitFailure = step => { if (step == phase) throw new IOException("injected " + phase); };
            try { Reject(() => profile.SettleAdventureAttempt(id, "Won"), phase); }
            finally { profile.CommitFailure = null; }
            Check(before.SequenceEqual(File.ReadAllBytes(path)) && profile.PendingAdventureAttempt.Id == id
                && profile.OwnedCount(Card) == 0 && profile.PendingAdventureReward == null, "partial " + phase);
        }
        LocalProfile.CommitFaultHook = _ => {
            File.Replace(path + ".tmp", path, null);
            throw new IOException("indeterminate replacement");
        };
        try { Check(profile.SettleAdventureAttempt(id, "Won").RewardId == 127, "resolve durable win"); }
        finally { LocalProfile.CommitFaultHook = null; }
        Check(profile.SettleAdventureAttempt(id, "Won").RewardId == 127 && profile.OwnedCount(Card) == 2
            && LocalProfile.ReadFile(path).Adventures.Results.Count == 1, "indeterminate replacement granted twice");
    }

    private static void Migration(string root)
    {
        string path = Path.Combine(root, "v17.xml");
        string pendingReward;
        using (var profile = new LocalProfile(path)) {
            pendingReward = profile.Begin("mage", "priest", 23).Id;
            profile.Settle(pendingReward, "Won", 8, false);
        }
        var old = LocalProfile.ReadFile(path);
        old.Version = 17;
        old.PremiumCollection.Add(new LocalCardCount { Id = Card, Premium = 1, Count = 1, Acquired = 1 });
        Write(path, old);
        var original = File.ReadAllBytes(path);
        using var migrated = new LocalProfile(path);
        Check(File.ReadAllBytes(path + ".v17.bak").SequenceEqual(original) && LocalProfile.ReadFile(path).Version == 18
            && !migrated.AdventureWing.Owned && migrated.PendingAdventureReward == null && migrated.PendingRewardPresentation?.Id == pendingReward
            && migrated.StoredOwnedCount(Card, 1) == 1
            && migrated.StoredCollection.OrderBy(row => row.Key).SequenceEqual(old.Collection.ToDictionary(c => c.Id, c => c.Count).OrderBy(row => row.Key)),
            "V17 migration changed existing cards or awarded access");
    }

    private static void PersonalDeck(string root)
    {
        using var profile = new LocalProfile(Path.Combine(root, "personal.xml"));
        profile.GrantAdventureWingAccess();
        profile.SettleAdventureAttempt(Begin(profile), "Won");
        var legacy = LocalDecks.Get("paladin");
        var cards = legacy.CardIds;
        cards[0] = "JAIL_397";
        var boards = new[] { new LocalSideboard { Source = "JAIL_397", Cards = new List<string> { Card } } };
        var deck = LocalProfile.PlayableDeck(legacy.Name, legacy.Hero, cards, boards, adventure: true);
        var saved = profile.CreateDraft(deck.Name, deck.Hero);
        string key = "custom:" + saved.Id;
        Reject(() => profile.BeginAdventureAttempt(key, deck, 0, 1), "empty draft");
        profile.CommitChange(data => {
            foreach (var group in deck.CardIds.GroupBy(id => id)) {
                var row = data.Collection.SingleOrDefault(card => card.Id == group.Key);
                if (row == null) data.Collection.Add(new LocalCardCount { Id = group.Key, Acquired = group.Count(), Count = group.Count() });
                else { row.Count = Math.Max(row.Count, group.Count()); row.Acquired = Math.Max(row.Acquired, group.Count()); }
            }
            return true;
        });
        profile.SaveDraft(saved.Id, deck.Name, deck.Hero, cards, saved.SortOrder, boards);
        Check(profile.CustomDecks.Any(draft => draft.Id == saved.Id), "supported Wild sideboard hidden from picker");
        Check(!profile.IsDeckComplete(saved.Id) && profile.IsDeckComplete(saved.Id, adventure: true), "Wild deck eligibility");
        Reject(() => profile.BeginAdventureAttempt(key, Starter, 0, 1), "another deck at the same native ID");
        string id = profile.BeginAdventureAttempt(key, deck, 0, 1);
        Check(profile.PendingAdventureAttempt.DeckFingerprint.Length == 64, "personal snapshot hash");
        profile.SettleAdventureAttempt(id, "Lost");
        profile.CommitChange(data => data.Collection.Single(row => row.Id == Card).Count = 0);
        Check(!profile.IsDeckComplete(saved.Id, adventure: true), "the adventure picker rejects an unowned Wild sideboard copy");
        Reject(() => profile.BeginAdventureAttempt(key, deck, 0, 1), "unowned Wild sideboard");
        profile.DeleteDraft(saved.Id);
        Reject(() => profile.BeginAdventureAttempt(key, deck, 0, 1), "deleted personal deck");
        Check(profile.PendingAdventureAttempt == null, "rejected deck left pending attempt");
    }
}
