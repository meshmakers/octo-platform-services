# CK model snapshots (test data)

| File | Source | SHA-256 |
|------|--------|---------|
| `ck-system.ui-2.11.0.json` | Published `System.UI-2.11.0` (ckLanguage 1), private CK catalog `meshmakers/construction-kit-libraries-build`, `ck-models/v3/s/System.UI/2/ck-system.ui-2.11.0.json`, copied byte for byte on 2026-11-09 (AB#8601) | `d499e87932fd0f958a4cbf2c81b085cf3acf53d3ba975546a7adb3354158b4db` |

`SystemUiSnapshotCkModel` serves the file as an embedded model of an "old" platform-services pod, so
`SystemUiCkLanguage2UpgradeTests` can put a tenant on the previous release and then upgrade it with the current service
(service restart). Published catalog versions are immutable; the file never needs a refresh. When the next ckLanguage 2
upgrade test needs a newer baseline, add that version next to it instead of replacing this one.
