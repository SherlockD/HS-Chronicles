using System.Runtime.Serialization;
using System.Text;
using SabberStoneCore.Campaigns;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

const string summon = "fixture_summon";
const string token = "fixture_runner";
const string baseCard = "fixture_sentinel";
const string copy = "fixture_copy";
const string transform = "fixture_transform";

Check(Cards.Count == 0 && !Cards.All.Any() && !Cards.AllStandard.Any(), "engine initializes without embedded current cards");
Check(!CampaignCardCatalog.IsInstalled("fixture-not-installed"), "catalog availability checks do not load missing resources");
Reject<InvalidDataException>(() => CampaignCardCatalog.Load("fixture-not-installed"), "missing declared catalog");

var early = Read("fixture-early", 1, 2);
var later = Read("fixture-later", 3, 5);
var earlyConfig = Config(early, later);
var earlyGame = new Game(earlyConfig);
earlyConfig.CampaignCards = later;
var laterGame = new Game(Config(later, early));
earlyGame.StartGame();
laterGame.StartGame();

Check(earlyGame.CampaignCards == early && laterGame.CampaignCards == later, "each match pins its configured definitions");
Check(earlyGame.Player1.Hero.HeroPower.Card == early.FromId("fixture_power"), "hero power resolves in the match catalog");
Check(earlyGame.Player2.HandZone.Any(card => card.Card.Id == "fixture_credit"), "startup resource resolves in the match catalog");
Check(earlyGame.Player1.HandZone.First(card => card.Card.Id == summon).Cost == 1, "earlier match uses earlier cost");
Check(laterGame.Player1.HandZone.First(card => card.Card.Id == summon).Cost == 3, "later match uses later cost");
Check(earlyGame.CardByAssetId(90005) == earlyGame.CardById(baseCard), "ID and dbf resolve to same catalog definition");
Check(early.FromId(token).Taunt && later.FromId(token).Taunt, "engine applies printed keyword tags");

foreach (var game in new[] { earlyGame, laterGame })
{
    var matchCard = game.CardById(summon);
    var filter = new OfflinePoolFilter(cardIds: new[] { summon, token });
    Check(OfflineCardPools.Candidates(game, CardClass.MAGE, filter, 0).Single() == matchCard,
        "random effects share scoped definitions and exclude noncollectible dependencies");
    Check(Cards.RandomPool(game).All.Contains(matchCard), "legacy random task pool uses the match catalog");
}
Check(earlyGame.OfflinePools.Hash != laterGame.OfflinePools.Hash, "catalog version participates in the snapshot hash");
Check(earlyGame.Process(PlayCardTask.Spell(earlyGame.Player1, earlyGame.Player1.HandZone.First(card => card.Card.Id == summon))),
    "existing summon task executes through the play entry point");
Check(earlyGame.Player1.BoardZone.Single().Card == early.FromId(token), "generated token uses earlier definition");
Check(laterGame.CardById(token).ATK == 5 && laterGame.Player1.BoardZone.IsEmpty,
    "creation in the earlier match does not affect the later match");

var runner = earlyGame.Player1.BoardZone.Single();
Check(earlyGame.Process(PlayCardTask.SpellTarget(earlyGame.Player1,
    earlyGame.Player1.HandZone.First(card => card.Card.Id == copy), runner)), "copy uses the ordinary play path");
Check(earlyGame.Player1.HandZone.Any(card => card.Card == early.FromId(token)), "copy retains the match definition");
Check(earlyGame.Process(PlayCardTask.SpellTarget(earlyGame.Player1,
    earlyGame.Player1.HandZone.First(card => card.Card.Id == transform), runner)), "transform uses the ordinary play path");
Check(earlyGame.Player1.BoardZone.Single().Card == early.FromId(baseCard), "transform uses the match definition");

