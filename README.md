# Data Copier (XrmToolBox tool by Myscotek)

<img src="images/icon-80.png" alt="" width="80" align="right" />

Data Copier is an [XrmToolBox](https://www.xrmtoolbox.com) tool that copies the records you select
from a **source** Dataverse / Dynamics 365 environment (online, or on-premises 9.x) to a
**destination** environment, **keeping the same GUIDs**.

- The records a copied record points at through its lookups are copied first (parents before
  children), as deep as needed, so every lookup still resolves in the destination.
- Optionally the **child records** come along too - the records in the selected records' subgrids
  (an account's contacts, its activities...) and their children in turn - and the records
  **associated** with them through many-to-many (N:N) relationships, which are copied and then
  associated in the destination as well.
- Records that already exist are handled by clear rules; users, teams and currencies are never
  created; virtual tables are only checked; won opportunities, resolved cases and other closing
  states are applied the way the platform requires.
- A **dry run** shows everything that would happen without writing anything, and every step of a
  copy is written to a colour-coded log.

Typical uses: moving test or reference data between environments, reproducing a production record
(with everything it depends on) in a development environment, or seeding a new environment with a
consistent set of records whose ids must not change.

## Screenshots

<!-- Placeholder: add images/screenshot.png (the tool with records loaded and a dry-run log) and show it here with
     ![Data Copier](images/screenshot.png) -->
_Coming soon._

## Installing

In XrmToolBox, open the **Tool Library**, search for **Data Copier** and install it. It needs
XrmToolBox 1.2025.10.74 or later.

To install a build by hand, close XrmToolBox and copy `MyscotekDataCopier.dll` into
`%AppData%\MscrmTools\XrmToolBox\Plugins`. That one file is the whole tool: XrmToolBox provides
every other assembly it uses.

## Using the tool

1. **Connect to the source.** Open Data Copier and connect to the environment to copy **from** with
   the normal XrmToolBox connection bar (without a connection, **Refresh entities** and the other
   controls that need one open the connection dialog). The entity list on the left fills in; type in
   the box above it to filter on display or logical name. Virtual tables are marked `(virtual)`.
2. **Choose the destination.** Click **Select destination environment...** on the toolbar and pick
   the environment to copy **to**. The toolbar shows `Source: ...` and `Destination: ...`; the tab's
   XrmToolBox connection (status bar, tab title) stays the source. If the
   destination looks like the same organisation as the source, you are asked to confirm before
   anything is copied.
3. **Pick an entity and a view.** Select an entity: its system views are listed in **View**, then
   your personal views (suffixed `(personal)`), each sorted by name. An entity without views gets an
   `(All records)` view.
4. **Load the records.** **Load records** loads the first page of the view (500 records by default),
   **Load more** the next page, **Load all** every page until done or until you press **Cancel**;
   the count at the right of the filter box says how many are loaded and whether there are more.
   The columns are headed with their display names (a column of a related entity reads e.g.
   `Email (Primary Contact)`); hover over a header to see its logical name.
5. **Tick the records to copy** in the **Copy** column (click the box, or press Space on a row).
   **Filter loaded records...** narrows the grid on any visible column and keeps the ticks.
   **Select all** / **Select none** act on the rows the filter shows; `Selected: N` counts every
   ticked record and says how many the filter hides.
6. **Choose the options** (below). With **Create related records for 1:N and N:N relationships
   (subgrids)** ticked, use **Relationships...** to see and change which child records and associated
   records come along.
7. **Copy.** Click **Copy selected records**. The progress shows next to **Cancel**
   (`3 / 25 - created 41, failed 1`) and every step appears in the log. **Cancel** stops the copy
   before the next record; the summary of what was done is still logged.

While something runs, the inputs are disabled and **Cancel** is enabled; XrmToolBox stays responsive.

## Options

| Option | Effect |
|---|---|
| **Dry run (write nothing)** | Resolves everything and logs what would happen (`[DRY RUN] Would create ...`) without writing anything. The source and the destination are still read. |
| **Preserve created on (overriddencreatedon)** | On create, the source `createdon` date is written to `overriddencreatedon`. If the destination refuses it (a missing privilege), the record is created without it and a warning is logged. |
| **Bypass custom plugins (online only)** | Sends `BypassCustomPluginExecution` with every create, update and close message so custom plugins do not run. Dataverse online only, and the user needs the `prvBypassCustomPlugins` privilege. |
| **Create related records for N:1 relationships (lookups)** | Ticked (the default): the records the copied records point at are created first, recursively. Unticked: no lookup target is created or updated; a lookup is kept only if its record exists in the destination by the end of the selected record's copy (see [Lookups not copied](#lookups-not-copied)). |
| **Create related records for 1:N and N:N relationships (subgrids)** | Unticked by default. Ticked: the child records of the selected records are copied too, recursively (see [Child records](#child-records)), and so are the records associated with them through N:N relationships, which are then associated in the destination (see [Associated records (N:N)](#associated-records-nn)). **Relationships...** chooses the relationships. |

The options are remembered between sessions. With both relationship options unticked, only the
selected records are written.

### The Relationships picker

**Relationships...** opens a tree with the selected entity at the top and its 1:N and N:N
relationships below it, for example `Contacts (contact) via parentcustomerid - contact_customer_accounts [subgrid]`
or `Leads (lead) - accountleads_association [N:N] [subgrid]` (`[N:N]`: a many-to-many relationship;
`[subgrid]`: shown as a subgrid on the entity's active main forms; `[custom]`: a custom
relationship). Each relationship expands into the relationships of ITS entity, so you can go down
level by level. Ticks belong to the entity: once you change them, contact shows the same ticks
wherever it appears. **Tick subgrids**, **Tick custom** and **Untick all** act on the relationships
under the selected line.

By default every entity follows the subgrids on its active main forms in the source - except where
it is reached through an N:N relationship: there it is a *peer*, and under an `[N:N]` line nothing is
ticked until you tick it (the tree says `(peer: nothing is followed unless ticked)`). An entity whose
ticks you change keeps them (remembered per source organisation), however it is reached; every other
entity keeps following its subgrids, and as a peer follows nothing. Opening the tree reads the
metadata of the related entities, which can take a while on a large organisation the first time.

## What gets copied, and how

### Selected records

- Created in the destination with the same GUID.
- A record with that GUID that already exists is **updated** (overwritten) with the source values; an
  inactive one is reactivated first so it can be updated.
- Records of a never-created entity or of a virtual table (below) cannot be selected for copying:
  the copy is refused with the reason.

### Related records (lookups)

While **Create related records for N:1 relationships (lookups)** is ticked:

- Lookup, Customer, Owner and Regarding columns and activity party lists are followed, recursively.
  Many-to-many associations are not followed through lookups: see
  [Associated records (N:N)](#associated-records-nn).
- A related record that **already exists** in the destination is **skipped, never updated**
  (`Exists, skipped`).
- A related record that **fails to create** is logged as an error and skipped; the run continues and
  the lookup that pointed at it is left **blank** (with a warning).
- Circular references (an account whose primary contact belongs to that account) are handled: the
  second record is created without the lookup, which is **backfilled** with an update as soon as the
  first record exists.
- There is no depth limit in practice; a safety cap of 100 levels logs a warning if ever reached.

### Child records

While **Create related records for 1:N and N:N relationships (subgrids)** is ticked:

- After a selected record is created or updated - or found already there - the records that point at
  it through the chosen 1:N relationships are copied too, then THEIR child records, as deep as it goes.
  The log shows `Children of account "Contoso" via contact_customer_accounts: 3 contact records`.
- Only downward from the selected records: the children of a record pulled in through a lookup are
  never copied.
- A child record that already exists is skipped (not updated), but its own children are still copied
  when they are missing. Hierarchies and loops end because every record is handled once per run.
- Activities listed through the generic activity relationship are copied as what they are (email,
  task, phone call...).
- Tables the platform fills itself (system jobs, duplicate-detection records, activity parties,
  addresses...) are never followed; notes are.

### Associated records (N:N)

While **Create related records for 1:N and N:N relationships (subgrids)** is ticked, the many-to-many
(N:N) relationships chosen in **Relationships...** are followed too:

- The records associated with a selected or child record in the source - for example the leads of
  an account through `accountleads_association` - are its **peers**. A peer that the destination does
  not have is created, with the same GUID (its lookups follow **Create related records for N:1
  relationships (lookups)** like any record's); one that exists is skipped, never updated.
- Then the **association** itself is created in the destination - unless the two records are
  associated there already, which is skipped. The log shows
  `Associated records of account "Contoso" via accountleads_association: 2 lead records`, then
  `Associated account "Contoso" <-> lead "Jane Lead" via accountleads_association` or
  `Association exists, skipped ...` for each.
- **Peers are not children.** A peer's own relationships are followed only when its entity has ticks
  saved in **Relationships...**; a peer whose entity you have not configured brings nothing else
  along, not even its subgrids. (A record that is also selected, or a child record, still gets its
  own related records as such.)
- Self-referencing relationships (account to account) are followed from both ends, and every
  association keeps the direction it has in the source.
- A peer that cannot be copied skips its association, and an association the destination refuses is
  logged; both are warnings and the copy carries on.
- Associations are made before the status of the records is applied, so a record that ends up
  closed (a won opportunity, say) is associated first.
- Security and system relationships (roles, privileges, field security profiles, queues, positions,
  team membership, sharing) and relationships to users, teams, currencies and other never-created
  entities, or to virtual tables, are never followed. A relationship the destination does not have
  is skipped with one warning.

### Lookups not copied

While **Create related records for N:1 relationships (lookups)** is unticked:

- No record is created or updated because a lookup points at it. A lookup is kept if the record it
  points at exists in the destination by the end of the selected record's copy (it was there already,
  or the copy brought it along, e.g. as a child record), and left blank otherwise.
- A lookup to a record that is not there yet is decided once the selected record and everything
  copied with it are done: filled in with an update if the record was copied meanwhile, otherwise
  blanked. A record that only a later selected record brings along does not count: copy again.
- Activity parties are decided at once: a party whose record is not in the destination is dropped.

### Never-created entities

By default `systemuser`, `team`, `businessunit`, `organization` and `transactioncurrency` (this
includes `ownerid` on every record). Their records are never created nor read from the source: a
lookup to one is kept only if the **same GUID already exists** in the destination, otherwise it is
left blank with a warning. The list is the `NeverCreateEntities` setting.

### Virtual tables

Tables whose rows live in an external data source (a virtual table data provider) are treated like
never-created entities: never read from the source, never created or updated, and a lookup to one is
kept only if the same row exists in the destination. That is checked by id, because some providers
fail every query but still return a single row.

### Lookups that cannot be checked

If the destination cannot say whether a user, team, currency or virtual-table row exists (the
provider fails, or the destination user may not read that table), the lookup is **kept** and a
warning says `Kept ... unverified` and why. If the record then cannot be written with such a lookup,
it is retried once without it, and the lookup is logged as blanked.

### Columns

- Values are copied as they are, including choices, multi-select choices, money and images.
- Skipped: columns the destination does not have (one warning per column per run), system columns
  (created/modified by and on, owning user/team/business unit, version number, process and stage,
  exchange rate, image metadata), calculated and rollup columns, file columns, and columns the
  destination does not allow on create (or update).
- Activity parties are rebuilt with only the party, participation type and address; unresolved
  e-mail addresses are kept.
- Duplicate detection is suppressed on every create and update.

### Status and closing states

The status (`statecode`/`statuscode`) is applied when it is not the default, once the selected record
and everything it pulled in are done, so every lookup is written into a record before it can be
closed (a won opportunity is read-only). Closing states use the message the platform insists on: a
won or lost opportunity is closed with Win/Lose Opportunity (keeping the actual revenue and close
date), a resolved case with Close Incident, a won or closed quote with Win/Close Quote (a draft quote
is activated first) and a cancelled or fulfilled order with Cancel/Fulfill Sales Order; the close
activity they create is titled `Closed by Data Copier`. If the destination refuses a state, the
record stays copied and a warning is logged.

## The log

Every line is time-stamped and coloured: black for information, green for success, amber for
warnings, red for errors. A copy logs a header (entity, count, source, destination, options and, with
related records, the 1:N and N:N relationships followed), then each record with the records it pulls in
(indented by depth) and the outcome (`Created`, `Updated`, `Exists, skipped`, `FAILED`), every lookup
blanked, kept unverified or backfilled, every association, every column skipped and every state
change. It ends with a summary: created, updated, skipped, failed, lookups blanked and backfilled,
child records found, the peer records found and the associations created, skipped and failed (when
there are any), state changes, elapsed time and one line per error.

**Copy log** copies it to the clipboard, **Save log...** writes a text file, **Clear** empties it. The
window keeps the last 20,000 lines. Errors, and each copy's header and summary, also go to XrmToolBox's
own log (`%AppData%\MscrmTools\XrmToolBox\Logs\MyscotekDataCopier.log`).

## Settings

Stored by XrmToolBox in `%AppData%\MscrmTools\XrmToolBox\Settings\MyscotekDataCopier.xml`; edit the
file only while XrmToolBox is closed.

| Setting | Default | Meaning |
|---|---|---|
| `DryRun`, `PreserveCreatedOn`, `BypassCustomPlugins` | false | The option check boxes. |
| `CopyLookups` | true | **Create related records for N:1 relationships (lookups)**. |
| `CopyChildren` | false | **Create related records for 1:N and N:N relationships (subgrids)**. |
| `RelationshipSelections` | (empty) | The relationships chosen in **Relationships...**, per source organisation and entity. |
| `PageSize` | 500 | Records per page when loading (1 - 5000). |
| `NeverCreateEntities` | `systemuser,team,businessunit,organization,transactioncurrency` | Comma-separated logical names; blank restores the default. |
| `LastEntity` | | The entity selected again when the entity list loads. |

**Refresh entities** reloads the source entity list and forgets the cached metadata of both
environments, so schema changes on either side are picked up without reconnecting.

## Limitations

- File columns and audit history are not copied. Many-to-many associations are copied only with
  **Create related records for 1:N and N:N relationships (subgrids)**, for the relationships that
  option follows.
- Users, teams, business units, the organisation and currencies are never created: create or
  synchronise them in the destination with the same GUIDs first, or accept the blank lookups.
- A selected record that exists in the destination is always overwritten; a related or child record
  that exists is never updated.
- Records are written one at a time, so very large copies take a while.
- Plugins, workflows and flows of the destination run on every create and update; **Bypass custom
  plugins** (online only) skips the custom plugins, not the workflows or flows.
- Calculated and rollup columns are left to the destination to compute.

## Troubleshooting

- **Records fail with a privilege error.** The destination user needs create and update rights on
  every entity involved; Preserve created on and Bypass custom plugins need their own privileges.
- **Bypass custom plugins fails on-premises.** The parameter exists on Dataverse online only: untick it.
- **Many `Skipped attribute ... not present in destination` warnings.** The destination schema is
  behind the source: deploy the missing columns (then press **Refresh entities**) or accept the skip.
- **Lookups left blank to users, teams or currencies.** Those records are never created; see
  [Never-created entities](#never-created-entities).
- **`Kept ... unverified` warnings.** The destination could not tell whether the record exists
  (typically a failing virtual table provider or a missing read privilege). Check the provider or the
  privilege, then copy again.
- **`... but state not applied` warnings.** The record was copied but the destination refused its
  state (e.g. a business rule on closing). Set the state by hand, or fix the cause and copy again.
- **Too many (or too few) child or associated records.** Change the entity in **Relationships...**
  (under an `[N:N]` line, tick what a peer should bring along).
- **The tool does not appear after installing a build by hand.** Make sure XrmToolBox was closed
  while copying the DLL. As a last resort, close XrmToolBox and delete `Plugins\manifest.json` (it is
  rebuilt on the next start).

## Building from source

Requirements: Windows, the .NET SDK (the projects target .NET Framework 4.8) and PowerShell.

```
dotnet build MyscotekDataCopier.sln -c Release
dotnet test  MyscotekDataCopier.sln -c Release
.\build-package.ps1
```

`build-package.ps1` builds, runs the tests, checks that the assembly version equals the package
version, packs `Myscotek.DataCopier.nuspec` into `dist\` (downloading `nuget.exe` into `tools\` if
needed) and checks that the package holds only `MyscotekDataCopier.dll` and its icon.

The tests run the copy engine and its services against in-memory fakes of the source and the
destination, and drive the tool's real control end to end: connecting (including the connection
requests made without one), loading, filtering, ticking, dry-run copies with child and associated
records, the guards, errors and Cancel, the relationship picker (1:N and N:N) and the layout at
different sizes. The design contract for contributors is [SPEC.md](SPEC.md); engineering notes are
in [CLAUDE.md](CLAUDE.md).

## Licence

[MIT](LICENSE) - Copyright (c) 2026 Myscotek.
