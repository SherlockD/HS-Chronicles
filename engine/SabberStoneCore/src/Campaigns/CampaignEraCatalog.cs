using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SabberStoneCore.Campaigns
{
    public sealed class CampaignEra
    {
        public string Id { get; }
        public string Title { get; }
        public DateTime ReleaseMonth { get; }
        public CampaignEraContent Content { get; }
        public IReadOnlyList<CampaignAdventure> Adventures { get; }
        public IReadOnlyList<CampaignEraUpdate> Updates { get; }

        internal CampaignEra(XElement row)
        {
            Id = CampaignEraCatalog.Identifier(row, "id");
            Title = CampaignEraCatalog.Required(row, "title");
            ReleaseMonth = CampaignEraCatalog.Month(row);

            var content = row.Elements("Content").ToArray();
            if (content.Length > 1 || row.Elements().Any(child =>
                child.Name != "Content" && child.Name != "Update" && child.Name != "Adventure"))
                throw new InvalidDataException("Invalid content declaration for era " + Id);
            if (content.Length == 1) Content = new CampaignEraContent(content[0]);

            var adventures = row.Elements("Adventure").Select(adventure => new CampaignAdventure(Id, adventure)).ToArray();
            Adventures = Array.AsReadOnly(adventures);
            var updates = row.Elements("Update").Select(update => new CampaignEraUpdate(update)).ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            DateTime previousMonth = ReleaseMonth;
            foreach (var update in updates)
            {
                if (!ids.Add(update.Id) || update.ReleaseMonth < previousMonth)
                    throw new InvalidDataException("Invalid update order or duplicate update ID in " + Id);
                previousMonth = update.ReleaseMonth;
            }
            if (Content == null && updates.Length > 0)
                throw new InvalidDataException("Campaign era updates require base content in " + Id);
            Updates = Array.AsReadOnly(updates);
        }
    }

    public sealed class CampaignAdventure
    {
        public string Id { get; }
        public string EraId { get; }
        public string Title { get; }
        public string EnglishTitle { get; }
        public IReadOnlyList<CampaignEncounter> Encounters { get; }

        internal CampaignAdventure(string eraId, XElement row)
        {
            Id = CampaignEraCatalog.Identifier(row, "id");
            EraId = eraId;
            Title = CampaignEraCatalog.Required(row, "title");
            string englishTitle = (string)row.Attribute("titleEn");
            EnglishTitle = string.IsNullOrWhiteSpace(englishTitle) ? Title : englishTitle;
            var children = row.Elements().ToArray();
            if (children.Any(child => child.Name != "Encounter"))
                throw new InvalidDataException("Invalid adventure declaration for " + Id);
            var encounters = children.Select(child => new CampaignEncounter(child)).ToArray();
            if (encounters.Select(encounter => encounter.ScenarioId).Distinct().Count() != encounters.Length)
                throw new InvalidDataException("Duplicate encounter in " + Id);
            Encounters = Array.AsReadOnly(encounters);
        }
    }

    public sealed class CampaignEncounter
    {
        public int ScenarioId { get; }
        public int DefinitionVersion { get; }
        public int BoardId { get; }
        public string HeroId { get; }
        public string HeroPowerId { get; }
        public IReadOnlyDictionary<string, int> Deck { get; }
        public IReadOnlyList<string> Dependencies { get; }

        internal CampaignEncounter(XElement row)
        {
            ScenarioId = Positive(row, "scenario");
            DefinitionVersion = Positive(row, "version");
            BoardId = Positive(row, "board");
            HeroId = CampaignEraCatalog.Required(row, "hero");
            HeroPowerId = CampaignEraCatalog.Required(row, "power");
            var deck = new Dictionary<string, int>(StringComparer.Ordinal);
            var dependencies = new List<string>();
            foreach (var child in row.Elements())
            {
                string id = CampaignEraCatalog.Required(child, "id");
                if (child.HasElements) throw new InvalidDataException("Invalid encounter entry " + id);
                if (child.Name == "Card" && deck.TryAdd(id, Positive(child, "count"))) continue;
                if (child.Name == "Requires" && !dependencies.Contains(id)) { dependencies.Add(id); continue; }
                throw new InvalidDataException("Unknown or repeated encounter entry " + id);
            }
            Deck = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(deck);
            Dependencies = dependencies.AsReadOnly();
        }

        private static int Positive(XElement row, string name)
        {
            if (int.TryParse((string)row.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0)
                return value;
            throw new InvalidDataException("Invalid encounter " + name);
        }
    }

    public sealed class CampaignEraUpdate
    {
        public string Id { get; }
        public DateTime ReleaseMonth { get; }
        public CampaignEraContent Content { get; }

        internal CampaignEraUpdate(XElement row)
        {
            Id = CampaignEraCatalog.Identifier(row, "id");
            ReleaseMonth = CampaignEraCatalog.Month(row);
            var children = row.Elements().ToArray();
            if (children.Length != 1 || children[0].Name != "Content")
                throw new InvalidDataException("Invalid content declaration for campaign update " + Id);

            Content = new CampaignEraContent(children[0]);
        }
    }

    public sealed class CampaignEraContent
    {
        public string RulesId { get; }
        public string CardPoolId { get; }
        public string CollectionId { get; }
        public string EconomyId { get; }
        public string HeroesId { get; }
        public string OpponentsId { get; }
        public IReadOnlyList<string> AdventureIds { get; }

        internal CampaignEraContent(XElement row)
        {
            RulesId = CampaignEraCatalog.Identifier(row, "rules");
            CardPoolId = CampaignEraCatalog.Identifier(row, "cards");
            CollectionId = CampaignEraCatalog.Identifier(row, "collection");
            EconomyId = CampaignEraCatalog.Identifier(row, "economy");
            HeroesId = CampaignEraCatalog.Identifier(row, "heroes");
            OpponentsId = CampaignEraCatalog.Identifier(row, "opponents");

            var items = row.Elements().ToArray();
            if (items.Any(item => item.Name != "Adventure" || item.HasElements))
                throw new InvalidDataException("Invalid campaign adventure declaration.");

            var ids = items.Select(item => CampaignEraCatalog.Identifier(item, "id")).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new InvalidDataException("Duplicate campaign adventure ID.");
            AdventureIds = Array.AsReadOnly(ids);
        }
    }

    public sealed class CampaignEraCatalog
    {
        public const string FirstEraId = "classic-2014";
        private const string ResourceName = "SabberStoneCore.Resources.CampaignEras.xml";
        private static readonly Regex IdPattern = new Regex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z");
        private static readonly Lazy<CampaignEraCatalog> Embedded = new Lazy<CampaignEraCatalog>(() =>
        {
            using (var stream = typeof(CampaignEraCatalog).Assembly.GetManifestResourceStream(ResourceName))
                return Read(stream ?? throw new InvalidDataException("Missing campaign era catalog."));
        });

        public static CampaignEraCatalog Installed => Embedded.Value;
        public IReadOnlyList<CampaignEra> Eras { get; }
        private readonly Dictionary<string, int> positions = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, CampaignAdventure> adventures = new Dictionary<string, CampaignAdventure>(StringComparer.Ordinal);

        private CampaignEraCatalog(CampaignEra[] eras)
        {
            if (eras.Length == 0 || eras[0].Id != FirstEraId)
                throw new InvalidDataException("Campaign era catalog must start with " + FirstEraId);
            for (int i = 0; i < eras.Length; i++)
            {
                var era = eras[i];
                if (!positions.TryAdd(era.Id, i))
                    throw new InvalidDataException("Duplicate campaign era " + era.Id);
                if (i > 0 && era.ReleaseMonth < eras[i - 1].ReleaseMonth)
                    throw new InvalidDataException("Campaign era is out of chronological order: " + era.Id);
                foreach (var adventure in era.Adventures)
                    if (!adventures.TryAdd(adventure.Id, adventure))
                        throw new InvalidDataException("Duplicate campaign adventure " + adventure.Id);
            }
            foreach (var era in eras)
            {
                ValidateAdventureReferences(era.Id, era.Content);
                foreach (var update in era.Updates)
                    ValidateAdventureReferences(era.Id, update.Content);
            }
            Eras = Array.AsReadOnly(eras);
        }

        private void ValidateAdventureReferences(string eraId, CampaignEraContent content)
        {
            if (content == null) return;
            foreach (string id in content.AdventureIds)
                if (FindAdventure(id)?.EraId != eraId)
                    throw new InvalidDataException("Adventure " + id + " is not declared for era " + eraId);
        }

        public CampaignEra Find(string id) =>
            id != null && positions.TryGetValue(id, out int index) ? Eras[index] : null;

        public CampaignAdventure FindAdventure(string id) =>
            id != null && adventures.TryGetValue(id, out var adventure) ? adventure : null;

        public CampaignEra Next(string id) =>
            id != null && positions.TryGetValue(id, out int index) && index + 1 < Eras.Count ? Eras[index + 1] : null;

        public static CampaignEraCatalog Read(Stream stream)
        {
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            {
                var root = XDocument.Load(reader).Root;
                if (root == null || root.Name != "CampaignEras" || (string)root.Attribute("schemaVersion") != "1"
                    || root.Elements().Any(row => row.Name != "Era"))
                    throw new InvalidDataException("Invalid campaign era catalog schema.");

                return new CampaignEraCatalog(root.Elements().Select(row => new CampaignEra(row)).ToArray());
            }
        }

        internal static string Identifier(XElement row, string name)
        {
            var id = Required(row, name);
            if (!IdPattern.IsMatch(id)) throw new InvalidDataException("Invalid campaign identifier: " + id);
            return id;
        }

        internal static DateTime Month(XElement row)
        {
            var value = Required(row, "month");
            if (!DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime month))
                throw new InvalidDataException("Invalid campaign release month: " + value);
            return month;
        }

        internal static string Required(XElement row, string name)
        {
            var value = (string)row.Attribute(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing campaign " + name);
            return value;
        }
    }
}
