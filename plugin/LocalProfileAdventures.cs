using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

// The profile owns the Arachnid Quarter in Normal independently of its encounter results.
public sealed class LocalAdventureWing
{
    public bool Owned;
    public int Progress;
    public int Ack;
    internal LocalAdventureWing Copy() => (LocalAdventureWing)MemberwiseClone();
}

public sealed class LocalAdventureAttempt
{
    public string Id;
    public int ScenarioId;
    public string DeckKey;
    public string DeckFingerprint;
    public int Seed;
    public int DefinitionVersion;
    internal LocalAdventureAttempt Copy() => (LocalAdventureAttempt)MemberwiseClone();
}

public sealed class LocalAdventureResult
{
    public LocalAdventureAttempt Attempt;
    public string Outcome;
    public int RewardId;
    [XmlIgnore] public string Id => Attempt.Id;
    internal LocalAdventureResult Copy() => new LocalAdventureResult {
        Attempt = Attempt.Copy(), Outcome = Outcome, RewardId = RewardId
    };
}

public sealed class LocalAdventureReward
{
    internal const string HauntedCreeper = "FP1_002";
    public string Id; // The attempt that first earned this reward in this profile.
    public int AdventureId;
    public int ModeId;
    public int ScenarioId;
    public int RewardId;
    public string CardId;
    public int Copies;
    public int CountBefore;
    public bool Shown;
    internal LocalAdventureReward Copy() => (LocalAdventureReward)MemberwiseClone();
}

public sealed class LocalAdventureState
{
    public LocalAdventureWing Wing = new LocalAdventureWing();
    public LocalAdventureAttempt Pending;
    public List<LocalAdventureResult> Results = new List<LocalAdventureResult>();
    public LocalAdventureReward Reward;
    internal LocalAdventureState Copy() => new LocalAdventureState {
        Wing = Wing.Copy(), Pending = Pending?.Copy(), Results = Results.Select(row => row.Copy()).ToList(), Reward = Reward?.Copy()
    };
}

public sealed partial class LocalProfile
{
    private readonly object matchGate = new object();
    public LocalAdventureWing AdventureWing => data.Adventures?.Wing.Copy() ?? new LocalAdventureWing();
    public LocalAdventureAttempt PendingAdventureAttempt => data.Adventures?.Pending?.Copy();
    public LocalAdventureReward PendingAdventureReward => data.Adventures?.Reward?.Shown == false ? data.Adventures.Reward.Copy() : null;
    public bool AdventureRecovered { get; private set; }

    // Call only with an explicit entitlement. Entering the adventure screen does not grant a wing.
    internal void GrantAdventureWingAccess()
    {
        lock (matchGate) {
            RequireAdventureLease();
            if (data.Adventures?.Wing.Owned == true) return;
            var next = Snapshot();
            next.Adventures = new LocalAdventureState { Wing = new LocalAdventureWing { Owned = true, Progress = 1, Ack = 1 } };
            CommitAdventureChange(next, saved => saved?.Wing.Owned == true);
        }
    }

    public string BeginAdventureAttempt(string deckKey, LocalDeck deck, int seed, int definitionVersion)
    {
        lock (matchGate) {
            RequireAdventureLease();
            if (data.Adventures?.Wing.Owned != true) throw new InvalidOperationException("The Arachnid Quarter is locked.");
            if (data.Pending != null || data.PendingRewardPresentationId != null || data.PackRevealPending)
                throw new InvalidOperationException("Finish the current match or presentation before starting the adventure.");
            if (deck == null || !ValidName(deckKey, 64) || seed < 0 || definitionVersion < 1)
                throw new ArgumentException("Invalid adventure match setup.");
            string fingerprint = AdventureDeckFingerprint(deckKey, deck);
            var pending = data.Adventures.Pending;
            if (pending != null) {
                if (pending.DeckKey == deckKey && pending.DeckFingerprint == fingerprint && pending.Seed == seed
                    && pending.DefinitionVersion == definitionVersion) return pending.Id;
                throw new InvalidOperationException("Another adventure attempt is already pending.");
            }
            if (data.Adventures.Results.Count >= MaximumRecords)
                throw new InvalidOperationException("The adventure result history is full.");
            var next = Snapshot();
            next.Adventures.Pending = new LocalAdventureAttempt { Id = Guid.NewGuid().ToString(), ScenarioId = 273,
                DeckKey = deckKey, DeckFingerprint = fingerprint, Seed = seed, DefinitionVersion = definitionVersion };
            CommitAdventureChange(next, saved => saved?.Pending?.Id == next.Adventures.Pending.Id);
            return next.Adventures.Pending.Id;
        }
    }

