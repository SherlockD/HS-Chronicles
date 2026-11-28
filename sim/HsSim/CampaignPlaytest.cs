#if SOURCE_ENGINE
using SabberStoneCore.Campaigns;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

namespace HsSim;

internal static class CampaignPlaytest
{
    internal static int Run(string[] args)
    {
        if (args.Length != 5)
            throw new ArgumentException("campaign <era-id> <eras.xml> <cards.json> <rules.xml> <save.xml>");

        using var eraFile = File.OpenRead(args[1]);
        var era = CampaignEraCatalog.Read(eraFile).Find(args[0]);
        if (era?.Content == null) throw new InvalidDataException("This era has no declared campaign content.");
        using var cardsFile = File.OpenRead(args[2]);
        var cards = CampaignCardCatalog.Read(era.Content.CardPoolId, cardsFile);
        using var rulesFile = File.OpenRead(args[3]);
        var rules = CampaignRules.Read(era, cards, rulesFile);

        string save = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(Path.GetDirectoryName(save));
        using var saveLock = new FileStream(save + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CampaignRun run;
        if (File.Exists(save))
        {
            using var saveFile = File.OpenRead(save);
            run = CampaignRun.Read(rules, saveFile);
        }
        else
        {
            run = new CampaignRun(rules, Random.Shared.Next());
            Save(run, save);
        }

        Console.WriteLine(era.Title);
        Console.WriteLine("Commands: balance, collection, buy, open, craft <id>, disenchant <id>, duel, quit");
        Console.WriteLine("Custom deck: duel <hero-id> <opponent-id> <card-id>... (one ID per copy)");
        Console.WriteLine("Heroes: " + string.Join(", ", rules.HeroIds) + " | Opponents: " + string.Join(", ", rules.OpponentIds));

        while (Console.ReadLine() is string line)
        {
            var words = Tokens(line);
            try
            {
                switch (words)
                {
                    case ["quit"]: return 0;
                    case []: break;
                    case ["balance"]:
                        Console.WriteLine($"Gold {run.Gold}, dust {run.Dust}, unopened packs {run.UnopenedPacks}");
                        break;
                    case ["collection"]:
                        foreach (var row in run.Collection.OrderBy(row => row.Key, StringComparer.Ordinal))
                            Console.WriteLine($"{row.Key}  {cards.FromId(row.Key).Name}  ×{row.Value}");
                        break;
                    case ["buy"]:
                        run.BuyPack();
                        Save(run, save);
                        Console.WriteLine("Pack bought.");
                        break;
                    case ["open"]:
                        var pack = run.OpenPack();
                        Save(run, save);
                        Console.WriteLine(string.Join(", ", pack.Select(id => cards.FromId(id).Name)));
                        break;
                    case ["craft", var id]:
                        run.Craft(id);
                        Save(run, save);
                        Console.WriteLine("Crafted " + id);
                        break;
                    case ["disenchant", var id]:
                        run.Disenchant(id);
                        Save(run, save);
                        Console.WriteLine("Disenchanted " + id);
                        break;
                    case ["duel"]:
                        var defaultDeck = run.DefaultDeck();
                        if (!Play(run, defaultDeck.HeroId, rules.OpponentIds[0], defaultDeck.Cards, save)) return 0;
                        break;
                    case ["duel", var hero, var opponent, .. var deck] when deck.Length > 0:
                        var counts = deck.GroupBy(id => id, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                        if (!Play(run, hero, opponent, counts, save)) return 0;
                        break;
                    default:
                        Console.Error.WriteLine("Unknown or incomplete campaign command.");
                        break;
                }
            }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine(error.Message);
            }
        }

        return 0;
    }

    private static bool Play(CampaignRun run, string hero, string opponent, IReadOnlyDictionary<string, int> deck, string save)
    {
        Game game = run.StartMatch(hero, deck, opponent, Random.Shared.Next());
        Console.WriteLine("Opponent: " + game.Player2.Name);
        while (game.State == State.RUNNING)
        {
            if (game.CurrentPlayer == game.Player2)
            {
                if (!run.StepOpponent()) throw new InvalidDataException("The opponent could not take a legal action.");
                continue;
            }

            Console.WriteLine($"Turn {game.Turn}  |  mana {game.Player1.RemainingMana}  |  health {game.Player1.Hero.Health}–{game.Player2.Hero.Health}");
            Console.WriteLine("Hand: " + string.Join(", ", game.Player1.HandZone.Select(card => $"{card.Card.Name} ({card.Cost})")));
            Console.WriteLine("Your board: " + DescribeBoard(game.Player1.BoardZone));
            Console.WriteLine("Opponent board: " + DescribeBoard(game.Player2.BoardZone));
            var moves = game.Player1.Options();
            for (int index = 0; index < moves.Count; index++) Console.WriteLine($"{index + 1}. {moves[index].FullPrint()}");

            string line = Console.ReadLine();
            if (line == null || line == "quit") return false;
            if (!int.TryParse(line, out int choice) || choice < 1 || choice > moves.Count)
            {
                Console.Error.WriteLine("Enter a listed move number or quit.");
                continue;
            }
            if (!game.Process(moves[choice - 1])) throw new InvalidDataException("The engine refused a listed move.");
        }

        int award = run.SettleMatch();
        Save(run, save);
        Console.WriteLine($"{game.Player1.PlayState}. Gold earned: {award}.");
        return true;
    }

    private static string DescribeBoard(IEnumerable<SabberStoneCore.Model.Entities.Minion> board) =>
        string.Join(", ", board.Select(minion => $"{minion.Card.Name} {minion.AttackDamage}/{minion.Health}"));

    private static string[] Tokens(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void Save(CampaignRun run, string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                run.Write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
#endif
