using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using SabberStoneCore.Campaigns;

// Which save the client opens on launch (ProfileCatalog.Startup). Every case is plain files: no Unity, no LocalProfile lease.
// The rule under test: Decide reads only, never throws, and any save it cannot vouch for becomes the ordinary picker, with the
// save's bytes untouched.
internal static class StartupSaveChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ProfileSlot Add(string legacy, string name, ProfileOptions options = null) => ProfileCatalog.Create(legacy, name, options ?? new ProfileOptions());
    private static string SlotDir(string legacy, string id) => Path.Combine(Path.GetDirectoryName(legacy)!, "profiles", id);
    private static string Profile(string legacy, string id) => Path.Combine(SlotDir(legacy, id), "profile.xml");

    // Every file of every save folder (not the catalog, lock, request or marker files): name -> hash. The folder tree is part of it.
    private static string Saves(string legacy)
    {
        string root = Path.GetDirectoryName(legacy)!;
        var parts = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, path);
            if (name.StartsWith("profiles.xml", StringComparison.Ordinal) || name.EndsWith(".lock", StringComparison.Ordinal)
                || name.StartsWith(ProfileCatalog.Startup.RequestFile, StringComparison.Ordinal) || name.StartsWith(ProfileCatalog.Startup.MarkerFile, StringComparison.Ordinal)) continue;
            parts.Add(name + (Directory.Exists(path) ? "/" : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }
        return string.Join("\n", parts);
    }

    private static StartupChoice Decide(string legacy, string request = null, bool useMarker = true) =>
        ProfileCatalog.Startup.Decide(legacy, request, useMarker ? ProfileCatalog.Startup.ReadMarker(legacy) : null);

    private static void Picker(StartupChoice choice, string reason, string message)
    {
        Check(!choice.IsAuto && choice.Mode == "picker" && choice.Reason == reason, message + ": " + choice.Mode + "/" + choice.Reason);
    }

    internal static void Run(string testRoot)
    {
        string root = Path.Combine(testRoot, "startup-save"); Directory.CreateDirectory(root);
        if (Path.DirectorySeparatorChar == '/' && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        Table(Path.Combine(root, "table"));
        FailuresAreReadOnly(Path.Combine(root, "failures"));
        Requests(Path.Combine(root, "requests"));
        UncleanMarker(Path.Combine(root, "marker"));
        RecordedAfterOpenOnly(Path.Combine(root, "recorded"));
        RealOpenFailureLeavesBytes(Path.Combine(root, "real-open"));
        PendingMatchSave(Path.Combine(root, "pending"));
        MarkerBeforeChecks(Path.Combine(root, "marker-first"));
        EraRoundTrip(Path.Combine(root, "era"));
        EraTransition(Path.Combine(root, "era-transition"));
        HomeSettle();
        Launch(Path.Combine(root, "launch"));
        RelaunchUnavailable(Path.Combine(root, "relaunch"));
        ReconcileAtLaunch(Path.Combine(root, "reconcile"));
        OpenStartupBehaviour(Path.Combine(root, "open"));
        CreateDoesNotRecord(Path.Combine(root, "create"));
        UndeletableRequest(Path.Combine(root, "request-delete"));
        MarkerWriteFailure(Path.Combine(root, "marker-fails"));
        OnlySaveVanished(Path.Combine(root, "only-save"));
        Console.WriteLine("PASS startup save: decision table, read-only failures, tool requests, unfinished-start marker, recorded only after open");
    }

    // The decision table: what each state of the catalog and the last-opened save leads to.
    private static void Table(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        // First ever launch: no files at all.
        Picker(Decide(legacy), "no-saves", "no saves -> picker");
        // Only the pre-catalog root save: nothing is recorded yet, so the player picks once.
        Directory.CreateDirectory(Path.Combine(root, "old")); string oldLegacy = Path.Combine(root, "old", "profile.xml");
        using (var profile = new LocalProfile(oldLegacy, new ProfileOptions())) profile.PersistNewProfile(new ProfileOptions());
        Picker(Decide(oldLegacy), "no-catalog", "root save without a catalog -> picker");
        Check(!File.Exists(Path.Combine(root, "old", "profiles.xml")), "deciding never wrote a catalog");
        var a = Add(legacy, "Save A");
        var one = Decide(legacy);
        Picker(one, "choose-profile", "one recorded profile still requires selection");
        Check(one.TargetId == a.Id && one.TargetPath == null && one.Notice == null, "last selection can be highlighted without opening it");
        // Several profiles also require a choice.
        var b = Add(legacy, "Save B"); var c = Add(legacy, "Save C");
        ProfileCatalog.CommitSelection(legacy, b.Id);
        var several = Decide(legacy);
        Picker(several, "choose-profile", "several profiles still require selection");
        Check(several.TargetId == b.Id, "the last selection can be highlighted");
        ProfileCatalog.CommitSelection(legacy, c.Id);
        Check(Decide(legacy).TargetId == c.Id, "the record follows the last pick");
        // The last-opened save was deleted (soft delete clears the record): nothing recorded, several saves or one.
        Check(ProfileCatalog.Delete(legacy, c.Id, null).Outcome == "deleted", "delete the recorded save");
        Picker(Decide(legacy), "none-recorded", "recorded save deleted, two left -> picker");
        Check(ProfileCatalog.Delete(legacy, b.Id, null).Outcome == "deleted", "delete another");
        var lone = Decide(legacy);
        Picker(lone, "none-recorded", "recorded save deleted, one left -> picker (the client never opens a save the player has not opened)");
        Check(lone.Notice == null, "nothing recorded is not an error: no popup");
        ProfileCatalog.CommitSelection(legacy, a.Id);
        Picker(Decide(legacy), "choose-profile", "picking the survivor records it for the next picker");
        // A listed slot whose folder was moved away behind the catalog's back.
        var gone = Add(legacy, "Save Gone");
        ProfileCatalog.CommitSelection(legacy, gone.Id);
        Directory.Move(SlotDir(legacy, gone.Id), SlotDir(legacy, gone.Id) + "-away");
        var missingFolder = Decide(legacy, gone.Id);
        Picker(missingFolder, "save-missing", "recorded save's folder vanished -> picker");
        Check(missingFolder.TargetName == "Save Gone" && missingFolder.Notice != null && missingFolder.Notice.Contains("Save Gone") && missingFolder.NoticeHeader == "PROFILE UNAVAILABLE",
            "the player is told which save and why the picker came up");
        Check(!Directory.Exists(SlotDir(legacy, gone.Id)), "the missing save was not recreated");
    }

    // Every way the recorded save can be unusable: picker with a reason, no exception, and not one byte of any save changed.
    private static void FailuresAreReadOnly(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var other = Add(legacy, "Other"); var target = Add(legacy, "Target");
        ProfileCatalog.CommitSelection(legacy, target.Id);
        string profile = Profile(legacy, target.Id), options = Path.Combine(SlotDir(legacy, target.Id), "profile-options.xml");
        byte[] good = File.ReadAllBytes(profile), goodOptions = File.ReadAllBytes(options);
        Check(Decide(legacy, target.Id).IsAuto, "explicit request opens the profile");

        void Case(string name, string reason, Action damage, Action restore)
        {
            damage();
            string before = Saves(legacy);
            StartupChoice choice = null;
            try { choice = Decide(legacy, target.Id); } catch (Exception error) { throw new Exception("Decide threw for " + name + ": " + error.Message); }
            Picker(choice, reason, name);
            Check(choice.TargetName == "Target" && choice.Notice != null && choice.Notice.Contains("Target") && choice.Notice.Contains("nothing was changed")
                && choice.Notice.Contains("only checked, not opened") && choice.Notice.Contains("log"), name + ": notice names the save and says it was only checked");
            Check(Saves(legacy) == before, name + ": the check changed save bytes or the folder tree");
            restore();
            Check(Decide(legacy, target.Id).IsAuto, name + ": a restored profile opens again");
        }
        void Put(string path, byte[] bytes) { File.Delete(path); File.WriteAllBytes(path, bytes); }
        var serializer = new XmlSerializer(typeof(LocalProfileData));
        LocalProfileData Load() { using var stream = File.OpenRead(profile); return (LocalProfileData)serializer.Deserialize(stream)!; }
        void Save(LocalProfileData data) { using var stream = File.Create(profile); serializer.Serialize(stream, data); }

        Case("profile.xml deleted", "save-missing", () => File.Delete(profile), () => Put(profile, good));
        Case("profile.xml garbage", "save-unreadable", () => Put(profile, new byte[] { 0, 1, 2, 3, 0xFF, 0xFE }), () => Put(profile, good));
        Case("profile.xml empty", "save-unreadable", () => Put(profile, new byte[0]), () => Put(profile, good));
        Case("profile.xml truncated", "save-unreadable", () => Put(profile, good.Take(good.Length / 2).ToArray()), () => Put(profile, good));
        Case("profile.xml not XML of this kind", "save-unreadable", () => Put(profile, System.Text.Encoding.UTF8.GetBytes("<Other><Version>15</Version></Other>")), () => Put(profile, good));
        Case("profile.xml oversized", "save-unreadable", () => Put(profile, new byte[34 * 1024 * 1024]), () => Put(profile, good));
        Case("old unsupported version", "save-incompatible", () => { var data = Load(); data.Version = 9; Save(data); }, () => Put(profile, good));
        Case("future version", "save-incompatible", () => { var data = Load(); data.Version = 99; Save(data); }, () => Put(profile, good));
        Case("options deleted", "options-missing", () => File.Delete(options), () => Put(options, goodOptions));
        Case("options garbage", "options-unreadable", () => Put(options, System.Text.Encoding.UTF8.GetBytes("not xml")), () => Put(options, goodOptions));
        Case("options from another meta", "options-unreadable", () => Put(options, System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(goodOptions).Replace("violet-hold", "someone-else"))), () => Put(options, goodOptions));
        if (!OperatingSystem.IsWindows())
        {
            string real = profile + ".real";
            Case("profile.xml is a link", "links-rejected", () => { File.Move(profile, real); File.CreateSymbolicLink(profile, real); },
                () => { File.Delete(profile); File.Move(real, profile); });
            string realOptions = options + ".real";
            Case("options is a link", "links-rejected", () => { File.Move(options, realOptions); File.CreateSymbolicLink(options, realOptions); },
                () => { File.Delete(options); File.Move(realOptions, options); });
        }
        // A save from an older version is not a failure: it upgrades through the ordinary open.
        foreach (int version in new[] { 13, 14, 15, 16 })
        {
            var old = Load(); old.Version = version; Save(old);
            Check(Decide(legacy, target.Id).IsAuto, "startup accepts V" + version + " for upgrade");
        }
        Put(profile, good);
        // The other save was never involved.
        Check(File.Exists(Profile(legacy, other.Id)), "the bystander save is intact");

        // A catalog nothing can read is not the startup check's to handle: it fails closed in ResolveActiveProfile as it always did.
        // Decide itself still returns (never throws) with the reason.
        string catalog = ProfileCatalog.CatalogPath(legacy), catalogBackup = catalog + ".keep";
        File.Copy(catalog, catalogBackup, true); File.Copy(catalog + ".bak", catalog + ".bak.keep", true);
        File.WriteAllText(catalog, "garbage"); File.WriteAllText(catalog + ".bak", "garbage");
        StartupChoice broken = null;
        try { broken = Decide(legacy); } catch (Exception error) { throw new Exception("Decide threw on a broken catalog: " + error.Message); }
        Picker(broken, "check-failed", "unreadable catalog -> picker choice, not an exception");
        Check(broken.Failure != null, "the catalog failure is kept for the log");
        File.Copy(catalogBackup, catalog, true); File.Copy(catalog + ".bak.keep", catalog + ".bak", true);
        Picker(Decide(legacy), "choose-profile", "catalog restored");
    }

    // One-shot tool requests: force the picker, force a save, junk. Always consumed.
    private static void Requests(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var a = Add(legacy, "Save A"); var b = Add(legacy, "Save B");
        ProfileCatalog.CommitSelection(legacy, a.Id);
        string request = ProfileCatalog.Startup.RequestPath(legacy);
        Check(ProfileCatalog.Startup.ConsumeRequest(legacy) == null, "no request file -> null");

        File.WriteAllText(request, "picker\n");
        string value = ProfileCatalog.Startup.ConsumeRequest(legacy);
        Check(value == "picker" && !File.Exists(request) && ProfileCatalog.Startup.ConsumeRequest(legacy) == null, "a request is read once and consumed");
        var forced = Decide(legacy, value);
        Picker(forced, "requested-picker", "picker request");
        Check(forced.Request == "picker" && forced.Notice == null, "a forced picker is silent");

        File.WriteAllText(request, b.Id);
        value = ProfileCatalog.Startup.ConsumeRequest(legacy);
        var save = Decide(legacy, value);
        Check(save.IsAuto && save.TargetId == b.Id && save.Reason == "requested-save" && save.Request == b.Id, "a save request opens that save, not the recorded one");
        Check(ProfileCatalog.SelectedId(legacy) == a.Id, "the request itself does not change the record");

        File.WriteAllText(request, Guid.NewGuid().ToString("N"));
        Picker(Decide(legacy, ProfileCatalog.Startup.ConsumeRequest(legacy)), "unknown-save", "a request for a save that is not in the catalog -> picker");
        foreach (string junk in new[] { "hello", "PICKER", "../../x", new string('a', 31), new string('a', 33), "", "legacy\nlegacy" })
        {
            File.WriteAllText(request, junk);
            var result = Decide(legacy, ProfileCatalog.Startup.ConsumeRequest(legacy));
            Picker(result, "request-invalid", "junk request '" + junk + "'");
            Check(!File.Exists(request), "junk request consumed");
        }
        File.WriteAllBytes(request, new byte[4096]);
        Picker(Decide(legacy, ProfileCatalog.Startup.ConsumeRequest(legacy)), "request-invalid", "oversized request");
        Check(!File.Exists(request), "oversized request consumed");

        // A requested save that is damaged gets the same checks as the recorded one.
        File.Delete(Profile(legacy, b.Id));
        var damaged = Decide(legacy, b.Id);
        Picker(damaged, "save-missing", "a requested damaged save -> picker with the reason");
        Check(damaged.Request == b.Id && damaged.TargetName == "Save B", "the request is reported");
        // A request outranks a leftover unfinished-start marker; its save is still checked.
        ProfileCatalog.Startup.Arm(legacy, a.Id, Guid.NewGuid().ToString("D"));
        Check(Decide(legacy, a.Id).IsAuto && Decide(legacy, a.Id).Reason == "requested-save", "a request overrides the marker");
        Picker(Decide(legacy), "unclean-exit", "without a request the marker wins");
    }

    // Lockout answer: a save that dies before Home leaves a marker; the next launch shows the picker; Home clears it.
    private static void UncleanMarker(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var a = Add(legacy, "Save A"); var b = Add(legacy, "Save B");
        ProfileCatalog.CommitSelection(legacy, b.Id);
        string run = Guid.NewGuid().ToString("D"), other = Guid.NewGuid().ToString("D");
        Check(ProfileCatalog.Startup.ReadMarker(legacy) == null, "no marker before opening");
        Picker(Decide(legacy), "choose-profile", "no marker still shows the picker");
        ProfileCatalog.Startup.Arm(legacy, b.Id, run);
        var marker = ProfileCatalog.Startup.ReadMarker(legacy);
        Check(marker != null && marker.SlotId == b.Id && marker.RunId == run, "marker records the slot and the run");
        var unclean = Decide(legacy);
        Picker(unclean, "unclean-exit", "a leftover marker -> picker");
        Check(unclean.TargetName == "Save B" && unclean.Notice != null && unclean.Notice.Contains("Save B") && unclean.NoticeHeader == "CHOOSE A PROFILE",
            "the player is told why, kindly");
        Check(ProfileCatalog.SelectedId(legacy) == b.Id, "the record is not touched: choosing it again still works");
        // Another run never clears it; the picker keeps coming until a save reaches Home.
        Check(!ProfileCatalog.Startup.Confirm(legacy, other) && ProfileCatalog.Startup.ReadMarker(legacy) != null, "only the arming run clears the marker");
        Picker(Decide(legacy), "unclean-exit", "still the picker on the launch after that");
        // Re-armed by the next save that opens (picker pick of another save), then confirmed at Home.
        ProfileCatalog.Startup.Arm(legacy, a.Id, other);
        Check(ProfileCatalog.Startup.ReadMarker(legacy)!.SlotId == a.Id, "a new open replaces the marker");
        Check(ProfileCatalog.Startup.Confirm(legacy, other) && ProfileCatalog.Startup.ReadMarker(legacy) == null, "Home clears it");
        Check(!File.Exists(ProfileCatalog.Startup.MarkerPath(legacy) + ".tmp"), "no temp file left");
        ProfileCatalog.CommitSelection(legacy, b.Id);
        Picker(Decide(legacy), "choose-profile", "after Home the next launch shows the picker");
        // A damaged marker still counts as unfinished (fail toward the picker).
        File.WriteAllText(ProfileCatalog.Startup.MarkerPath(legacy), "\0\0garbage");
        var garbled = ProfileCatalog.Startup.ReadMarker(legacy);
        Check(garbled != null && garbled.SlotId == "unknown", "an unreadable marker is still a marker");
        Picker(Decide(legacy), "unclean-exit", "garbled marker -> picker");
        File.Delete(ProfileCatalog.Startup.MarkerPath(legacy));
        // Invalid arm values are refused.
        Reject(() => ProfileCatalog.Startup.Arm(legacy, "not-an-id", run), "bad slot id");
        Reject(() => ProfileCatalog.Startup.Arm(legacy, a.Id, "run\nslotId=" + b.Id), "newline in run id");
        Check(ProfileCatalog.Startup.ReadMarker(legacy) == null, "refused arms wrote nothing");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection: " + message);
    }

    // The record is the catalog's SelectedId: it moves only through CommitSelection/CompleteRelaunch, which the client calls
    // after the open succeeded (ClientBridge.BindStartupPath), and creating a save records only the first one.
    private static void RecordedAfterOpenOnly(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var a = Add(legacy, "Save A");
        Check(ProfileCatalog.SelectedId(legacy) == a.Id, "the first created save is recorded");
        var b = Add(legacy, "Save B");
        Check(ProfileCatalog.SelectedId(legacy) == a.Id, "creating another save does not change the record until it opens");
        string saves = Saves(legacy);
        Check(Decide(legacy).TargetId == a.Id, "so the picker still highlights the one that was open");
        string catalog = File.ReadAllText(ProfileCatalog.CatalogPath(legacy));
        Decide(legacy); Decide(legacy, "picker"); Decide(legacy, b.Id);
        Check(File.ReadAllText(ProfileCatalog.CatalogPath(legacy)) == catalog && Saves(legacy) == saves, "deciding writes neither the catalog nor any save");
        Check(!catalog.Contains("startup") && !File.ReadAllText(Profile(legacy, a.Id)).Contains("startup"),
            "the record lives in the catalog; no save file mentions it");
        // Durable across a restart: a new process reads it back from disk.
        ProfileCatalog.CommitSelection(legacy, b.Id);
        Check(ProfileCatalog.SelectedId(legacy) == b.Id && Decide(legacy).TargetId == b.Id, "the record is durable");
    }

    // The checks above are read-only by construction. The real open (LocalProfile) is what remains after them, and it fails
    // before it writes: a profile the preflight cannot see is wrong leaves its bytes alone when the constructor refuses it.
    private static void RealOpenFailureLeavesBytes(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var target = Add(legacy, "Target"); ProfileCatalog.CommitSelection(legacy, target.Id);
        string path = Profile(legacy, target.Id);
        var serializer = new XmlSerializer(typeof(LocalProfileData));
        LocalProfileData data;
        using (var stream = File.OpenRead(path)) data = (LocalProfileData)serializer.Deserialize(stream)!;
        data.GrindTier = "no-such-tier";
        using (var stream = File.Create(path)) serializer.Serialize(stream, data);
        string before = Saves(legacy);
        var choice = Decide(legacy, target.Id);
        Check(choice.IsAuto, "the preflight cannot see a content-level problem");
        var options = ProfileCatalog.OptionsForProfile(legacy, path);
        bool refused = false;
        try { using (new LocalProfile(path, options)) { } } catch (Exception) { refused = true; }
        Check(refused, "the real open refuses the damaged save");
        Check(Saves(legacy) == before, "the refused open left every save byte and the folder tree as they were");
        // The client then takes the picker route: the choice becomes a picker with the failure, the record stays, nothing is deleted.
        string notice = choice.FailOpen(new InvalidOperationException("boom"));
        Check(!choice.IsAuto && choice.Reason == "open-failed" && choice.Failure != null && choice.Failure.Contains("boom") && notice.Contains("Target")
            && choice.LogLine().Contains("reason=open-failed") && choice.LogLine().Contains("failure=") && choice.TargetPath == null, "a failed open becomes a logged picker choice");
        Check(notice.Contains("may have changed") && notice.Contains("recovery or an upgrade") && !notice.Contains("not deleted or repaired") && !notice.Contains("nothing was changed"),
            "after the real open started the notice promises no more than opening by hand: " + notice);
        Check(ProfileCatalog.SelectedId(legacy) == target.Id && Saves(legacy) == before, "and changes nothing on disk");
    }

    // A save with an unsettled match opens through the same path as when picked: the choice does not special-case it.
    private static void PendingMatchSave(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var target = Add(legacy, "Target"); ProfileCatalog.CommitSelection(legacy, target.Id);
        string path = Profile(legacy, target.Id);
        var options = ProfileCatalog.OptionsForProfile(legacy, path);
        using (var profile = new LocalProfile(path, options)) profile.Begin("mage", "warrior", 7);
        string before = Saves(legacy);
        var choice = Decide(legacy, target.Id);
        Check(choice.IsAuto && choice.TargetId == target.Id, "a save with a pending match is still auto-opened");
        Check(Saves(legacy) == before, "deciding left the pending match unsettled on disk");
        // The open then does what the picker's open does: RecoverInterrupted settles it.
        using (var profile = new LocalProfile(path, options)) { Check(profile.HasPendingMatch, "pending after open"); profile.RecoverInterrupted(); Check(!profile.HasPendingMatch, "settled by the ordinary recovery"); }
    }

    // Finding: the marker must be down before anything reads the target (preflight, options, the open), so a crash in them leaves it.
    private static void MarkerBeforeChecks(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var target = Add(legacy, "Target"); ProfileCatalog.CommitSelection(legacy, target.Id);
        string run = Guid.NewGuid().ToString("D");
        string seen = null;
        ProfileCatalog.FaultHook = phase => { if (phase.StartsWith("startup-checks:")) seen = ProfileCatalog.Startup.ReadMarker(legacy)?.RunId; };
        try
        {
            var choice = Decide(legacy, target.Id, false);   // no run id: nothing is armed (decision tests)
            Check(choice.IsAuto && seen == null && ProfileCatalog.Startup.ReadMarker(legacy) == null, "without a run id the decision arms nothing");
            seen = null;
            choice = ProfileCatalog.Startup.Decide(legacy, target.Id, null, run);
            Check(choice.IsAuto && seen == run, "the marker is already down when the target is first read");
            Check(ProfileCatalog.Startup.ReadMarker(legacy)?.RunId == run && ProfileCatalog.Startup.ReadMarker(legacy)!.SlotId == target.Id, "and stays down for an auto-open");
            // A picker outcome after arming disarms again: a refused target must not look like an unfinished start.
            ProfileCatalog.Startup.Confirm(legacy, run);
            File.Delete(Profile(legacy, target.Id));
            choice = ProfileCatalog.Startup.Decide(legacy, target.Id, null, run);
            Picker(choice, "save-missing", "damaged target");
            Check(seen == run && ProfileCatalog.Startup.ReadMarker(legacy) == null, "armed before the checks, cleared again when they refuse");
            // An old run's leftover marker (from a failed open) is replaced by the new run's, then cleared with it.
            ProfileCatalog.Startup.Arm(legacy, target.Id, Guid.NewGuid().ToString("D"));
            Picker(ProfileCatalog.Startup.Decide(legacy, null, ProfileCatalog.Startup.ReadMarker(legacy), run), "unclean-exit", "stale marker is honoured, not overwritten");
            Check(ProfileCatalog.Startup.ReadMarker(legacy) != null, "an unclean-exit picker keeps the marker");
        }
        finally { ProfileCatalog.FaultHook = null; }
    }

    private static void EraRoundTrip(string root)
    {
        Directory.CreateDirectory(root);
        string legacy = Path.Combine(root, "profile.xml");
        var first = Add(legacy, "First", new ProfileOptions { NpcDifficulty = NpcDifficultyIds.Easy });
        var other = Add(legacy, "Other");
        string path = Profile(legacy, first.Id);
        var options = ProfileCatalog.OptionsForProfile(legacy, path);
        var serializer = new XmlSerializer(typeof(LocalProfileData));
        LocalProfileData data;
        using (var stream = File.OpenRead(path)) data = (LocalProfileData)serializer.Deserialize(stream)!;
        Check(data.CurrentEraId == ProfileCatalog.MetaId && ProfileCatalog.ReadCurrentEraId(path) == ProfileCatalog.MetaId,
            "new profiles store the starting era");
        int gold = data.GoldBalance;
        data.CurrentEraId = null;
        using (var stream = File.Create(path)) serializer.Serialize(stream, data);
        using (var profile = new LocalProfile(path, options))
            Check(profile.CurrentEraId == ProfileCatalog.MetaId && profile.NpcDifficulty == NpcDifficultyIds.Easy,
                "older profiles get their existing era and keep their chosen difficulty");
        Check(ProfileCatalog.ReadCurrentEraId(path) == ProfileCatalog.MetaId, "picker reads the era of older profiles");

        data.CurrentEraId = "test-era";
        using (var stream = File.Create(path)) serializer.Serialize(stream, data);
        using (var profile = new LocalProfile(path, options))
            Check(profile.CurrentEraId == "test-era" && profile.SetNpcDifficulty(NpcDifficultyIds.Hard), "a normal profile edit uses the stored era");
        using (var stream = File.OpenRead(path)) data = (LocalProfileData)serializer.Deserialize(stream)!;
        Check(data.CurrentEraId == "test-era" && data.GoldBalance == gold && ProfileCatalog.ReadCurrentEraId(path) == "test-era"
            && ProfileCatalog.ProfilePathForId(legacy, first.Id) == path, "reopen keeps the profile id, era and gold after an ordinary write");
        Check(ProfileCatalog.ReadCurrentEraId(Profile(legacy, other.Id)) == ProfileCatalog.MetaId, "the other profile keeps its own era");
    }

    private static void EraTransition(string root)
    {
        const string classic = "classic-2014", naxxramas = "curse-of-naxxramas", gnomes = "goblins-vs-gnomes";
        var installed = CampaignEraCatalog.Installed;
        Check(installed.Eras.Count == 40 && installed.Eras[0].Id == classic
            && installed.Eras[39].Id == "reign-of-the-black-empire", "complete chronological campaign catalog");
        Check(installed.Next(classic).Id == naxxramas && installed.Next(naxxramas).Id == gnomes
            && ProfileCatalog.EraName(classic) == installed.Find(classic).Title, "catalog orders and names epochs");
        var installedAdventure = installed.FindAdventure(naxxramas);
        Check(installedAdventure?.EraId == naxxramas && installedAdventure.Title == installed.Find(naxxramas).Title
            && installedAdventure.EnglishTitle == "Curse of Naxxramas"
            && installed.Find(naxxramas).Adventures.Single() == installedAdventure
            && installed.Eras.Where(era => era.Id != naxxramas).All(era => era.Adventures.Count == 0),
            "Naxxramas is the only installed adventure");
        Check(installed.Find(naxxramas).Content == null && installed.FindAdventure("unknown") == null,
            "declaring an adventure does not install playable era content");

        const string content = "<Content rules='rules-v1' cards='cards-v1' collection='collection-v1' economy='economy-v1' heroes='heroes-v1' opponents='opponents-v1' />";
        const string adventure = "<Adventure id='trial' title='Trial' />";
        string contentWithAdventure = content.Replace("/>", "><Adventure id='trial' /></Content>");
        string first = "<Era id='classic-2014' title='First' month='2014-06'>" + content + "</Era>";
        string update = "<Update id='test-update' month='2014-09'>" + contentWithAdventure.Replace("rules-v1", "rules-v2") + "</Update>";
        const string nextEra = "<Era id='curse-of-naxxramas' title='Next' month='2014-07'>";
        string next = nextEra + adventure + contentWithAdventure + update + "</Era>";
        string later = "<Era id='goblins-vs-gnomes' title='Later' month='2014-12' />";
        var catalog = ReadEraCatalog(first + next + later);
        var catalogWithoutNextContent = ReadEraCatalog(first + nextEra + adventure + "</Era>" + later);
        Check(catalog.Next(classic).Updates.Single().Content.RulesId == "rules-v2"
            && catalog.Next(naxxramas).Id == gnomes, "updates have their own rules without a new era");
        Check(catalog.FindAdventure("trial")?.EraId == naxxramas && catalog.FindAdventure("trial").EnglishTitle == "Trial"
            && catalog.Find(naxxramas).Content.AdventureIds.Single() == "trial"
            && catalog.Find(naxxramas).Updates.Single().Content.AdventureIds.Single() == "trial",
            "era content and updates link to the same adventure declaration");
        foreach (string invalid in new[] { first + first, first + next.Replace("2014-07", "2014-01"),
                     first + next.Replace(" opponents='opponents-v1'", ""), first + next.Replace("2014-09", "2014-05"),
                     first + next.Replace(adventure, adventure + adventure),
                     first.Replace("</Era>", adventure + "</Era>") + next,
                     first + next.Replace(adventure, ""),
                     first.Replace(content, contentWithAdventure) + next,
                     first + next.Replace(update, update.Replace("id='trial'", "id='unknown'")) })
        {
            bool rejected = false;
            try { ReadEraCatalog(invalid); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "reject malformed campaign catalogs");
        }

        string path = Path.Combine(root, "profile.xml");
        long draftId;
        int historyCount;
        using (var profile = new LocalProfile(path))
        {
            profile.CommitChange(save => save.CurrentEraId = classic);
            draftId = profile.CreateDraft("Test campaign deck", "HERO_08").Id;
            Check(profile.UnlockedNextEra(catalog) == null, "start at the bottom of the ladder");
            for (int seed = 0; seed < 180 && !profile.Ladder.LegendReached; seed++)
                SettleRanked(profile, profile.Begin("player-core-mage", "priest", seed));
            historyCount = profile.History.Count;
            Check(profile.UnlockedNextEra(catalog)?.Id == naxxramas, "Legend opens the next era");
            Check(profile.UnlockedNextEra(installed) == null, "a catalog entry alone does not open an era");
            Check(profile.UnlockedNextEra(catalogWithoutNextContent) == null
                && !profile.TryAdvanceEra(catalogWithoutNextContent, classic, naxxramas),
                "declaring an adventure does not permit advancing into an era without playable content");
            Check(File.ReadAllText(path).Contains("<UnlockedEraId>curse-of-naxxramas</UnlockedEraId>"),
                "earned unlock is stored independently of the current era and package readiness");
        }

        using (var profile = new LocalProfile(path))
        {
            Check(profile.CurrentEraId == classic && profile.UnlockedNextEra(catalog)?.Id == naxxramas,
                "reopen permits staying at Legend or continuing");
            Check(!profile.TryAdvanceEra(catalog, classic, gnomes), "cannot skip the next era");
            var pending = profile.Begin("player-core-mage", "priest", 180);
            Check(!profile.TryAdvanceEra(catalog, classic, naxxramas), "cannot move during a match");
            SettleRanked(profile, pending);
            historyCount++;
            var collection = profile.Collection.ToDictionary(row => row.Key, row => row.Value);
            int gold = profile.Gold;
            long dust = profile.DustBalance;
            var before = File.ReadAllBytes(path);
            profile.CommitFailure = checkpoint =>
            {
                if (checkpoint == "replace") throw new IOException("Campaign test failure");
            };
            bool failed = false;
            try { profile.TryAdvanceEra(catalog, classic, naxxramas); }
            catch (IOException) { failed = true; }
            finally { profile.CommitFailure = null; }
            Check(failed && before.SequenceEqual(File.ReadAllBytes(path)) && profile.CurrentEraId == classic
                && profile.Ladder.LegendReached, "failed transition leaves the previous save active");
            LocalProfile.CommitFaultHook = checkpoint =>
            {
                if (checkpoint != "replace") return;
                File.Delete(path);
                throw new IOException("Partial campaign replace");
            };
            try { Check(profile.TryAdvanceEra(catalog, classic, naxxramas), "recovered replacement commits transition"); }
            finally { LocalProfile.CommitFaultHook = null; }
            Check(!profile.TryAdvanceEra(catalog, classic, naxxramas)
                && profile.SetNpcDifficulty(NpcDifficultyIds.Easy), "retry and later saves use the recovered era");
            Check(profile.CurrentEraId == naxxramas && profile.Stars == 0 && profile.Ladder.PlacementGames == 0
                && profile.RankedHistory.Count == 0 && profile.History.Count == historyCount, "reset only the active ladder");
            Check(gold == profile.Gold && dust == profile.DustBalance && collection.Count == profile.Collection.Count
                && collection.All(row => profile.Collection.TryGetValue(row.Key, out int copies) && copies == row.Value),
                "collection and balances survive transition");
        }

        using (var profile = new LocalProfile(path))
        {
            Check(profile.CurrentEraId == naxxramas && ProfileCatalog.ReadCurrentEraId(path) == naxxramas
                && profile.Stars == 0 && profile.History.Count == historyCount
                && profile.GetDraft(draftId)?.Name == "Test campaign deck", "new era, reset and player deck survive reopen");
            string priorMatch = profile.History.Last().Id;
            int currentGold = profile.Gold;
            profile.Settle(priorMatch, "Won", 8, false);
            Check(profile.Gold == currentGold && profile.Stars == 0, "retry of an earlier settlement does not change the new era");
            SettleRanked(profile, profile.Begin("player-core-mage", "priest", 181));
            Check(profile.RankedHistory.Count == 1 && profile.History.Count == historyCount + 1
                && profile.RankedHistory[0].Ladder.StarsBefore == 0, "new ranked receipt starts at Bronze");
        }
        using (var profile = new LocalProfile(path))
            Check(profile.CurrentEraId == naxxramas && profile.RankedHistory.Count == 1, "ladder receipts replay across the transition");

        var serializer = new XmlSerializer(typeof(LocalProfileData));
        LocalProfileData tampered;
        using (var stream = File.OpenRead(path)) tampered = (LocalProfileData)serializer.Deserialize(stream)!;
        tampered.EraTransitions.Clear();
        string invalidPath = Path.Combine(root, "no-transition.xml");
        using (var stream = File.Create(invalidPath)) serializer.Serialize(stream, tampered);
        bool rejectedSave = false;
        try { using var profile = new LocalProfile(invalidPath); }
        catch (InvalidOperationException) { rejectedSave = true; }
        Check(rejectedSave, "a later campaign era requires a recorded transition");

        Console.WriteLine("PASS campaign eras: catalog, persistent unlock, atomic advance and ranked receipt replay");
    }

    private static CampaignEraCatalog ReadEraCatalog(string entries)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<CampaignEras schemaVersion='1'>" + entries + "</CampaignEras>"));
        return CampaignEraCatalog.Read(stream);
    }

    private static void SettleRanked(LocalProfile profile, LocalMatchRecord match)
    {
        profile.Settle(match.Id, "Won", 8, false);
        profile.AcknowledgeRewardPresentation(match.Id);
    }

    private static void HomeSettle()
    {
        var settle = new StartupHomeSettle();
        Check(!settle.Tick(0, false) && !settle.Tick(5, true) && !settle.Tick(14.9, true), "not settled before the hold");
        Check(settle.Tick(15, true), "settled after StartupHomeSettle.HoldSeconds of idle Home");
        var broken = new StartupHomeSettle();
        broken.Tick(0, true); broken.Tick(9, true);
        Check(!broken.Tick(9.5, false), "a presentation or dialog restarts the hold");
        Check(!broken.Tick(12, true) && !broken.Tick(21.9, true) && broken.Tick(22, true), "and it counts from the next idle moment, not from arrival");
        var navigated = new StartupHomeSettle();
        Check(!navigated.Tick(0, true) && navigated.Tick(3, false, leftHome: true), "using Home (Play, collection, packs) after it appeared settles it before the hold");
        var late = new StartupHomeSettle();
        Check(!late.Tick(1000, true) && late.Tick(1010, true), "arrival itself never confirms");
    }

    private static string Run() => Guid.NewGuid().ToString("D");

    // ResolveLaunch is the platform-free body of OfflineGuard.ResolveLaunch's selector branch (which only runs where the path separator is \).
    private static void Launch(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var a = Add(legacy, "Save A"); var b = Add(legacy, "Save B");
        ProfileCatalog.CommitSelection(legacy, b.Id);
        string run = Run();
        var picker = ProfileCatalog.Startup.ResolveLaunch(legacy, run, null, null);
        Check(picker.Selector && picker.Profile == legacy && picker.RelaunchTarget == null && !picker.Startup.IsAuto && picker.Startup.TargetId == b.Id
            && picker.Startup.Reconciled, "ordinary launch starts unbound in the profile picker");
        Check(ProfileCatalog.Startup.ReadMarker(legacy) == null, "opening the picker arms no profile");
        // Tool requests are consumed by the launch.
        File.WriteAllText(ProfileCatalog.Startup.RequestPath(legacy), "picker");
        var forced = ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), null, null);
        Check(forced.Selector && !forced.Startup.IsAuto && forced.Startup.Reason == "requested-picker" && !File.Exists(ProfileCatalog.Startup.RequestPath(legacy))
            && ProfileCatalog.Startup.ReadMarker(legacy) == null, "a picker request shows the picker, is consumed, arms nothing");
        File.WriteAllText(ProfileCatalog.Startup.RequestPath(legacy), a.Id);
        var asked = ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), null, null);
        Check(!asked.Selector && asked.Startup.TargetId == a.Id && asked.Profile == Profile(legacy, a.Id), "a save request auto-opens that save");
        ProfileCatalog.Startup.Confirm(legacy, ProfileCatalog.Startup.ReadMarker(legacy)!.RunId!);
        // Fresh install: no saves.
        string empty = Path.Combine(root, "empty", "profile.xml"); Directory.CreateDirectory(Path.GetDirectoryName(empty)!);
        var none = ProfileCatalog.Startup.ResolveLaunch(empty, Run(), null, null);
        Check(none.Selector && none.Startup.Reason == "no-saves", "first launch ever: the picker");
        // A resolvable relaunch binds its target and decides nothing else.
        string old = Run(), next = Run();
        string token = ProfileCatalog.RequestRelaunch(legacy, old, a.Id);
        var relaunch = ProfileCatalog.Startup.ResolveLaunch(legacy, next, old, token);
        Check(!relaunch.Selector && relaunch.RelaunchTarget == a.Id && relaunch.Profile == Profile(legacy, a.Id) && relaunch.Startup == null
            && ProfileCatalog.Startup.ReadMarker(legacy) == null, "explicit profile relaunch still binds its target");
        ProfileCatalog.CompleteRelaunch(legacy, next, old, token, a.Id);

        old = next; next = Run();
        string before = Saves(legacy);
        token = ProfileCatalog.RequestRelaunch(legacy, old, ProfileCatalog.PickerRelaunchTarget);
        var switched = ProfileCatalog.Startup.ResolveLaunch(legacy, next, old, token);
        Check(switched.Selector && switched.Profile == legacy && switched.RelaunchTarget == null && switched.Startup.Reason == "requested-picker",
            "Change Profile relaunch opens the startup picker unbound");
        Check(!File.Exists(ProfileCatalog.SwitchPath(legacy)) && ProfileCatalog.SelectedId(legacy) == a.Id
            && ProfileCatalog.Startup.ReadMarker(legacy) == null, "picker relaunch consumes the request, leaving the last selected profile recorded");
        Check(Saves(legacy) == before, "consuming the switch request does not change profile files");
        Reject(() => ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), old, token), "picker relaunch token cannot be reused");
    }

    // Finding: Change Profile to B with B damaged before the relaunch must not silently reopen A.
    private static void RelaunchUnavailable(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var a = Add(legacy, "Save A"); var b = Add(legacy, "Save B");
        ProfileCatalog.CommitSelection(legacy, a.Id);
        string old = Run(), next = Run();
        string token = ProfileCatalog.RequestRelaunch(legacy, old, b.Id);
        File.WriteAllText(Path.Combine(SlotDir(legacy, b.Id), "profile-options.xml"), "damaged after the request");
        string before = Saves(legacy);
        var result = ProfileCatalog.Startup.ResolveLaunch(legacy, next, old, token);
        Check(result.Selector && !result.Startup.IsAuto && result.Startup.Reason == "relaunch-unavailable" && result.RelaunchTarget == null
            && result.Startup.Notice == ProfileCatalog.StartupNotice.RelaunchUnavailable && result.Startup.NoticeHeader == "PROFILE UNAVAILABLE",
            "an unusable relaunch target shows the picker with a notice: " + result.Startup.Reason);
        Check(ProfileCatalog.SelectedId(legacy) == a.Id && ProfileCatalog.Startup.ReadMarker(legacy) == null, "A was not opened (no marker) and stays recorded");
        Check(Saves(legacy) == before, "no save byte changed");
        // Malformed selectors keep failing as before (never a silent auto-open).
        Reject(() => ProfileCatalog.Startup.ResolveLaunch(legacy, next, "not-a-guid", "x"), "malformed relaunch selectors");
    }

    // Finding: the picker's Reconcile (retries move-deferred folders, drops vanished slots) now runs at launch, before the choice.
    private static void ReconcileAtLaunch(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var keep = Add(legacy, "Keep"); var gone = Add(legacy, "Gone"); var victim = Add(legacy, "Victim");
        ProfileCatalog.CommitSelection(legacy, gone.Id);
        // 1. The recorded save's folder vanished: dropped with an honest notice, picker, nothing else touched.
        Directory.Delete(SlotDir(legacy, gone.Id), true);
        string keepBefore = File.ReadAllText(Profile(legacy, keep.Id));
        var dropped = ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), null, null);
        Check(dropped.Selector && dropped.Startup.Reason == "save-missing" && dropped.Startup.TargetName == "Gone" && dropped.Startup.Notice!.Contains("taken off the list")
            && dropped.Startup.Notice.Contains("Nothing else was changed") && !dropped.Startup.Notice.Contains("repaired")
            && ProfileCatalog.SelectedId(legacy) == null && ProfileCatalog.List(legacy).All(row => row.Id != gone.Id)
            && dropped.Startup.LogLines.Any(line => line.StartsWith("PROFILE_RECONCILE dropped=" + gone.Id)), "vanished last save: dropped, told so, logged");
        Check(File.ReadAllText(Profile(legacy, keep.Id)) == keepBefore, "healthy saves untouched");
        // 2. A move-deferred delete leaves an unlisted folder; the next launch moves it without the picker being needed.
        ProfileCatalog.CommitSelection(legacy, keep.Id);
        ProfileCatalog.FaultHook = phase => { if (phase.StartsWith("before-delete-move:")) throw new IOException("locked"); };
        try { Check(ProfileCatalog.Delete(legacy, victim.Id, null).Outcome == "move-deferred", "deferred delete"); } finally { ProfileCatalog.FaultHook = null; }
        Check(Directory.Exists(SlotDir(legacy, victim.Id)), "the folder lingers after a deferred delete");
        var launch = ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), null, null);
        Check(launch.Selector && !launch.Startup.IsAuto && launch.Startup.TargetId == keep.Id && !Directory.Exists(SlotDir(legacy, victim.Id))
            && launch.Startup.LogLines.Any(line => line.Contains("moved=" + victim.Id)) && ProfileCatalog.DeletedFolders(legacy).Any(name => name.StartsWith(victim.Id)),
            "an ordinary picker launch retries the deferred move and logs it");
    }

    // Finding: seeding, requireExisting and the open itself, run for real (SelectedProfile.cs is compiled into this suite).
    private static void OpenStartupBehaviour(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var target = Add(legacy, "Target"); ProfileCatalog.CommitSelection(legacy, target.Id);
        string path = Profile(legacy, target.Id), fixture = Path.Combine(root, "fixture.xml");
        File.WriteAllText(fixture, "fixture");
        File.WriteAllText(ProfileCatalog.Startup.RequestPath(legacy), target.Id);
        var auto = ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), null, null);
        var launch = new OfflineLaunchContext(Run(), null, auto.Profile, 0, false, legacy, auto.Selector, null, null, null, null, auto.Startup);
        Check(launch.AutoOpenTargetId == target.Id && !launch.ShowProfileSelector, "the context carries the auto target");
        // A vanished auto-open target: no fixture seed, and the open refuses instead of creating a welcome-gift save.
        File.Delete(path);
        OfflineGuard.SeedProfileIfMissing(launch, fixture);
        Check(!File.Exists(path), "an auto-opened save is never fixture-seeded");
        bool refused = false;
        try { SelectedProfile.OpenStartup(launch, path, true); } catch (FileNotFoundException) { refused = true; }
        Check(refused && !File.Exists(path) && !File.Exists(path + ".tmp"), "requireExisting: a missing file fails the open and creates nothing");
        // Control: a fixed-profile launch (lanes) still seeds and still creates, as before.
        string laneRoot = Path.Combine(root, "lane"); Directory.CreateDirectory(laneRoot);
        var fixedLaunch = new OfflineLaunchContext(Run(), null, Path.Combine(laneRoot, "profile.xml"), 1);
        OfflineGuard.SeedProfileIfMissing(fixedLaunch, fixture);
        Check(File.Exists(Path.Combine(laneRoot, "profile.xml")), "fixed lane launches still seed");
        // An existing save with a pending match: the picked-save open settles it exactly once (the same call an auto-open makes).
        var options = ProfileCatalog.OptionsForProfile(legacy, path);
        File.Delete(path);
        var again = Add(legacy, "Pending"); string pendingPath = Profile(legacy, again.Id);
        var pendingOptions = ProfileCatalog.OptionsForProfile(legacy, pendingPath);
        using (var profile = new LocalProfile(pendingPath, pendingOptions)) profile.Begin("mage", "warrior", 7);
        var pendingLaunch = new OfflineLaunchContext(Run(), null, pendingPath, 0, false, legacy, false, null, null, null, null,
            new StartupChoice { Mode = "auto", TargetId = again.Id, TargetPath = pendingPath, Reason = "requested-save" });
        using (var opened = SelectedProfile.OpenStartup(pendingLaunch, pendingPath, true))
            Check(!opened.HasPendingMatch && opened.History.Any(match => match.Result == "Abandoned"), "the open settles a pending match as an Abandoned loss, same as picked");
    }

    private static void CreateDoesNotRecord(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var first = ProfileCatalog.Create(legacy, "First", new ProfileOptions(), false);
        Check(ProfileCatalog.SelectedId(legacy) == null && ProfileCatalog.List(legacy).Count == 1, "Create(record: false) leaves nothing recorded until the save opens");
        Picker(Decide(legacy), "none-recorded", "so a save that never opened is not auto-opened next launch");
        ProfileCatalog.CommitSelection(legacy, first.Id);
        Picker(Decide(legacy), "choose-profile", "recording after the open still requires the next launch to show the picker");
    }

    private static void UndeletableRequest(string root)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var a = Add(legacy, "Save A");
        string request = ProfileCatalog.Startup.RequestPath(legacy);
        File.WriteAllText(request, a.Id);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            string value = ProfileCatalog.Startup.ConsumeRequest(legacy);
            Check(value == ProfileCatalog.Startup.RequestInvalid, "a request that cannot be removed is not obeyed (it would repeat every launch)");
        }
        finally { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        File.Delete(request);
    }

    // A marker that cannot be written must not refuse an open (a pick, a created save), only the auto-open's own arm failing sends the launch
    // to the picker. Failure is forced by making the marker's temp path a directory.
    private static void MarkerWriteFailure(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var a = Add(legacy, "Save A"); ProfileCatalog.CommitSelection(legacy, a.Id);
        Directory.CreateDirectory(ProfileCatalog.Startup.MarkerPath(legacy) + ".tmp");
        string run = Run();
        // A tool-requested auto-open must arm its marker before opening.
        var choice = ProfileCatalog.Startup.Decide(legacy, a.Id, null, run);
        Picker(choice, "check-failed", "an auto-open whose marker cannot be written goes to the picker");
        Check(choice.Failure != null && choice.Notice != null && ProfileCatalog.Startup.ReadMarker(legacy) == null, "with the failure kept and no marker");
        // Picks and created saves: BeginOpen never throws and the open proceeds without the marker.
        int slots = ProfileCatalog.List(legacy).Count;
        for (int i = 0; i < 3; i++)
        {
            var created = ProfileCatalog.Create(legacy, "New " + i, new ProfileOptions(), false);
            bool owned = ProfileCatalog.Startup.BeginOpen(legacy, created.Id, run, false, out string warning);
            Check(!owned && warning != null, "a pick or new save proceeds without unfinished-start protection and says why");
            ProfileCatalog.CommitSelection(legacy, created.Id);                 // the bind that follows still records it
        }
        Check(ProfileCatalog.List(legacy).Count == slots + 3 && ProfileCatalog.SelectedId(legacy) != a.Id, "each created save is opened and recorded exactly once; none is left behind by an Arm failure");
        Check(ProfileCatalog.Startup.BeginOpen(legacy, a.Id, run, true, out string quiet) && quiet == null, "an auto-open's marker (written by Decide) is simply owned");
        // Once the obstacle is gone everything is back to normal.
        Directory.Delete(ProfileCatalog.Startup.MarkerPath(legacy) + ".tmp");
        Check(ProfileCatalog.Startup.BeginOpen(legacy, a.Id, run, false, out _) && ProfileCatalog.Startup.ReadMarker(legacy)?.SlotId == a.Id, "and the marker is written again");
    }

    // The only save's folder vanished: Reconcile empties the catalog; the player still gets the "taken off the list" notice.
    private static void OnlySaveVanished(string root)
    {
        Directory.CreateDirectory(root); string legacy = Path.Combine(root, "profile.xml");
        var only = Add(legacy, "Only");
        Directory.Delete(SlotDir(legacy, only.Id), true);
        var result = ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), null, null);
        Check(result.Selector && result.Startup.Reason == "save-missing" && result.Startup.TargetName == "Only" && result.Startup.Notice!.Contains("taken off the list")
            && ProfileCatalog.List(legacy).Count == 0, "the only save vanished: empty picker, but with the notice");
        Check(ProfileCatalog.Startup.ResolveLaunch(legacy, Run(), null, null).Startup.Reason == "no-saves", "and the next launch is the plain first-run picker");
    }
}
