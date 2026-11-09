# Changelog

## 2.12.0 - 2026-11-09

_Required bump level: minor._

### Changed

- Model 'System.UI': ckLanguage changed '1' → '2' _(minor — CK language version raised; older engines reject the model with a clear error)_ (AB#8601, Feature AB#5697 F4.3)
- Dependency 'System': version changed '2.5.0' → '2.8.0' _(none — resolved dependency System moved 2.5.0 -> 2.8.0 inside its declared range [2.5,3.0) of a range-retaining model; the closure is compiler-set metadata and the model itself is unchanged, no bump needed (row D8))_
- All 11 types (`UIElement`, `Branding`, `Dashboard`, `DashboardWidget`, `EntityForm`, `Folder`, `MappingCoverageConfiguration`,
  `ProcessDiagram`, `SymbolDefinition`, `SymbolLibrary`, `TreeNavigationConfiguration`) and all 8 records (`EntityFormColumn`,
  `EntityFormField`, `EntityFormSection`, `Theme.Colors`, `ThemeGradient`, `ThemePalette`, `TreeNavigationRoleConfig`,
  `TreePerspectiveConfig`) declare `derivable: Any` explicitly. ckLanguage 2 changes the default to `Model`; without the explicit
  value every element would be a MAJOR change (row T6, `OCTO-CK200`). `UIElement` stays the stable, derivable base of UI types.

### Compatibility

- The compiled model equals the published 2.11.0 after the model version, `ckLanguage`, `minEngineVersion`, `derivable` and the
  resolved dependency closure are removed (same normalised SHA-256). Ids, attributes, defaults, ownership, descriptions and the
  association roles are unchanged; `visibility` stays `Public` and attribute `access` stays `ReadWrite`.
- `minEngineVersion` 3.5.1 (the CK v2 minimum, as 2.11.0), published under `ck-models/v3/` only.
- Dependents (`ValidateCascade`): `EnergyCommunity.Registration` 2.10.0 and 3.1.0 are Compatible. The exact-pinned legacy
  `EnergyCommunity-2.0.0` (pinned to `System.UI-2.0.0`, `Theme.Colors`) reports `NeedsRepin` exactly as for the unchanged
  2.11.0; it stays pinned to 2.0.0 and needs no rebuild for this release.
- Upgrade: a tenant on 2.11.0 moves to 2.12.0 by a platform-services restart (service-managed descriptor, downgrade guard);
  no data migration.
- Not in this release: the System interfaces (`Named-1`, `Describable-1`, `Hierarchical-1`) need `System` 2.7, and raising the
  floor of `System-[2.5,3.0)` beyond the System 2.5.0 that 2.11.0 resolved is a MAJOR (row D1). No attribute is tightened:
  every attribute is `SeedOwned` and written by Refinery Studio or the blueprints through the generic API, none is
  `securitySensitive` (a tightening would be a MAJOR, row T7). No methods: platform-services has no hand-written UI operation.
