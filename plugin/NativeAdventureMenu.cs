using System;
using Hearthstone.Core;

internal static partial class NativeUIProbe
{
    private static int adventureMenuGeneration;

    private static bool RussianAdventureMenu => NativeLanguageOption.Running == "ruRU";
    internal static string HomeModesLabel => AdventureMenuFlow.ModesLabel(RussianAdventureMenu);
    private static string HomeModesTooltip => HomeModesReady ? AdventureMenuFlow.AdventuresLabel(RussianAdventureMenu) : null;

    private static string AdventureMenuTitle
    {
        get
        {
#if SOLOSTONE_PREBUILT_ENGINE
            return null;
#else
            var adventure = SabberStoneCore.Campaigns.CampaignEraCatalog.Installed.FindAdventure("curse-of-naxxramas");
            return RussianAdventureMenu ? adventure?.Title : adventure?.EnglishTitle;
#endif
        }
    }

    internal static bool HomeModesReady => AtHome() && AdventureMenuTitle != null;

    internal static void OpenLocalModes()
    {
        if (!HomeModesReady || Box.Get()?.IsInStateWithButtons() != true
            || DialogManager.Get()?.ShowingDialog() != false || UniversalInputManager.Get()?.IsDialogActive() == true)
            return;

        int generation = adventureMenuGeneration + 1;
        if (ShowAdventurePage(AdventureMenuFlow.Start(), Box.Get(), generation))
            adventureMenuGeneration = generation;
    }

    // The local pages are modals on the HUB. A response closes the current popup before we ask for the next one.
    private static bool ShowAdventurePage(AdventureMenuFlow.Page page, Box owner, int generation)
    {
        try
        {
            if (!HomeModesReady || owner == null || Box.Get() != owner || sceneWaiting
                || SceneMgr.Get().IsTransitionNowOrPending()) return false;
            var view = AdventureMenuFlow.Describe(page, AdventureMenuTitle, RussianAdventureMenu, Profile.AdventureWing.Owned);
            string body = page == AdventureMenuFlow.Page.Card && Profile.AdventureWing.Owned
                ? ClientBridge.AdventureUnavailableReason() ?? view.Body : view.Body;
            bool responded = false;
            return DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
                m_id = "HS_CHRONICLES_ADVENTURES_" + page, m_headerText = view.Title, m_text = body,
                m_showAlertIcon = false, m_iconSet = AlertPopup.PopupInfo.IconSet.None,
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_confirmText = view.Confirm, m_cancelText = view.Cancel, m_keyboardEscIsCancel = true,
                m_responseCallback = (response, _) => {
                    if (responded || generation != adventureMenuGeneration) return;
                    responded = true;
                    if (response != AlertPopup.Response.CONFIRM && response != AlertPopup.Response.CANCEL) return;
                    var next = AdventureMenuFlow.Next(page, response == AlertPopup.Response.CONFIRM);
                    if (next != AdventureMenuFlow.Page.Hub)
                        Processor.ScheduleCallback(0.05f, false, _ => {
                            if (generation != adventureMenuGeneration || !HomeModesReady || owner != Box.Get()) return;
                            if (next == AdventureMenuFlow.Page.BossList) ReturnToAdventure();
                            else ShowAdventurePage(next, owner, generation);
                        });
                }
            }) == true;
        }
        catch (Exception error)
        {
            log?.LogWarning("NATIVE_ADVENTURES popup unavailable: " + error.Message);
            return false;
        }
    }
}
