using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SabberStoneCore.Enchants;
using SabberStoneCore.Enums;
using SabberStoneCore.Loader;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Snapshots;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.SimpleTasks;

namespace SabberStoneCore.Campaigns
{
    /// <summary>A versioned set of card definitions shared by matches that select this catalog.
    /// As with the engine's other canonical definitions, clone a Card before modifying it.</summary>
    public sealed class CampaignCardCatalog
    {
        private static readonly Regex IdPattern = new Regex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z");
        private static readonly ConcurrentDictionary<string, Lazy<CampaignCardCatalog>> Installed =
            new ConcurrentDictionary<string, Lazy<CampaignCardCatalog>>(StringComparer.Ordinal);
        private static readonly HashSet<GameTag> SupportedTags = new HashSet<GameTag>
        {
            GameTag.CARDTYPE, GameTag.CLASS, GameTag.CARD_SET, GameTag.COLLECTIBLE, GameTag.RARITY,
            GameTag.ELITE, GameTag.CARDRACE, GameTag.FACTION, GameTag.MULTIPLE_CLASSES, GameTag.SPELL_SCHOOL,
            GameTag.COST, GameTag.ATK, GameTag.HEALTH, GameTag.ARMOR, GameTag.DURABILITY, GameTag.HERO_POWER,
            GameTag.TAUNT, GameTag.WINDFURY, GameTag.DIVINE_SHIELD, GameTag.CHARGE, GameTag.STEALTH,
            GameTag.POISONOUS, GameTag.LIFESTEAL, GameTag.RUSH, GameTag.OVERLOAD, GameTag.SPELLPOWER,
            GameTag.CANT_ATTACK, GameTag.CANT_BE_TARGETED_BY_SPELLS, GameTag.CANT_BE_TARGETED_BY_HERO_POWERS,
            GameTag.TAG_SCRIPT_DATA_NUM_1, GameTag.BATTLECRY
        };
        private static readonly Dictionary<string, GameTag> PrintedKeywords = new Dictionary<string, GameTag>(StringComparer.OrdinalIgnoreCase)
        {
            ["Taunt"] = GameTag.TAUNT, ["Windfury"] = GameTag.WINDFURY, ["Divine Shield"] = GameTag.DIVINE_SHIELD,
            ["Charge"] = GameTag.CHARGE, ["Stealth"] = GameTag.STEALTH, ["Poisonous"] = GameTag.POISONOUS,
            ["Lifesteal"] = GameTag.LIFESTEAL, ["Rush"] = GameTag.RUSH
        };
        private readonly Dictionary<string, Card> _byId;
        private readonly Dictionary<int, Card> _byAssetId;
        private readonly string _coinId;
        private readonly Lazy<ContinuationRegistry> _continuations;

        public string Id { get; }
        public IReadOnlyList<Card> All { get; }
        internal OfflinePoolManifest Pools { get; }
        internal ContinuationRegistry Continuations => _continuations.Value;

        private CampaignCardCatalog(CardFacts.Document facts, string hash)
        {
            Id = facts.Meta;
            _coinId = facts.CoinId;
            _byId = new Dictionary<string, Card>(StringComparer.Ordinal);
            _byAssetId = new Dictionary<int, Card>();
            var cards = new List<Card>(facts.Cards.Length);
            foreach (var row in facts.Cards)
            {
                if (string.IsNullOrWhiteSpace(row.Id) || row.Id.StartsWith("$", StringComparison.Ordinal) || row.AssetId <= 0 ||
                    _byId.ContainsKey(row.Id) || _byAssetId.ContainsKey(row.AssetId))
                    throw new InvalidDataException("Invalid or conflicting campaign card identity: " + row.Id);

                var card = CardLoader.Build(row, overlay: true);
                if (card.Type != CardType.HERO && card.Type != CardType.HERO_POWER && card.Type != CardType.MINION &&
                    card.Type != CardType.SPELL && card.Type != CardType.WEAPON && card.Type != CardType.ENCHANTMENT)
                    throw new InvalidDataException("Unsupported campaign card type: " + row.Id);
                card.CampaignCatalog = this;
                _byId.Add(card.Id, card);
                _byAssetId.Add(card.AssetId, card);
                cards.Add(card);
            }
            if (cards.Count == 0) throw new InvalidDataException("Campaign card catalog is empty: " + Id);
            if (Coin?.Type != CardType.SPELL)
                throw new InvalidDataException("Campaign card catalog has no starting coin spell: " + Id);
            if (!cards.Any(card => card.Type == CardType.HERO))
                throw new InvalidDataException("Campaign card catalog has no starting hero: " + Id);
            foreach (var row in facts.Cards)
            {
                var card = _byId[row.Id];
                if (card.Entourage.Any(id => FromId(id) == null))
                    throw new InvalidDataException("Campaign card has an unknown entourage card: " + card.Id);
                if (card.Type == CardType.HERO && FromAssetId(card[GameTag.HERO_POWER])?.Type != CardType.HERO_POWER)
                    throw new InvalidDataException("Campaign hero has an unknown hero power: " + card.Id);
                BindEffect(card, row.EffectId);
            }

            All = cards.AsReadOnly();
            _continuations = new Lazy<ContinuationRegistry>(() => new ContinuationRegistry(All));
            Pools = new OfflinePoolManifest(this, hash);
        }

