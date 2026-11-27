using System.Text;
using SabberStoneCore.Campaigns;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

internal static class AdventureEncounterProof
{
    private static void Check(bool passed, string message) => Core2026Proof.Check(passed, message);

    // A deliberately invented encounter with a normal class hero and repeated minions.
    private static CampaignAdventure Fixture(string entries, string power = "CS2_034")
    {
        var xml = $@"<CampaignEras schemaVersion='1'><Era id='classic-2014' title='fixture' month='2014-06'>
            <Adventure id='test' title='fixture'><Encounter scenario='42' version='2' board='1' hero='HERO_08' power='{power}'>
            {entries}</Encounter></Adventure></Era></CampaignEras>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return CampaignEraCatalog.Read(stream).FindAdventure("test");
    }

    private static void Reject(CampaignAdventure adventure, string message)
    {
        Check(!AdventureEncounter.TryResolve(adventure, 42, out var encounter, out var reason)
            && encounter == null && reason.Contains(message), "adventure rejects " + message + ": " + reason);
    }

    [GroupProof("adventure-encounter")]
    internal static void Run()
    {
        Check(!AdventureEncounter.TryResolve(273, out var absent, out var reason) && absent == null
            && reason.Contains("30-card") && reason.Contains("NAX1_04") && reason.Contains("NAX1_05")
            && !reason.Contains("NAX1_03"), "bundled encounter identifies missing rules and deck: " + reason);
        Check(!AdventureEncounter.TryResolve(-1, out absent, out reason) && absent == null && reason.Contains("-1"),
            "unregistered scenario");
        const string deck = "<Card id='CS2_182' count='30'/>";
        Reject(Fixture(deck, "CS2_101"), "not bound");
        Reject(Fixture("<Card id='CS2_182' count='2147483647'/>"), "30-card");
        Reject(Fixture("<Card id='MISSING' count='30'/>"), "MISSING");
        Reject(Fixture("<Card id='CS2_034' count='30'/>"), "CS2_034");
        Reject(Fixture("<Card id='NAX1_05' count='30'/>"), "NAX1_05");
        Reject(Fixture(deck + "<Requires id='MISSING_TOKEN'/>"), "MISSING_TOKEN");
        foreach (var invalid in new[] { "<Card id='CS2_182' count='0'/>", deck + deck })
        {
            bool rejected = false;
            try { Fixture(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "campaign catalog rejects invalid count or repeated deck entry");
        }

        Check(AdventureEncounter.TryResolve(Fixture(deck + "<Requires id='CS2_101t'/>"), 42, out var fixture, out reason), "admit fixture: " + reason);
        Check(fixture.ScenarioId == 42 && fixture.DefinitionVersion == 2 && fixture.BoardId == 1
            && fixture.BossHero.Id == "HERO_08" && fixture.BossDeck.Count == 30
            && fixture.BossDeck.All(card => card.Id == "CS2_182") && fixture.DependencyIds.Contains("CS2_101t"), "resolved fixture quantities and identity");
        var game = new Game(new GameConfig {
            RandomSeed = 421, StartPlayer = 2, Shuffle = true, SkipMulligan = true, History = true,
            FormatType = FormatType.FT_WILD, Player1HeroClass = CardClass.PALADIN,
            Player1Deck = Enumerable.Repeat(Cards.FromId("CS2_179"), 30).ToList(),
            Player2HeroCard = fixture.BossHero, Player2HeroClass = fixture.BossHero.Class, Player2Deck = fixture.BossDeck.ToList()
        });
        game.StartGame();
        var boss = game.Player2;
        Check(game.CurrentPlayer == boss && boss.Hero.Card == fixture.BossHero
            && boss.DeckZone.All(card => card.Card.Id == "CS2_182")
            && game.Player1.DeckZone.All(card => card.Card.Id == "CS2_179"), "separate human and opponent deck snapshots");
        boss.BaseMana = 1;
        Check(!boss.Options().Any(task => task.PlayerTaskType == PlayerTaskType.HERO_POWER), "fixture power gated by mana");
        boss.BaseMana = 2;
        var hit = boss.Options().Single(task => task.PlayerTaskType == PlayerTaskType.HERO_POWER && task.Target == game.Player1.Hero);
        Check(game.Process(hit) && boss.UsedMana == 2 && game.Player1.Hero.Damage == 1,
            "legal fixture power changes target state");
        int decisionIndex = 0;
        var budget = BotBudget.ForStars(0).WithoutClock();
        for (int i = 0; i < 600 && game.State == State.RUNNING; i++)
        {
            var legal = game.CurrentPlayer.Options();
            PlayerTask task;
            if (game.CurrentPlayer == boss)
            {
                var decision = BotDriver.Decide(game, boss.PlayerId, DeckStyle.Value,
                    state => PilotDecision.From(BotSearch.Choose(state, budget)), ref decisionIndex, tasks: legal);
                Check(decision.Trace.FallbackReason == null, "boss search failed: " + decision.Trace.FallbackReason);
                task = decision.Task;
            }
            else task = LocalBot.Choose(legal);
            Check(legal.Contains(task) && game.Process(task), "legal synthetic encounter action");
        }
        Check(game.State == State.COMPLETE, "synthetic encounter reaches an outcome");
        Console.WriteLine("PASS adventure encounter admission and synthetic SabberStone match");
    }
}
