using System;
using System.Collections.Generic;
using System.Linq;
#if !SOLOSTONE_PREBUILT_ENGINE
using System.Runtime.CompilerServices;
using SabberStoneCore.Campaigns;
#endif
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

internal static class LocalNativeText
{
    // CardTextBuilder.IsAssembledCard and TextUtils.ParseTextForCraftableSplit read component dbfIds.
    internal const int FirstCompositeHalfTag = 4572;
    internal const int SecondCompositeHalfTag = 4573;
    private const string ObsoleteSpellDamageSuffix = " @spelldmg";
    private static readonly string[] AdditionalEngineIds = {
        "GAME_005",
        "NEW1_012o", "EX1_019e", "EX1_603e", "CS2_222o", "CS2_004e", "EX1_561e",
        "EX1_411e", "EX1_414e", "CS1_129e", "CS2_236e", "CS2_087e", "CS2_092e",
        "EX1_360e", "EX1_382e", "EX1_619e", "EX1_366e", "EX1_355e"
    };
    private static class LegacyScope
    {
        internal static readonly Dictionary<string, string> EngineIds = new Dictionary<string, string>(StringComparer.Ordinal);
        internal static readonly Dictionary<string, string> Texts = BuildTextByNativeId(EngineIds);
    }

#if !SOLOSTONE_PREBUILT_ENGINE
    private static readonly ConditionalWeakTable<CampaignCardCatalog, Dictionary<string, EngineCard>> campaignDefinitions =
        new ConditionalWeakTable<CampaignCardCatalog, Dictionary<string, EngineCard>>();
#endif
    private static string clientLocale;

    internal static IReadOnlyCollection<string> SupportedIds => LegacyScope.Texts.Keys;

    internal static bool TryGetNameOverride(bool localContext, string cardId, Func<string, string> transform,
        out string name)
    {
        name = null;
        if (!localContext || cardId != "EX1_084e" || transform == null) return false;
        name = transform(Cards.FromId(cardId).Name);
        return true;
    }

    internal static bool TryGetOverride(bool staged, string cardId, Func<string, string> defaultVariant,
        out string text) => TryGetOverride(staged, cardId, defaultVariant, ClientLocale(), out text);

#if !SOLOSTONE_PREBUILT_ENGINE
    internal static bool TryGetCampaignNameOverride(CampaignCardCatalog catalog, bool localContext, string cardId,
        Func<string, string> transform, out string name)
    {
        name = null;
        if (!localContext || transform == null) return false;
        var card = CampaignDefinition(catalog, cardId);
        if (string.IsNullOrEmpty(card?.Name)) return false;
        name = transform(card.Name);
        return true;
    }

    internal static bool TryGetOverride(CampaignCardCatalog catalog, bool staged, string cardId,
        Func<string, string> defaultVariant, out string text) =>
        TryGetOverride(catalog, staged, cardId, defaultVariant, ClientLocale(), out text);

    internal static bool TryGetOverride(CampaignCardCatalog catalog, bool staged, string cardId,
        Func<string, string> defaultVariant, string locale, out string text)
    {
        text = null;
        if (!staged || defaultVariant == null) return false;
        try
        {
            var definition = CampaignDefinition(catalog, cardId);
            if (definition == null || UsesClientBuilder(definition)) return false;
            return TryRender(RawForDefinition(catalog.Id, definition, locale, out _), defaultVariant, out text);
        }
        catch
        {
            text = null;
            return false;
        }
    }

    internal static string RawFor(CampaignCardCatalog catalog, string cardId, string locale, out LocalizedCardText.Status status)
    {
        status = LocalizedCardText.Status.English;
        var definition = CampaignDefinition(catalog, cardId);
        return definition == null ? null : RawForDefinition(catalog.Id, definition, locale, out status);
    }

    private static string RawForDefinition(string catalogId, EngineCard definition, string locale, out LocalizedCardText.Status status)
    {
        status = LocalizedCardText.Lookup(catalogId, locale, definition.Id, definition.Text, out string translated);
        return status == LocalizedCardText.Status.Localized ? translated : definition.Text;
    }

    private static EngineCard CampaignDefinition(CampaignCardCatalog catalog, string nativeId)
    {
        if (catalog == null || string.IsNullOrEmpty(nativeId)) return null;
        // A text request carries only the native ID. Use the client default when multiple cards share that ID.
        var definitions = campaignDefinitions.GetValue(catalog, current => current.All
            .GroupBy(card => NativeMatch.TransportCardId(card.Id), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal));
        return definitions.TryGetValue(nativeId, out var card) ? card : null;
    }
#endif

    // The client's language, as the client reports it (Localization.GetActualLocale); the Options > Language choice when the client
    // cannot answer. Null for English and for anything that is not one of the client's locales. Remembered once it is settled:
    // never while the client says English and Options started it in another language (it can answer before its language is set up).
    internal static string ClientLocale() => ClientLocale(ReadClientLocale, RunningLocale);

    // Options > Language's running locale. NativeLanguageOption.Initialize sets it; this file stays free of client UI types, so
    // the client-free test suites compile it. While unset, the answer is never remembered.
    internal static Func<string> RunningLocale;

    internal static string ClientLocale(Func<string> client, Func<string> running)
    {
        string known = clientLocale;
        if (known != null) return known.Length == 0 ? null : known;
        string reported = null, started = null;
        try { reported = client(); } catch (Exception) { reported = null; }
        try { started = running?.Invoke(); } catch (Exception) { started = null; }
        string locale = LocalizedCardText.Resolve(reported, started, out bool cacheable);
        if (cacheable && running != null) clientLocale = locale ?? "";
        return locale;
    }

