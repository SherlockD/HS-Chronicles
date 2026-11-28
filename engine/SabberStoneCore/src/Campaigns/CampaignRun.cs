using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

namespace SabberStoneCore.Campaigns
{
    /// <summary>Local profile progress and match entry points for an era with a validated rules bundle.</summary>
    public sealed class CampaignRun
    {
        private readonly object _gate = new object();
        private readonly int _seed;
        private Dictionary<string, int> _owned;
        private readonly Queue<string[]> _packs = new Queue<string[]>();
        private int _gold;
        private int _dust;
        private int _packSequence;
        private Game _match;

        public CampaignRules Rules { get; }
        public int Gold { get { lock (_gate) return _gold; } }
        public int Dust { get { lock (_gate) return _dust; } }
        public int UnopenedPacks { get { lock (_gate) return _packs.Count; } }

        public IReadOnlyDictionary<string, int> Collection
        {
            get
            {
                lock (_gate) return new ReadOnlyDictionary<string, int>(
                    new Dictionary<string, int>(_owned, StringComparer.Ordinal));
            }
        }

        public CampaignRun(CampaignRules rules, int seed)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            if (seed < 0) throw new ArgumentOutOfRangeException(nameof(seed));
            _seed = seed;
            _gold = rules.StartingGold;
            _dust = rules.StartingDust;
            _owned = new Dictionary<string, int>(rules.Starter, StringComparer.Ordinal);
        }

        public void BuyPack()
        {
            lock (_gate)
            {
                if (_gold < Rules.PackGold) throw new InvalidOperationException("Not enough gold for a pack.");
                int next = checked(_packSequence + 1);
                var random = new Util.DeepCloneableRandom(((long)_seed << 32) | (uint)next);
                var cards = Enumerable.Range(0, Rules.PackSize)
                    .Select(_ => Rules.PackPool[random.Next(Rules.PackPool.Count)]).ToArray();
                _packs.Enqueue(cards);
                _packSequence = next;
                _gold -= Rules.PackGold;
            }
        }

        public IReadOnlyList<string> OpenPack()
        {
            lock (_gate)
            {
                if (_packs.Count == 0) throw new InvalidOperationException("No unopened packs.");
                var cards = _packs.Peek();
                var updated = new Dictionary<string, int>(_owned, StringComparer.Ordinal);
                foreach (string id in cards) updated[id] = checked(Count(updated, id) + 1);
                _owned = updated;
                _packs.Dequeue();
                return Array.AsReadOnly(cards);
            }
        }

        public void Craft(string id)
        {
            lock (_gate)
            {
                var price = Rules.Price(id);
                if (_dust < price.Cost) throw new InvalidOperationException("Not enough dust.");
                _owned[id] = checked(Count(_owned, id) + 1);
                _dust -= price.Cost;
            }
        }

        public void Disenchant(string id)
        {
            lock (_gate)
            {
                var price = Rules.Price(id);
                int count = Count(_owned, id);
                if (count == 0) throw new InvalidOperationException("The profile does not own this card.");
                int dust = checked(_dust + price.Refund);
                var updated = new Dictionary<string, int>(_owned, StringComparer.Ordinal);
                if (count == 1) updated.Remove(id);
                else updated[id] = count - 1;
                if (Rules.FindDeck(updated).HeroId == null)
                    throw new InvalidOperationException("Keep enough cards to build a legal deck before disenchanting this card.");
                _owned = updated;
                _dust = dust;
            }
        }

        public (string HeroId, IReadOnlyDictionary<string, int> Cards) DefaultDeck()
        {
            lock (_gate) return Rules.FindDeck(_owned);
        }

        public Game StartMatch(string heroId, IReadOnlyDictionary<string, int> deck, string opponentId, int seed)
        {
            lock (_gate)
            {
                if (_match != null) throw new InvalidOperationException("Settle the current match first.");
                var cards = Rules.ResolveDeck(heroId, deck);
                if (cards.GroupBy(card => card.Id).Any(group => group.Count() > Count(_owned, group.Key)))
                    throw new InvalidOperationException("The deck contains cards the profile does not own.");
                var hero = Rules.Hero(heroId);
                var opponent = Rules.OpponentById(opponentId);
                var game = new Game(new GameConfig
                {
                    CampaignCards = Rules.Cards,
                    Player1HeroCard = hero,
                    Player1HeroClass = hero.Class,
                    Player1Deck = cards,
                    Player2Name = opponent.Name,
                    Player2HeroCard = opponent.Hero,
                    Player2HeroClass = opponent.Hero.Class,
                    Player2Deck = opponent.Deck.ToList(),
                    RandomSeed = seed,
                    Logging = false
                });
                game.StartGame();
                _match = game;
                return game;
            }
        }

