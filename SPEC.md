# Data Copier — design specification

XrmToolBox plugin that copies selected records from a SOURCE Dataverse / Dynamics 365 environment
(cloud or on-premises 9.x) to a DESTINATION environment, **keeping the same GUIDs**, and recursively
creating any records the copied records point at through lookups (parents first), as deep as needed.
Two relationship options (5.10) switch the lookup targets off and the child records reached through
1:N relationships (subgrids) on.

This document is the contract for the code. Where it is silent, follow the conventions of the existing
code (section 2). Do not widen the scope.

The tool is published as **Data Copier** by **Myscotek**: XrmToolBox Tool Library package
`Myscotek.DataCopier`, source https://github.com/scottm1985/DataCopier, MIT licence. The assembly
(`MyscotekDataCopier.dll`), its namespaces and its settings file keep the `MyscotekDataCopier` name.

## 1. Decisions taken by the owner — do not change

| Topic | Decision |
|---|---|
| Selected records that already exist in the destination (same GUID) | **Always update** them with the source values (overwrite). |
| Related (lookup-target) records that already exist in the destination | **Skip** them, log "already exists". Never update them. |
| Related record that fails to create | Log the error, skip it, **continue**; the lookup that pointed at it is left **blank** on the referencing record. |
| Lookups to non-creatable system entities: `systemuser`, `team`, `businessunit`, `organization`, `transactioncurrency` | **Never create.** If the same GUID exists in the destination keep the lookup, otherwise leave it blank and log a warning. This list is configurable (settings) but those are the defaults. Applies to `ownerid` on every record. |
| Which related records are followed | **N:1 lookups** (Lookup, Customer, Owner, Regarding and PartyList members), recursively, to any depth - while **Create related records for N:1 relationships (lookups)** is ticked (the default). **1:N child records** only when **Create related records for 1:N relationships (subgrids)** is ticked (default off), see 5.10. **No** N:N associations. |
| Depth | Unlimited ("as deep as necessary"); cycles are handled (see 5.6). A safety cap `MaxDepth` (default 100) only guards against pathological data and logs a warning when hit. |
| Extras (all wanted) | (a) **Dry run** checkbox: resolve everything and log what would happen, write nothing. (b) **Preserve created on** checkbox: set `overriddencreatedon` = source `createdon` on create. (c) **Personal views** (`userquery`) always listed, after the system views (`savedquery`) - no option since 1.2026.10.2. (d) **Bypass custom plugins** checkbox: set `BypassCustomPluginExecution = true` on create/update requests (online only, needs `prvBypassCustomPlugins`). |
| Logging | Everything visible in an on-screen log while running: each record created / updated / skipped / failed, each lookup blanked or backfilled, warnings, and a summary. Colour per level. Copy / Save / Clear buttons. |
| GUIDs | Preserved for selected AND related records. |
| Virtual tables (added 2026-09-30) | Treated like never-create entities: never retrieved from the source, never created or updated; a lookup to one is kept when the same id exists in the destination (checked with a `Retrieve` by id, because virtual-table providers may fail every query). A virtual table cannot be the selected entity. See 5.8. |
| Lookup targets that cannot be checked (added 2026-09-30) | When the destination cannot answer the existence check of a never-create or virtual target (anything but "does not exist"), the lookup is **kept** (not blanked) with a warning; if the create/update then fails, it is retried once without those lookups. See 5.8. |
| Closing states (added 2026-09-30) | State changes are applied only after the selected record's whole tree is done (every backfill into a record comes first), with the platform's close messages where a plain update is refused (won/lost opportunity, resolved case, won/closed quote, cancelled/fulfilled order). See 5.9. |
| Relationship options (added 2026-10-01) | Two persisted checkboxes (relabelled in 1.2026.10.2). **Create related records for N:1 relationships (lookups)**, default ticked: today's behaviour; unticked, no lookup target is ever created or updated - a lookup is kept if its target exists in the destination by the end of the selected record's copy, otherwise blanked (`... (lookups not copied)`); party-list members are decided at once; never-create and virtual targets unchanged. **Create related records for 1:N relationships (subgrids)**, default unticked: ticked, the CHILD records of the selected records are copied too, recursively, but only downward from the selected records and their children (never for lookup targets, never-create or virtual records); an existing child is skipped but its children are still walked. Which 1:N relationships: chosen per entity in a picker (`Relationships...`), remembered per source organisation; an entity not configured there follows the subgrids on its active main forms in the source. Both unticked: only the selected records are written. See 5.10. |

## 2. Conventions

* Branding and plugin metadata: the publisher's logo constants (`PluginImages`) and `[ExportMetadata]` colours.
* Connections: the source is the tool's normal XrmToolBox connection; the destination is a second connection
  requested with `RaiseRequestConnectionEvent` and caught in the `UpdateConnection` override (6).
* Toolbar `Source:` / `Destination:` labels and a same-organisation guard before copying (6).
* A colour-per-level RichTextBox log; settings persisted with `SettingsManager.Instance.TryLoad/Save`.
* csproj with the host DLLs excluded from the output (`ExcludeAssets="runtime"`); an xUnit net48 test project.

Known gotchas:
* Pin `XrmToolBoxPackage 1.2025.10.74` **and** `MscrmTools.Xrm.Connection 1.2025.9.64` explicitly (otherwise CS1705/MSB3277). `Microsoft.CrmSdk.CoreAssemblies 9.0.2.60`. Use `ExcludeAssets="runtime"` on all three so the host DLLs are not copied to the output; the only file deployed is `MyscotekDataCopier.dll`.
* All seven `ExportMetadata` keys are mandatory or MEF silently drops the plugin.
* `AssemblyCompany` must be set ("Myscotek"). `AssemblyInfo.cs` is hand-written (`<GenerateAssemblyInfo>false</GenerateAssemblyInfo>`, `<Deterministic>true</Deterministic>`) and carries the release version, date style `1.YYYY.M.N` (N from 1), as `AssemblyVersion`, `AssemblyFileVersion` and `AssemblyInformationalVersion`, equal to the `<version>` of `Myscotek.DataCopier.nuspec`: the Tool Library compares the package version with the assembly version, and XrmToolBox re-reads a plugin's metadata (cached in `Plugins\manifest.json`) only when its `AssemblyVersion` changes.
* Alias `Label = System.Windows.Forms.Label` where `Microsoft.Xrm.Sdk.Label` collides.
* No .resx / WinForms designer files: build the UI in code.
* Plugins live in `%AppData%\MscrmTools\XrmToolBox\Plugins`. The DLL cannot be copied while `XrmToolBox.exe` is running.

## 3. Project layout

