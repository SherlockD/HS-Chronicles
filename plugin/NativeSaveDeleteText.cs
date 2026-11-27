using System;
using System.Globalization;

// Save deletion on the startup picker and in-game Change Profile (owner-approved): copy and test-control ids. Pure text, so test-control checks it on the Mac.
internal static class NativeSaveDeleteText
{
    internal const string ManageId = "save.manage", ManageLabel = "Manage", DoneLabel = "Done", RowPrefix = "save.delete:";
    // In-game manage mode: the save open now is listed, disabled, under its own id; it is never a save.delete row.
    internal const string CurrentRowPrefix = "save.current:", CurrentSuffix = " (current)";
    internal const string DismissId = "save.dismiss";
    internal const string PopupId = "SOLOSTONE_SAVE_DELETE", Header = "DELETE PROFILE", ConfirmLabel = "DELETE", CancelLabel = "CANCEL";
    internal const string ConfirmId = "save.delete.confirm", CancelId = "save.delete.cancel";
    internal const string ErrorPopupId = "SOLOSTONE_SAVE_ERROR", ErrorBackId = "save.error.back";
    // The startup notice (a fallback to the picker, an unfinished last start) is its own popup: tools may dismiss it without ever touching
    // a real save error above.
    internal const string NoticePopupId = "SOLOSTONE_STARTUP_NOTICE", NoticeBackId = "save.notice.back";
    // Touch has no hover, so a manage-mode row carries this mark in red. U+00D7 (Latin-1) over U+2715: the HS fonts may lack the
    // latter; plain "X" is the fallback if a live screenshot shows this one missing. The only place that names the glyph.
    internal const string DeleteMark = "×";

    internal static string RowId(string slotId) => RowPrefix + slotId;
    internal static string MarkedLabel(string name) => DeleteMark + "  " + name;
    internal static string CurrentRowId(string slotId) => CurrentRowPrefix + slotId;
    internal static string CurrentLabel(string name) => name + CurrentSuffix;

    internal static string DialogButtonId(string popupId, string button) =>
        popupId == PopupId ? button == "confirm" ? ConfirmId : button == "cancel" ? CancelId : null
        : popupId == ErrorPopupId && button == "cancel" ? ErrorBackId
        : popupId == NoticePopupId && button == "cancel" ? NoticeBackId : null;

    // The save's own name is player text: the test-control denylist reads a dialog's header only, never this body.
    internal static string Body(string name, string createdUtc, bool readable, bool sandbox, string rank, int gold, bool unlimitedGold, int decks)
    {
        string created = DateTime.TryParse(createdUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when)
            ? when.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "unknown date";
        string lines = "Delete \"" + name + "\"?\n";
        if (!readable) lines += "Created " + created + "\nDetails unavailable: this profile's files could not be read.\n";
        else lines += "Created " + created + " · " + (sandbox ? "Sandbox" : "Real") + " profile\nRank " + rank + " · "
            + (unlimitedGold ? "unlimited" : gold.ToString("N0", CultureInfo.InvariantCulture)) + " gold · " + decks + (decks == 1 ? " deck" : " decks") + "\n";
        return lines + "It leaves the profile list and cannot be restored from the game.";
    }

    internal static string Failure(string outcome) =>
        outcome == "refused-pending" ? "This profile has an unfinished match. Open it and finish or concede first."
        : outcome == "refused-legacy" ? "The original profile file cannot be deleted here."
        : outcome == "refused-in-use" ? "This profile is open elsewhere. Close it first."
        : outcome == "refused-unknown" ? "That profile is no longer in the list."
        : outcome == "refused-unreadable" ? "This profile's files are locked or unreadable right now, so a pending match cannot be ruled out. Retry in a moment."
        : outcome == "refused-current" ? "This profile is open right now. Switch to another profile first."
        : outcome == "refused-match" ? "Finish the current match first."
        : outcome == "refused-bound" ? "Profiles cannot be deleted right now."
        : "The profile could not be deleted: " + (outcome != null && outcome.StartsWith("failed:", StringComparison.Ordinal) ? outcome.Substring(7) : outcome);
}