    private static System.Reflection.MethodInfo actualLocale;
    private static bool actualLocaleLooked;

    private static string ReadClientLocale()
    {
        if (!actualLocaleLooked)
        {
            // Looked up once, found or not.
            actualLocaleLooked = true;
            try
            {
                var localization = Type.GetType("Localization, Blizzard.T5.Localization", false);
                actualLocale = localization?.GetMethod("GetActualLocale", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
            }
            catch (Exception) { actualLocale = null; }
        }
        return actualLocale?.Invoke(null, null)?.ToString();
    }

    internal static void ResetLocaleCache() { clientLocale = null; }

    // The engine id a native (transport) id stands for, or null when SoloStone has no text for it.
    internal static string EngineIdFor(string nativeId) =>
        nativeId != null && LegacyScope.EngineIds.TryGetValue(nativeId, out string engineId) ? engineId : null;

    // Raw text before the variant pick: the locale's own text for this card while the engine still plays the English it was made
    // from, else the engine's English. `status` says which and why.
    internal static string RawFor(string cardId, string locale) => RawFor(cardId, locale, out _);

    internal static string RawFor(string cardId, string locale, out LocalizedCardText.Status status)
    {
        status = LocalizedCardText.Status.English;
        if (string.IsNullOrEmpty(cardId) || !LegacyScope.Texts.TryGetValue(cardId, out string english)) return null;
#if SOLOSTONE_PREBUILT_ENGINE
        if (locale != null && LegacyScope.EngineIds.TryGetValue(cardId, out string engineId))
        {
            status = LocalizedCardText.Lookup("violet-hold-2026-09", locale, engineId, english, out string localized);
            if (status == LocalizedCardText.Status.Localized) return localized;
        }
#endif
        return english;
    }

    // A modular builder's own text is only its two part slots ("{0} {1}", "{0}\n{1}"); counters like Jade's "{0}/{1}" keep text.
    private static bool IsBuilderTemplate(string text) => text != null
        && System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^\{0\}\s+\{1\}$");

    internal static bool TryGetOverride(bool staged, string cardId, Func<string, string> defaultVariant,
        string locale, out string text)
    {
        text = null;
        if (!staged || string.IsNullOrEmpty(cardId) || defaultVariant == null) return false;
        try
        {
            // Native modular builders compose text; base templates lack parts until assembly.
            string engineId = EngineIdFor(cardId);
            var definition = engineId == null ? null : Cards.FromId(engineId);
            if (UsesClientBuilder(definition)) return false;
            return TryRender(RawFor(cardId, locale), defaultVariant, out text);
        }
        catch
        {
            text = null;
            return false;
        }
    }

    private static bool UsesClientBuilder(EngineCard definition) => definition != null &&
        (definition.Tags.ContainsKey(SabberStoneCore.Enums.GameTag.MODULAR_ENTITY_PART_1)
        || definition.Tags.ContainsKey(SabberStoneCore.Enums.GameTag.MODULAR_ENTITY_PART_2)
        || IsBuilderTemplate(definition.Text));

    private static bool TryRender(string rawText, Func<string, string> defaultVariant, out string text)
    {
        text = string.IsNullOrEmpty(rawText) ? null : NormalizeRawText(defaultVariant(rawText));
        return !string.IsNullOrEmpty(text);
    }

    internal static string NormalizeRawText(string text) =>
        text != null && text.EndsWith(ObsoleteSpellDamageSuffix, StringComparison.Ordinal)
            ? text.Substring(0, text.Length - ObsoleteSpellDamageSuffix.Length)
            : text;

    private static Dictionary<string, string> BuildTextByNativeId(Dictionary<string, string> engineIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Cards shown through another native identity keep the client's text.
        var keepNativeText = new HashSet<string>(
            LocalCardCatalog.QuarantinedIds.Concat(new[] { "EX1_614t", "EX1_finkle" }), StringComparer.Ordinal);

        void Add(EngineCard card)
        {
            if (card == null) return;
            string native = NativeMatch.TransportCardId(card.Id);
            result[native] = card.Text;
            engineIds[native] = card.Id;
        }

        try
        {
            foreach (var card in LocalEconomy.Catalog) Add(card);
        }
        catch { }
        var heroesAndPowers = LocalDecks.PlayerHeroes.SelectMany(id => new[] {
            id, Cards.FromAssetId(Cards.FromId(id)[SabberStoneCore.Enums.GameTag.HERO_POWER]).Id
        });
        foreach (string id in heroesAndPowers.Concat(AdditionalEngineIds).Concat(LocalDecks.TestStandardCardIds).Concat(LocalCardCatalog.SupplementalGeneratedIds)
                     .Concat(LocalDecks.TestLoaners.SelectMany(deck => deck.CardIds)).Concat(LocalCardCatalog.LegacyGeneratedPublicIds).Concat(LocalCardCatalog.CardStateGeneratedIds))
        {
            try { Add(Cards.FromId(id)); }
            catch { }
        }
        // Every other card the engine plays: tokens, generated cards and enchantments (Kazakus' trial parts, the cracked
        // Eggs of Khelos). A newer client has their post-patch text, but the engine plays the fixed set. Cards the
        // transport renames keep the lists above.
        foreach (EngineCard card in Cards.All)
        {
            try
            {
                if (card.Implemented && !result.ContainsKey(card.Id) && !keepNativeText.Contains(card.Id)
                    && NativeMatch.TransportCardId(card.Id) == card.Id)
                {
                    result[card.Id] = card.Text;
                    engineIds[card.Id] = card.Id;
                }
            }
            catch { }
        }
        return result;
    }
}