var clone = earlyGame.CloneExact(history: true);
Check(clone.CampaignCards == early && clone.Player1.BoardZone.Single().Card == early.FromId(baseCard), "clone preserves catalog and card binding");
var snapshot = earlyGame.SaveSnapshot();
var reloaded = Game.LoadSnapshot(snapshot, Config(early, early));
Check(reloaded.CampaignCards == early && reloaded.Player1.BoardZone.Single().Card == early.FromId(baseCard), "snapshot restores the pinned definitions");
Check(snapshot.SequenceEqual(reloaded.SaveSnapshot()), "snapshot round trip preserves bytes");
Reject<SerializationException>(() => Game.LoadSnapshot(snapshot, Config(later, later)), "snapshot rejected against a different catalog");
var revised = Read("fixture-early", 4, 2);
Reject<SerializationException>(() => Game.LoadSnapshot(snapshot, Config(revised, revised)), "snapshot rejected against revised definitions under the same ID");
Reject<InvalidDataException>(() => Read("fixture-bad", 1, 2, "fixture_absent"), "missing token dependency");
Reject<InvalidDataException>(() => Read("fixture-bad", 1, 2, extraTag: ",[217,1]"), "unsupported death trigger");
Reject<InvalidDataException>(() => Read("fixture-bad", 1, 2, extraText: "Unbound rules text."), "unbound rules text");
Reject<InvalidDataException>(() => Read("fixture-bad", 1, 2, summonEffect: "unsupported-effect"), "unknown effect recipe");
var withSideboard = Config(early, early);
withSideboard.Player1Sideboards = new Dictionary<string, string[]> { [summon] = new[] { baseCard } };
Reject<ArgumentException>(() => new Game(withSideboard), "unsupported sideboard selection");
Console.WriteLine("Campaign engine checks passed.");

static GameConfig Config(CampaignCardCatalog catalog, CampaignCardCatalog submitted) => new()
{
    CampaignCards = catalog,
    Player1HeroClass = CardClass.MAGE,
    Player2HeroClass = CardClass.MAGE,
    Player1Deck = new[] { submitted.FromId("fixture_copy"), submitted.FromId("fixture_transform") }
        .Concat(Enumerable.Repeat(submitted.FromId("fixture_summon"), 6)).ToList(),
    Player2Deck = Enumerable.Repeat(submitted.FromId("fixture_sentinel"), 8).ToList(),
    StartPlayer = 1,
    RandomSeed = 17,
    Shuffle = false,
    SkipMulligan = true,
    History = true,
    Logging = false
};

static CampaignCardCatalog Read(string id, int cost, int attack, string dependency = "fixture_runner", string extraTag = "",
    string extraText = "", string summonEffect = "summon")
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes($$"""
        {"schemaVersion":1,"meta":"{{id}}","coin":"fixture_credit","cards":[
          {"id":"fixture_hero","dbf":90001,"name":"Survey Leader","tags":[[202,3],[199,4],[45,30],[380,90002]]},
          {"id":"fixture_power","dbf":90002,"name":"Radio Check","tags":[[202,10],[199,4],[48,2],[2,1]],"effect":"temporary-mana"},
          {"id":"fixture_credit","dbf":90003,"name":"Field Voucher","tags":[[202,5],[199,12],[2,2]],"effect":"temporary-mana"},
          {"id":"fixture_summon","dbf":90004,"name":"Send a Runner","tags":[[202,5],[199,4],[321,1],[48,{{cost}}],[183,1]],"effect":"{{summonEffect}}","entourage":["{{dependency}}"]},
          {"id":"fixture_sentinel","dbf":90005,"name":"Camp Sentinel","text":"{{extraText}}","tags":[[202,4],[199,12],[321,1],[48,2],[47,2],[45,2],[183,1]{{extraTag}}]},
          {"id":"fixture_runner","dbf":90006,"name":"Trail Runner","text":"<b>Taunt</b>","tags":[[202,4],[199,12],[47,{{attack}}],[45,1],[190,1],[183,1]]},
          {"id":"fixture_copy","dbf":90007,"name":"Recruit a Double","tags":[[202,5],[199,4],[321,1],[183,1]],"effect":"copy-target-to-hand","reqs":[[1,0],[11,0]]},
          {"id":"fixture_transform","dbf":90008,"name":"Change Assignments","tags":[[202,5],[199,4],[321,1],[183,1]],"effect":"transform-target","entourage":["fixture_sentinel"],"reqs":[[1,0],[11,0]]}
        ]}
        """));
    return CampaignCardCatalog.Read(id, stream);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

static void Reject<TException>(Action action, string name) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException("Accepted " + name);
}
