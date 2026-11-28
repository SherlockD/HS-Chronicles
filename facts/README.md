# Card facts by campaign catalog

Put original card definitions in one JSON file per catalog under `facts/`. The resource stem must match the epoch's `cards` reference in `CampaignEras.xml`. Adding a chronology entry alone leaves the era unavailable.

The engine reads version 1. The root has `schemaVersion`, `meta`, `cards` and `coin`, the ID of the starting coin spell. Each card row has an ID, a numeric ID and integer tag pairs. Optional fields include the name, text, requirements, referenced tags, generated-card IDs and an effect recipe. Keep exact card references and stable collection IDs. The campaign engine tests show a complete original fixture.

For each translated language, add `<catalog>.<locale>.json`. A translation is keyed to the English text of the same version. The plugin uses English when a translation is missing or stale.

The repository does not include material extracted from a game client. Use historical archives for comparison. Record the source version, the rules that were checked and any unresolved differences in the audit.

The previous Violet Hold facts and their proof suites are available at Git revision `e24aa018e0ecc0c87736beab5be1e3d8759e7cdf`.