        public bool StepOpponent()
        {
            lock (_gate)
            {
                if (_match == null || _match.State != State.RUNNING || _match.CurrentPlayer != _match.Player2)
                    return false;
                var options = _match.Player2.Options();
                var action = options.FirstOrDefault(task => task.PlayerTaskType == PlayerTaskType.MINION_ATTACK &&
                    task.ActionTarget == _match.Player1.Hero)
                    ?? options.FirstOrDefault(task => task.PlayerTaskType == PlayerTaskType.MINION_ATTACK ||
                        task.PlayerTaskType == PlayerTaskType.HERO_ATTACK)
                    ?? options.FirstOrDefault(task => task.PlayerTaskType == PlayerTaskType.PLAY_CARD)
                    ?? options.FirstOrDefault(task => task.PlayerTaskType == PlayerTaskType.HERO_POWER)
                    ?? options.FirstOrDefault(task => task.PlayerTaskType == PlayerTaskType.CHOOSE ||
                        task.PlayerTaskType == PlayerTaskType.END_TURN);
                return action != null && _match.Process(action);
            }
        }

        public int SettleMatch()
        {
            lock (_gate)
            {
                if (_match?.State != State.COMPLETE) throw new InvalidOperationException("No completed match to settle.");
                int award = _match.Player1.PlayState == PlayState.WON ? Math.Min(Rules.WinGold, int.MaxValue - _gold) : 0;
                _gold += award;
                _match = null;
                return award;
            }
        }

        public void Write(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            lock (_gate)
            {
                if (_match != null) throw new InvalidOperationException("Settle the current match before saving progress.");
                new XDocument(new XElement("CampaignRun", new XAttribute("schemaVersion", 1),
                    new XAttribute("era", Rules.EraId), new XAttribute("rules", Rules.RulesId),
                    new XAttribute("rulesHash", Rules.Hash),
                    new XAttribute("catalog", Rules.Cards.Pools.Hash), new XAttribute("seed", _seed),
                    new XAttribute("gold", _gold), new XAttribute("dust", _dust), new XAttribute("packSequence", _packSequence),
                    new XElement("Collection", _owned.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row =>
                        new XElement("Card", new XAttribute("id", row.Key), new XAttribute("count", row.Value)))),
                    new XElement("Packs", _packs.Select(pack =>
                        new XElement("Pack", pack.Select(id => new XElement("Card", new XAttribute("id", id)))))))).Save(stream);
            }
        }

        public static CampaignRun Read(CampaignRules rules, Stream stream)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            XElement root;
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 8 * 1024 * 1024 }))
                root = XDocument.Load(reader).Root;
            if (root == null || root.Name != "CampaignRun" || (string)root.Attribute("schemaVersion") != "1" ||
                (string)root.Attribute("era") != rules.EraId || (string)root.Attribute("rules") != rules.RulesId ||
                (string)root.Attribute("rulesHash") != rules.Hash || (string)root.Attribute("catalog") != rules.Cards.Pools.Hash ||
                root.Elements().Count() != 2 ||
                root.Elements("Collection").Count() != 1 || root.Elements("Packs").Count() != 1)
                throw new InvalidDataException("The profile is incompatible with the selected era.");

            var run = new CampaignRun(rules, CampaignRules.Number(root, "seed", 0))
            {
                _gold = CampaignRules.Number(root, "gold", 0),
                _dust = CampaignRules.Number(root, "dust", 0),
                _packSequence = CampaignRules.Number(root, "packSequence", 0)
            };
            run._owned.Clear();
            foreach (var row in root.Element("Collection").Elements())
            {
                string id = CampaignRules.Required(row, "id");
                if (row.Name != "Card" || row.HasElements || !rules.CanOwn(id) ||
                    !run._owned.TryAdd(id, CampaignRules.Number(row, "count", 1)))
                    throw new InvalidDataException("Invalid collection entry " + id);
            }
            if (rules.FindDeck(run._owned).HeroId == null) throw new InvalidDataException("Profile cannot build a legal deck.");
            foreach (var pack in root.Element("Packs").Elements())
            {
                if (pack.Name != "Pack" || pack.Elements().Count() != rules.PackSize ||
                    pack.Elements().Any(row => row.Name != "Card" || row.HasElements))
                    throw new InvalidDataException("Invalid unopened pack.");
                var ids = pack.Elements("Card").Select(row => CampaignRules.Required(row, "id")).ToArray();
                if (ids.Any(id => !rules.PackPool.Contains(id))) throw new InvalidDataException("Unopened pack has an invalid card.");
                run._packs.Enqueue(ids);
            }
            if (run._packs.Count > run._packSequence) throw new InvalidDataException("Invalid pack sequence.");
            return run;
        }

        private static int Count(IReadOnlyDictionary<string, int> counts, string id) =>
            counts.TryGetValue(id, out int count) ? count : 0;
    }
}
