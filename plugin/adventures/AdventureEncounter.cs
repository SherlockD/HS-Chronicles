using System;
using System.Collections.Generic;
using System.Linq;
using EngineCard = SabberStoneCore.Model.Card;
#if ENGINE_SOURCE
using SabberStoneCore.Campaigns;
using SabberStoneCore.Model;
using CardType = SabberStoneCore.Enums.CardType;
using EngineTag = SabberStoneCore.Enums.GameTag;
#endif

internal sealed class AdventureEncounter
{
    internal int ScenarioId { get; }
    internal int BoardId { get; }
    internal int DefinitionVersion { get; }
    internal EngineCard BossHero { get; }
    internal IReadOnlyList<EngineCard> BossDeck { get; }
    internal IReadOnlyList<string> DependencyIds { get; }

    private AdventureEncounter(int scenario, int board, int version, EngineCard hero, List<EngineCard> deck, IEnumerable<string> dependencies)
    {
        ScenarioId = scenario;
        BoardId = board;
        DefinitionVersion = version;
        BossHero = hero;
        BossDeck = deck.AsReadOnly();
        DependencyIds = Array.AsReadOnly(dependencies.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    internal static bool TryResolve(int scenarioId, out AdventureEncounter encounter, out string reason)
    {
#if ENGINE_SOURCE
        return TryResolve(CampaignEraCatalog.Installed.FindAdventure("curse-of-naxxramas"), scenarioId, out encounter, out reason);
#else
        encounter = null;
        reason = "Adventure encounters require the source rules engine.";
        return false;
#endif
    }

#if ENGINE_SOURCE
    internal static bool TryResolve(CampaignAdventure adventure, int scenarioId, out AdventureEncounter encounter, out string reason)
    {
        encounter = null;
        CampaignEncounter row = adventure?.Encounters.FirstOrDefault(item => item.ScenarioId == scenarioId);
        if (row == null)
        {
            reason = "The campaign catalog has no encounter for scenario " + scenarioId + ".";
            return false;
        }

        var errors = new List<string>();
        EngineCard hero = Lookup(row.HeroId);
        EngineCard power = hero == null ? null : Cards.FromAssetId(hero[EngineTag.HERO_POWER]);
        if (hero?.Type != CardType.HERO || hero.Health <= 0) errors.Add("invalid boss hero " + row.HeroId);
        if (power?.Type != CardType.HERO_POWER || power.Id != row.HeroPowerId)
            errors.Add("hero power " + row.HeroPowerId + " is not bound to " + row.HeroId);
        else if (!power.Implemented || power.Power?.PowerTask == null)
            errors.Add("missing hero power effect " + power.Id);

        const int deckSize = 30;
        var deck = new List<EngineCard>(deckSize);
        var dependencies = new Queue<string>(row.Dependencies);
        void EnqueueEntourage(EngineCard card)
        {
            if (card?.Entourage != null)
                foreach (var id in card.Entourage) dependencies.Enqueue(id);
        }
        EnqueueEntourage(hero);
        EnqueueEntourage(power);
        foreach (var entry in row.Deck)
        {
            EngineCard card = Lookup(entry.Key);
            if (card == null || !(card.Type is CardType.MINION or CardType.SPELL or CardType.WEAPON or CardType.HERO or CardType.LOCATION)
                || !HasRules(card))
                errors.Add("missing playable card or effect " + entry.Key);
            else if (entry.Value <= deckSize - deck.Count)
                deck.AddRange(Enumerable.Repeat(card, entry.Value));
            EnqueueEntourage(card);
        }
        if (row.Deck.Sum(entry => (long)entry.Value) != deckSize)
            errors.Add("a " + deckSize + "-card boss deck is required");

        var checkedIds = new HashSet<string>(StringComparer.Ordinal);
        while (dependencies.Count > 0)
        {
            var id = dependencies.Dequeue();
            if (!checkedIds.Add(id)) continue;
            EngineCard dependency = Lookup(id);
            // Generated plain minions can be instantiated from their stats without a registered power.
            bool plainToken = dependency?.Type == CardType.MINION && !dependency.Collectible
                && dependency.Health > 0 && string.IsNullOrEmpty(dependency.Text);
            if (dependency == null || (!plainToken && !HasRules(dependency)))
                errors.Add("missing dependency or effect " + id);
            EnqueueEntourage(dependency);
        }
        if (errors.Count != 0)
        {
            reason = string.Join(", ", errors) + ".";
            return false;
        }
        encounter = new AdventureEncounter(row.ScenarioId, row.BoardId, row.DefinitionVersion, hero, deck, checkedIds);
        reason = null;
        return true;
    }

    private static EngineCard Lookup(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try { return Cards.FromId(id); }
        catch (KeyNotFoundException) { return null; }
    }

    private static bool HasRules(EngineCard card)
    {
        if (!card.Implemented) return false;
        if (card.Type == CardType.SPELL)
            return card.Power?.PowerTask != null || card.Power?.Trigger != null || card.Power?.Aura != null;
        if (card.Type == CardType.LOCATION) return card.Power?.LocationActivation?.Task != null;
        return true;
    }
#endif
}
