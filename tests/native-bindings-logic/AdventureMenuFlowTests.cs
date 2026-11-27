internal static class AdventureMenuFlowTests
{
    internal static void Run()
    {
        const string adventure = "Проклятие Наксрамаса";
        const bool russian = true;
        var modes = AdventureMenuFlow.Start();
        var modesView = AdventureMenuFlow.Describe(modes, adventure, russian);
        Check(modesView.Title == "Режимы" && modesView.Confirm == "Приключения" && modesView.Cancel == "В хаб",
            "Modes links to Adventures and can close to the Hub");

        var list = AdventureMenuFlow.Next(modes, confirm: true);
        var listView = AdventureMenuFlow.Describe(list, adventure, russian);
        Check(listView.Title == "Приключения" && listView.Confirm == "Наксрамас" && listView.Cancel == "Назад",
            "the list offers Naxxramas and Back");

        var card = AdventureMenuFlow.Next(list, confirm: true);
        var cardView = AdventureMenuFlow.Describe(card, adventure, russian);
        Check(cardView.Title == adventure && cardView.Body.Contains("закрыт")
            && cardView.Confirm == "Открыть список боссов" && cardView.Cancel == "Назад",
            "a profile without the wing can browse the boss list");
        Check(AdventureMenuFlow.Describe(card, adventure, russian, wingOwned: true).Body == ""
            && AdventureMenuFlow.Next(card, confirm: true) == AdventureMenuFlow.Page.BossList,
            "an entitled profile opens the stock mission list");
        Check(AdventureMenuFlow.Next(card, confirm: false) == list
            && AdventureMenuFlow.Next(list, confirm: false) == modes
            && AdventureMenuFlow.Next(modes, confirm: false) == AdventureMenuFlow.Page.Hub,
            "Back or Escape walks all the way to the Hub");

        var englishList = AdventureMenuFlow.Describe(list, "Curse of Naxxramas", russian: false);
        Check(englishList.Title == "Adventures" && englishList.Confirm == "Naxxramas" && englishList.Cancel == "Back",
            "the alternate labels fit the same route");
        Check(AdventureMenuFlow.MatchesRequest(100, 0, 100, 0)
            && AdventureMenuFlow.MatchesRequest(0, 9, 100, 9), "both native forms identify the selected deck");
        Check(!AdventureMenuFlow.MatchesRequest(100, 9, 100, 9)
            && !AdventureMenuFlow.MatchesRequest(101, 0, 100, 0)
            && !AdventureMenuFlow.MatchesRequest(0, 0, 0, 0)
            && !AdventureMenuFlow.MatchesRequest(0, 10, 100, 9), "stale or mixed caller arguments cannot launch a deck");
        bool starting = false;
        Check(AdventureMenuFlow.EnterPickerCallback(1, 1, false, ref starting)
            && !AdventureMenuFlow.EnterPickerCallback(1, 1, false, ref starting), "reentrant activation starts once");
        starting = false;
        Check(!AdventureMenuFlow.EnterPickerCallback(1, 1, true, ref starting)
            && !AdventureMenuFlow.EnterPickerCallback(1, 2, false, ref starting)
            && AdventureMenuFlow.EnterPickerCallback(2, 2, false, ref starting),
            "a double click and a callback from a previous screen leave the current match alone");
        var reward = new AdventureMenuFlow.RewardVisit();
        Check(reward.Begin("reward", out int first), "the first visit begins loading its reward");
        Check(!reward.Begin("reward", out _) && reward.Current("reward", first),
            "later updates cannot enqueue the same reward while loading or visible");
        reward.Leave();
        Check(!reward.Current("reward", first) && reward.Begin("reward", out int retry)
            && reward.Current("reward", retry) && !reward.Current("reward", first),
            "a failed load or a new visit can retry, and a stale load or dismissal cannot act on the new visit");
        Console.WriteLine("PASS adventures menu: Modes, Adventures, catalog card, Back and Hub");
    }

    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("Adventure menu: " + description);
    }
}