```
(repository root)
  MyscotekDataCopier.sln
  .gitignore                       (bin/ obj/ .vs/ dist/ tools/nuget.exe TestResults/ *.user *.suo .claude/settings.local.json)
  LICENSE                          (MIT, Copyright (c) 2026 Myscotek)
  README.md                        (public: what it does, install, use, the rules of sections 1 and 5 in user terms, limitations, build)
  CLAUDE.md                        (short: build/test/package/release commands, gotchas, file map)
  SPEC.md                          (this file)
  Myscotek.DataCopier.nuspec       (the Tool Library package: the DLL under lib\net48\Plugins and the icon, nothing else)
  build-package.ps1                (build, test, version check, nuget pack into dist\, package content check)
  images\                          (icon-32.png, icon-80.png, icon-128.png, made from PluginImages; icon-128 is the package icon)
  src\MyscotekDataCopier\
    MyscotekDataCopier.csproj      (net48, SDK-style, UseWindowsForms, LangVersion 10.0, RootNamespace MyscotekDataCopier)
    Properties\AssemblyInfo.cs     (Title/Product "Data Copier", Company "Myscotek", Copyright "Copyright (c) 2026 Myscotek", the release version)
    MyscotekDataCopierPlugin.cs    (Export + PluginImages)
    Core\                          (no WinForms references anywhere under Core)
      CopyEngine.cs
      CopyOptions.cs
      CopySummary.cs / CopyProgress.cs
      ICopyLogger.cs / LogLevel.cs
      IChildRelationshipSelector.cs / DefaultChildRelationshipSelector.cs (+ ChildRelationshipEligibility)   (5.10)
      Schema\ (EntitySchema.cs, AttributeSchema.cs, ChildRelationship.cs, ISchemaProvider.cs, DataverseSchemaProvider.cs)
      Services\ (EntityCatalog.cs, ViewService.cs, LayoutParser.cs, RecordPager.cs, CellFormatter.cs, FetchXmlHelper.cs,
                 ColumnHeaderResolver.cs, FormSubgridService.cs)
    UI\
      DataCopierControl.cs         (PluginControlBase; logic)
      DataCopierControl.Layout.cs  (partial: BuildUi() with all control construction)
      RelationshipPickerForm.cs    (the 1:N relationship picker, 5.10 / 6)
      UiLogger.cs                  (ICopyLogger that marshals to the RichTextBox)
      DataCopierSettings.cs        (+ RelationshipSelection)
  tests\MyscotekDataCopier.Tests\
    MyscotekDataCopier.Tests.csproj (xUnit, net48)
    Fakes\FakeOrganizationService.cs, Fakes\FakeSchemaProvider.cs (+ builders)
    CopyEngineTests.cs, LayoutParserTests.cs, FetchXmlHelperTests.cs, CellFormatterTests.cs, ...
    CopyEngineChildRecordTests.cs, ChildRelationshipTests.cs, RelationshipPickerFormTests.cs   (5.10)
    UiSmokeTests.cs, UiFlowTests.cs, UiLayoutTests.cs, PluginMetadataTests.cs                 (4, 6)
```

Namespaces: `MyscotekDataCopier`, `MyscotekDataCopier.Core`, `MyscotekDataCopier.Core.Schema`, `MyscotekDataCopier.Core.Services`, `MyscotekDataCopier.UI`.

## 4. Plugin entry

```csharp
[Export(typeof(IXrmToolBoxPlugin))]
[ExportMetadata("Name", "Data Copier")]
[ExportMetadata("Description", "Copies selected records from a source to a destination Dataverse / Dynamics 365 environment keeping the same GUIDs, with the records their lookups point at and, optionally, their child records.")]
[ExportMetadata("BackgroundColor", "#2868B1")]
[ExportMetadata("PrimaryFontColor", "White")]
[ExportMetadata("SecondaryFontColor", "WhiteSmoke")]
[ExportMetadata("SmallImageBase64", PluginImages.Small)]
[ExportMetadata("BigImageBase64", PluginImages.Big)]
public class MyscotekDataCopierPlugin : PluginBase
{
    public override IXrmToolBoxPluginControl GetControl() => new UI.DataCopierControl();
}
```
`PluginImages` = the Myscotek logo as base64 PNG constants, `Small` 32x32 and `Big` 120x120 (the Tool Library
wants a dedicated image for each display mode); never edited by hand. `images\icon-32.png` is `Small`;
`icon-80.png` and `icon-128.png` are `Big` resized (high-quality bicubic, transparent background).
The control implements `IGitHubPlugin` (`UserName` "scottm1985", `RepositoryName` "DataCopier") and
`IHelpPlugin` (`HelpUrl` "https://github.com/scottm1985/DataCopier#readme").

## 5. Core engine (`MyscotekDataCopier.Core`)

### 5.1 Public API (exact)

```csharp
public enum LogLevel { Info, Success, Warning, Error }

public interface ICopyLogger { void Log(LogLevel level, string message); }

public sealed class CopyOptions
{
    public bool DryRun { get; set; }
    public bool PreserveCreatedOn { get; set; }              // createdon -> overriddencreatedon on create
    public bool BypassCustomPluginExecution { get; set; }   // sets request parameter on create/update
    public bool UpdateExistingSelectedRecords { get; set; } = true;   // decision: always true; UI does not expose it
    public HashSet<string> NeverCreateEntities { get; }     // default: systemuser, team, businessunit, organization, transactioncurrency (OrdinalIgnoreCase)
    public int MaxDepth { get; set; } = 100;
    public bool CopyLookups { get; set; } = true;           // 5.10: false = lookup targets are never created, only checked for existence
    public bool CopyChildren { get; set; }                  // 5.10: true = child records through 1:N relationships, recursively
    public IChildRelationshipSelector ChildRelationshipSelector { get; set; }   // 5.10: which 1:N relationships; null = no children
}

public interface IChildRelationshipSelector { IReadOnlyList<ChildRelationship> GetChildRelationships(string parentEntity); }

public sealed class CopyProgress
{
    public int SelectedIndex, SelectedTotal;      // 1-based index of the selected record being processed
    public int Created, Updated, SkippedExisting, Failed, LookupsBlanked, LookupsBackfilled;
    public int ChildRecordsFound;                 // 5.10
    public string CurrentRecord;                  // e.g. contact "Jane Doe"
}

public sealed class CopySummary
{
    public int SelectedTotal, Created, Updated, SkippedExisting, Failed, LookupsBlanked, LookupsBackfilled, StateChanges;
    public int ChildRecordsFound;                 // 5.10: child records listed through 1:N relationships
    public bool Cancelled, DryRun;
    public TimeSpan Elapsed;
    public IReadOnlyList<string> Errors;          // one line per failure, for the summary block
}

public sealed class CopyEngine
{
    public CopyEngine(IOrganizationService source, IOrganizationService destination,
                      ISchemaProvider sourceSchema, ISchemaProvider destinationSchema,
                      CopyOptions options, ICopyLogger logger);

    /// Copies the selected records (and, recursively, their lookup targets). Never throws for a
    /// per-record failure; throws OperationCanceledException on cancellation and lets genuine setup
    /// failures (e.g. selected entity missing in destination) propagate after logging them.
    public CopySummary Copy(string entityLogicalName, IReadOnlyList<Guid> selectedIds,
                            IProgress<CopyProgress> progress, CancellationToken cancellationToken);
}
```

### 5.2 Schema abstraction (so the engine is unit-testable without Dataverse)

```csharp
public sealed class EntitySchema
{
    public string LogicalName, PrimaryIdAttribute, PrimaryNameAttribute, DisplayName;
    public string DisplayCollectionName;                              // plural display name (picker labels); may be null
    public bool IsIntersect;
    public bool IsPrivate;                                            // 5.10: never followed as a child entity
    public bool IsVirtual;                                            // virtual table (5.8): DataverseSchemaProvider.IsVirtualTable(metadata)
    public IReadOnlyDictionary<string, AttributeSchema> Attributes;   // keyed by logical name, OrdinalIgnoreCase
    public IReadOnlyDictionary<int, int> DefaultStatusByState;        // statecode -> default statuscode (from StateOptionMetadata.DefaultStatus); may be empty
    public IReadOnlyList<ChildRelationship> OneToManyRelationships;   // 5.10: the entity's 1:N relationships (it is the referenced entity), metadata order
    public bool HasStateCode => Attributes.ContainsKey("statecode");
}
public sealed class ChildRelationship   // from OneToManyRelationshipMetadata (entries without schema name, child entity or lookup are dropped)
{
    public string SchemaName { get; set; }            // e.g. contact_customer_accounts
    public string ParentEntity { get; set; }          // ReferencedEntity (the entity itself when absent)
    public string ChildEntity { get; set; }           // ReferencingEntity
    public string ChildLookupAttribute { get; set; }  // ReferencingAttribute
    public bool IsCustomRelationship { get; set; }
}
public sealed class AttributeSchema
{
    public string LogicalName;
    public AttributeTypeCode AttributeType;     // Microsoft.Xrm.Sdk.Metadata
    public bool IsValidForCreate, IsValidForUpdate;
    public string AttributeOf;                  // non-null => derived attribute (e.g. *_base, *name): never copy
    public int SourceType;                      // 0 simple, 1 calculated, 2 rollup: copy only 0
    public bool IsFile;                         // FileAttributeMetadata: never copy
    public bool IsMultiSelect;                  // MultiSelectPicklistAttributeMetadata (Virtual type but copyable)
    public string[] LookupTargets;              // for Lookup/Customer/Owner/PartyList
}
public interface ISchemaProvider { EntitySchema GetEntity(string logicalName); /* null if the entity does not exist */ }
public sealed class DataverseSchemaProvider : ISchemaProvider  // RetrieveEntityRequest(Entity|Attributes|Relationships), thread-safe cache, caches "missing" too
```
`DataverseSchemaProvider.IsVirtualTable(EntityMetadata)` (also used by `EntityCatalog`): when the server reports `EntityMetadata.TableType` (a string: "Standard", "Virtual", "Elastic"...), the table is virtual exactly when it is "Virtual" (case-insensitive); otherwise (older servers) when `DataProviderId` is set, is not `Guid.Empty` and is not the elastic-table provider `1d9bde74-9ebd-4da9-8ff5-aa74945b9f74` (`ElasticTableDataProviderId`).