    private string AdventureDeckFingerprint(string deckKey, LocalDeck resolved)
    {
        IEnumerable<LocalDeckCard> main;
        IEnumerable<LocalSideboard> boards;
        string hero;
        if (deckKey.StartsWith("custom:", StringComparison.Ordinal)) {
            if (!long.TryParse(deckKey.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out long id)
                || !IsDeckComplete(id, adventure: true))
                throw new InvalidOperationException("The personal adventure deck is missing or incomplete.");
            var saved = GetDraft(id);
            hero = saved.Hero;
            main = saved.CardEntries;
            boards = saved.Sideboards;
        } else {
            LocalDeck starter = LocalDecks.PlayerCoreStarterDecks.SingleOrDefault(row => row.Id == deckKey);
            if (starter == null || IsLoanerHidden(deckKey))
                throw new InvalidOperationException("The selected starter deck is unavailable.");
            hero = starter.Hero;
            main = starter.CardIds.Select(id => new LocalDeckCard { Id = id });
            boards = starter.Sideboards.Select(row => new LocalSideboard { Source = row.Key, Cards = row.Value.ToList() });
        }
        var resolvedBoards = resolved.Sideboards;
        if (hero != resolved.Hero || !resolved.CardIds.SequenceEqual(main.Select(card => card.Id))
            || resolvedBoards.Count != boards.Count() || boards.Any(board => !resolvedBoards.TryGetValue(board.Source, out var ids)
                || !ids.SequenceEqual(board.Cards)))
            throw new InvalidOperationException("The selected adventure deck differs from its saved definition.");
        resolved.Build();
        string Entries(IEnumerable<LocalDeckCard> entries) => string.Join(",", entries.Select(card => card.Id + "/" + card.Premium.ToString(CultureInfo.InvariantCulture)));
        return LocalAchievementFingerprint.Hash(hero + "\n" + Entries(main) + "\n"
            + string.Join("\n", boards.OrderBy(board => board.Source, StringComparer.Ordinal).Select(board => board.Source + ":" + Entries(board.CardEntries))));
    }

    public LocalAdventureResult SettleAdventureAttempt(string attemptId, string outcome)
    {
        lock (matchGate) {
            RequireAdventureLease();
            if (!Guid.TryParse(attemptId, out _) || !AdventureOutcome(outcome)) throw new ArgumentException("Invalid adventure result.");
            var prior = data.Adventures?.Results.SingleOrDefault(row => row.Id == attemptId);
            if (prior != null) {
                if (prior.Outcome != outcome) throw new InvalidOperationException("Conflicting result for this adventure attempt.");
                return prior.Copy();
            }
            if (data.Adventures?.Pending?.Id != attemptId) throw new InvalidOperationException("Unknown adventure attempt.");
            var next = Snapshot();
            var state = next.Adventures;
            var result = new LocalAdventureResult { Attempt = state.Pending, Outcome = outcome };
            if (outcome == "Won" && state.Reward == null) {
                var card = GetCollectionCard(next, LocalAdventureReward.HauntedCreeper, 0);
                state.Reward = new LocalAdventureReward {
                    Id = attemptId, AdventureId = 3, ModeId = 1, ScenarioId = 273, RewardId = 127,
                    CardId = card.Id, CountBefore = card.Count, Copies = 2
                };
                card.Count = checked(card.Count + state.Reward.Copies);
                card.Acquired = Math.Min(Playset(card.Id), card.Acquired + state.Reward.Copies);
                state.Wing.Progress = Math.Max(2, state.Wing.Progress);
                result.RewardId = state.Reward.RewardId;
            }
            state.Results.Add(result);
            state.Pending = null;
            CommitAdventureChange(next, saved => saved?.Results.Any(row => row.Id == attemptId && row.Outcome == outcome) == true);
            return result.Copy();
        }
    }

