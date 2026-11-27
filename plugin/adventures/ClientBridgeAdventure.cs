using System;
using System.Linq;
using HarmonyLib;
using Hearthstone.Core;
using UnityEngine;
using EngineState = SabberStoneCore.Enums.State;

internal static partial class ClientBridge
{
    private sealed class AdventureSession
    {
        internal readonly LocalProfile Profile;
        internal readonly NativeMatch Match;
        internal readonly string AttemptId;
        internal EndGameScreen EndScreen;
        internal bool Interrupted, Settled;
        internal float RetryAt;

        internal AdventureSession(LocalProfile profile, NativeMatch game, string attemptId)
        { Profile = profile; Match = game; AttemptId = attemptId; }
    }

    private static AdventureSession adventure;
    internal static bool OwnsAdventureMatch => adventure != null;
    private const int FirstAdventureScenario = 273;

    internal static string AdventureUnavailableReason() =>
        AdventureEncounter.TryResolve(FirstAdventureScenario, out _, out string reason) ? null : reason;

    internal static void StartAdventureMatch(LocalProfile profile, string deckKey, LocalDeck deck, bool personal)
    {
        if (profile == null || !ReferenceEquals(profile, LocalLadder.Profile) || !profile.AdventureWing.Owned)
            throw new InvalidOperationException("This profile has no access to the wing.");
        if (adventure != null) return; // A second release must not create another match.
        EnsureCanStart();
        if (profile.Pending != null || profile.PendingRewardPresentation != null || LocalLadder.Screen != "Menu")
            throw new InvalidOperationException("Finish the previous local match first.");
        if (!AdventureEncounter.TryResolve(FirstAdventureScenario, out var encounter, out string reason))
            throw new InvalidOperationException(reason);
        var entries = NativeUIProjection.ValidateStart(profile, deckKey, deck, personal, adventure: true);
        var power = SabberStoneCore.Model.Cards.FromAssetId(encounter.BossHero[SabberStoneCore.Enums.GameTag.HERO_POWER]);
        var ids = deck.CardIds.Concat(deck.Sideboards.SelectMany(board => board.Value))
            .Concat(encounter.BossDeck.Select(card => card.Id)).Concat(encounter.DependencyIds)
            .Concat(new[] { deck.Hero, deck.Hero + "bp", encounter.BossHero.Id, power.Id });
        foreach (string id in ids.Distinct(StringComparer.Ordinal))
            if (!ClientKnowsCard(id)) throw new InvalidOperationException("Missing client definition: " + id);

        int seed = Guid.NewGuid().GetHashCode() & int.MaxValue;
        var prepared = new NativeMatch(Send, deck, encounter.BossHero, encounter.BossDeck, encounter.ScenarioId, seed, entries)
        { CosmeticDeckId = PersonalDeckId(deckKey), PlainCards = NativePlainCardsOption.Enabled };
        ClearLocalStage();
        string attemptId = profile.BeginAdventureAttempt(deckKey, deck, seed, encounter.DefinitionVersion);
        adventure = new AdventureSession(profile, prepared, attemptId);
        match = prepared;
        matchId = attemptId;
        npcFailed = false;
        try
        {
            match.MatchId = attemptId;
            ConfigureNativeMatch();
            QueueLocalGame(false, null, encounter.ScenarioId, encounter.BoardId);
        }
        catch
        {
            InterruptAdventure();
            throw;
        }
    }

    private static void InterruptAdventure()
    {
        if (adventure == null) return;
        adventure.Interrupted = true;
        adventure.Match.StopNpc();
        ObserveAdventure(SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY);
    }

    private static void ObserveAdventure(bool gameplay)
    {
        var session = adventure;
        if (session == null || !ReferenceEquals(session.Match, match) || session.AttemptId != matchId
            || !ReferenceEquals(LocalLadder.Profile, session.Profile)) return;
        bool terminal = match.Game?.State == EngineState.COMPLETE;
        if (!terminal && (npcFailed || staged && !localConnected || npcGameplaySeen && !gameplay))
            session.Interrupted = true;
        if (!session.Settled && (terminal || session.Interrupted))
        {
            if (Time.realtimeSinceStartup < session.RetryAt) return;
            try
            {
                var result = terminal ? session.Match.SettleAdventure(session.Profile, session.AttemptId)
                    : session.Profile.SettleAdventureAttempt(session.AttemptId, "Interrupted");
                session.Settled = true;
                if (result.RewardId != 0)
                {
                    try { NativeUIProbe.RefreshAdventureCollection(LocalAdventureReward.HauntedCreeper); }
                    catch (Exception error) { log.LogWarning("ADVENTURE_COLLECTION_PENDING " + error.Message); }
                }
            }
            catch (Exception error)
            {
                session.RetryAt = Time.realtimeSinceStartup + 2;
                log.LogError("ADVENTURE_SAVE_FAILED " + error.Message);
                return;
            }
        }
        if (!session.Settled) return;
        if (session.Interrupted && gameplay)
        {
            NativeUIProbe.ReturnToAdventure();
            return;
        }
        if (!gameplay && (npcGameplaySeen || session.Interrupted))
        {
            ClearLocalStage();
            adventure = null;
        }
    }

    private static void InitializeAdventureEndScreen(Harmony harmony)
    {
        foreach (string name in new[] { "ContinueButtonPress_PrevMode", "ContinueButtonPress_Common" })
            Patch(harmony, typeof(EndGameScreen), name, nameof(AdventureContinue));
        harmony.Patch(AccessTools.Method(typeof(EndGameScreen), "Show", Type.EmptyTypes),
            postfix: new HarmonyMethod(typeof(ClientBridge), nameof(AdventureEndScreenShown)));
    }

    private static void AdventureEndScreenShown(EndGameScreen __instance)
    {
        var session = adventure;
        if (session == null || session.Match.Game?.State != EngineState.COMPLETE || __instance?.m_hitbox == null
            || session.EndScreen == __instance) return;
        session.EndScreen = __instance;
        __instance.m_hitbox.AddEventListener(UIEventType.RELEASE, _ => {
            if (ReferenceEquals(session, adventure)) AdventureContinue(__instance);
        });
    }

    private static bool AdventureContinue(EndGameScreen __instance)
    {
        if (adventure == null) return true;
        if (adventure.EndScreen != __instance || adventure.Match.Game?.State != EngineState.COMPLETE) return false;
        ObserveAdventure(true);
        if (adventure?.Settled == true) NativeUIProbe.ReturnToAdventure();
        return false;
    }
}