### 5.3 Attribute copy rules (applied when building the destination Entity)

Iterate the SOURCE record's attributes (retrieved with `ColumnSet(true)`). For each attribute:
1. Skip if the DESTINATION schema has no such attribute. Warning, **once per entity+attribute per run**: `Skipped attribute {attr} on {entity}: not present in destination`.
2. Skip if the source value is null.
3. Skip the primary id attribute (set `Entity.Id` instead).
4. Skip these always: `createdby, createdonbehalfby, modifiedby, modifiedon, modifiedonbehalfby, owninguser, owningteam, owningbusinessunit, versionnumber, statecode, statuscode, processid, stageid, traversedpath, entityimage_timestamp, entityimage_url, entityimageid, exchangerate, overriddencreatedon`. `createdon` is skipped too, EXCEPT that when `PreserveCreatedOn` is on and the operation is a **create**, the source `createdon` is written to `overriddencreatedon` (if that attribute exists and is valid for create in the destination).
5. Skip if destination `AttributeOf != null`, `SourceType != 0`, `IsFile`, or `AttributeType == Virtual` unless `IsMultiSelect`. Skip `AttributeType` in { `ManagedProperty`, `EntityName`, `CalendarRules` }.
6. Skip if not `IsValidForCreate` (create) / not `IsValidForUpdate` (update).
7. Lookup-typed values (`EntityReference`, attribute type Lookup / Customer / Owner):
   * If the target entity is in `NeverCreateEntities` or is a virtual table (5.8): keep the reference only if the target GUID exists in the destination (existence check cached per run); if it does not exist, omit the attribute and log Warning `Blanked {attr} on {entity} "{name}": {targetEntity} {id} does not exist in destination` (for a virtual table `... does not exist in destination (virtual table, never created)`), count `LookupsBlanked`. If the destination cannot answer the check, the reference is **kept unverified** (5.8).
   * Otherwise, when lookups are copied (`CopyLookups`, the default), call `EnsureRecord(reference, depth+1)` (5.5). Result Available: keep the reference (LogicalName + Id; `Name` may be dropped). Unavailable: omit the attribute, Warning `Blanked {attr} on {entity} "{name}": related {targetEntity} {id} could not be copied`, count `LookupsBlanked`. Deferred (cycle): omit the attribute now and register a backfill (5.6).
   * When lookups are NOT copied (`CopyLookups = false`, 5.10) every other target is existence-only too: kept when the target GUID exists in the destination (the same cached check; a record this run created counts), kept unverified when the check fails (5.8). Otherwise the attribute is omitted and the lookup **deferred** to the end of the selected record's tree (5.10): written then with a backfill update if the tree copied the target meanwhile, else Warning `Blanked {attr} on {entity} "{name}": {targetEntity} {id} does not exist in destination (lookups not copied)`, counted in `LookupsBlanked` (a table the destination does not have is blanked at once, with the same wording). Never `EnsureRecord`: no lookup target is created, updated or read from the source. A reference of a record to itself (the target is the record in progress) is backfilled after its create (5.6).
8. PartyList values (`EntityCollection` of `activityparty`): rebuild the collection keeping, for each party, only `partyid`, `participationtypemask`, `addressused` (drop `activitypartyid`, `activityid` and anything else). For each `partyid` apply rule 7 (including its lookups-not-copied branch, but decided at once - parties are never deferred: `Dropped party {target} {id} from {attr} on {entity} "{name}": does not exist in destination (lookups not copied; party lists are not backfilled)`, without the note when the destination has no such table); a party whose target is Unavailable or Deferred is **dropped** from the list with a Warning (no backfill for party lists). Parties with no `partyid` but an `addressused` (unresolved e-mail address) are kept. A party whose never-create/virtual target cannot be checked is kept, and the party-list attribute as a whole becomes an unverified lookup (5.8).
9. Everything else (`string, int, long, decimal, double, bool, DateTime, Money, OptionSetValue, OptionSetValueCollection, byte[] image, Guid`) is copied as-is.

### 5.4 Create / update mechanics

* Create: `new Entity(logicalName) { Id = sourceId }` + attributes, sent as a `CreateRequest` via `Execute`, with `Parameters["SuppressDuplicateDetection"] = true` and, when the option is on, `Parameters["BypassCustomPluginExecution"] = true`.
* If a create or update fails, it is retried without what may have caused it - at most three retries, each logged (Info) before it is sent as `{Create|Update} failed, retrying without {what}: {error}`:
  1. without the record's unverified lookups (5.8), if it has any;
  2. create only, if `overriddencreatedon` was included: without `overriddencreatedon` (a missing override privilege must not fail the whole record);
  3. when both apply: without both.

  The retry that works logs what it left out, each with the error of the latest attempt that still sent it: per unverified lookup a Warning `Blanked {attr} on {entity} "{name}": {create|update} failed with the unverified lookup ({error})` (counted in `LookupsBlanked`), and for `overriddencreatedon` a Warning `{entity} "{name}" ({id}) created without overriddencreatedon: {error}`. When every attempt fails, the last error fails the record.
* Update (selected records that already exist): `UpdateRequest` with the same parameters; attributes filtered by `IsValidForUpdate` (rule 6); never sends `overriddencreatedon`.
  * If the destination record is currently inactive (`statecode != 0`) and there are attributes to write, first reactivate it (`Update` with `statecode = 0` and the default status for state 0 if known) and log Info `Reactivated ... for update`.