    public bool AcknowledgeAdventureReward(string receiptId)
    {
        lock (matchGate) {
            RequireAdventureLease();
            var reward = data.Adventures?.Reward;
            if (reward == null || reward.Id != receiptId || reward.Shown) return false;
            var next = Snapshot();
            next.Adventures.Reward.Shown = true;
            CommitAdventureChange(next, saved => saved?.Reward?.Id == receiptId && saved.Reward.Shown);
            return true;
        }
    }

    public bool AcknowledgeAdventureWing(int progress)
    {
        lock (matchGate) {
            RequireAdventureLease();
            var wing = data.Adventures?.Wing;
            if (wing == null || !wing.Owned || progress <= wing.Ack || progress > wing.Progress) return false;
            var next = Snapshot();
            next.Adventures.Wing.Ack = progress;
            CommitAdventureChange(next, saved => saved?.Wing.Ack == progress);
            return true;
        }
    }

    private void RequireAdventureLease()
    {
        if (!lease.CanWrite) throw new ObjectDisposedException(nameof(LocalProfile));
    }

    private void CommitAdventureChange(LocalProfileData next, Func<LocalAdventureState, bool> committed)
    {
        try { Commit(next, acceptRecoveredReplace: true); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
            // A filesystem may report failure after the replacement reached disk. Reconcile under the profile lease.
            if (!File.Exists(path)) throw;
            var saved = ReadFile(path);
            ValidateV14(saved, profileOptions.AllCosmetics, profileOptions.LegendRating, profileOptions.AllDecks);
            if (!committed(saved.Adventures)) throw;
            data = saved;
            pendingSeen.Clear();
        }
    }

    private static bool AdventureOutcome(string value) => value == "Won" || value == "Lost" || value == "Tied"
        || value == "Conceded" || value == "Interrupted";

    private static void ValidateAdventures(LocalProfileData profile)
    {
        var state = profile?.Adventures;
        if (state == null) return;
        if (profile.Version < 18 || state.Wing == null || state.Wing.Ack < (state.Wing.Owned ? 1 : 0)
            || state.Wing.Ack > state.Wing.Progress || state.Results == null || state.Results.Count > MaximumRecords
            || state.Pending != null && (profile.Pending != null || profile.PendingRewardPresentationId != null || state.Results.Count >= MaximumRecords))
            throw new InvalidOperationException("Invalid adventure wing or match ownership.");
        if (!state.Wing.Owned) {
            if (state.Wing.Progress != 0 || state.Pending != null || state.Results.Count != 0 || state.Reward != null)
                throw new InvalidOperationException("A locked adventure wing cannot contain local progress.");
            return;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void ValidateAttempt(LocalAdventureAttempt attempt) {
            if (attempt == null || !Guid.TryParseExact(attempt.Id, "D", out _) || !ids.Add(attempt.Id)
                || attempt.ScenarioId != 273 || !ValidName(attempt.DeckKey, 64) || !ValidFingerprint(attempt.DeckFingerprint)
                || attempt.Seed < 0 || attempt.DefinitionVersion < 1)
                throw new InvalidOperationException("Invalid adventure attempt.");
        }
        if (state.Pending != null) ValidateAttempt(state.Pending);
        string firstWin = null;
        foreach (var row in state.Results) {
            ValidateAttempt(row?.Attempt);
            int expectedReward = row.Outcome == "Won" && firstWin == null ? 127 : 0;
            if (!AdventureOutcome(row.Outcome) || row.RewardId != expectedReward)
                throw new InvalidOperationException("Invalid adventure result.");
            if (expectedReward != 0) firstWin = row.Id;
        }
        var reward = state.Reward;
        if (state.Wing.Progress != (firstWin == null ? 1 : 2) || (firstWin == null) != (reward == null)
            || reward != null && (reward.Id != firstWin || reward.AdventureId != 3 || reward.ModeId != 1 || reward.ScenarioId != 273
                || reward.RewardId != 127 || reward.CardId != LocalAdventureReward.HauntedCreeper || reward.Copies != 2
                || reward.CountBefore < 0 || reward.CountBefore > 2
                || profile.Collection?.Any(row => row?.Id == reward.CardId && row.Premium == 0 && row.Acquired == 2) != true))
            throw new InvalidOperationException("Invalid first adventure reward.");
    }
}
