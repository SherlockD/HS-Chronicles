using System;

internal static class AdventureMenuFlow
{
    internal enum Page { Hub, Modes, Adventures, Card, BossList }

    internal readonly struct View
    {
        internal readonly string Title, Body, Confirm, Cancel;

        internal View(string title, string body, string confirm, string cancel)
        { Title = title; Body = body; Confirm = confirm; Cancel = cancel; }
    }

    internal static Page Start() => Page.Modes;

    internal static bool MatchesRequest(long deck, int template, long selectedDeck, int selectedTemplate) =>
        template == selectedTemplate && (template == 0 ? deck > 0 && deck == selectedDeck : template > 0 && deck == 0);

    internal static bool EnterPickerCallback(int generation, int currentGeneration, bool matchOwned, ref bool starting)
    {
        if (generation != currentGeneration || matchOwned || starting) return false;
        starting = true;
        return true;
    }

    internal sealed class RewardVisit
    {
        private int generation;
        internal string Receipt { get; private set; }
        internal bool Begin(string id, out int token)
        {
            token = generation;
            if (id == null || Receipt != null) return false;
            Receipt = id;
            token = ++generation;
            return true;
        }
        internal bool Current(string id, int token) => Receipt != null && Receipt == id && generation == token;
        internal void Leave() => Receipt = null;
    }

    internal static string ModesLabel(bool russian) => russian ? "Режимы" : "Modes";
    internal static string AdventuresLabel(bool russian) => russian ? "Приключения" : "Adventures";

    internal static View Describe(Page page, string adventureTitle, bool russian, bool wingOwned = false)
    {
        string adventures = AdventuresLabel(russian);
        string back = russian ? "Назад" : "Back";
        string hub = russian ? "В хаб" : "Back to Hub";
        return page switch {
            Page.Modes => new View(ModesLabel(russian), "", adventures, hub),
            Page.Adventures => new View(adventures, "", russian ? "Наксрамас" : "Naxxramas", back),
            Page.Card => new View(adventureTitle, wingOwned ? ""
                : russian ? "В этом профиле Паучий квартал закрыт." : "The Arachnid Quarter is locked in this profile.",
                russian ? "Открыть список боссов" : "View bosses", back),
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };
    }

    internal static Page Next(Page page, bool confirm) => page switch {
        Page.Modes => confirm ? Page.Adventures : Page.Hub,
        Page.Adventures => confirm ? Page.Card : Page.Modes,
        Page.Card => confirm ? Page.BossList : Page.Adventures,
        _ => Page.Hub
    };
}
