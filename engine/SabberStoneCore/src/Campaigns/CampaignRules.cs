using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

namespace SabberStoneCore.Campaigns
{
    /// <summary>Validated acquisition rules and decks for one version of an era.</summary>
    public sealed class CampaignRules
    {
        private readonly Dictionary<string, Card> _heroes;
        private readonly Dictionary<string, Opponent> _opponents;
        private readonly Dictionary<string, CraftPrice> _craft;
        internal IReadOnlyList<string> PackPool { get; }
        internal int PackSize { get; }
        internal int PackGold { get; }
        internal int StartingGold { get; }
        internal int StartingDust { get; }
        internal int WinGold { get; }
        internal string Hash { get; }

        public CampaignCardCatalog Cards { get; }
        public string EraId { get; }
        public string RulesId { get; }
        public int DeckSize { get; }
        public IReadOnlyDictionary<string, int> Starter { get; }
        public IReadOnlyList<string> HeroIds { get; }
        public IReadOnlyList<string> OpponentIds { get; }

        private CampaignRules(CampaignEra era, CampaignCardCatalog cards, XElement root)
        {
            Cards = cards;
            EraId = era.Id;
            RulesId = era.Content.RulesId;
            if (root == null || root.Name != "CampaignRules" || (string)root.Attribute("schemaVersion") != "1" ||
                (string)root.Attribute("id") != RulesId || root.Elements().Count() != 4)
                throw Invalid("Invalid rules for " + EraId);

            using (var sha = SHA256.Create())
                Hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting))));
            DeckSize = Number(root, "deckSize", 5, 30);
            var heroes = Section(root, "Heroes", era.Content.HeroesId);
            _heroes = new Dictionary<string, Card>(StringComparer.Ordinal);
            foreach (var row in Children(heroes, "Hero"))
            {
                string id = Required(row, "id");
                var card = Cards.FromId(id);
                if (card?.Type != CardType.HERO || !Enum.IsDefined(typeof(CardClass), card.Class) ||
                    card.Class == CardClass.NEUTRAL || (int)card.Class == 0 || !_heroes.TryAdd(id, card))
                    throw Invalid("Invalid or repeated hero " + id);
            }
            if (_heroes.Count == 0) throw Invalid("The rules require at least one hero.");
            HeroIds = Array.AsReadOnly(_heroes.Keys.ToArray());

            var starter = ReadCardCounts(Section(root, "Collection", era.Content.CollectionId));
            Starter = new ReadOnlyDictionary<string, int>(starter);
            if (FindDeck(Starter).HeroId == null) throw Invalid("The starter collection cannot build a legal deck.");

            var economy = Section(root, "Economy", era.Content.EconomyId);
            if (economy.Elements().Any(row => row.Name != "Pack" && row.Name != "Craft"))
                throw Invalid("Unknown economy entry.");
            StartingGold = Number(economy, "startingGold", 0);
            StartingDust = Number(economy, "startingDust", 0);
            WinGold = Number(economy, "winGold", 0);
            var packs = economy.Elements("Pack").ToArray();
            if (packs.Length != 1) throw Invalid("The rules require exactly one pack pool.");
            PackGold = Number(packs[0], "gold", 1);
            PackSize = Number(packs[0], "size", 1, 30);
            if (StartingGold < PackGold && WinGold == 0) throw Invalid("The rules give the player no way to afford a pack.");
            var packPool = Children(packs[0], "Card").Select(row => Required(row, "id")).ToArray();
            if (packPool.Length == 0 || packPool.Distinct(StringComparer.Ordinal).Count() != packPool.Length ||
                packPool.Any(id => !IsDeckCard(Cards.FromId(id))))
                throw Invalid("The pack pool contains missing, uncollectible or repeated cards.");
            PackPool = Array.AsReadOnly(packPool);

            _craft = new Dictionary<string, CraftPrice>(StringComparer.Ordinal);
            foreach (var row in economy.Elements("Craft"))
            {
                string id = Required(row, "id");
                int cost = Number(row, "dust", 1);
                int refund = Number(row, "refund", 0, cost - 1);
                if (!IsDeckCard(Cards.FromId(id)) || row.HasElements || !_craft.TryAdd(id, new CraftPrice(cost, refund)))
                    throw Invalid("Invalid or repeated craft rule for " + id);
            }

            if (_craft.Count == 0 || !(_craft.Values.Any(price => price.Cost <= StartingDust) ||
                PackPool.Any(id => _craft.TryGetValue(id, out var price) && price.Refund > 0)))
                throw Invalid("The rules give the player no way to afford a craft.");

            _opponents = new Dictionary<string, Opponent>(StringComparer.Ordinal);
            foreach (var row in Children(Section(root, "Opponents", era.Content.OpponentsId), "Opponent"))
            {
                string id = Required(row, "id");
                var deck = ReadCardCounts(row);
                string hero = Required(row, "hero");
                List<Card> cardsInDeck;
                try { cardsInDeck = ResolveDeck(hero, deck); }
                catch (InvalidOperationException error) { throw new InvalidDataException("Invalid deck for opponent " + id, error); }
                if (!_opponents.TryAdd(id, new Opponent(Required(row, "name"), _heroes[hero], cardsInDeck)))
                    throw Invalid("Repeated opponent " + id);
            }
            if (_opponents.Count == 0) throw Invalid("The rules require at least one opponent.");
            OpponentIds = Array.AsReadOnly(_opponents.Keys.ToArray());
        }

        public static CampaignRules Read(CampaignEra era, CampaignCardCatalog cards, Stream stream)
        {
            if (era == null) throw new ArgumentNullException(nameof(era));
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (era.Content == null || era.Content.CardPoolId != cards.Id)
                throw Invalid("The era has no matching card catalog.");
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 2 * 1024 * 1024 }))
                return new CampaignRules(era, cards, XDocument.Load(reader).Root);
        }

        internal (string HeroId, IReadOnlyDictionary<string, int> Cards) FindDeck(IReadOnlyDictionary<string, int> collection)
        {
            foreach (string heroId in HeroIds)
            {
                var hero = _heroes[heroId];
                var deck = new Dictionary<string, int>(StringComparer.Ordinal);
                int remaining = DeckSize;
                foreach (var row in collection)
                {
                    var card = Cards.FromId(row.Key);
                    if (!CardClassRules.IsAllowedInDeck(card, hero.Class)) continue;
                    int copies = Math.Min(remaining, Math.Min(row.Value, DeckCopyLimit(card)));
                    deck.Add(card.Id, copies);
                    remaining -= copies;
                    if (remaining == 0)
                        return (heroId, new ReadOnlyDictionary<string, int>(deck));
                }
            }
            return (null, null);
        }

        internal bool CanOwn(string id) => IsDeckCard(Cards.FromId(id));
        internal Card Hero(string id) => id != null && _heroes.TryGetValue(id, out var hero)
            ? hero : throw new InvalidOperationException("Hero is unavailable in this era: " + id);
        internal Opponent OpponentById(string id) => id != null && _opponents.TryGetValue(id, out var opponent)
            ? opponent : throw new InvalidOperationException("Opponent is unavailable in this era: " + id);
        internal CraftPrice Price(string id) => id != null && _craft.TryGetValue(id, out var price)
            ? price : throw new InvalidOperationException("Crafting is unavailable for " + id);

        internal List<Card> ResolveDeck(string heroId, IReadOnlyDictionary<string, int> rows)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            Card hero = Hero(heroId);
            var deck = new List<Card>(DeckSize);
            foreach (var row in rows)
            {
                var card = Cards.FromId(row.Key);
                if (!IsDeckCard(card) || !CardClassRules.IsAllowedInDeck(card, hero.Class) ||
                    row.Value <= 0 || row.Value > DeckCopyLimit(card) || row.Value > DeckSize - deck.Count)
                    throw new InvalidOperationException("Card or copy count is illegal for this deck: " + row.Key);
                for (int copy = 0; copy < row.Value; copy++) deck.Add(card);
            }
            if (deck.Count != DeckSize) throw new InvalidOperationException("The deck must have exactly " + DeckSize + " cards.");
            return deck;
        }

        private static bool IsDeckCard(Card card) => card?.Collectible == true &&
            (card.Type == CardType.MINION || card.Type == CardType.SPELL || card.Type == CardType.WEAPON);

        private static int DeckCopyLimit(Card card) => card.Rarity == Rarity.LEGENDARY ? 1 : 2;

        private Dictionary<string, int> ReadCardCounts(XElement element)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in Children(element, "Card"))
            {
                string id = Required(row, "id");
                if (!CanOwn(id) || !counts.TryAdd(id, Number(row, "count", 1, 30)))
                    throw Invalid("Invalid or repeated card " + id);
            }
            return counts;
        }

        private static XElement Section(XElement root, string name, string expectedId)
        {
            var rows = root.Elements(name).ToArray();
            if (rows.Length != 1 || (string)rows[0].Attribute("id") != expectedId)
                throw Invalid("Missing or repeated rules section " + name + " for " + expectedId);
            return rows[0];
        }

        private static IEnumerable<XElement> Children(XElement root, string name)
        {
            if (root.Elements().Any(row => row.Name != name)) throw Invalid("Expected only " + name + " entries in " + root.Name);
            return root.Elements(name);
        }

        internal static string Required(XElement row, string name)
        {
            string value = (string)row.Attribute(name);
            return string.IsNullOrWhiteSpace(value) ? throw Invalid("Missing " + name + " on " + row.Name) : value;
        }

        internal static int Number(XElement row, string name, int min, int max = int.MaxValue)
        {
            if (!int.TryParse(Required(row, name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
                throw Invalid("Invalid " + name + " on " + row.Name);
            return value;
        }

        private static InvalidDataException Invalid(string message) => new InvalidDataException(message);

        internal sealed class Opponent
        {
            internal string Name { get; }
            internal Card Hero { get; }
            internal IReadOnlyList<Card> Deck { get; }

            internal Opponent(string name, Card hero, List<Card> deck)
            {
                Name = name;
                Hero = hero;
                Deck = deck.AsReadOnly();
            }
        }

        internal readonly struct CraftPrice
        {
            internal int Cost { get; }
            internal int Refund { get; }

            internal CraftPrice(int cost, int refund)
            {
                Cost = cost;
                Refund = refund;
            }
        }
    }
}
