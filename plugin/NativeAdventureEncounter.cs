using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hearthstone.Core;
using PegasusShared;
using UnityEngine;
using SubScene = Assets.AdventureData.Adventuresubscene;
using Format = PegasusShared.FormatType;
using GameKind = PegasusShared.GameType;

internal static partial class NativeUIProbe
{
    private const int FirstBoss = 273;
    private const int FirstWing = 1;
    private static LocalProfile adventureProfile;
    private static bool adventureOpening, startingAdventure, resumeAdventureOnHub;
    private static DeckPickerTrayDisplay adventureTray;
    private static PlayButton adventureButton;
    private static UIEvent.Handler adventureClick;
    private static int adventurePickerGeneration;
    private static readonly Dictionary<long, bool> adventureDeckEligibility = new Dictionary<long, bool>();
    private static readonly AdventureMenuFlow.RewardVisit adventureRewardVisit = new AdventureMenuFlow.RewardVisit();
    private static float adventureRewardRetry;
    private static bool adventureLoadNotice;

    private static bool AdventureRoute => adventureProfile != null && ReferenceEquals(adventureProfile, Profile);
    private static bool AtAdventure => AdventureRoute && SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.ADVENTURE;
    internal static bool AtAdventurePicker => AtAdventure && AdventureConfig.Get()?.CurrentSubScene == SubScene.MISSION_DECK_PICKER;