* State/status: after a successful create or update, a state change (source `statecode`/`statuscode` differing from the destination's current state) is **queued**, and applied only once the selected record's whole tree is done, with the close messages where the platform requires them - see 5.9.
* Existence check in the destination: `QueryExpression` on the entity, `ColumnSet(primaryId, primaryName, statecode, statuscode)` (only the columns that exist), condition `primaryId Equal id`, `TopCount = 1`. Results cached per (entity,id) for the run and updated when the engine creates a record. A **virtual table** is checked with `Retrieve(entity, id, ColumnSet(primaryId[, primaryName]))` instead, the record-not-found fault meaning "does not exist" (5.8). Do not use `Retrieve` + catch for the existence of other entities (avoids fault noise), but `Retrieve` with `ColumnSet(true)` **is** used to load the source record.
* Dry run: identical resolution and logging path, but no `Create`/`Update`/state/close calls are issued; log lines are prefixed `[DRY RUN] Would create ...`, `[DRY RUN] Would keep ... unverified ...`, `[DRY RUN] Would apply state to ...` etc.; counters count as if done. Existence checks and source retrieves still happen (reads only).

### 5.5 EnsureRecord(reference, depth): the recursive core

State per (entity, id) key for the run: `Unknown | InProgress | Created | Updated | Exists | Failed | Missing`.

```
EnsureRecord(ref, depth, isSelected, isChild = false):        (isChild: reached through a 1:N relationship, 5.10)
  key = (ref.LogicalName, ref.Id)
  if state[key] in {Created, Updated}          -> (isSelected or isChild: Info "Already copied earlier in this run: ..."; WalkChildren) Available
  if state[key] == Exists                      -> isSelected: carry on (it is updated) | isChild: WalkChildren, Available | else Available
  if state[key] in {Failed, Missing}           -> Unavailable
  if state[key] == InProgress                  -> Deferred   (cycle)
  if depth > MaxDepth -> Warning, mark Failed -> Unavailable
  if entity not in NeverCreateEntities:
     destSchema = destinationSchema.GetEntity(entity); if null -> Warning "entity {x} does not exist in destination", Failed -> Unavailable
     if destSchema.IsVirtual -> existence only, like a never-create entity (5.8)
     else srcSchema = sourceSchema.GetEntity(entity);  if null -> Warning, Failed -> Unavailable
  exists = ExistsInDestination(key)          (a Retrieve by id for a virtual table)
  if !isSelected and exists -> state Exists; Info "Exists, skipped {entity} \"{name}\" ({id})"; count SkippedExisting; if isChild: WalkChildren -> Available
     (name: from the existence query's primary name column when present; otherwise the id. Do NOT retrieve the source record just for the name)
  if NeverCreateEntities contains entity, or it is virtual: -> exists ? Available : (Missing, Warning) Unavailable   [never retrieved from source, never created]
  src = source.Retrieve(entity, id, ColumnSet(true)); on "does not exist" fault -> Missing, Warning "{entity} {id} not found in source" -> Unavailable
  state[key] = InProgress
  build destination entity per 5.3 (this recurses into EnsureRecord for lookups)
  if isSelected and exists and UpdateExistingSelectedRecords -> Update path; state Updated; Success "Updated {entity} \"{name}\" ({id})"; count Updated
  else -> Create path; state Created; Success "Created {entity} \"{name}\" ({id})"; count Created
  on failure (after the retries of 5.4) -> state Failed; Error "FAILED to create {entity} \"{name}\" ({id}): {message}"; add to summary Errors; count Failed -> Unavailable
  queue the state/status change, if any (5.9)
  apply any pending backfills that reference this key (5.6)
  if isSelected or isChild: WalkChildren (5.10: the child records, each EnsureRecord(child, depth+1, isSelected:false, isChild:true))
  -> Available
```
(Lookups and parties that point at a never-create entity or a virtual table are resolved by rule 7/8 with the existence check alone, without calling `EnsureRecord`; the branches above keep the rule for any other path.)
* `Copy()` iterates `selectedIds` in order, calling `EnsureRecord(new EntityReference(entity,id), 0, isSelected:true)` inside `try/catch` so one bad record never stops the run; right after it returns (success or failure) the deferred lookups of that tree are resolved (lookups not copied, 5.10) and then its queued state changes applied (5.9) - both once more at the end of the run; checks `cancellationToken` before each record (and inside the recursion before each create); reports progress after every create/update and after every selected record.
* Log indentation: two spaces per depth level so the tree is visible. Log `Copying {entity} "{name}" ({id})...` (Info) when a record starts, then its children (indented one level deeper), then the outcome line at the record's own depth, e.g.
  ```
  Copying account "Contoso" (guid)...
    Copying contact "Jane Doe" (guid)...
      Blanked parentcustomerid on contact "Jane Doe": account xxx could not be copied
    Created contact "Jane Doe" (guid)
  Created account "Contoso" (guid)
  ```
  Never-create lookups that resolve successfully are not logged (only blanks are).
* Selected record whose entity is in `NeverCreateEntities`: refuse up-front (Copy logs an Error and returns). The UI also blocks it. The same for a virtual table (destination schema, else the source's when the destination has no such entity): Error `{entity} is a virtual table: its rows live in an external data source and cannot be created here.` (5.8).

### 5.6 Cycles and backfill

Example: `account.primarycontactid -> contact`, `contact.parentcustomerid -> account`. Copying the account: account InProgress, contact needs account, so Deferred. Contact is created **without** `parentcustomerid`, and a backfill `(target = contact/id, attribute = parentcustomerid, value = account ref)` is queued against key account/id. When the account create succeeds, the engine applies the backfill with `Update` (same request parameters), logs Success `Backfilled contact.parentcustomerid -> account (id)` and counts `LookupsBackfilled`; on failure Warning. If the account create fails, its queued backfills are dropped with a Warning (`lookup left blank`) and counted in `LookupsBlanked`. In dry run: `[DRY RUN] Would backfill ...`.

### 5.7 Services used by the UI (also in Core, unit-tested where practical)

* `EntityCatalog.GetEntities(IOrganizationService)` returns `IList<EntityInfo>`: `RetrieveAllEntitiesRequest(EntityFilters.Entity)`; exclude `IsIntersect == true` and `IsPrivate == true`; `EntityInfo { LogicalName, DisplayName (fallback SchemaName), SchemaName, ObjectTypeCode, PrimaryIdAttribute, PrimaryNameAttribute, IsActivity, IsVirtual }` (`IsVirtual` per `DataverseSchemaProvider.IsVirtualTable`, 5.2); sorted by DisplayName.
* `ViewService.GetViews(IOrganizationService, string entity, bool includePersonal)` returns `IList<ViewInfo> { Id, Name, IsPersonal, FetchXml, LayoutXml }`: `savedquery` where `returnedtypecode = entity`, `querytype = 0`, `statecode = 0`, `fetchxml` not null, ordered by name; then, when `includePersonal` is true - the UI always passes true (6) -, `userquery` (same filters, `querytype = 0`, ordered by name: the personal views the current user can read). Wrap each query in try/catch and log-through (e.g. `userquery` may be unavailable); never let a failing personal-view query hide the system views. If no views at all, the UI synthesises `(All records)`: `<fetch><entity name="x"><attribute name="{primaryname}"/><attribute name="{primaryid}"/><attribute name="createdon"/><order attribute="{primaryname}"/></entity></fetch>` with a matching layout.
* `LayoutParser.Parse(string layoutXml)` returns `IList<ViewColumn> { Name, Width }` from `<cell name=".." width=".."/>` (name may be `alias.attribute`); ignore cells whose name is empty; tolerate missing/invalid XML (return empty list).
* `FetchXmlHelper.ApplyPaging(string fetchXml, int page, int count, string pagingCookie)` returns a new fetch string with `count`, `page`, `paging-cookie` (XML-escaped) set on the root and any `top` attribute removed; also `EnsureAttribute(fetch, primaryIdAttribute)` so the id is always returned (add `<attribute name=".."/>` to the main entity if `<all-attributes/>` is absent and the attribute is not already listed).
* `RecordPager.Fetch(IOrganizationService, string fetchXml, int page, int pageSize, string cookie)` returns `RecordPage { EntityCollection Entities; bool MoreRecords; string PagingCookie }` using `FetchExpression`.
* `CellFormatter.Format(Entity e, string column)` returns a string: `FormattedValues[column]` when present; else by type: `EntityReference.Name` (fallback id), `AliasedValue` (unwrap then same rules), `Money.Value` ("N2"), `OptionSetValue.Value`, `OptionSetValueCollection` (joined), `DateTime` (`ToLocalTime()` `"g"`), `bool`, `byte[]` as "(image)", null as "".
* `FormSubgridService.GetMainFormSubgridRelationships(IOrganizationService source, string entity)`: the relationship names of the subgrids on the entity's active main forms (5.10).

### 5.8 Virtual tables and unverifiable lookups

Seen in practice (cloud to cloud): lookups to virtual tables whose rows live in an external data provider failed the existence query - in that destination ANY RetrieveMultiple/FetchXML on ANY virtual table failed (`Error calling into the virtual table data provider ... for query expression: Bad Request / A task was canceled.`, code -2147220891) while a `Retrieve` by id returned the row - and were blanked although the rows existed there.

* A table is virtual per `EntitySchema.IsVirtual` (5.2) of the destination schema, or of the source schema when the destination has no such entity (or its metadata cannot be read); decided once per entity per run.
* A virtual table is treated like a never-create entity: its records are never retrieved from the source, never created, never updated. A lookup (or party) to one is kept when the same id exists in the destination and blanked otherwise: Warning `Blanked {attr} on {entity} "{name}": {target} {id} does not exist in destination (virtual table, never created)`. When the table exists only in the source schema (the destination has no such entity), nothing is asked: `... does not exist in destination (virtual table, the destination has no such table)`.
* The existence check of a virtual row is `Retrieve(entity, id, ColumnSet(primaryId[, primaryName]))`, cached per (entity, id) for the run; the record-not-found fault (`IsRecordNotFound`) means "does not exist". Every other entity keeps the `QueryExpression` check (5.4).
* A virtual table as the selected entity is refused up front (5.5), and the UI refuses Copy for it (6).
* Unverifiable lookups: when the existence check of a never-create or virtual target throws anything other than record-not-found (a failing provider, a missing read privilege...), the lookup is **not** blanked. The reference is kept, the attribute is remembered in the record's `UnverifiedLookups`, and Warning `Kept {attr} on {entity} "{name}" unverified: {target} {id} could not be checked in destination: {error}`. The failure is cached per (entity, id) for the run - a broken provider is asked once per id - and later lookups to that id log the shorter `Kept {attr} on {entity} "{name}" unverified (check failed earlier): {target} {id}`. A party whose target cannot be checked is kept (`Kept party {target} {id} in {attr} on {entity} "{name}" unverified: could not be checked in destination: {error}`, or `... unverified (check failed earlier)`), and the party-list attribute as a whole becomes unverified. Dry run: `[DRY RUN] Would keep ... unverified ...`.
* If the create/update of a record with unverified lookups fails, it is retried without them (5.4), each one left out logged `Blanked {attr} on {entity} "{name}": {create|update} failed with the unverified lookup ({error})` and counted in `LookupsBlanked`.

### 5.9 State changes and closing states

Seen in practice: a Won opportunity (statecode 1, statuscode 3) was created, then the generic state update failed (`This message can not be used to set the state of opportunity to won. In order to set state of opportunity to won, use the won message instead.`), and a backfill into that opportunity (a custom lookup to a custom table that points back at it) came after the state step - had the Win worked, the opportunity would have been read-only by then.

* After a successful create or update, when the source `statecode`/`statuscode` differ from the destination's current state (after a create: 0 with the default status of 0; after an update: the state read by the existence check, after any reactivation), the change is **queued** (record, depth, state, status, the source record).
* The queue is applied in `Copy()` right after each SELECTED record's `EnsureRecord` returns (success or failure) - once every write into the records of that tree (creates, updates, backfills, deferred lookups (5.10)) is done - in the order the records were written; and once more at the end of the run as a safety net. On cancellation it is dropped with a Warning `{n} pending state change(s) not applied: run cancelled`, followed by one Warning per record: `{entity} "{name}" ({id}): statecode={s}, statuscode={t}`.
* A change is applied with the close message the platform requires where it refuses a plain update, otherwise with an `Update` of `statecode` + `statuscode` (same parameters as every update):

  | Entity | statecode | Request |
  |---|---|---|
  | opportunity | 1 Won, 2 Lost | `WinOpportunityRequest` / `LoseOpportunityRequest` { `OpportunityClose` = `opportunityclose` { `opportunityid`, `subject`, `actualrevenue` = the source's Actual Revenue (`actualvalue`; an `actualrevenue` column when there is no `actualvalue`), `actualend` = source `actualclosedate` - each only when present }, `Status` } |
  | incident | 1 Resolved | `CloseIncidentRequest` { `IncidentResolution` = `incidentresolution` { `incidentid`, `subject` }, `Status` }; 2 Cancelled: plain update |
  | quote | 2 Won, 3 Closed | `WinQuoteRequest` / `CloseQuoteRequest` { `QuoteClose` = `quoteclose` { `quoteid`, `subject` }, `Status` }; a quote still in Draft (0) is first activated with a plain update to statecode 1 (default status of 1), as only an active quote can be won or closed; 1 Active: plain update |
  | salesorder | 2 Cancelled, 3 Fulfilled | `CancelSalesOrderRequest` / `FulfillSalesOrderRequest` { `OrderClose` = `orderclose` { `salesorderid`, `subject` }, `Status` }; other states: plain update |

  `subject` = `Closed by Data Copier`; `Status` = the source statuscode, else the default status of that state, else -1 (the platform's default). The close messages carry `BypassCustomPluginExecution` when the option is on (not `SuppressDuplicateDetection`, which is for create/update).
* Log, at the record's own depth: Info `State applied to {entity} "{name}" ({id}): statecode={s}, statuscode={t}`, plus ` ({Message})` when a close message was used (` (activate + WinQuote)` for a draft quote); dry run `[DRY RUN] Would apply state to ...` (nothing sent, counted). A failure is a Warning `{Created|Updated} {entity} "{name}" ({id}) but state not applied[ ({Message})]: {error}` - the record stays copied and nothing counts as failed. Successes are counted in `StateChanges`.

### 5.10 Relationship options and child records (added 2026-10-01)

Two options, decided by the owner, persisted in the settings (6):

| Option | Default | Effect |
|---|---|---|
| **Create related records for N:1 relationships (lookups)** - `CopyOptions.CopyLookups` | ticked | Ticked: rule 7 as it always was (lookup targets created recursively, cycles backfilled). Unticked: no lookup target is ever created or updated - existence checks only (5.3 rule 7, last bullet). |
| **Create related records for 1:N relationships (subgrids)** - `CopyOptions.CopyChildren` + `ChildRelationshipSelector` | unticked | Ticked: the child records of the selected records are copied too, recursively (below). |

Both unticked: only the selected records are created/updated; lookups are kept only where the target already exists.

**Lookups not copied.** A lookup is kept if its target exists in the destination by the end of the selected record's copy, and blanked otherwise:
* Every lookup target that is not a never-create entity or a virtual table is checked like one (5.8): the per-run existence cache, the `QueryExpression` check (virtual tables keep their `Retrieve`), "kept unverified" when the check itself fails. A record already created or updated by this run counts as existing (the existence cache is updated on create).
* A target that is not there yet is not decided at once, since the selected record's tree may still copy it (e.g. an account's primary contact that is then copied as one of its children): the attribute is left out of the create/update and a **deferred lookup** (record, attribute, target, depth) is queued. The queue is resolved in `Copy()` right after the SELECTED record's `EnsureRecord` returns, before the tree's state changes (5.9). For each deferred lookup whose record was written (those of a record that failed are dropped silently): when the target is in the destination now - state Created/Updated/Exists in this run, or the existence check says so - the lookup is written with the backfill mechanics of 5.6 (an `Update` of that attribute with the same request parameters, Success `Backfilled {entity}.{attr} -> {target} ({id})`, counted in `LookupsBackfilled`; a failing update is Warning `Backfill of ... failed, lookup left blank: {error}`, counted in `LookupsBlanked`); otherwise Warning `Blanked {attr} on {entity} "{name}": {target} {id} does not exist in destination (lookups not copied)`, counted in `LookupsBlanked` at that point. Both are logged one level below the record's depth. Dry run: `[DRY RUN] Would backfill ...` / `[DRY RUN] Would blank ...`, at the same point. On cancellation the deferred lookups not yet resolved are reported like pending backfills, one Warning each: `Deferred lookup {entity}.{attr} -> {target} ({id}) not resolved: run cancelled, lookup left blank` (counted in `LookupsBlanked`). A target copied only by a LATER selected record does not count.
* Decided at once, as before: a target table the destination does not have (blanked the same way, without asking); never-create and virtual targets (exactly as with lookups copied - they are never created in the run); party-list members (party lists are not backfilled: a party whose record is not in the destination yet is dropped, `Dropped party ... does not exist in destination (lookups not copied; party lists are not backfilled)`).
* A lookup of a record to ITSELF is backfilled after its create (5.6), since the record is written by the run anyway.

**Child records.** When `CopyChildren` is on and a selector is set, after a record's create or update succeeds - or when it is found existing (skipped) - and only for a SELECTED record or a CHILD record, the engine walks its child records (`WalkChildren`):
* for each relationship the `IChildRelationshipSelector` returns for the record's entity (asked once per entity per run; a failure is a Warning `Could not determine the 1:N relationships of {entity}: {error}; its child records are not copied`), it pages through the SOURCE with a `QueryExpression` on the child entity: condition `ChildLookupAttribute Equal parentId`, `ColumnSet(primary id)` (+ `activitytypecode` for `activitypointer`), ordered by the primary id, `PageInfo { Count = 500, PageNumber, PagingCookie }` (a new query per page);
* when it finds n > 0 records it logs Info `Children of {entity} "{name}" via {relationship}: {n} {childEntity} record(s)` ("record" / "records") at the record's depth, adds n to `ChildRecordsFound`, and calls `EnsureRecord(childRef, depth + 1, isSelected: false, isChild: true)` for each, checking cancellation before every page and every child;
* a failing children query is a Warning `Could not list {relationship} children of {entity} "{name}": {error}` and the run continues; an unexpected failure inside the walk is a Warning `Child records of {entity} "{name}" not all copied: {error}` - the record itself stays copied;
* `activitypointer` rows become references to their concrete activity, `new EntityReference(activitytypecode, activityid)` (rows of a system-excluded, never-create or virtual type are skipped); the per-type relationships (`Account_Emails`...) may be ticked too: the record-state cache prevents double copies;
* relationships whose child entity is system-excluded, never-create or virtual are never queried, even if a selector returns them; one whose child entity the destination does not have is not followed, with one Warning per run: `1:N relationship {relationship} of {entity} not followed: {child} does not exist in destination`.

A child record that already exists in the destination is skipped like a related record (`Exists, skipped ...`, never updated), but its own children are still walked so that missing grandchildren are created; a child copied earlier in the run logs `Already copied earlier in this run: ...`. Each record is walked at most once per run, which bounds self-referencing hierarchies and cycles. The walk goes only DOWNWARD from the selected records and their children: never from records reached as lookup targets (N:1), never from never-create or virtual records. The children's own lookups follow `CopyLookups`. The walk comes after the record's backfills (5.6) and before the state changes of the selected record's tree (5.9). Dry run: the children queries still run (reads only) and creates are logged `[DRY RUN] Would create ...`.

**Eligibility** (`ChildRelationshipEligibility`, used by the picker and the selector alike). From the SOURCE entity's `OneToManyRelationships` (the `RetrieveEntityRequest` includes `EntityFilters.Relationships`), a relationship is eligible unless its child entity is on the built-in system exclusion list (`asyncoperation, syncerror, duplicaterecord, processsession, workflowlog, bulkdeletefailure, principalobjectattributeaccess, userentityinstancedata, mailboxtrackingfolder, customeraddress, actioncard, postfollow, postregarding, recordcountsnapshot, tracelog, activityparty`; `annotation` stays eligible), in the never-create list, missing from the source (or its metadata cannot be read), intersect, private or virtual; or its child lookup attribute is missing or not `IsValidForCreate`; or it is an `msdyn_` table that is not creatable (its primary id is not valid for create - `msdyn_` tables are not excluded as such). `activitypointer` children (e.g. `Account_ActivityPointers` via `regardingobjectid`) are eligible whatever the create flag of the lookup.

**Selections.** `DefaultChildRelationshipSelector(ISchemaProvider sourceSchema, Func<string, IReadOnlyCollection<string>> subgridRelationshipNames, IReadOnlyDictionary<string, ISet<string>> configuredSelections, ISet<string> neverCreate)`: for a configured entity (present in `configuredSelections`, even with an empty set) its ticked relationships; for any other entity the relationships that appear as subgrids on its active main forms in the source (computed lazily); in both cases only the eligible ones, in metadata order, names compared case-insensitively. Cached per entity for the selector's lifetime (the UI builds one per run); a failure propagates and is not cached. `IsConfigured(entity)` tells the two apart for the run header.

**Form subgrids.** `FormSubgridService.GetMainFormSubgridRelationships(IOrganizationService source, string entity)`: `systemform` where `objecttypecode = entity`, `type = 2` (main), `formactivationstate = 1`, column `formxml`; in each form every `<control>` whose `classid` is `{E7A81278-8635-4D9E-8D4D-59480B391C5B}` (GUID compare, case-insensitive) gives its `<parameters><RelationshipName>`, and so does any other `<control>` whose `<parameters>` hold a `RelationshipName` (modern/custom subgrid controls); empty names are ignored; distinct names in form order; a form whose XML cannot be read is skipped. Cached per entity per instance (one per run, one per picker session); a failing query propagates.

## 6. UI (`MyscotekDataCopier.UI.DataCopierControl : PluginControlBase, IGitHubPlugin, IHelpPlugin`)

Built in code (`BuildUi()` in a partial class), `Font = Segoe UI 9`, log in `Consolas 9`. Layout, top to bottom:

1. **ToolStrip** (GripStyle Hidden): `Select destination environment...` | sep | `Refresh entities` | sep | `Close` (right-aligned, `CloseTool()`), plus right-aligned `ToolStripLabel`s `Destination: (none)` and `Source: (none)`.
2. **SplitContainer (vertical)**.
   * Left (320 px, or the width the user dragged it to; narrower - down to 200 px - when the right side would get less than 560 px): label `Entities`, a filter `TextBox` (filters on display or logical name as you type), a `ListView` (Details, FullRowSelect, HideSelection=false, columns *Display name* / *Logical name*), sorted by display name. Selecting an entity loads its views. A virtual table (`EntityInfo.IsVirtual`) shows its logical name with the suffix ` (virtual)`; selecting one (or a never-create entity) logs that its records can be listed but not copied.
   * Right: a `SplitContainer (horizontal)`:
     * Top: row with `View:` `ComboBox` (DropDownList; the system views, then the personal views - always listed, suffixed ` (personal)` -, each group by name), `Button` `Load records`, `Button` `Load more`, `Button` `Load all`; a `TextBox` `Filter loaded records...` (client-side, all visible columns) with the `Label` `0 records loaded` at its right (that line never wraps, so the view row keeps one line on a narrow tool); a `DataGridView` (first column = checkbox `Copy`, then the view's columns from `LayoutParser`, values from `CellFormatter`; hidden id column; ReadOnly except the checkbox; AllowUserToAddRows=false; RowHeadersVisible=false; SelectionMode FullRowSelect; AutoSizeColumnsMode None with widths from layout). Below the grid: `Select all` / `Select none` (act on the *filtered* rows), `Label` `Selected: 0`. Options row: `CheckBox` `Dry run (write nothing)`, `CheckBox` `Preserve created on (overriddencreatedon)`, `CheckBox` `Bypass custom plugins (online only)`; then, on the row's second line (5.10), `CheckBox` `Create related records for N:1 relationships (lookups)` (default ticked), `CheckBox` `Create related records for 1:N relationships (subgrids)` (default unticked) and `Button` `Relationships...` (enabled when the 1:N option is ticked, an entity that can be copied - not never-create, not virtual - is selected and nothing runs). On a line of their own: `Button` **`Copy selected records`** (bold, enabled only when a destination is set, the grid has at least one checked row and no run is active), `Button` `Cancel` (enabled only during a run) and a progress `Label` taking the rest of the line (AutoEllipsis).
     * Resizing (the tool library checklist): every row of buttons wraps; the view list narrows (down to 120 px) to keep the load buttons on its line, or else to stay beside its label; the records area always gets the height its rows need at their current width plus a 90 px grid (its splitter minimum), the log at least 110 px; only a tool too small for both scrolls the records area. The fit runs after each layout pass (`QueueFitLayout` - a SplitterDistance set during a SplitContainer's own resize does not lay out the docked controls).
     * Bottom: label `Log`, `RichTextBox` (ReadOnly, white background, WordWrap off, DetectUrls false) with `Copy log`, `Save log...` (SaveFileDialog, .txt) and `Clear` buttons. Colours: Info = `SystemColors.WindowText`, Success = `ForestGreen`, Warning = `DarkGoldenrod`, Error = `Firebrick`. Prefix every line `[HH:mm:ss] `. Append via `BeginInvoke` guarded by `IsDisposed`/`IsHandleCreated`; `ScrollToCaret`. Cap at about 20 000 lines by trimming the top when exceeded.
3. Selection state must survive filtering: bind the grid to a `DataTable` (bool `Copy` column, hidden `__id` column, one string column per view column) through a `BindingSource` whose `Filter` implements the client-side filter (escape `'`, `[`, `%`, `*` properly). Track selected ids from the DataTable, not from visible rows.

Behaviour:
* **Connections.** Source = the normal XrmToolBox connection (`Service` / `ConnectionDetail`). Destination = a second connection requested as XrmToolBox's additional organisation: `RaiseRequestConnectionEvent(new RequestConnectionEventArgs { ActionName = "AdditionalOrganization", Parameter = "destination", Control = this })` (constants `DestinationActionName`, `DestinationParameter`). For that action name XrmToolBox leaves the tab's own connection alone - its status bar connection, the tab title and highlight and the connection it records for the tab stay the source's - and calls `UpdateConnection(service, detail, "AdditionalOrganization", "destination")`; any other action name would make the destination the tab's XrmToolBox connection and retitle the tab (read in XrmToolBox 1.2026.8.75; its own `MultipleConnectionsPluginControlBase` uses the same name). In the `UpdateConnection` override, when `actionName` equals that constant (the parameter is not checked: the tool asks for no other additional organisation) store `_destinationService` / `_destinationDetail`, reset the destination schema provider, refresh labels and log, and **return without calling base** (base would replace `Service`, which must stay the source, then look for a method named AdditionalOrganization). Otherwise call base FIRST (it sets `Service` / `ConnectionDetail` and, for a connection an action asked for, invokes that action's method by name - see below), then refresh labels and log the source; and reset everything (entities, views, grid, caches) and load the entity list asynchronously - unless the action was `Refresh entities`, which has just done that. Same-org guard: if source and destination resolve to the same organisation (same organisation name, or same web/service URL, or the same service), warn with a Yes/No dialog when the user presses Copy (not when selecting).
* **Actions that need the source** (the tool library checklist: without a connection they open XrmToolBox's connection dialog): `Refresh entities`, loading views (entity selected), `Load records` / `Load more` / `Load all`, `Relationships...` and `Copy` run through `PluginControlBase.ExecuteMethod(Action)`: at once when there is a source, else it raises `OnRequestConnection` with `ActionName` = the method's name and, once the user has connected, XrmToolBox calls `UpdateConnection(service, detail, thatName, null)` and the base class invokes the method by reflection (`Instance | Public | NonPublic`, no parameters). So each is a parameterless instance method with a unique name (`RefreshEntities`, `LoadSelectedEntityViews`, `LoadRecords`, `LoadMoreRecords`, `LoadAllRecords`, `ChooseRelationships`, `CopySelectedRecords`) - never a lambda, which ExecuteMethod refuses - and guards itself. Without a source, `Refresh entities` is enabled and asks for one; the others are disabled until there is something to act on. Controls that need no connection (filters, options, log buttons) work without one.
* **Long operations** (entity list, views, record pages, the copy itself) run with `Task.Run` + `async/await` + a `CancellationTokenSource`, NOT `WorkAsync`, so the log stays visible while the copy runs; nothing blocks the UI thread. While busy, disable the inputs (entity list, view combo, load buttons, options - the relationship options and `Relationships...` too -, Copy) and enable Cancel. Every handler is `async void` with try/catch that logs the error and shows `MessageBox.Show(this, ex.Message, "Data Copier", OK, Error)`.
* **Record loading**: page size from settings (default 500). `Load records` clears and loads page 1; `Load more` appends the next page; `Load all` keeps loading until `MoreRecords` is false or Cancel is pressed; the label shows `N records loaded (more available)` / `N records loaded (all)`.
* **Copy**: validate (destination set; at least one selected; entity not in the never-create list and not a virtual table - each refused with its reason in a message box and a `Copy refused: ...` log line; same-org confirmation), build `CopyOptions` from the checkboxes + settings (with the 1:N option, a `DefaultChildRelationshipSelector` over the cached source schema, a fresh `FormSubgridService` and the selections saved for the source organisation), construct `CopyEngine` with `DataverseSchemaProvider`s for source and destination (the destination provider is cached for the lifetime of that connection), log a header (`Copying N {entity} records from {source} to {destination}`, then `Options: dry run on|off, preserve created on on|off, bypass custom plugins on|off, create related records for N:1 relationships (lookups) on|off, create related records for 1:N relationships (subgrids) on|off; never created: ...`; with the 1:N option also - computed off the UI thread - `1:N relationships followed from {entity} ({ticked in Relationships...|the subgrids on its active main forms}): {schema (child.lookup), ...|none}` and `Other entities reached as child records follow the relationships ticked for them in Relationships..., otherwise the subgrids on their active main forms.`), run it, then log the summary block (created / updated / skipped / failed / lookups blanked / backfilled / child records found / state changes / elapsed, then the error lines). The progress is also pushed to the label next to Cancel (`3 / 25 - created 41, failed 1`; updated, skipped and `children found` when not zero). On `OperationCanceledException` log Warning `Cancelled by user` and still print the summary of what happened.
* **Relationship picker** (`Relationships...`, 5.10): a modal `RelationshipPickerForm` (built in code, Segoe UI 9, resizable, 700 x 500, `ShowDialog(this)`). A `TreeView` with check boxes: the root is the selected entity (`{DisplayName} ({logicalname})`, no check box), its children the eligible 1:N relationships, labelled `{child plural display name} ({child}) via {lookup} — {schema name}` plus ` [subgrid]` when it is on a main form and ` [custom]` for a custom relationship, subgrids first. Each relationship node expands lazily (a `Loading...` child + `BeforeExpand`) into the eligible relationships of its CHILD entity, so the tree is shaped level by level; an entity met again deeper works the same (loaded once per dialog; expansion is user-driven, never automatic). Metadata (the entity and its child entities) and main forms are read on `Task.Run` with a wait cursor and progress in a status line; errors are shown in a message box and the node can be expanded again to retry; forms that cannot be read pre-tick nothing (warning). Ticks belong to the ENTITY: the same entity shows the same ticks wherever it appears. Pre-ticked for an entity not configured: its main-form subgrids. An entity whose ticks are changed (a tick, or a button) becomes configured with the ticks it shows. Buttons `Tick subgrids`, `Tick custom` (add to the ticks), `Untick all`, acting on the relationships under the selected node (the child entity of a relationship; the root entity when the root or nothing is selected; loaded first if needed), then `OK` / `Cancel`. OK saves the configured entities of the session (plus those configured before) for the source organisation and logs them.
* **Settings** (`DataCopierSettings`: `DryRun, PreserveCreatedOn, BypassCustomPlugins, CopyLookups = true, CopyChildren = false, PageSize = 500, NeverCreateEntities = "systemuser,team,businessunit,organization,transactioncurrency", LastEntity, RelationshipSelections`) loaded in the constructor via `SettingsManager.Instance.TryLoad(typeof(DataCopierSettings), out ...)` and saved in `ClosingPlugin`, whenever a checkbox changes and when the picker is confirmed. Re-select `LastEntity` after the entity list loads if it exists. `RelationshipSelections` is a list of `RelationshipSelection { Organization (the source URL, lower case, no trailing slash; the organisation name when there is no URL), Entity, Configured, Relationships (schema names) }`; an entry that is not `Configured` is ignored; OK in the picker replaces the entries of that organisation. An element the class no longer has (`IncludePersonalViews`, dropped in 1.2026.10.2) is skipped when the file is read.
* Also route Error-level log lines to `LogError(...)` and the copy header/summary to `LogInfo(...)` (XrmToolBox's own log). Cheap and useful.

## 7. Tests (`tests\MyscotekDataCopier.Tests`, xUnit, net48)

`FakeOrganizationService : IOrganizationService`: in-memory store keyed by (entity, id); implements `Create`, `Update`, `Retrieve` (throws `FaultException<OrganizationServiceFault>` with `ErrorCode = -2147220969` when missing), `RetrieveMultiple` for the existence `QueryExpression` (primary id Equal) and `Execute` for `CreateRequest` / `UpdateRequest` / `RetrieveRequest`; records every request (with its `Parameters`) in an `Executed` list; `FailCreates` set of (entity or entity/id) that throw; `FailOverriddenCreatedOn` flag. `FakeSchemaProvider` with a fluent builder (`Entity("account").Lookup("primarycontactid","contact").String("name")...`).

Required test cases (at minimum):
1. simple record copied with same GUID and only copyable attributes;
2. parent lookup created before the child, same GUIDs, log order;
3. related record that already exists is skipped and not updated;
4. selected record that already exists is updated (and reactivated first if inactive);
5. failed related create is logged, the lookup is blanked, the selected record is still created; `Failed`/`LookupsBlanked` counts;
6. never-create entity: kept when it exists in destination, blanked (with warning) when not; never retrieved from source or created;
7. cycle account/contact produces one backfill update, with counts;
8. dry run issues no Create/Update/state requests but produces the same log shape;
9. attribute missing in destination is skipped and warned once;
10. `PreserveCreatedOn` maps `createdon` to `overriddencreatedon` on create only, and retry-without-it on failure;
11. `BypassCustomPluginExecution` and `SuppressDuplicateDetection` parameters are present on create/update requests;
12. state/status applied post-create when non-default, not applied when default;
13. party list: parties re-created, dropped when target unavailable;
14. cancellation stops the run and `Cancelled = true`;
15. `LayoutParser`, `FetchXmlHelper.ApplyPaging` (count/page/cookie set, `top` removed, cookie escaped), `EnsureAttribute`, `CellFormatter` basics;
16. virtual tables (5.8): `FromMetadata` detection (no provider, elastic provider, other provider, `TableType`) and `EntityInfo.IsVirtual`; a virtual target is checked with `Retrieve` (never `RetrieveMultiple`), never retrieved from the source or created; kept when it exists, blanked with the virtual wording when not; a selected virtual table is refused; the UI marks it `(virtual)` and refuses Copy;
17. unverifiable lookups (5.8): kept with a warning, the failed check cached for the run; a create/update that then fails is retried without them (counts), combined with the `overriddencreatedon` retry; party lists; dry run `Would keep`;
18. state changes (5.9): applied after the selected record's tree, AFTER the backfills into the record (order asserted on `Executed`); `WinOpportunity`/`LoseOpportunity` (with `OpportunityClose` and `Status`), `CloseIncident`, `WinQuote`/`CloseQuote` (draft activated first), `CancelSalesOrder`/`FulfillSalesOrder`; plain states deferred too; a failing close message is a warning; cancellation reports the pending changes; dry run names the message.
19. child records (5.10): copied after the parent with the parent lookup set (query shape: child entity, lookup Equal parent, primary id, 500 a page); two levels deep; an existing child skipped (not updated) but its children walked; an existing selected record updated and its children copied; children of a LOOKUP TARGET not copied; a selected record first copied as a lookup target still gets its children; a self-referencing hierarchy terminates (each record walked once); `activitypointer` children copied as their concrete types, once; system/never-create/virtual child entities never queried even if a selector names them; a child entity missing in the destination warned once; a failing children query and a failing selector are warnings; an unexpected failure in the walk does not fail the written record; paging with the cookie (501 children); dry run; cancellation inside the children loop; state changes after the children; `ChildRecordsFound` counts;
20. lookups not copied (5.10): existing targets kept, missing ones blanked with `(lookups not copied)` once the tree is done, nothing created or read from the source, unverifiable ones kept; party lists (decided at once); a self-reference backfilled; a table the destination lacks blanked without asking; a lookup to a record copied earlier as a child kept; a lookup to a record the tree copies later as a child backfilled after that child's create and before the state change (request parameters asserted); one whose record never appears blanked at the end of its tree, before the next selected record; dry run `Would backfill` / `Would blank` at the same point as the real run; cancellation reports the unresolved deferred lookups; both options off copy only the selected record;
21. eligibility and selections (5.10): every exclusion rule (system list, never-create, virtual, private, intersect, lookup not valid for create, `msdyn_` not creatable, missing/unreadable metadata) and the `activitypointer` exception; configured selection overrides the main-form subgrids; an unconfigured (deeper) entity follows its subgrids; configured-empty means none without metadata reads; caching (failures not cached). `FormSubgridService`: a realistic main form with two subgrids and a non-subgrid control; custom subgrid controls, empty names, duplicates, bad XML; only active main forms of the entity, read once; a failing query propagates. `DataverseSchemaProvider` maps the 1:N relationships, `IsPrivate`, the plural name and asks for `Relationships`;
22. UI (5.10): the new controls and their defaults; the settings round-trip (XmlSerializer) of `CopyLookups`, `CopyChildren`, `RelationshipSelections` and the per-organisation helpers, an old file's `IncludePersonalViews` element skipped; `Relationships...` follows the 1:N option and the entity (busy, never-create); the picker on its own (labels and order, root without a check box, lazy expansion, ticks synchronised per entity, the buttons, saved selections re-applied, Cancel, load and form failures) and end to end from the control (`ShowPicker` seam: OK saves per organisation, then a dry-run copy logs the header lines and walks two levels of children);
23. connections (6): without a source, Refresh entities raises `OnRequestConnection` with `ActionName` "RefreshEntities" and the simulated XrmToolBox callback (`UpdateConnection` with that name) loads the entities exactly once; with a destination but no source, Copy (forced on) asks for the source instead of failing and its callback runs the copy once; the destination request is an additional organisation (`ActionName` "AdditionalOrganization", `Parameter` "destination") and its callback never replaces `Service` / `ConnectionDetail`, with or without a source, nor reloads the source (that XrmToolBox then keeps the tab's own connection and title is host behaviour, not testable here); every control that needs the source asks for it, each `ActionName` resolving to a parameterless instance method; the controls that need no connection raise nothing and no error;
24. layout (6): at 800 x 500 and 1600 x 900 (and back) the toolbar spans the top with every item visible, the panels fill their splits, the rows stack without gaps or overlaps, the grid and the log fill what is left (grid at least 90 px), no control of a row is clipped or overlaps another; the entity list gives way on the small size and gets its width back (or the width the user dragged it to); 800 x 500 still fits with ticks hidden by the filter (the longest `Selected` text); at 640 x 400 the records area scrolls instead of overlapping;
25. plugin metadata (4): the seven export keys, the name and the one-sentence description, the two PNG images (32x32, 120x120), the assembly attributes, AssemblyVersion = FileVersion = InformationalVersion = the nuspec version, IGitHubPlugin / IHelpPlugin.

The fake refuses a plain `Update` into a closing state with the platform's wording, applies the close messages to the stored record (from the states the platform closes from), and can make `RetrieveMultiple` fail for an entity while `Retrieve` works (`FailRetrieveMultiple`) or make `Retrieve` fail (`FailRetrieves`), as virtual-table providers can. It evaluates any `QueryExpression` over its store - AND-ed Equal/Null/NotNull conditions, orders, `TopCount`, and `PageInfo` paging with a `<cookie page="N" />` paging cookie - which answers the children and `systemform` queries; a `RetrieveMultipleHandler` that returns null falls through to that evaluation. `FakeSchemaProvider` builds 1:N relationships (`OneToMany(schema, child, lookup, custom)`), `Private()`, `Intersect()`, plural names, and can make `GetEntity` fail per entity (`Failures`).

## 8. Build, test, package, deploy

```
dotnet build MyscotekDataCopier.sln -c Release
dotnet test  MyscotekDataCopier.sln -c Release
.\build-package.ps1
copy src\MyscotekDataCopier\bin\Release\MyscotekDataCopier.dll  %AppData%\MscrmTools\XrmToolBox\Plugins\
```
(`AppendTargetFrameworkToOutputPath=false` so the output is `bin\Release\`.) `build-package.ps1` builds, tests, refuses a DLL whose AssemblyVersion or FileVersion differs from the nuspec version, packs into `dist\` with `tools\nuget.exe` (downloaded when missing) and fails unless the package holds only `lib/net48/Plugins/MyscotekDataCopier.dll`, `images/icon-128.png` and NuGet's own metadata. A manual deployment must be skipped with a clear message if `XrmToolBox.exe` is running. Only `MyscotekDataCopier.dll` is deployed.