        private void BindEffect(Card card, string effectId)
        {
            foreach (var tag in card.Tags)
                if (tag.Value != 0 && !SupportedTags.Contains(tag.Key))
                    throw new InvalidDataException("Unsupported campaign rule tag " + tag.Key + " (" + card.Id + ")");

            int amount = card[GameTag.TAG_SCRIPT_DATA_NUM_1];
            bool battlecry = card[GameTag.BATTLECRY] != 0;
            bool battlecryType = card.Type == CardType.MINION || card.Type == CardType.WEAPON;
            if (battlecry && !battlecryType)
                throw new InvalidDataException("Invalid campaign battlecry card: " + card.Id);
            if (effectId == null && (battlecry || card.Type == CardType.SPELL || card.Type == CardType.HERO_POWER ||
                amount != 0 || !HasOnlyKeywordText(card)))
                throw new InvalidDataException("Campaign card requires an effect recipe: " + card.Id);
            if (effectId != null && card.Type != CardType.SPELL && card.Type != CardType.HERO_POWER &&
                !battlecry)
                throw new InvalidDataException("Effect recipe cannot activate for campaign card: " + card.Id);
            ISimpleTask task;
            switch (effectId)
            {
                case null:
                    task = null;
                    break;
                case "summon" when card.Entourage.Length == 1 && FromId(card.Entourage[0]).Type == CardType.MINION:
                    task = new SummonTask(card: FromId(card.Entourage[0]));
                    break;
                case "damage-target" when amount > 0 && card.RequiresTarget:
                    task = new DamageTask(amount, EntityType.TARGET);
                    break;
                case "heal-target" when amount > 0 && card.RequiresTarget:
                    task = new HealTask(amount, EntityType.TARGET);
                    break;
                case "transform-target" when card.RequiresTarget && card.PlayRequirements.ContainsKey(PlayReq.REQ_MINION_TARGET) && card.Entourage.Length == 1 &&
                    FromId(card.Entourage[0]).Type == CardType.MINION:
                    task = new TransformTask(FromId(card.Entourage[0]), EntityType.TARGET);
                    break;
                case "copy-target-to-hand" when card.RequiresTarget && card.PlayRequirements.ContainsKey(PlayReq.REQ_MINION_TARGET):
                    task = new CopyTask(EntityType.TARGET, Zone.HAND);
                    break;
                case "temporary-mana" when amount > 0:
                    task = new TempManaTask(amount);
                    break;
                default:
                    throw new InvalidDataException("Unknown or incomplete campaign effect: " + effectId + " (" + card.Id + ")");
            }
            if (task != null) card.Power = new Power { PowerTask = task };
            card.Implemented = true;
        }

        private static bool HasOnlyKeywordText(Card card)
        {
            var text = card.Text?.Replace("<b>", "").Replace("</b>", "");
            return string.IsNullOrWhiteSpace(text) || text.Split(new[] { '.', ',', '\r', '\n' })
                .Select(keyword => keyword.Trim()).Where(keyword => keyword.Length != 0)
                .All(keyword => PrintedKeywords.TryGetValue(keyword, out var tag) && card[tag] > 0);
        }

        public Card FromId(string id) => id != null && _byId.TryGetValue(id, out var card) ? card : null;
        public Card FromAssetId(int id) => _byAssetId.TryGetValue(id, out var card) ? card : null;
        internal Card Coin => FromId(_coinId);
        internal Card DefaultHero(CardClass heroClass) => All.FirstOrDefault(card => card.Type == CardType.HERO && card.Class == heroClass);

        public static bool IsInstalled(string catalogId) => catalogId != null && IdPattern.IsMatch(catalogId) &&
            typeof(CampaignCardCatalog).Assembly.GetManifestResourceInfo(ResourceName(catalogId)) != null;

        public static CampaignCardCatalog Load(string catalogId)
        {
            ValidateId(catalogId);
            return Installed.GetOrAdd(catalogId, id => new Lazy<CampaignCardCatalog>(() =>
            {
                using (var stream = typeof(CampaignCardCatalog).Assembly.GetManifestResourceStream(ResourceName(id)))
                    return Read(id, stream ?? throw new InvalidDataException("Missing campaign card catalog: " + id));
            })).Value;
        }

        public static CampaignCardCatalog Read(string catalogId, Stream stream)
        {
            ValidateId(catalogId);
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using (var buffer = new MemoryStream())
            using (var sha = SHA256.Create())
            {
                stream.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();
                var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                return new CampaignCardCatalog(CardFacts.Read(bytes, catalogId, campaign: true), hash);
            }
        }

        private static string ResourceName(string id) => CardFacts.ResourcePrefix + id + ".json";

        private static void ValidateId(string id)
        {
            if (id == null || !IdPattern.IsMatch(id)) throw new ArgumentException("Invalid card catalog ID.", nameof(id));
        }
    }
}