    private static void InitializeAdventure(Harmony harmony)
    {
        Hook(harmony, typeof(Network), "RequestAdventureProgress", nameof(RequestLocalAdventureProgress), Type.EmptyTypes);
        Hook(harmony, typeof(Network), "GetAdventureProgressResponse", nameof(LocalAdventureProgress), Type.EmptyTypes);
        Hook(harmony, typeof(AdventureProgressMgr), "SetWingAck", nameof(AckAdventureWing), new[] { typeof(int), typeof(int) });
        Hook(harmony, typeof(AdventureProgressMgr), "IsWingEventActive", nameof(AdventureWingEvent), new[] { typeof(int) });
        harmony.Patch(Required(typeof(AdventureConfig), "OnAdventureSceneAwake", Type.EmptyTypes),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(AdventureSceneAwake)));
        Hook(harmony, typeof(AdventureMissionDisplay), "OnNavigateBack", nameof(AdventureListBack), Type.EmptyTypes);
        Hook(harmony, typeof(AdventureMissionDisplay), "ShowAdventureStore", nameof(AdventureStore));
        Hook(harmony, typeof(AdventureMissionDisplay), "ChangeToDeckPicker", nameof(AdventureChooseMission), Type.EmptyTypes);
        Hook(harmony, typeof(GameUtils), "IsFormatPlayable", nameof(AdventureWildFormat), new[] { typeof(VisualsFormatType) });
        Hook(harmony, typeof(CollectionDeck), "get_FormatType", nameof(AdventureDeckFormat), Type.EmptyTypes);
        Hook(harmony, typeof(GameMgr), "FindGame", nameof(AdventureFindGame));
        // Covers direct callers and FindGameWithHero, whose selection cannot represent a published deck.
        Hook(harmony, typeof(GameMgr), "FindGameInternal", nameof(AllowOtherFindGame));
    }

    internal static void ReturnToAdventure()
    {
        if (Profile == null || !ready || sceneWaiting || SceneMgr.Get().IsTransitionNowOrPending()
            || !NativeResultsUI.CanNavigate(false)) return;
        adventureProfile = Profile;
        adventureOpening = true;
        try
        {
            ProjectAdventureWing();
            EnsurePreference(GameSaveKeyId.ADVENTURE_DATA_CLIENT_NAXX);
            if (AtAdventure) AdventureSceneLoaded();
            else Navigate(SceneMgr.Mode.ADVENTURE);
        }
        catch (Exception error) { AdventureLoadFailed(error); }
    }

    private static void AdventureLoadFailed(Exception error)
    {
        resumeAdventureOnHub = adventureOpening = sceneWaiting = false;
        adventureLoadNotice = true;
        log?.LogWarning("ADVENTURE_SCENE " + error.GetBaseException().Message);
        try
        {
            var scene = SceneMgr.Get();
            if (scene == null || scene.IsModeRequested(SceneMgr.Mode.FATAL_ERROR)) return;
            var next = scene.GetNextMode();
            if (next == SceneMgr.Mode.ADVENTURE || next == SceneMgr.Mode.INVALID && scene.GetMode() != SceneMgr.Mode.HUB)
                scene.SetNextMode(SceneMgr.Mode.HUB);
        }
        catch (Exception recovery) { log?.LogWarning("ADVENTURE_SCENE_RETURN " + recovery.Message); }
    }

    private static void SelectAdventure(AdventureConfig config) =>
        config.SetSelectedAdventureMode(AdventureDbId.NAXXRAMAS, AdventureModeDbId.LINEAR);

    private static void AdventureSceneAwake(AdventureConfig __instance)
    {
        if (!AdventureRoute || !adventureOpening) return;
        // The config can be absent at the hub. Awake runs before the adventure scene chooses
        // its first native prefab, so a return never restores the previous deck picker.
        SelectAdventure(__instance);
        __instance.SetMission((ScenarioDbId)FirstBoss, showDetails: false);
        __instance.ResetSubScene(SubScene.MISSION_DISPLAY);
    }

    private static void AdventureSceneLoaded()
    {
        if (!AtAdventure || !adventureOpening) return;
        adventureOpening = false;
        var config = AdventureConfig.Get();
        if (config.GetSelectedAdventure() != AdventureDbId.NAXXRAMAS || config.GetSelectedMode() != AdventureModeDbId.LINEAR)
            SelectAdventure(config);
        if (config.CurrentSubScene != SubScene.MISSION_DISPLAY) config.ChangeSubSceneToSelectedAdventure();
    }

    private static bool AdventurePreference(GameSaveKeyId key) => AdventureRoute && (adventureOpening || AtAdventure)
        && (key == GameSaveKeyId.ADVENTURE_DATA_CLIENT_NAXX || (int)key == 0);

    private static void ProjectAdventureWing()
    {
        var manager = AdventureProgressMgr.Get();
        if (manager != null) Invoke(manager, "OnAdventureProgress");
    }

    private static bool RequestLocalAdventureProgress()
    {
        if (!IsolatedLaunch) return true;
        ProjectAdventureWing();
        return false;
    }

    // Feed the stock progress callback, including its normal ready flag and native listeners.
    private static bool LocalAdventureProgress(ref List<Network.AdventureProgress> __result)
    {
        if (!IsolatedLaunch) return true;
        __result = new List<Network.AdventureProgress>();
        var wing = Profile?.AdventureWing;
        if (wing != null) __result.Add(new Network.AdventureProgress { Wing = FirstWing, Progress = wing.Progress,
            Flags = wing.Owned ? 1UL : 0UL, Ack = wing.Ack });
        return false;
    }

    private static bool AdventureWingEvent(int __0, ref bool __result)
    {
        if (!AdventureRoute || (!adventureOpening && !AtAdventure) || __0 != FirstWing) return true;
        // Only this local encounter's release event is active. Ownership and Ack still come from the save.
        __result = (int)AdventureProgressMgr.GetWingEventTiming(FirstWing) == 10000003;
        return false;
    }

    private static bool AckAdventureWing(AdventureProgressMgr __instance, int __0, int __1, ref bool __result)
    {
        if (!IsolatedLaunch) return true;
        __result = false;
        if (!AtAdventure || __0 != FirstWing) return false;
        try
        {
            var wing = Profile.AdventureWing;
            if (!wing.Owned || __1 < wing.Ack || __1 > wing.Progress) return false;
            if (__1 != wing.Ack) Profile.AcknowledgeAdventureWing(__1);
            int saved = Profile.AdventureWing.Ack;
            Invoke(__instance, "CreateOrUpdateWingAck", FirstWing, saved);
            __result = saved == __1;
        }
        catch (Exception e) { log?.LogWarning("ADVENTURE_ACK " + e.Message); }
        return false;
    }

    private static bool AdventureListBack(ref bool __result)
    {
        if (!AtAdventure) return true;
        Navigate(SceneMgr.Mode.HUB);
        __result = true;
        return false;
    }

    private static bool AdventureStore()
    {
        if (!AtAdventure) return true;
        AdventureNotice(RussianAdventureMenu ? "Это крыло закрыто в локальном профиле." : "This wing is locked in the local profile.");
        return false;
    }

    private static bool AdventureChooseMission()
    {
        if (!AtAdventure) return true;
        if ((int)AdventureConfig.Get().GetMission() == FirstBoss && AdventureConfig.IsMissionAvailable(FirstBoss))
            return true;
        AdventureNotice(UnsupportedAdventureMission());
        return false;
    }

    private static string UnsupportedAdventureMission() => RussianAdventureMenu
        ? "Эта встреча недоступна для локальной игры." : "This encounter is unavailable for local play.";

    private static void AdventureNotice(string message)
    {
        log?.LogInfo("ADVENTURE_UNAVAILABLE " + message);
        DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo { m_id = "HS_CHRONICLES_ADVENTURE_NOTICE",
            m_headerText = AdventureMenuTitle, m_text = message, m_responseDisplay = AlertPopup.ResponseDisplay.OK });
    }

    private static bool AdventureWildFormat(VisualsFormatType __0, ref bool __result)
    {
        if (!AtAdventurePicker || __0 != VisualsFormatType.VFT_WILD) return true;
        __result = true;
        return false;
    }

    // Both CanSelectDeck's ruleset and IsDeckPlayable's sideboard count read this property.
    // SideboardDeck delegates it to its parent. The header stored in the collection stays intact.
    private static bool AdventureDeckFormat(CollectionDeck __instance, ref Format __result)
    {
        if (!AtAdventurePicker || !__instance.NetworkContentsLoaded() || !decks.TryGetValue(__instance.ID, out var local)
            || !ReferenceEquals(CollectionManager.Get()?.GetDeck(__instance.ID), __instance)) return true;
        if (!adventureDeckEligibility.TryGetValue(__instance.ID, out bool eligible))
        {
            var saved = Profile.GetDraft(__instance.ID);
            eligible = saved != null ? Profile.IsDeckComplete(saved.Id, adventure: true)
                : NativeUIProjection.PlayerLoanerKeys.Contains(local.Id) && !Profile.IsLoanerHidden(local.Id);
            adventureDeckEligibility[__instance.ID] = eligible;
        }
        if (!eligible) return true;
        __result = Format.FT_WILD;
        return false;
    }

    private static void UpdateAdventureUI()
    {
        if (adventureLoadNotice && AtHome() && !sceneWaiting && !SceneMgr.Get().IsTransitionNowOrPending()
            && DialogManager.Get()?.ShowingDialog() == false)
        {
            adventureLoadNotice = false;
            AdventureNotice(RussianAdventureMenu ? "Не удалось загрузить экран приключения. Попробуйте открыть его ещё раз."
                : "The adventure screen could not load. Open it again to retry.");
        }
        if (resumeAdventureOnHub && AtHome() && !sceneWaiting && Box.Get()?.IsInStateWithButtons() == true
            && DialogManager.Get()?.ShowingDialog() == false && !NativeProfileSelector.BlocksHome
            && NativeResultsUI.CanNavigate(false) && !SceneMgr.Get().IsTransitionNowOrPending())
        {
            ReturnToAdventure();
            if (sceneWaiting) resumeAdventureOnHub = false;
        }
        var tray = AtAdventurePicker ? DeckPickerTrayDisplay.Get() : null;
        var button = Field(tray, "m_playButton") as PlayButton;
        if (!ReferenceEquals(adventureButton, button) || !ReferenceEquals(adventureTray, tray))
            BindAdventurePicker(tray, button);
        if (!adventureOpening && !AtAdventure && !ClientBridge.OwnsAdventureMatch)
        {
            if (adventureProfile != null)
            {
                Options.SetFormatType(LocalDeckFormat);
                Options.SetInRankedPlayMode(!NativeUIProjection.CasualPlayMode);
            }
            adventureProfile = null;
        }
        if (!AtAdventurePicker && adventureDeckEligibility.Count != 0) adventureDeckEligibility.Clear();
        if (AtAdventureRewardList) PresentAdventureReward();
        else adventureRewardVisit.Leave();
    }

    private static void BindAdventurePicker(DeckPickerTrayDisplay tray, PlayButton button)
    {
        int generation = ++adventurePickerGeneration;
        if (adventureButton != null && adventureClick != null)
            adventureButton.RemoveEventListener(UIEventType.RELEASE, adventureClick);
        adventureButton = button;
        adventureTray = tray;
        adventureClick = null;
        if (button == null) return;
        adventureClick = _ => StartAdventureSelection(tray, generation);
        tray.OverridePlayButtonCallback(adventureClick);
    }

    private static bool AllowOtherFindGame(int __2) => !IsolatedLaunch || (__2 != FirstBoss
        && !(SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.ADVENTURE) && !ClientBridge.OwnsAdventureMatch);

    private static bool AdventureFindGame(GameKind __0, Format __1, int __2, long __4, int __12)
    {
        if (AllowOtherFindGame(__2)) return true;
        if (AtAdventurePicker && __0 == GameKind.GT_VS_AI && __1 == Format.FT_WILD && __2 == FirstBoss
            && adventureTray != null && adventureTray == DeckPickerTrayDisplay.Get()
            && AdventureMenuFlow.MatchesRequest(__4, __12, adventureTray.GetSelectedDeckID(), adventureTray.GetSelectedDeckTemplateID()))
            StartAdventureSelection(adventureTray, adventurePickerGeneration);
        return false;
    }

    private static void StartAdventureSelection(DeckPickerTrayDisplay tray, int generation)
    {
        if (!AtAdventurePicker || sceneWaiting || SceneMgr.Get().IsTransitionNowOrPending()
            || tray == null || tray != adventureTray || tray != DeckPickerTrayDisplay.Get() || !tray.gameObject.activeInHierarchy
            || !AdventureMenuFlow.EnterPickerCallback(generation, adventurePickerGeneration, ClientBridge.OwnsAdventureMatch, ref startingAdventure))
            return;
        try
        {
            var config = AdventureConfig.Get();
            if (config.GetSelectedAdventure() != AdventureDbId.NAXXRAMAS || config.GetSelectedMode() != AdventureModeDbId.LINEAR
                || (int)config.GetMissionToPlay() != FirstBoss || !AdventureConfig.IsMissionAvailable(FirstBoss))
                throw new InvalidOperationException(UnsupportedAdventureMission());
            var native = ResolveAdventureDeck(tray);
            var local = ValidateNative(native);
            if (!AdventureContentsMatch(native, CraftingRecipe(native.ID)))
                throw new InvalidOperationException("The selected card versions or sideboard differ from the saved deck.");
            ClientBridge.StartAdventureMatch(Profile, DeckKeyOf(native), local, Profile.GetDraft(native.ID) != null);
        }
        catch (Exception error) { AdventureNotice(error.Message); }
        finally { startingAdventure = false; }
    }

    private static bool AdventureContentsMatch(CollectionDeck native, LocalCustomDeck recipe) =>
        NativeBaseHero(native) == recipe.Hero && SameDeckEntries(DeckEntries(native), recipe.CardEntries)
        && SameSideboardEntries(ReadSideboards(native), recipe.Sideboards.OrderBy(row => row.Source, StringComparer.Ordinal).ToArray());

    private static CollectionDeck ResolveAdventureDeck(DeckPickerTrayDisplay tray)
    {
        var native = tray.GetSelectedCollectionDeck();
        if (native == null) throw new InvalidOperationException("Select a published deck first.");
        if (tray.GetSelectedDeckTemplateID() == 0)
        {
            if (native.ID != tray.GetSelectedDeckID() || !ReferenceEquals(native, CollectionManager.Get().GetDeck(native.ID)))
                throw new InvalidOperationException("The selected deck is no longer available.");
            return native;
        }

        // Retail passes zero as deckId for a template. Only a unique, published starter with the
        // same hero and actual versioned contents may stand in for that template.
        if (native.DeckTemplateId == tray.GetSelectedDeckTemplateID())
        {
            var matches = decks.Where(pair => NativeUIProjection.PlayerLoanerKeys.Contains(pair.Value.Id)
                && !Profile.IsLoanerHidden(pair.Value.Id) && AdventureContentsMatch(native, CraftingRecipe(pair.Key)))
                .Take(2).ToArray();
            if (matches.Length == 1) return CollectionManager.Get().GetDeck(matches[0].Key);
        }
        throw new InvalidOperationException("The selected template does not identify a unique published starter deck.");
    }

    private static bool AtAdventureRewardList => AtAdventure && !sceneWaiting && !SceneMgr.Get().IsTransitionNowOrPending()
        && AdventureConfig.Get()?.CurrentSubScene == SubScene.MISSION_DISPLAY;

    private static void PresentAdventureReward()
    {
        var profile = Profile;
        if (adventureRewardVisit.Receipt != null || profile == null || DialogManager.Get()?.ShowingDialog() != false || PopupDisplayManager.Get()?.IsShowing != false
            || BannerManager.Get()?.IsShowing == true || UniversalInputManager.Get()?.IsDialogActive() == true) return;
        if (Time.realtimeSinceStartup < adventureRewardRetry) return;
        var reward = profile.PendingAdventureReward;
        if (reward == null || !adventureRewardVisit.Begin(reward.Id, out int token)) return;
        try
        {
            RefreshAdventureCollection(reward.CardId);
            var tile = new CardRewardData(reward.CardId, TAG_PREMIUM.NORMAL, reward.Copies);
            tile.LoadRewardObject((actor, _) => ShowLoadedAdventureReward(actor, profile, reward.Id, token));
        }
        catch (Exception error) { AdventureRewardFailed(reward.Id, token, error); }
    }

    private static void ShowLoadedAdventureReward(Reward actor, LocalProfile profile, string id, int token)
    {
        if (!adventureRewardVisit.Current(id, token) || !ReferenceEquals(profile, Profile) || !AtAdventureRewardList)
        {
            if (actor != null) UnityEngine.Object.Destroy(actor.gameObject);
            return;
        }
        try
        {
            if (actor == null) throw new InvalidOperationException("Reward asset did not load.");
            var manager = PopupDisplayManager.Get();
            if (manager?.RewardPopups == null || manager.IsShowing) throw new InvalidOperationException("Reward display is busy.");
            manager.RewardPopups.DisplayLoadedRewardObject(actor, null);
            actor.RegisterClickListener((_, __) => {
                if (!adventureRewardVisit.Current(id, token) || !ReferenceEquals(profile, Profile) || !AtAdventureRewardList) return;
                try { profile.AcknowledgeAdventureReward(id); adventureRewardVisit.Leave(); }
                catch (Exception error) { AdventureRewardFailed(id, token, error); }
            });
        }
        catch (Exception error)
        {
            if (actor != null) UnityEngine.Object.Destroy(actor.gameObject);
            AdventureRewardFailed(id, token, error);
        }
    }

    private static void AdventureRewardFailed(string id, int token, Exception error)
    {
        if (!adventureRewardVisit.Current(id, token)) return;
        adventureRewardVisit.Leave();
        adventureRewardRetry = Time.realtimeSinceStartup + 2f;
        log?.LogWarning("ADVENTURE_REWARD_DISPLAY " + error.GetBaseException().Message);
    }
}
