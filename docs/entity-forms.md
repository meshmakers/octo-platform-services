---
description: The System.UI.EntityForms blueprint: delivered forms per version, tenant overrides, the resolution algorithm and the record seed format.
applies_to: src/SystemUiCkModel/Blueprints/System.UI.EntityForms/**
---

# Entity forms

## `System.UI.EntityForms` blueprint (1.6.0)

`System.UI/EntityForm` (System.UI **2.8.0**, Minor, no migration; 2.9.0 adds
`EntityFormField.ReferenceDisplayAttributes`, Minor, no migration — both shifted one minor up behind
the System 2.5 repin release System.UI 2.7.0 from `main`, which has no EntityForm) describes how the Refinery Studio lists,
creates and edits entities of a CK type: sections, fields, list columns and capabilities, as flat record
arrays `Sections` / `Fields` / `ListColumns` (records `EntityFormSection` / `EntityFormField` /
`EntityFormColumn`). The Studio renders generic Settings pages from it instead of one hand-written page per
configuration type (concept: `octo-frontend-refinery-studio/docs/concepts/studio-shell-and-entity-forms.md` §5).

- **Where forms live.** Delivered forms are seed data of the service-managed blueprint
  `src/SystemUiCkModel/Blueprints/System.UI.EntityForms/` (registered in `Program.cs` like the cockpits;
  applied to every tenant because of the `System.UI.` prefix). Not in `defaultData.yaml` (a leftover nothing
  reads) and not in the communication controller (it would need a System.UI dependency and race the
  System.UI install). 1.0.0 ships `form-default` (target `System/Entity`, `IncludeDerivedTypes: true`,
  `Priority: 0`, no `Category`) plus the six wave-1 forms (SFTP, Grafana, Discord, Loxone, E-Mail sender,
  E-Mail receiver). 1.1.0 adds the wave-3 forms WeClapp, EDA (exact type only) and Energy Community
  (`Category: connections`, rtIds `…20`–`…22`). Bump the blueprint version on every seed change: the next
  cold start rolls the higher embedded version forward on every tenant (no `Program.cs` change, the DI
  extension is keyed on the major version). Until then the Studio still shows these types under
  Settings › Connections › *All configurations* (form-default).
- **1.2.0** adds waves 2 and 4 and retires the last hand-written Studio configuration pages:
  `…30`–`…34` SAP, Microsoft Graph, finAPI (`form-finapi-configuration`), Helm repository, Service
  accounts (`connections`); `…40`–`…42` AI configuration, AI agent configuration, AI quota limit (`ai`;
  agent config and quota limit are created by the AI service, so `CanCreate`/`CanDelete: false`);
  `…50`–`…51` Tenant mode and Tenant configuration (`tenant`). Credentials (SAP `Password`, Graph / finAPI
  `ClientSecret`, finAPI / Helm `Password`, AI `ApiKey`, service account `ClientSecret`) are
  `Editor: password` + `Secret: true` (write-only in the Studio). Helm repository shows the inbound
  `System.Communication/HelmRepository` association as a read-only `Used by` reference field.
  - **Tenant mode** is the only singleton: `Singleton: true`, `SingletonWellKnownName: TenantMode` (the
    entity `System.TenantMode` seeds); the former page's field hints are `Help` texts.
  - **Tenant configuration** and **AI configuration** are deliberately *lists*, not singletons as the
    concept's wave 4 says: `TenantConfiguration` is the engine's key/value store (one entity per key,
    `TenantContext.SetConfigurationAsync`), and pipelines reference several named `AiConfiguration`s via
    `ApiKeyConfigurationName`. A singleton form would hide all but one entry.
  - **Service accounts** stay a hand-written Studio page (rotation flow, concept §5.9). The form gives
    them a Settings entry and list columns; `CanEdit: false` makes every generic view read-only — the
    Studio routes list / create / edit of this entry to its custom page.
  - The Studio carries built-in copies of these entries (`settings-fallback-forms.ts`) that apply only
    where its resolution would otherwise end at `form-default`, i.e. until this version is rolled out
    by a cold start. Keep both in step when changing a wave-2/4 form here: the Studio checks its
    copies against a snapshot of this seed (`entity-forms-seed-1.4.1.snapshot.ts`), which must be
    regenerated too.
- **1.3.0** moves the detail forms of the communication runtime objects into the seed:
  `…60` `form-adapter`, `…61` `form-pool`, `…62` `form-application`, `…63` `form-data-flow` (targets
  `System.Communication/Adapter|Pool|Application|DataFlow`, `IncludeDerivedTypes: true`, no `Category` —
  they shape the Studio's detail pages, not Settings entries; `CanDelete`/`CanDuplicate`/`CanExport: false`,
  deletion and moves are page actions). `GenerateRemainingFields: true` with the section title "Further
  attributes", so attributes of derived types (e.g. a mesh adapter subtype) still appear; every inherited
  runtime-state attribute (`DeploymentState`, `StatusMessage`, `LastDeploymentError*`,
  `CommunicationState*`, `ConfigurationState`, `LastConfigurationError*`, `LifecycleState`,
  `LastActivityAt`, `OnDemandCapable`, `OnDemandBlockingReasons`, adapter `LastSyncedSequenceNumber`) and
  the encrypted `Values` overrides are listed `Hidden: true`. Defaults: pool `Environment` = `Edge`,
  adapter `LifecycleMode` = `AlwaysOn`, `IdleTimeoutMinutes` = `30` (visible only for `OnDemand`).
  The pool (`ManagedBy`, role `System.Communication/Manages`) is `ReadOnly: afterCreate` on the adapter
  (Move… re-homes it) and editable on the application; applications hide `LifecycleMode` /
  `IdleTimeoutMinutes`. Attribute names are checked against `SystemCommunicationCkModel` in
  octo-communication-controller-services.
  - The Studio keeps identical built-in copies (`runtime-object-forms.ts`) as fallbacks only and checks
    them against the snapshot (`entity-forms-seed-1.4.1.snapshot.ts`).
- **1.4.0** sets `ReferenceDisplayAttributes: "repositoryUrl,channel"` on the `HelmRepository`
  reference field of `form-adapter` and `form-application`, so the delivered forms' Helm repository picker
  shows `name · URL · channel` like the Studio fallbacks. Needs **System.UI 2.9.0**
  (`EntityFormField.ReferenceDisplayAttributes`, a comma-separated String — record values in blueprint seeds must be scalars, the seed's record converter cannot read a YAML sequence; the same applies to `RecordColumns` should a seed ever set it): the dependency floor in
  `blueprint.yaml` and the seed `dependencies` are 2.9.0. Entries are target attribute names in camelCase
  (the picker passes them verbatim as GraphQL `attributeNames`); list only non-secret attributes.
- **1.4.1** only raises the System.UI floor (`blueprint.yaml` + seed `dependencies`) to 2.9.0
  after the studio-rebuild System.UI versions moved one minor up (2.7.0 is the System 2.5 repin
  release from `main`). The forms are unchanged; the patch bump rolls the new floor forward.
- **1.5.0** makes the communication detail forms work on **System.Communication 3.x and 4.x**
  (4.0.0 renamed `Pool` -> `DeploymentSite` and `Manages`/`ManagedBy` -> `Hosts`/`HostedBy`, no alias).
  `form-adapter` / `form-application` carry **both** reference fields in `general`, order 1: `ManagedBy`
  (`Pool`, role `System.Communication/Manages`) and `HostedBy` (`DeploymentSite`, role
  `System.Communication/Hosts`, label "Deployment site", same `ReadOnly` as the pool field). The Studio's
  resolver (octo-ui/entity-forms `entity-form-resolver.ts`) skips a `reference` field whose
  `AssociationRoleId` is not a role of the tenant's type, with a warning, so exactly one renders per major.
  `TargetCkTypeId` is a single type id, so `form-pool` (`…61`, unchanged, stable name) cannot also target
  `DeploymentSite`: the new `…64` `form-deployment-site` (same fields) does; only the form whose type exists
  in the tenant resolves. Neither form has an association field (the Studio fallback has none either), so
  no `Hosts`/`Manages` field on the site/pool form. Still no `System.Communication` dependency — and none may
  be added: it would have to admit both majors. The Studio snapshot `entity-forms-seed-1.4.1.snapshot.ts`
  needs refreshing to 1.5.0.
- **1.6.0** adds the **"Resources"** section (`resources`, 2 columns) to `form-adapter` (after "Runtime", order 2;
  "Helm deployment" / "Ingress" move to 3 / 4) and `form-application` (after "General", order 1), with the four
  `DeployableWorkload` attributes of System.Communication 4.10.0: `CpuRequest`, `CpuLimit`, `MemoryRequest`,
  `MemoryLimit` (reading order request | limit per resource, `Width: half`, `Placeholder: "chart default"`, help text with
  `250m` / `512Mi` / `1Gi`). Each carries the Kubernetes quantity `Pattern` (same grammar as the controller's
  `KubernetesQuantity`: number plus `Ki..Ei`, `n u m k M G T P E` or an exponent; case-sensitive, so `512 MB` and `1gi`
  fail); the exact controller texts and the request <= limit rule are added by the Studio host (cross-field validator).
  The runtime attribute `LastDeploymentWarning` is listed `Hidden`. **No System.Communication dependency was
  added** (the blueprint must admit 3.x and 4.x tenants): the Studio resolver silently skips a field whose attribute the type
  lacks, and a section without fields is dropped, so a tenant below 4.10.0 shows no Resources section. The pool form is
  unchanged (a pool is sized by `PoolMember*`; the controller rejects the inherited fields on a pool). The Studio snapshot
  `entity-forms-seed-1.6.0.snapshot.ts` replaces the 1.5.0 one.
- **Naming.** `rtWellKnownName` = `form-<kebab-type>`, e.g. `form-sftp-configuration` (no colon).
- **Tenant overrides.** A tenant customises a delivered form by creating its **own** `EntityForm` for the same
  `TargetCkTypeId` (empty `rtBlueprintSource`). Never edit a delivered `form-*` entity: a blueprint re-apply
  upserts it (force re-apply rewrites every seeded attribute, see TenantMode in `docs/service-managed-blueprints.md`) and wipes the edit.
- **Resolution (one algorithm, no special case).** (1) Pick exactly one form F for type T: forms with
  `TargetCkTypeId = T`, else the nearest ancestor with a form where `IncludeDerivedTypes = true`;
  `form-default` on `System/Entity` always matches. Ties: tenant form beats seeded, then higher `Priority`.
  Forms are never merged. (2) Fields = F.Fields whose `AttributePath` exists on T (unknown paths skipped
  silently), minus `Hidden`. (3) Unless `GenerateRemainingFields = false` (absent = true), all other
  attributes of T are appended in model order into section `GeneratedSectionTitle` (default "Further
  attributes"). (4) If `form-default` is deleted, the Studio falls back to a built-in constant equal to the
  seed and logs a warning.
- **Normative defaults.** No form → `form-default`. Abstract T → `CanCreate` forced false, list shows derived
  instances, create asks for a concrete subtype. Singleton is never implied (only `Singleton` +
  `SingletonWellKnownName`). No write permission → read-only, create/edit/delete hidden regardless of F. A
  form appears on Settings home only if `Category` is set. Capability defaults when absent:
  Create/Edit/Delete true, Duplicate/Export false. Associations become reference fields only when a form
  lists them; `rtId` is never a field; system timestamps and `RtBlueprint*` are never editable; Secret
  attributes/fields are write-only.
- **String values, not CK enums.** `Editor` (auto|text|multiline|password|number|toggle|enum|datetime|url|
  email|json|yaml|cron|reference|records; unknown = auto), `ReadOnly` (never|always|afterCreate), `Width`
  (full|half) and `Display` (text|chip|date|mono) are Strings. System.UI uses no enums, and CK enums store
  numeric keys, so every new value would force a Minor bump plus schema regeneration. `Required` is an
  optional Boolean: absent = from the model's `isOptional`; `true` makes it stricter; `false` cannot loosen
  a mandatory attribute.
- **Record seed format** is the runtime import format (`ckRecordId` + `attributes`), not a `{ Key: x }`
  shorthand:

  ```yaml
  - id: System.UI/EntityForm.Sections
    value:
      - ckRecordId: System.UI/EntityFormSection
        attributes:
          - { id: System.UI/EntityFormSection.Key, value: server }
          - { id: System.UI/EntityFormSection.Title, value: Server }
          - { id: System.UI/EntityFormSection.Columns, value: 2 }
  ```
- **Verify** after import: `runtime { systemUIEntityForm { items { rtWellKnownName targetCkTypeId
  sections { key title } fields { attributePath editor secret } } } }`.
