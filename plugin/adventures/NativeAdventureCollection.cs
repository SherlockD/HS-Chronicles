using System;

internal static partial class NativeUIProbe
{
    internal static void RefreshAdventureCollection(string cardId)
    {
        var manager = CollectionManager.Get() ?? throw new InvalidOperationException("Native collection unavailable.");
        var sets = manager.GetDisplayableCardSets();
        var set = DefLoader.Get().GetEntityDef(cardId).GetCardSet();
        if (!sets.Contains(set)) sets.Add(set);
        AddNativeGrants(NativeUIProjection.ShopGrantCounts(Profile.Collection, new[] { cardId },
            id => manager.GetOwnedCount(id, TAG_PREMIUM.NORMAL)));
    }
}
