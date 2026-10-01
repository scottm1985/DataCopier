using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Core
{
    /// <summary>
    /// Copies selected records from a source to a destination organisation keeping their GUIDs,
    /// recursively creating the records they point at through N:1 lookups (parents first).
    /// Implements SPEC.md sections 5.3 - 5.6, 5.8 (virtual tables and unverifiable lookups), 5.9
    /// (state changes and closing states) and 5.10 (relationship options: lookups not copied, the
    /// child records reached through 1:N relationships, and the peers reached through N:N relationships
    /// with their associations). One instance may run <see cref="Copy"/> repeatedly (each run starts
    /// with fresh state) but not concurrently.
    /// </summary>
    public sealed class CopyEngine
    {
        /// <summary>SPEC 5.3 rule 4: attributes that are never written (createdon is handled separately).</summary>
        internal static readonly HashSet<string> AlwaysSkippedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "createdby", "createdonbehalfby", "modifiedby", "modifiedon", "modifiedonbehalfby",
            "owninguser", "owningteam", "owningbusinessunit", "versionnumber", "statecode", "statuscode",
            "processid", "stageid", "traversedpath", "entityimage_timestamp", "entityimage_url", "entityimageid",
            "exchangerate", "overriddencreatedon"
        };

        internal const string DryRunPrefix = "[DRY RUN] ";

        /// <summary>Subject of the close activity a close message creates (opportunityclose, incidentresolution, quoteclose, orderclose).</summary>
        internal const string CloseActivitySubject = "Closed by Data Copier";

        private const string CreatedOn = "createdon";
        private const string OverriddenCreatedOn = "overriddencreatedon";
        private const string StateCode = "statecode";
        private const string StatusCode = "statuscode";
        private const int ObjectDoesNotExistErrorCode = -2147220969;   // 0x80040217
        private const int DuplicateRecordErrorCode = -2147220937;      // 0x80040237: "Cannot insert duplicate key."
        private const int MaxNameLength = 100;
        private const int QuoteDraft = 0;
        private const int QuoteActive = 1;
        private const string ActivityTypeCode = "activitytypecode";

        /// <summary>Why a lookup is blanked while lookups are not copied (SPEC 5.10).</summary>
        private const string NotCopiedProblem = "does not exist in destination (lookups not copied)";

        /// <summary>Why a party is dropped while lookups are not copied: parties are decided at once, never deferred.</summary>
        private const string NotCopiedPartyProblem = "does not exist in destination (lookups not copied; party lists are not backfilled)";

        /// <summary>Records per page when listing child records (SPEC 5.10).</summary>
        internal const int ChildPageSize = 500;

        private readonly IOrganizationService _source;
        private readonly IOrganizationService _destination;
        private readonly ISchemaProvider _sourceSchema;
        private readonly ISchemaProvider _destinationSchema;
        private readonly CopyOptions _options;
        private readonly ICopyLogger _logger;

        // ---- per-run state (reset by ResetRun) ----
        private Dictionary<RecordKey, RecordState> _states;
        private Dictionary<RecordKey, ExistenceInfo> _existence;
        private Dictionary<RecordKey, string> _failedChecks;          // existence checks of never-create/virtual targets that failed: the error
        private Dictionary<string, TargetKind> _targetKinds;          // how the records of each entity are treated as lookup targets
        private Dictionary<RecordKey, string> _names;
        private Dictionary<RecordKey, List<Backfill>> _backfills;
        private Queue<DeferredLookup> _deferredLookups;               // lookups not copied, record not there yet: decided when the selected record's tree is done
        private Queue<PendingState> _pendingStates;
        private HashSet<string> _warnedAttributes;
        private Dictionary<RecordKey, RelationshipContext> _walked;   // records whose relationships were followed, and as what (once per run)
        private Dictionary<RecordKey, HashSet<string>> _followed;     // per record: the relationships (1:N and N:N) followed from it
        private Dictionary<string, IReadOnlyList<ChildRelationship>> _childRelationships;   // per entity and context: the 1:N relationships followed
        private Dictionary<string, IReadOnlyList<ManyToManyRelationship>> _manyToManyRelationships;   // per entity and context: the N:N ones
        private HashSet<string> _warnedRelationships;                 // entity|relationship: "not followed" warned once per run
        private Dictionary<AssociationKey, bool> _associations;       // pairs checked in (or associated by this run in) the destination
        private HashSet<string> _uncheckedRelationships;              // N:N relationships whose destination intersect could not be queried
        private List<string> _errors;
        private int _selectedIndex, _selectedTotal;
        private int _created, _updated, _skippedExisting, _failed, _lookupsBlanked, _lookupsBackfilled, _stateChanges, _childRecordsFound;
        private int _peerRecordsFound, _associationsCreated, _associationsSkipped, _associationsFailed;
        private string _currentRecord;
        private IProgress<CopyProgress> _progress;
        private CancellationToken _cancellationToken;

        public CopyEngine(IOrganizationService source, IOrganizationService destination,
                          ISchemaProvider sourceSchema, ISchemaProvider destinationSchema,
                          CopyOptions options, ICopyLogger logger)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _destination = destination ?? throw new ArgumentNullException(nameof(destination));
            _sourceSchema = sourceSchema ?? throw new ArgumentNullException(nameof(sourceSchema));
            _destinationSchema = destinationSchema ?? throw new ArgumentNullException(nameof(destinationSchema));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ResetRun(0, null, CancellationToken.None);
        }

        /// <summary>
        /// The summary of the most recent <see cref="Copy"/> call, set even when that call threw:
        /// after a cancellation it holds the partial counts with <see cref="CopySummary.Cancelled"/> = true,
        /// so the UI can print what happened. Null before the first run.
        /// </summary>
        public CopySummary LastSummary { get; private set; }

        /// <summary>
        /// Copies the selected records (and, recursively, their lookup targets). Never throws for a
        /// per-record failure; throws OperationCanceledException on cancellation and lets genuine setup
        /// failures (e.g. selected entity missing in destination) propagate after logging them.
        /// In every case <see cref="LastSummary"/> is set before the method returns or throws.
        /// </summary>
        public CopySummary Copy(string entityLogicalName, IReadOnlyList<Guid> selectedIds,
                                IProgress<CopyProgress> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(entityLogicalName)) throw new ArgumentException("An entity logical name is required.", nameof(entityLogicalName));
            if (selectedIds == null) throw new ArgumentNullException(nameof(selectedIds));

            ResetRun(selectedIds.Count, progress, cancellationToken);
            LastSummary = null;
            var stopwatch = Stopwatch.StartNew();
            bool cancelled = false;
            try
            {
                Run(entityLogicalName.Trim().ToLowerInvariant(), selectedIds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                AbandonPendingBackfills();
                AbandonDeferredLookups();
                AbandonPendingStates();
                throw;
            }
            finally
            {
                stopwatch.Stop();
                LastSummary = BuildSummary(stopwatch.Elapsed, cancelled);
            }
            return LastSummary;
        }

        /// <summary>Why a virtual table cannot be copied (logged by the engine, shown by the UI).</summary>
        internal static string VirtualTableRefusal(string entity) =>
            $"{entity} is a virtual table: its rows live in an external data source and cannot be created here.";

        // =====================================================================================
        // Run loop
        // =====================================================================================

        private void Run(string entity, IReadOnlyList<Guid> selectedIds)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_options.NeverCreateEntities.Contains(entity))
            {
                string list = string.Join(", ", _options.NeverCreateEntities.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                Refuse($"{entity} is in the never-create list ({list}): its records are never created, so they cannot be copied.");
                return;
            }

            // A virtual table - by the destination schema, or by the source's when the destination has
            // no such entity - is refused the same way: its rows cannot be created (SPEC 5.8).
            EntitySchema destination = ReadSchema(_destinationSchema, entity, "destination");
            bool isVirtual = destination != null
                ? destination.IsVirtual
                : ReadSchema(_sourceSchema, entity, "source")?.IsVirtual == true;
            if (isVirtual)
            {
                Refuse(VirtualTableRefusal(entity));
                return;
            }
            RequireSchema(destination, entity, "destination");
            RequireSchema(ReadSchema(_sourceSchema, entity, "source"), entity, "source");

            var seen = new HashSet<Guid>();
            for (int i = 0; i < selectedIds.Count; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _selectedIndex = i + 1;
                Guid id = selectedIds[i];
                _currentRecord = $"{entity} ({id})";

                if (!seen.Add(id))
                {
                    Log(LogLevel.Info, 0, $"Skipped duplicate selection {entity} ({id})");
                }
                else
                {
                    try
                    {
                        EnsureRecord(new EntityReference(entity, id), 0, isSelected: true);
                    }
                    catch (Exception ex) when (!IsCancellation(ex))
                    {
                        // EnsureRecord turns per-record failures into log lines itself; this guard only
                        // stops an unexpected exception from ending the run.
                        string message = $"FAILED to copy {entity} ({id}): {ErrorText(ex)}";
                        Log(LogLevel.Error, 0, message);
                        _errors.Add(message);
                        _failed++;
                    }

                    // Lookups not copied: the lookups whose record was not in the destination yet are
                    // decided now that the whole tree is written - kept when the tree copied the record.
                    ResolveDeferredLookups();

                    // Every write into the records of this tree is done (creates, updates, backfills,
                    // deferred lookups), so their state changes can be applied now: a closed record may
                    // be read-only.
                    ApplyPendingStates();
                }
                ReportProgress();
            }

            // Safety nets: nothing is normally left at this point.
            ResolveDeferredLookups();
            ApplyPendingStates();
        }

        /// <summary>A selected entity that cannot be copied at all: logged as an error, nothing is read or written.</summary>
        private void Refuse(string refusal)
        {
            Log(LogLevel.Error, 0, refusal);
            _errors.Add(refusal);
        }

        /// <summary>The entity's schema, null when the entity does not exist; a failing read is logged and rethrown.</summary>
        private EntitySchema ReadSchema(ISchemaProvider provider, string entity, string side)
        {
            try
            {
                return provider.GetEntity(entity);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                string failure = $"Could not read the {side} metadata of {entity}: {ErrorText(ex)}";
                Log(LogLevel.Error, 0, failure);
                _errors.Add(failure);
                throw;
            }
        }

        private void RequireSchema(EntitySchema schema, string entity, string side)
        {
            if (schema != null) return;
            string missing = $"Entity {entity} does not exist in the {side} environment.";
            Log(LogLevel.Error, 0, missing);
            _errors.Add(missing);
            throw new InvalidOperationException(missing);
        }

        // =====================================================================================
        // EnsureRecord (SPEC 5.5)
        // =====================================================================================

        /// <param name="isSelected">A record the user ticked (updated when it exists).</param>
        /// <param name="isChild">
        /// A child record reached through a 1:N relationship (SPEC 5.10): like a lookup target it is
        /// skipped when it exists, but its own child records are copied too.
        /// </param>
        /// <param name="isPeer">
        /// A peer reached through an N:N relationship (SPEC 5.10): like a lookup target it is skipped when
        /// it exists; its own relationships are followed only when its entity is configured in the picker.
        /// </param>
        private EnsureResult EnsureRecord(EntityReference reference, int depth, bool isSelected, bool isChild = false, bool isPeer = false)
        {
            var key = new RecordKey(reference.LogicalName, reference.Id);
            RelationshipContext context = isPeer ? RelationshipContext.Peer : RelationshipContext.SelectedOrChild;
            _states.TryGetValue(key, out RecordState state);
            switch (state)
            {
                case RecordState.Created:
                case RecordState.Updated:
                    if (isSelected || isChild || isPeer)
                    {
                        Log(LogLevel.Info, depth, $"Already copied earlier in this run: {Describe(key, NameOf(key))}");
                        // First copied as a lookup target (or a peer), perhaps: as a selected, child or peer
                        // record its own relationships are due now (WalkRelationships does each record once).
                        WalkRelationships(key, NameOf(key), depth, context);
                    }
                    return EnsureResult.Available;
                case RecordState.Exists:
                    // A related record is never updated. A SELECTED record first met as an existing
                    // lookup target must still be overwritten (decision: always update), so carry on.
                    if (!isSelected)
                    {
                        // An existing child or peer is skipped; its own related records are not.
                        if (isChild || isPeer) WalkRelationships(key, NameOf(key), depth, context);
                        return EnsureResult.Available;
                    }
                    break;
                case RecordState.Failed:
                case RecordState.Missing:
                    if (isSelected) Log(LogLevel.Warning, depth, $"{Describe(key, NameOf(key))} could not be copied earlier in this run, skipped");
                    return EnsureResult.Unavailable;
                case RecordState.InProgress:
                    return EnsureResult.Deferred;   // cycle: the caller omits the lookup and queues a backfill
            }

            var record = new RecordContext(key, depth, isSelected, isChild, isPeer, CleanName(reference.Name));
            try
            {
                return EnsureRecordCore(record);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                return FailRecord(record, "copy", ex);
            }
        }

        private EnsureResult EnsureRecordCore(RecordContext record)
        {
            RecordKey key = record.Key;
            int depth = record.Depth;

            if (depth > _options.MaxDepth)
            {
                Log(LogLevel.Warning, depth, $"Maximum depth ({_options.MaxDepth}) reached at {key.Entity} {key.Id}: not copied");
                _states[key] = RecordState.Failed;
                return EnsureResult.Unavailable;
            }

            // Never-create entities and virtual tables are only checked for existence: never retrieved
            // from the source, never created or updated. (Lookups to them are resolved by ResolveLookup
            // and RebuildPartyList without coming here; this keeps the rule for any other caller.)
            bool neverCreate = _options.NeverCreateEntities.Contains(key.Entity);
            if (neverCreate)
            {
                record.Destination = TryGetSchema(_destinationSchema, key.Entity, out _);
            }
            else
            {
                record.Destination = _destinationSchema.GetEntity(key.Entity);
                if (record.Destination == null)
                {
                    Log(LogLevel.Warning, depth, $"Entity {key.Entity} does not exist in destination: {key.Entity} {key.Id} not copied");
                    _states[key] = RecordState.Failed;
                    return EnsureResult.Unavailable;
                }
                if (!record.Destination.IsVirtual)
                {
                    record.Source = _sourceSchema.GetEntity(key.Entity);
                    if (record.Source == null)
                    {
                        Log(LogLevel.Warning, depth, $"Entity {key.Entity} does not exist in source: {key.Entity} {key.Id} not copied");
                        _states[key] = RecordState.Failed;
                        return EnsureResult.Unavailable;
                    }
                }
            }
            bool isVirtual = record.Destination != null && record.Destination.IsVirtual;

            ExistenceInfo existing = GetExistence(key, record.Destination, isVirtual);
            record.Existing = existing;

            // Related records that already exist are skipped, never updated. (A selected record is
            // only skipped here if UpdateExistingSelectedRecords was switched off.)
            if (existing.Exists && (!record.IsSelected || !_options.UpdateExistingSelectedRecords))
            {
                _states[key] = RecordState.Exists;
                string existingName = FirstNonEmpty(existing.Name, record.Name);
                Remember(key, existingName);
                Log(LogLevel.Info, depth, $"Exists, skipped {Describe(key, existingName)}");
                _skippedExisting++;
                // Not written, but the related records of a selected, child or peer record are still copied (SPEC 5.10).
                if (record.IsSelected || record.IsChild || record.IsPeer) WalkRelationships(key, existingName, depth, record.Context);
                return EnsureResult.Available;
            }

            if (neverCreate || isVirtual)
            {
                // Never retrieved from the source, never created.
                if (existing.Exists)
                {
                    _states[key] = RecordState.Exists;
                    return EnsureResult.Available;
                }
                _states[key] = RecordState.Missing;
                Log(LogLevel.Warning, depth, isVirtual
                    ? $"{key.Entity} {key.Id} does not exist in destination (virtual table, never created)"
                    : $"{key.Entity} {key.Id} does not exist in destination and {key.Entity} records are never created");
                return EnsureResult.Unavailable;
            }

            Entity source = RetrieveSource(key);
            if (source == null)
            {
                _states[key] = RecordState.Missing;
                Log(LogLevel.Warning, depth, $"{key.Entity} {key.Id} not found in source");
                return EnsureResult.Unavailable;
            }

            record.Name = FirstNonEmpty(PrimaryName(source, record), record.Name);
            Remember(key, record.Name);
            _currentRecord = DescribeOwner(key, record.Name);
            _states[key] = RecordState.InProgress;
            Log(LogLevel.Info, depth, $"Copying {Describe(key, record.Name)}...");

            record.IsUpdate = record.IsSelected && existing.Exists;
            Entity target = BuildTarget(source, record);

            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (record.IsUpdate) UpdateRecord(target, record);
                else CreateRecord(target, record);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                return FailRecord(record, record.IsUpdate ? "update" : "create", ex);
            }

            QueueState(source, record);
            ApplyBackfills(key, depth);
            // Child records, peers and associations come after the backfills into this record and before
            // the state changes of the selected record's tree (SPEC 5.10); only for selected, child and
            // peer records.
            if (record.IsSelected || record.IsChild || record.IsPeer) WalkRelationships(key, record.Name, depth, record.Context);
            return EnsureResult.Available;
        }

        /// <summary>Logs and counts a record failure; the run continues.</summary>
        private EnsureResult FailRecord(RecordContext record, string verb, Exception ex)
        {
            RecordKey key = record.Key;
            string message = $"FAILED to {verb} {Describe(key, record.Name)}: {ErrorText(ex)}";
            Log(LogLevel.Error, record.Depth, message);
            _errors.Add(message);
            _failed++;

            // A selected record whose UPDATE failed still exists in the destination with the same
            // GUID, so lookups (and queued backfills) that point at it remain valid.
            bool stillExists = record.Existing != null && record.Existing.Exists;
            _states[key] = stillExists ? RecordState.Exists : RecordState.Failed;
            if (stillExists) ApplyBackfills(key, record.Depth);
            else DropBackfills(key, record.Depth);

            ReportProgress();
            return stillExists ? EnsureResult.Available : EnsureResult.Unavailable;
        }

        // =====================================================================================
        // Building the destination entity (SPEC 5.3)
        // =====================================================================================

        private Entity BuildTarget(Entity source, RecordContext record)
        {
            RecordKey key = record.Key;
            EntitySchema destination = record.Destination;
            bool isCreate = !record.IsUpdate;
            var target = new Entity(key.Entity) { Id = key.Id };

            foreach (KeyValuePair<string, object> pair in source.Attributes.ToList())
            {
                string attribute = pair.Key;
                object value = pair.Value;
                if (string.IsNullOrEmpty(attribute)) continue;

                // 1. Must exist in the destination; warn once per entity+attribute per run.
                if (!destination.Attributes.TryGetValue(attribute, out AttributeSchema meta) || meta == null)
                {
                    if (_warnedAttributes.Add(key.Entity + "|" + attribute.ToLowerInvariant()))
                    {
                        Log(LogLevel.Warning, record.Depth + 1, $"Skipped attribute {attribute} on {key.Entity}: not present in destination");
                    }
                    continue;
                }

                // 2. Null values are not written.
                if (value == null) continue;

                // 3. The primary id travels as Entity.Id.
                if (IsPrimaryId(attribute, record)) continue;

                // 4. System attributes; createdon -> overriddencreatedon on create when asked.
                if (string.Equals(attribute, CreatedOn, StringComparison.OrdinalIgnoreCase))
                {
                    if (isCreate && _options.PreserveCreatedOn
                        && destination.Attributes.TryGetValue(OverriddenCreatedOn, out AttributeSchema overridden)
                        && overridden != null && overridden.IsValidForCreate)
                    {
                        target[OverriddenCreatedOn] = value;
                    }
                    continue;
                }
                if (AlwaysSkippedAttributes.Contains(attribute)) continue;

                // 5. Derived, calculated, rollup, file, virtual and non-data types.
                if (!IsCopyableType(meta)) continue;

                // 6. Valid for the operation being performed.
                if (isCreate ? !meta.IsValidForCreate : !meta.IsValidForUpdate) continue;

                switch (value)
                {
                    case EntityReference reference:        // 7. lookups
                        ResolveLookup(target, attribute, meta, reference, record);
                        break;
                    case EntityCollection parties:         // 8. party lists
                        EntityCollection rebuilt = RebuildPartyList(parties, attribute, meta, record);
                        if (rebuilt.Entities.Count > 0) target[attribute] = rebuilt;
                        break;
                    default:                               // 9. everything else as-is
                        target[attribute] = value;
                        break;
                }
            }
            return target;
        }

        /// <summary>SPEC 5.3 rule 5 (plus image columns, which are Virtual-typed but hold byte[] data).</summary>
        internal static bool IsCopyableType(AttributeSchema meta)
        {
            if (!string.IsNullOrEmpty(meta.AttributeOf)) return false;
            if (meta.SourceType != 0) return false;
            if (meta.IsFile) return false;
            if (meta.AttributeType == AttributeTypeCode.Virtual && !meta.IsMultiSelect && !meta.IsImage) return false;
            switch (meta.AttributeType)
            {
                case AttributeTypeCode.ManagedProperty:
                case AttributeTypeCode.EntityName:
                case AttributeTypeCode.CalendarRules:
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsPrimaryId(string attribute, RecordContext record) =>
            string.Equals(attribute, record.Destination.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase)
            || (record.Source != null && string.Equals(attribute, record.Source.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase));

        /// <summary>SPEC 5.3 rule 7 (and 5.8 for never-create and virtual targets, 5.10 when lookups are not copied).</summary>
        private void ResolveLookup(Entity target, string attribute, AttributeSchema meta, EntityReference reference, RecordContext record)
        {
            if (reference.Id == Guid.Empty) return;   // an empty reference carries no value
            int childDepth = record.Depth + 1;
            string owner = DescribeOwner(record.Key, record.Name);
            string targetEntity = TargetEntity(reference, meta);
            if (targetEntity == null)
            {
                Log(LogLevel.Warning, childDepth, $"Blanked {attribute} on {owner}: the reference to {reference.Id} has no entity name");
                _lookupsBlanked++;
                return;
            }

            TargetKind kind = Classify(targetEntity);
            if (kind.ExistenceOnly)
            {
                // A never-create entity or a virtual table - or any target while lookups are not copied:
                // the lookup is kept when the same id exists in the destination, blanked when it does
                // not, and kept unverified when nobody can tell. While lookups are not copied, a record
                // that is not there yet is decided only once the selected record's tree is done.
                TargetCheck check = CheckTarget(kind, targetEntity, reference.Id);
                if (check.Exists == false)
                {
                    var targetKey = new RecordKey(targetEntity, reference.Id);
                    if (kind.NotCopied && IsInProgress(targetKey))
                    {
                        // The record being written itself (a self-reference): this run creates it anyway,
                        // so the lookup is backfilled once it exists, as with lookups copied.
                        QueueBackfill(record, attribute, targetKey);
                        return;
                    }
                    if (kind.NotCopied && !kind.MissingInDestination)
                    {
                        // Not there YET: this selected record's tree may still copy it (e.g. as a child
                        // record). Left out of the write for now and decided once the tree is done
                        // (ResolveDeferredLookups). Never-create and virtual targets, and tables the
                        // destination does not have, are never created in the run: decided now.
                        _deferredLookups.Enqueue(new DeferredLookup(record, attribute, targetKey));
                        return;
                    }
                    Log(LogLevel.Warning, childDepth, $"Blanked {attribute} on {owner}: {targetEntity} {reference.Id} {check.Problem}");
                    _lookupsBlanked++;
                    return;
                }

                target[attribute] = new EntityReference(targetEntity, reference.Id);   // resolved: not logged
                if (check.Exists == null)
                {
                    record.MarkUnverified(attribute);
                    Log(LogLevel.Warning, childDepth, check.FailedEarlier
                        ? $"{KeepVerb()} {attribute} on {owner} unverified (check failed earlier): {targetEntity} {reference.Id}"
                        : $"{KeepVerb()} {attribute} on {owner} unverified: {targetEntity} {reference.Id} could not be checked in destination: {check.Error}");
                }
                return;
            }

            switch (EnsureRecord(new EntityReference(targetEntity, reference.Id) { Name = reference.Name }, childDepth, isSelected: false))
            {
                case EnsureResult.Available:
                    target[attribute] = new EntityReference(targetEntity, reference.Id);
                    break;
                case EnsureResult.Unavailable:
                    Log(LogLevel.Warning, childDepth, $"Blanked {attribute} on {owner}: related {targetEntity} {reference.Id} could not be copied");
                    _lookupsBlanked++;
                    break;
                case EnsureResult.Deferred:
                    QueueBackfill(record, attribute, new RecordKey(targetEntity, reference.Id));
                    break;
            }
        }

        /// <summary>
        /// SPEC 5.3 rule 8: rebuild an activity-party list keeping only partyid, participationtypemask
        /// and addressused. Parties whose target is unavailable or still in progress are dropped (party
        /// lists are not backfilled); address-only parties (unresolved e-mail addresses) are kept. A
        /// party whose never-create or virtual target cannot be checked is kept, and then the whole list
        /// counts as an unverified lookup (SPEC 5.8).
        /// </summary>
        private EntityCollection RebuildPartyList(EntityCollection parties, string attribute, AttributeSchema meta, RecordContext record)
        {
            var rebuilt = new EntityCollection { EntityName = string.IsNullOrEmpty(parties.EntityName) ? "activityparty" : parties.EntityName };
            int childDepth = record.Depth + 1;
            string owner = DescribeOwner(record.Key, record.Name);
            bool unverified = false;

            foreach (Entity party in parties.Entities)
            {
                if (party == null) continue;
                EntityReference partyId = party.Attributes.TryGetValue("partyid", out object partyValue) ? partyValue as EntityReference : null;
                string addressUsed = party.Attributes.TryGetValue("addressused", out object addressValue) ? addressValue as string : null;
                var copy = new Entity(string.IsNullOrEmpty(party.LogicalName) ? "activityparty" : party.LogicalName);
                if (partyId != null && partyId.Id == Guid.Empty) partyId = null;   // an empty reference carries no value
                string partyEntity = partyId == null ? null : TargetEntity(partyId, meta);

                if (partyId != null && partyEntity == null)
                {
                    // Nothing to resolve the party against: drop the link (an address used is still kept below).
                    string kept = string.IsNullOrEmpty(addressUsed) ? string.Empty : ", its address " + addressUsed + " is kept";
                    Log(LogLevel.Warning, childDepth, $"Dropped party {partyId.Id} from {attribute} on {owner}: the party reference has no entity name{kept}");
                    _lookupsBlanked++;
                    partyId = null;
                }

                if (partyId != null)
                {
                    string dropReason = null;
                    TargetKind kind = Classify(partyEntity);
                    if (kind.ExistenceOnly)
                    {
                        TargetCheck check = CheckTarget(kind, partyEntity, partyId.Id);
                        if (check.Exists == false)
                        {
                            // Lookups not copied: unlike a lookup, a party is decided now, so one whose record
                            // the tree copies later stays dropped (party lists are not backfilled).
                            dropReason = kind.NotCopied && !kind.MissingInDestination ? NotCopiedPartyProblem : check.Problem;
                        }
                        else if (check.Exists == null)
                        {
                            unverified = true;
                            Log(LogLevel.Warning, childDepth, check.FailedEarlier
                                ? $"{KeepVerb()} party {partyEntity} {partyId.Id} in {attribute} on {owner} unverified (check failed earlier)"
                                : $"{KeepVerb()} party {partyEntity} {partyId.Id} in {attribute} on {owner} unverified: could not be checked in destination: {check.Error}");
                        }
                    }
                    else
                    {
                        switch (EnsureRecord(new EntityReference(partyEntity, partyId.Id) { Name = partyId.Name }, childDepth, isSelected: false))
                        {
                            case EnsureResult.Unavailable:
                                dropReason = "could not be copied";
                                break;
                            case EnsureResult.Deferred:
                                dropReason = "is still being copied (circular reference; party lists are not backfilled)";
                                break;
                        }
                    }

                    if (dropReason != null)
                    {
                        Log(LogLevel.Warning, childDepth, $"Dropped party {partyEntity} {partyId.Id} from {attribute} on {owner}: {dropReason}");
                        _lookupsBlanked++;
                        continue;
                    }
                    copy["partyid"] = new EntityReference(partyEntity, partyId.Id);
                }
                else if (string.IsNullOrEmpty(addressUsed))
                {
                    continue;   // neither a resolvable party nor an address: nothing to keep
                }

                if (party.Attributes.TryGetValue("participationtypemask", out object mask) && mask != null) copy["participationtypemask"] = mask;
                if (!string.IsNullOrEmpty(addressUsed)) copy["addressused"] = addressUsed;
                rebuilt.Entities.Add(copy);
            }

            if (unverified) record.MarkUnverified(attribute);
            return rebuilt;
        }

        /// <summary>"Kept", or "[DRY RUN] Would keep" in a dry run (nothing is written, so nothing is kept yet).</summary>
        private string KeepVerb() => _options.DryRun ? DryRunPrefix + "Would keep" : "Kept";

        // =====================================================================================
        // Never-create and virtual lookup targets (SPEC 5.8)
        // =====================================================================================

        /// <summary>
        /// How the records of an entity are treated as lookup targets, decided once per run. Records of
        /// a never-create entity or of a virtual table - by the destination schema, or by the source's
        /// when the destination's is unavailable - are only checked for existence in the destination,
        /// and so are the records of every other entity while lookups are not copied (SPEC 5.10).
        /// </summary>
        private TargetKind Classify(string entity)
        {
            if (_targetKinds.TryGetValue(entity, out TargetKind kind)) return kind;

            kind = new TargetKind { NeverCreate = _options.NeverCreateEntities.Contains(entity) };
            EntitySchema destination = TryGetSchema(_destinationSchema, entity, out bool destinationFailed);
            if (destination != null)
            {
                kind.Schema = destination;
                kind.IsVirtual = destination.IsVirtual;
            }
            else
            {
                EntitySchema source = TryGetSchema(_sourceSchema, entity, out _);
                if (source != null && source.IsVirtual)
                {
                    kind.IsVirtual = true;
                    kind.Schema = source;                               // its primary id and name, for the check
                    kind.MissingInDestination = !destinationFailed;     // the destination answered: it has no such table
                }
            }
            if (!_options.CopyLookups && !kind.NeverCreate && !kind.IsVirtual)
            {
                // Lookups not copied: checked for existence only; a table the destination does not have
                // cannot hold the record (asking would only fail).
                kind.NotCopied = true;
                kind.MissingInDestination = destination == null && !destinationFailed;
            }
            _targetKinds[entity] = kind;
            return kind;
        }

        /// <summary>
        /// Does an existence-only lookup target (never-create, virtual, or any while lookups are not
        /// copied) exist in the destination? True / false, or null when the destination could not answer
        /// (a failing virtual-table provider, a missing read privilege...). Answers and failures are both
        /// cached per record for the run, so a broken provider is asked once per id; a record this run
        /// created counts as existing.
        /// </summary>
        private TargetCheck CheckTarget(TargetKind kind, string entity, Guid id)
        {
            string missing = kind.IsVirtual ? "does not exist in destination (virtual table, never created)"
                : kind.NotCopied ? NotCopiedProblem
                : "does not exist in destination";
            if (kind.MissingInDestination)
                return TargetCheck.Missing(kind.IsVirtual ? "does not exist in destination (virtual table, the destination has no such table)" : missing);

            var key = new RecordKey(entity, id);
            if (_failedChecks.TryGetValue(key, out string earlier)) return TargetCheck.Unverified(earlier, failedEarlier: true);
            try
            {
                return GetExistence(key, kind.Schema, kind.IsVirtual).Exists ? TargetCheck.Found : TargetCheck.Missing(missing);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                if (IsRecordNotFound(ex))
                {
                    // A "does not exist" answer is an answer (the Retrieve of a virtual row maps it already).
                    _existence[key] = new ExistenceInfo { Exists = false };
                    return TargetCheck.Missing(missing);
                }
                string error = ErrorText(ex);
                _failedChecks[key] = error;
                return TargetCheck.Unverified(error, failedEarlier: false);
            }
        }

        // =====================================================================================
        // Writes (SPEC 5.4)
        // =====================================================================================

        private void CreateRecord(Entity target, RecordContext record)
        {
            RecordKey key = record.Key;
            if (_options.DryRun)
            {
                Log(LogLevel.Success, record.Depth, $"{DryRunPrefix}Would create {Describe(key, record.Name)}");
            }
            else
            {
                Write(target, record, isCreate: true);
                Log(LogLevel.Success, record.Depth, $"Created {Describe(key, record.Name)}");
            }

            _states[key] = RecordState.Created;
            _created++;
            // New records start active with the default status (the state step may then change it).
            bool hasState = record.Destination.HasStateCode;
            record.Existing = new ExistenceInfo
            {
                Exists = true,
                Name = record.Name,
                StateCode = hasState ? 0 : (int?)null,
                StatusCode = hasState ? DefaultStatus(record.Destination, 0) : null
            };
            _existence[key] = record.Existing;
            ReportProgress();
        }

        private void UpdateRecord(Entity target, RecordContext record)
        {
            RecordKey key = record.Key;
            EntitySchema destination = record.Destination;
            ExistenceInfo existing = record.Existing;
            bool hasAttributes = target.Attributes.Count > 0;

            // An inactive record usually cannot be edited: reactivate it first.
            if (hasAttributes && destination.HasStateCode && existing.StateCode.HasValue && existing.StateCode.Value != 0)
            {
                int? activeStatus = DefaultStatus(destination, 0);
                if (_options.DryRun)
                {
                    Log(LogLevel.Info, record.Depth + 1, $"{DryRunPrefix}Would reactivate {Describe(key, record.Name)} for update");
                }
                else
                {
                    ExecuteWrite(StateUpdate(key, destination, 0, activeStatus));
                    Log(LogLevel.Info, record.Depth + 1, $"Reactivated {Describe(key, record.Name)} for update");
                }
                existing.StateCode = 0;
                existing.StatusCode = activeStatus;
            }

            string suffix = hasAttributes ? string.Empty : " (no attributes to write)";
            if (_options.DryRun)
            {
                Log(LogLevel.Success, record.Depth, $"{DryRunPrefix}Would update {Describe(key, record.Name)}{suffix}");
            }
            else
            {
                if (hasAttributes) Write(target, record, isCreate: false);
                Log(LogLevel.Success, record.Depth, $"Updated {Describe(key, record.Name)}{suffix}");
            }

            _states[key] = RecordState.Updated;
            _updated++;
            ReportProgress();
        }

        /// <summary>
        /// Sends the create or update. If it fails it is retried without what may have caused it:
        /// first without the record's unverified lookups (SPEC 5.8), then - create only - without
        /// overriddencreatedon (5.4), then without both; at most three retries, each logged before it
        /// is sent. The retry that works logs what it left out; when every attempt fails the last
        /// failure propagates (and fails the record).
        /// </summary>
        private void Write(Entity target, RecordContext record, bool isCreate)
        {
            string[] unverified = record.UnverifiedLookups.Where(target.Attributes.ContainsKey).ToArray();
            bool hasOverride = isCreate && target.Attributes.ContainsKey(OverriddenCreatedOn);
            var attempts = new List<(bool WithoutLookups, bool WithoutOverride)> { (false, false) };
            if (unverified.Length > 0) attempts.Add((true, false));
            if (hasOverride) attempts.Add((false, true));
            if (unverified.Length > 0 && hasOverride) attempts.Add((true, true));

            string verb = isCreate ? "create" : "update";
            string lookupError = null;     // the latest failure of an attempt that still sent the unverified lookups
            string overrideError = null;   // ... that still sent overriddencreatedon
            for (int i = 0; ; i++)
            {
                (bool withoutLookups, bool withoutOverride) = attempts[i];
                Entity attempt = target;
                if (withoutLookups || withoutOverride)
                {
                    var leftOut = new List<string>();
                    if (withoutLookups) leftOut.AddRange(unverified);
                    if (withoutOverride) leftOut.Add(OverriddenCreatedOn);
                    attempt = CloneWithout(target, leftOut);
                }

                try
                {
                    if (isCreate) ExecuteWrite(new CreateRequest { Target = attempt });
                    else if (attempt.Attributes.Count > 0) ExecuteWrite(new UpdateRequest { Target = attempt });
                }
                catch (Exception ex) when (!IsCancellation(ex) && i + 1 < attempts.Count)
                {
                    string error = ErrorText(ex);
                    if (!withoutLookups) lookupError = error;
                    if (!withoutOverride) overrideError = error;
                    (bool nextWithoutLookups, bool nextWithoutOverride) = attempts[i + 1];
                    Log(LogLevel.Info, record.Depth + 1,
                        $"{(isCreate ? "Create" : "Update")} failed, retrying without {LeftOutText(unverified, nextWithoutLookups, nextWithoutOverride)}: {error}");
                    continue;
                }

                if (withoutLookups)
                {
                    string owner = DescribeOwner(record.Key, record.Name);
                    foreach (string attribute in unverified)
                    {
                        Log(LogLevel.Warning, record.Depth + 1, $"Blanked {attribute} on {owner}: {verb} failed with the unverified lookup ({lookupError})");
                        _lookupsBlanked++;
                    }
                }
                if (withoutOverride)
                {
                    Log(LogLevel.Warning, record.Depth + 1, $"{Describe(record.Key, record.Name)} created without overriddencreatedon: {overrideError}");
                }
                return;
            }
        }

        private static string LeftOutText(string[] lookups, bool withoutLookups, bool withoutOverride)
        {
            string lookupText = (lookups.Length == 1 ? "the unverified lookup " : "the unverified lookups ") + string.Join(", ", lookups);
            if (withoutLookups && withoutOverride) return lookupText + " and " + OverriddenCreatedOn;
            return withoutLookups ? lookupText : OverriddenCreatedOn;
        }

        /// <summary>Sends a create/update with the run's request parameters (SPEC 5.4).</summary>
        private void ExecuteWrite(OrganizationRequest request)
        {
            request.Parameters["SuppressDuplicateDetection"] = true;
            if (_options.BypassCustomPluginExecution) request.Parameters["BypassCustomPluginExecution"] = true;
            _destination.Execute(request);
        }

        /// <summary>
        /// Sends a close message (SPEC 5.9) or an AssociateRequest (5.10): BypassCustomPluginExecution when
        /// asked; duplicate detection does not apply.
        /// </summary>
        private void ExecuteMessage(OrganizationRequest request)
        {
            if (_options.BypassCustomPluginExecution) request.Parameters["BypassCustomPluginExecution"] = true;
            _destination.Execute(request);
        }

        // =====================================================================================
        // State and status (SPEC 5.9)
        // =====================================================================================

        /// <summary>
        /// Queues the source statecode/statuscode when it differs from the destination's current state
        /// (after a create: state 0 with its default status; after an update: the state read by the
        /// existence check, after any reactivation). The queue is applied once the selected record's
        /// whole tree is done (<see cref="ApplyPendingStates"/>), so every write into a record -
        /// backfills included - happens before the record can become read-only.
        /// </summary>
        private void QueueState(Entity source, RecordContext record)
        {
            EntitySchema destination = record.Destination;
            if (!destination.HasStateCode) return;
            int? sourceState = OptionValue(source, StateCode);
            if (!sourceState.HasValue) return;
            int? sourceStatus = OptionValue(source, StatusCode);

            ExistenceInfo current = record.Existing;
            int currentState = current?.StateCode ?? 0;
            int? currentStatus = current?.StatusCode ?? DefaultStatus(destination, currentState);

            bool differs = sourceState.Value != currentState
                           || (sourceStatus.HasValue && currentStatus.HasValue && sourceStatus.Value != currentStatus.Value);
            if (differs) _pendingStates.Enqueue(new PendingState(record, source, sourceState.Value, sourceStatus));
        }

        /// <summary>Applies the queued state changes, in the order their records were written.</summary>
        private void ApplyPendingStates()
        {
            while (_pendingStates.Count > 0) ApplyState(_pendingStates.Dequeue());
        }

        /// <summary>
        /// One state change: the close message the platform requires for a closing state (won or lost
        /// opportunity, resolved case, won or closed quote - activated first while still a draft -,
        /// cancelled or fulfilled order), otherwise an Update of statecode + statuscode. A failure is
        /// a warning: the record stays copied.
        /// </summary>
        private void ApplyState(PendingState pending)
        {
            string record = Describe(pending.Key, pending.Name);
            IList<OrganizationRequest> requests = StateRequests(pending, out string message);
            string via = message == null ? string.Empty : " (" + message + ")";
            string description = StateDescription(pending);

            if (_options.DryRun)
            {
                Log(LogLevel.Info, pending.Depth, $"{DryRunPrefix}Would apply state to {record}: {description}{via}");
            }
            else
            {
                try
                {
                    foreach (OrganizationRequest request in requests)
                    {
                        if (request is UpdateRequest) ExecuteWrite(request);   // like every other update
                        else ExecuteMessage(request);                           // a close message
                    }
                }
                catch (Exception ex) when (!IsCancellation(ex))
                {
                    Log(LogLevel.Warning, pending.Depth, $"{(pending.IsUpdate ? "Updated" : "Created")} {record} but state not applied{via}: {ErrorText(ex)}");
                    return;
                }
                Log(LogLevel.Info, pending.Depth, $"State applied to {record}: {description}{via}");
            }

            _stateChanges++;
            if (pending.Current != null)
            {
                pending.Current.StateCode = pending.State;
                pending.Current.StatusCode = pending.Status;
            }
        }

        /// <summary>The run was cancelled inside a selected record's tree: its queued state changes are not applied.</summary>
        private void AbandonPendingStates()
        {
            int count = _pendingStates.Count;
            if (count == 0) return;
            Log(LogLevel.Warning, 0, $"{count.ToString(CultureInfo.InvariantCulture)} pending state {(count == 1 ? "change" : "changes")} not applied: run cancelled");
            foreach (PendingState pending in _pendingStates)
            {
                Log(LogLevel.Warning, 1, $"{Describe(pending.Key, pending.Name)}: {StateDescription(pending)}");
            }
            _pendingStates.Clear();
        }

        private static string StateDescription(PendingState pending) =>
            "statecode=" + pending.State.ToString(CultureInfo.InvariantCulture)
            + ", statuscode=" + (pending.Status.HasValue ? pending.Status.Value.ToString(CultureInfo.InvariantCulture) : "(default)");

        /// <summary>The requests that apply a state change; <paramref name="message"/> names the close message, if one is used.</summary>
        private static IList<OrganizationRequest> StateRequests(PendingState pending, out string message)
        {
            RecordKey key = pending.Key;
            OrganizationRequest close = CloseRequest(pending);
            if (close == null)
            {
                message = null;
                return new OrganizationRequest[] { StateUpdate(key, pending.Destination, pending.State, pending.Status) };
            }

            message = close.RequestName;
            var requests = new List<OrganizationRequest>();
            if (key.Entity == "quote" && (pending.Current?.StateCode ?? QuoteDraft) == QuoteDraft)
            {
                // A quote is won or closed only once it is active: a draft is activated first.
                requests.Add(StateUpdate(key, pending.Destination, QuoteActive, DefaultStatus(pending.Destination, QuoteActive)));
                message = "activate + " + message;
            }
            requests.Add(close);
            return requests;
        }

        /// <summary>An Update of statecode (+ statuscode when known and the entity has one).</summary>
        private static UpdateRequest StateUpdate(RecordKey key, EntitySchema destination, int state, int? status)
        {
            var change = new Entity(key.Entity) { Id = key.Id };
            change[StateCode] = new OptionSetValue(state);
            if (status.HasValue && destination.Attributes.ContainsKey(StatusCode)) change[StatusCode] = new OptionSetValue(status.Value);
            return new UpdateRequest { Target = change };
        }

        /// <summary>
        /// The close message the platform requires for this state (it refuses a plain Update there), or
        /// null when an Update does: opportunity won (1) / lost (2), incident resolved (1), quote won (2)
        /// / closed (3), salesorder cancelled (2) / fulfilled (3). The status is the source's, else the
        /// state's default (-1 when unknown lets the platform choose).
        /// </summary>
        private static OrganizationRequest CloseRequest(PendingState pending)
        {
            var record = new EntityReference(pending.Key.Entity, pending.Key.Id);
            var status = new OptionSetValue(pending.Status ?? DefaultStatus(pending.Destination, pending.State) ?? -1);
            switch (pending.Key.Entity)
            {
                case "opportunity" when pending.State == 1:
                    return new WinOpportunityRequest { OpportunityClose = OpportunityClose(record, pending.Source), Status = status };
                case "opportunity" when pending.State == 2:
                    return new LoseOpportunityRequest { OpportunityClose = OpportunityClose(record, pending.Source), Status = status };
                case "incident" when pending.State == 1:
                    return new CloseIncidentRequest { IncidentResolution = CloseActivity("incidentresolution", "incidentid", record), Status = status };
                case "quote" when pending.State == 2:
                    return new WinQuoteRequest { QuoteClose = CloseActivity("quoteclose", "quoteid", record), Status = status };
                case "quote" when pending.State == 3:
                    return new CloseQuoteRequest { QuoteClose = CloseActivity("quoteclose", "quoteid", record), Status = status };
                case "salesorder" when pending.State == 2:
                    return new CancelSalesOrderRequest { OrderClose = CloseActivity("orderclose", "salesorderid", record), Status = status };
                case "salesorder" when pending.State == 3:
                    return new FulfillSalesOrderRequest { OrderClose = CloseActivity("orderclose", "salesorderid", record), Status = status };
                default:
                    return null;
            }
        }

        /// <summary>The close activity a close message creates: linked to the record, with the copier's subject.</summary>
        private static Entity CloseActivity(string entity, string lookup, EntityReference record)
        {
            var close = new Entity(entity);
            close[lookup] = record;
            close["subject"] = CloseActivitySubject;
            return close;
        }

        /// <summary>
        /// An opportunityclose carrying the source opportunity's actual revenue (its actualvalue column;
        /// an actualrevenue column is taken when there is no actualvalue) and actual close date, when present.
        /// </summary>
        private static Entity OpportunityClose(EntityReference opportunity, Entity source)
        {
            Entity close = CloseActivity("opportunityclose", "opportunityid", opportunity);
            Money revenue = ValueOf<Money>(source, "actualvalue") ?? ValueOf<Money>(source, "actualrevenue");
            if (revenue != null) close["actualrevenue"] = revenue;
            if (source != null && source.Attributes.TryGetValue("actualclosedate", out object closed) && closed is DateTime closedOn) close["actualend"] = closedOn;
            return close;
        }

        // =====================================================================================
        // Cycles and backfill (SPEC 5.6)
        // =====================================================================================

        private void QueueBackfill(RecordContext owner, string attribute, RecordKey against)
        {
            if (!_backfills.TryGetValue(against, out List<Backfill> pending))
            {
                pending = new List<Backfill>();
                _backfills[against] = pending;
            }
            pending.Add(new Backfill(owner.Key, attribute));
            Log(LogLevel.Info, owner.Depth + 1, $"Deferred {attribute} on {DescribeOwner(owner.Key, owner.Name)}: {against.Entity} {against.Id} is still being copied (circular reference), will backfill");
        }

        /// <summary>Writes the lookups that were deferred because <paramref name="key"/> was in progress.</summary>
        private void ApplyBackfills(RecordKey key, int depth)
        {
            if (!_backfills.TryGetValue(key, out List<Backfill> pending)) return;
            _backfills.Remove(key);

            foreach (Backfill backfill in pending)
            {
                if (!IsInDestination(backfill.Owner)) continue;   // the referencing record itself failed
                WriteBackfill(backfill.Owner, backfill.Attribute, key, depth + 1);
            }
        }

        /// <summary>
        /// Writes one lookup into a record that is in the destination: an Update of that attribute alone,
        /// with the run's request parameters (SPEC 5.6). A failure is a warning: the lookup stays blank.
        /// </summary>
        private void WriteBackfill(RecordKey owner, string attribute, RecordKey target, int logDepth)
        {
            string description = $"{owner.Entity}.{attribute} -> {target.Entity} ({target.Id})";
            if (_options.DryRun)
            {
                Log(LogLevel.Success, logDepth, $"{DryRunPrefix}Would backfill {description}");
                _lookupsBackfilled++;
                return;
            }

            var update = new Entity(owner.Entity) { Id = owner.Id };
            update[attribute] = new EntityReference(target.Entity, target.Id);
            try
            {
                ExecuteWrite(new UpdateRequest { Target = update });
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Log(LogLevel.Warning, logDepth, $"Backfill of {description} failed, lookup left blank: {ErrorText(ex)}");
                _lookupsBlanked++;
                return;
            }
            Log(LogLevel.Success, logDepth, $"Backfilled {description}");
            _lookupsBackfilled++;
        }

        /// <summary>The record the backfills pointed at could not be copied: the lookups stay blank.</summary>
        private void DropBackfills(RecordKey key, int depth)
        {
            if (!_backfills.TryGetValue(key, out List<Backfill> pending)) return;
            _backfills.Remove(key);

            foreach (Backfill backfill in pending)
            {
                if (!IsInDestination(backfill.Owner)) continue;
                Log(LogLevel.Warning, depth + 1, $"Backfill of {backfill.Owner.Entity}.{backfill.Attribute} -> {key.Entity} ({key.Id}) dropped: {key.Entity} could not be copied, lookup left blank");
                _lookupsBlanked++;
            }
        }

        private void AbandonPendingBackfills()
        {
            foreach (KeyValuePair<RecordKey, List<Backfill>> pair in _backfills.ToList())
            {
                foreach (Backfill backfill in pair.Value)
                {
                    if (!IsInDestination(backfill.Owner)) continue;
                    Log(LogLevel.Warning, 0, $"Backfill of {backfill.Owner.Entity}.{backfill.Attribute} -> {pair.Key.Entity} ({pair.Key.Id}) not applied: run cancelled, lookup left blank");
                    _lookupsBlanked++;
                }
            }
            _backfills.Clear();
        }

        private bool IsInDestination(RecordKey key)
        {
            _states.TryGetValue(key, out RecordState state);
            return state == RecordState.Created || state == RecordState.Updated || state == RecordState.Exists;
        }

        private bool IsInProgress(RecordKey key) => _states.TryGetValue(key, out RecordState state) && state == RecordState.InProgress;

        // =====================================================================================
        // Deferred lookups: lookups not copied (SPEC 5.10)
        // =====================================================================================

        /// <summary>
        /// Decides the lookups left out while the selected record's tree was written because their
        /// record was not in the destination then (lookups not copied). Called once the tree is done,
        /// before its state changes. A lookup whose record is there now - copied by the tree, e.g. as a
        /// child record, or found by the existence check - is written with a backfill update (SPEC 5.6);
        /// any other is blanked, and counted, at this point. The lookups of a record that was not
        /// written (it failed) are dropped silently. Dry run: "Would backfill" / "Would blank".
        /// </summary>
        private void ResolveDeferredLookups()
        {
            while (_deferredLookups.Count > 0)
            {
                DeferredLookup lookup = _deferredLookups.Dequeue();
                if (!IsInDestination(lookup.Owner)) continue;   // the referencing record itself was not written
                RecordKey target = lookup.Target;
                int logDepth = lookup.Depth + 1;                // where the record's lookups are logged
                if (IsInDestination(target) || CheckTarget(Classify(target.Entity), target.Entity, target.Id).Exists == true)
                {
                    WriteBackfill(lookup.Owner, lookup.Attribute, target, logDepth);
                    continue;
                }
                string blanked = _options.DryRun ? DryRunPrefix + "Would blank" : "Blanked";
                Log(LogLevel.Warning, logDepth, $"{blanked} {lookup.Attribute} on {DescribeOwner(lookup.Owner, lookup.OwnerName)}: {target.Entity} {target.Id} {NotCopiedProblem}");
                _lookupsBlanked++;
            }
        }

        /// <summary>The run was cancelled inside a selected record's tree: its deferred lookups are never decided and stay blank.</summary>
        private void AbandonDeferredLookups()
        {
            foreach (DeferredLookup lookup in _deferredLookups)
            {
                if (!IsInDestination(lookup.Owner)) continue;
                Log(LogLevel.Warning, 0, $"Deferred lookup {lookup.Owner.Entity}.{lookup.Attribute} -> {lookup.Target.Entity} ({lookup.Target.Id}) not resolved: run cancelled, lookup left blank");
                _lookupsBlanked++;
            }
            _deferredLookups.Clear();
        }

        // =====================================================================================
        // Related records: child records (1:N) and peers (N:N) (SPEC 5.10)
        // =====================================================================================

        /// <summary>
        /// Follows the relationships of a selected, child or peer record that was just created, updated
        /// or found existing, while <see cref="CopyOptions.CopyChildren"/> is on with a selector: first its
        /// 1:N relationships - the source records whose lookup points at it, copied as child records, so
        /// recursively - then its N:N relationships - the records associated with it, copied as peers and
        /// then associated (<see cref="WalkManyToMany"/>). Which relationships depends on how the record was
        /// reached (<paramref name="context"/>). Each record is walked once per run, which bounds
        /// hierarchies and cycles - again only when it is reached as a selected or child record after a
        /// walk as a peer (which may have followed less), and then only through the relationships not
        /// followed from it yet. Never a never-create or virtual record.
        /// </summary>
        private void WalkRelationships(RecordKey key, string name, int depth, RelationshipContext context)
        {
            if (!_options.CopyChildren || _options.ChildRelationshipSelector == null) return;
            if (_walked.TryGetValue(key, out RelationshipContext walked)
                && (walked == RelationshipContext.SelectedOrChild || context == RelationshipContext.Peer))
            {
                return;
            }
            _walked[key] = context;
            TargetKind kind = Classify(key.Entity);
            if (kind.NeverCreate || kind.IsVirtual) return;
            if (!_followed.TryGetValue(key, out HashSet<string> followed))
            {
                followed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _followed[key] = followed;
            }

            // Every expected failure is handled inside (per relationship, per related record); these only
            // keep an unexpected one from failing the record itself, which is written already.
            try
            {
                WalkChildRelationships(key, name, depth, context, followed);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Log(LogLevel.Warning, depth, $"Child records of {DescribeOwner(key, name)} not all copied: {ErrorText(ex)}");
            }
            try
            {
                WalkManyToMany(key, name, depth, context, followed);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Log(LogLevel.Warning, depth, $"Associated records of {DescribeOwner(key, name)} not all copied: {ErrorText(ex)}");
            }
        }

        private void WalkChildRelationships(RecordKey key, string name, int depth, RelationshipContext context, HashSet<string> followed)
        {
            foreach (ChildRelationship relationship in ChildRelationshipsOf(key.Entity, context, depth))
            {
                if (!followed.Add(relationship.SchemaName)) continue;   // followed from this record already (in a walk as a peer)
                _cancellationToken.ThrowIfCancellationRequested();
                List<EntityReference> children = ListChildren(relationship, key, name, depth);
                if (children == null || children.Count == 0) continue;

                _childRecordsFound += children.Count;
                string childEntity = relationship.ChildEntity.Trim().ToLowerInvariant();
                Log(LogLevel.Info, depth, $"Children of {DescribeOwner(key, name)} via {relationship.SchemaName}: " +
                    $"{children.Count.ToString(CultureInfo.InvariantCulture)} {childEntity} {(children.Count == 1 ? "record" : "records")}");
                ReportProgress();
                foreach (EntityReference child in children)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    EnsureRecord(child, depth + 1, isSelected: false, isChild: true);
                }
            }
        }

        /// <summary>
        /// The 1:N relationships followed from the records of <paramref name="entity"/> reached as
        /// <paramref name="context"/>, asked of the selector once per entity and context per run (a plain
        /// <see cref="IChildRelationshipSelector"/> answers for selected and child records only).
        /// Relationships to a system-excluded, never-create or virtual child entity are left out, and one
        /// whose child entity the destination does not have is left out with a warning (once per run). A
        /// failing selector is a warning too: the records of that entity then get no child records.
        /// </summary>
        private IReadOnlyList<ChildRelationship> ChildRelationshipsOf(string entity, RelationshipContext context, int depth)
        {
            string cacheKey = ContextKey(entity, context);
            if (_childRelationships.TryGetValue(cacheKey, out IReadOnlyList<ChildRelationship> known)) return known;

            IReadOnlyList<ChildRelationship> chosen;
            try
            {
                IChildRelationshipSelector selector = _options.ChildRelationshipSelector;
                chosen = (selector is IRelationshipSelector relationships ? relationships.GetChildRelationships(entity, context)
                          : context == RelationshipContext.Peer ? null
                          : selector.GetChildRelationships(entity))
                         ?? Array.Empty<ChildRelationship>();
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Log(LogLevel.Warning, depth, $"Could not determine the 1:N relationships of {entity}: {ErrorText(ex)}; its child records are not copied");
                chosen = Array.Empty<ChildRelationship>();
            }

            var followed = new List<ChildRelationship>();
            foreach (ChildRelationship relationship in chosen)
            {
                if (relationship == null || string.IsNullOrWhiteSpace(relationship.ChildEntity) || string.IsNullOrWhiteSpace(relationship.ChildLookupAttribute)) continue;
                string child = relationship.ChildEntity.Trim().ToLowerInvariant();
                if (ChildRelationshipEligibility.IsSystemExcluded(child)) continue;
                if (child != ChildRelationshipEligibility.ActivityPointer)
                {
                    TargetKind childKind = Classify(child);
                    if (childKind.NeverCreate || childKind.IsVirtual) continue;
                }
                if (TryGetSchema(_destinationSchema, child, out bool failed) == null && !failed)
                {
                    if (_warnedRelationships.Add(entity + "|" + relationship.SchemaName))
                    {
                        Log(LogLevel.Warning, depth, $"1:N relationship {relationship.SchemaName} of {entity} not followed: {child} does not exist in destination");
                    }
                    continue;
                }
                followed.Add(relationship);
            }

            IReadOnlyList<ChildRelationship> result = followed.AsReadOnly();
            _childRelationships[cacheKey] = result;
            return result;
        }

        private static string ContextKey(string entity, RelationshipContext context) =>
            (context == RelationshipContext.Peer ? "peer|" : "record|") + entity;

        /// <summary>
        /// The child records of <paramref name="parent"/> through <paramref name="relationship"/>, read
        /// from the source <see cref="ChildPageSize"/> at a time with the paging cookie: a QueryExpression
        /// on the child entity for its lookup Equal the parent id, returning the primary id only - plus
        /// activitytypecode for activitypointer, whose rows become references to their concrete activity
        /// entity. Null when the query fails (a warning: the run continues).
        /// </summary>
        private List<EntityReference> ListChildren(ChildRelationship relationship, RecordKey parent, string parentName, int depth)
        {
            string childEntity = relationship.ChildEntity.Trim().ToLowerInvariant();
            bool activities = childEntity == ChildRelationshipEligibility.ActivityPointer;
            EntitySchema childSchema = TryGetSchema(_sourceSchema, childEntity, out _);
            string primaryId = !string.IsNullOrEmpty(childSchema?.PrimaryIdAttribute) ? childSchema.PrimaryIdAttribute
                : activities ? "activityid" : childEntity + "id";

            var children = new List<EntityReference>();
            var seen = new HashSet<RecordKey>();
            int page = 1;
            string cookie = null;
            try
            {
                while (true)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var query = new QueryExpression(childEntity)
                    {
                        ColumnSet = activities ? new ColumnSet(primaryId, ActivityTypeCode) : new ColumnSet(primaryId),
                        PageInfo = new PagingInfo { Count = ChildPageSize, PageNumber = page, PagingCookie = cookie }
                    };
                    query.Criteria.AddCondition(relationship.ChildLookupAttribute.Trim(), ConditionOperator.Equal, parent.Id);
                    query.AddOrder(primaryId, OrderType.Ascending);

                    EntityCollection result = _source.RetrieveMultiple(query);
                    foreach (Entity row in result?.Entities ?? Enumerable.Empty<Entity>())
                    {
                        EntityReference child = ChildReference(row, childEntity, primaryId, activities);
                        if (child != null && seen.Add(new RecordKey(child.LogicalName, child.Id))) children.Add(child);
                    }
                    if (result == null || !result.MoreRecords) break;
                    page++;
                    cookie = result.PagingCookie;
                }
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Log(LogLevel.Warning, depth, $"Could not list {relationship.SchemaName} children of {DescribeOwner(parent, parentName)}: {ErrorText(ex)}");
                return null;
            }
            return children;
        }

        /// <summary>
        /// A child row as a reference: to its own entity, or - for an activitypointer row - to its
        /// concrete activity entity (null when that type is not copied: unknown, system-excluded,
        /// never-create or virtual).
        /// </summary>
        private EntityReference ChildReference(Entity row, string childEntity, string primaryId, bool activities)
        {
            Guid id = row.Id;
            if (id == Guid.Empty && row.Attributes.TryGetValue(primaryId, out object value) && value is Guid attributeId) id = attributeId;
            if (id == Guid.Empty) return null;
            if (!activities) return new EntityReference(childEntity, id);

            string type = row.Attributes.TryGetValue(ActivityTypeCode, out object code) ? (code as string)?.Trim().ToLowerInvariant() : null;
            if (string.IsNullOrEmpty(type) || type == ChildRelationshipEligibility.ActivityPointer || ChildRelationshipEligibility.IsSystemExcluded(type)) return null;
            TargetKind kind = Classify(type);
            return kind.NeverCreate || kind.IsVirtual ? null : new EntityReference(type, id);
        }

        /// <summary>
        /// Copies the peers of a selected, child or peer record and associates them with it: for each N:N
        /// relationship chosen for its entity (and how it was reached), the records associated with it in
        /// the source - each copied as a peer (created when missing, skipped when it exists; its own
        /// relationships followed only when its entity is configured), then the pair associated in the
        /// destination unless it is associated there already (<see cref="AssociatePeer"/>).
        /// </summary>
        private void WalkManyToMany(RecordKey key, string name, int depth, RelationshipContext context, HashSet<string> followed)
        {
            foreach (ManyToManyRelationship relationship in ManyToManyRelationshipsOf(key.Entity, context, depth))
            {
                if (!followed.Add(relationship.SchemaName)) continue;   // followed from this record already (in a walk as a peer)
                _cancellationToken.ThrowIfCancellationRequested();
                List<Association> associations = ListAssociations(relationship, key, name, depth);
                if (associations == null || associations.Count == 0) continue;

                _peerRecordsFound += associations.Count;
                string peerEntity = relationship.OtherEntity(key.Entity);
                Log(LogLevel.Info, depth, $"Associated records of {DescribeOwner(key, name)} via {relationship.SchemaName}: " +
                    $"{associations.Count.ToString(CultureInfo.InvariantCulture)} {peerEntity} {(associations.Count == 1 ? "record" : "records")}");
                ReportProgress();
                foreach (Association association in associations)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    AssociatePeer(key, name, depth, relationship, association);
                }
            }
        }

        /// <summary>
        /// The N:N relationships followed from the records of <paramref name="entity"/> reached as
        /// <paramref name="context"/>, asked of the selector once per entity and context per run (none
        /// unless it is an <see cref="IRelationshipSelector"/>). Relationships through a system intersect
        /// entity or to a never-create, excluded or virtual entity are left out; one the destination does
        /// not have (the relationship or its intersect entity) is left out with a warning, once per run. A
        /// failing selector is a warning too: the records of that entity then get no associations.
        /// </summary>
        private IReadOnlyList<ManyToManyRelationship> ManyToManyRelationshipsOf(string entity, RelationshipContext context, int depth)
        {
            if (!(_options.ChildRelationshipSelector is IRelationshipSelector selector)) return Array.Empty<ManyToManyRelationship>();
            string cacheKey = ContextKey(entity, context);
            if (_manyToManyRelationships.TryGetValue(cacheKey, out IReadOnlyList<ManyToManyRelationship> known)) return known;

            IReadOnlyList<ManyToManyRelationship> chosen;
            try
            {
                chosen = selector.GetManyToManyRelationships(entity, context) ?? Array.Empty<ManyToManyRelationship>();
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Log(LogLevel.Warning, depth, $"Could not determine the N:N relationships of {entity}: {ErrorText(ex)}; its associations are not copied");
                chosen = Array.Empty<ManyToManyRelationship>();
            }

            var followed = new List<ManyToManyRelationship>();
            foreach (ManyToManyRelationship relationship in chosen)
            {
                if (relationship == null || string.IsNullOrWhiteSpace(relationship.SchemaName) || string.IsNullOrWhiteSpace(relationship.IntersectEntity)
                    || string.IsNullOrWhiteSpace(relationship.Entity1IntersectAttribute) || string.IsNullOrWhiteSpace(relationship.Entity2IntersectAttribute))
                {
                    continue;
                }
                string peer = relationship.OtherEntity(entity);
                if (peer == null) continue;
                if (ManyToManyEligibility.IsSystemIntersect(relationship.IntersectEntity) || ManyToManyEligibility.IsExcludedPeerEntity(peer)) continue;
                TargetKind peerKind = Classify(peer);
                if (peerKind.NeverCreate || peerKind.IsVirtual) continue;
                string problem = ManyToManyEligibility.DestinationProblem(_destinationSchema, entity, relationship);
                if (problem != null)
                {
                    if (_warnedRelationships.Add(entity + "|" + relationship.SchemaName))
                    {
                        Log(LogLevel.Warning, depth, $"N:N relationship {relationship.SchemaName} of {entity} not followed: {problem}");
                    }
                    continue;
                }
                followed.Add(relationship);
            }

            IReadOnlyList<ManyToManyRelationship> result = followed.AsReadOnly();
            _manyToManyRelationships[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// The records associated with <paramref name="record"/> through <paramref name="relationship"/>,
        /// read from the SOURCE intersect entity <see cref="ChildPageSize"/> rows at a time with the paging
        /// cookie: a QueryExpression for the record's side attribute Equal its id, returning the other
        /// side's attribute, ordered by it - both sides of a self-referential relationship, each row
        /// keeping its orientation. Null when a query fails (a warning: the run continues).
        /// </summary>
        private List<Association> ListAssociations(ManyToManyRelationship relationship, RecordKey record, string recordName, int depth)
        {
            string peerEntity = relationship.OtherEntity(record.Entity);
            string attribute1 = relationship.Entity1IntersectAttribute.Trim();
            string attribute2 = relationship.Entity2IntersectAttribute.Trim();
            var associations = new List<Association>();
            var seen = new HashSet<AssociationKey>();
            try
            {
                if (IsEntity(relationship.Entity1LogicalName, record.Entity))
                    ListAssociationsFromSide(relationship, attribute1, attribute2, record, peerEntity, recordIsSide1: true, associations, seen);
                if (IsEntity(relationship.Entity2LogicalName, record.Entity))
                    ListAssociationsFromSide(relationship, attribute2, attribute1, record, peerEntity, recordIsSide1: false, associations, seen);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                Log(LogLevel.Warning, depth, $"Could not list {relationship.SchemaName} associations of {DescribeOwner(record, recordName)}: {ErrorText(ex)}");
                return null;
            }
            return associations;
        }

        private void ListAssociationsFromSide(ManyToManyRelationship relationship, string recordAttribute, string peerAttribute, RecordKey record,
                                              string peerEntity, bool recordIsSide1, List<Association> associations, HashSet<AssociationKey> seen)
        {
            string intersect = relationship.IntersectEntity.Trim().ToLowerInvariant();
            int page = 1;
            string cookie = null;
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var query = new QueryExpression(intersect)
                {
                    ColumnSet = new ColumnSet(peerAttribute),
                    PageInfo = new PagingInfo { Count = ChildPageSize, PageNumber = page, PagingCookie = cookie }
                };
                query.Criteria.AddCondition(recordAttribute, ConditionOperator.Equal, record.Id);
                query.AddOrder(peerAttribute, OrderType.Ascending);

                EntityCollection result = _source.RetrieveMultiple(query);
                foreach (Entity row in result?.Entities ?? Enumerable.Empty<Entity>())
                {
                    Guid peerId = IdOf(row, peerAttribute);
                    if (peerId == Guid.Empty) continue;
                    var pair = new AssociationKey(relationship.SchemaName, recordIsSide1 ? record.Id : peerId, recordIsSide1 ? peerId : record.Id);
                    if (seen.Add(pair)) associations.Add(new Association(new RecordKey(peerEntity, peerId), recordIsSide1, pair));
                }
                if (result == null || !result.MoreRecords) break;
                page++;
                cookie = result.PagingCookie;
            }
        }

        /// <summary>The id an intersect column holds (a Guid; a reference on some system intersects), else Guid.Empty.</summary>
        private static Guid IdOf(Entity row, string attribute)
        {
            if (row == null || !row.Attributes.TryGetValue(attribute, out object value)) return Guid.Empty;
            switch (value)
            {
                case Guid id: return id;
                case EntityReference reference: return reference.Id;
                default: return Guid.Empty;
            }
        }

        private static bool IsEntity(string name, string entity) =>
            !string.IsNullOrWhiteSpace(name) && string.Equals(name.Trim(), entity, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// One association found in the source: the peer is copied (as a peer: created when missing,
        /// skipped when it exists) and then, once it is in the destination, the pair is associated there -
        /// unless the destination intersect already holds it ("Association exists, skipped"). A peer that
        /// could not be copied, and an association the destination refuses, are warnings counted as
        /// failed associations. Dry run: "Would associate", counted as if done.
        /// </summary>
        private void AssociatePeer(RecordKey record, string recordName, int depth, ManyToManyRelationship relationship, Association association)
        {
            RecordKey peer = association.Peer;
            EnsureResult result = EnsureRecord(new EntityReference(peer.Entity, peer.Id), depth + 1, isSelected: false, isPeer: true);
            int logDepth = depth + 1;   // the peer's own depth
            string pair = $"{DescribeOwner(record, recordName)} <-> {DescribeOwner(peer, NameOf(peer))} via {relationship.SchemaName}";
            if (result != EnsureResult.Available)
            {
                string reason = result == EnsureResult.Deferred ? "is still being copied" : "could not be copied";
                Log(LogLevel.Warning, logDepth, $"Skipped association {pair}: {peer.Entity} {peer.Id} {reason}");
                _associationsFailed++;
                ReportProgress();
                return;
            }

            _cancellationToken.ThrowIfCancellationRequested();   // before the write, as before every create
            AssociationKey key = association.Key;
            if (AssociationExists(relationship, key, logDepth) == true)
            {
                Log(LogLevel.Info, logDepth, $"Association exists, skipped {pair}");
                _associationsSkipped++;
                ReportProgress();
                return;
            }

            if (_options.DryRun)
            {
                Log(LogLevel.Success, logDepth, $"{DryRunPrefix}Would associate {pair}");
            }
            else
            {
                try
                {
                    ExecuteMessage(BuildAssociateRequest(relationship, record, peer, association.RecordIsSide1));
                }
                catch (Exception ex) when (!IsCancellation(ex))
                {
                    if (IsDuplicateKey(ex))
                    {
                        // The destination holds the pair already (its intersect could not be checked first).
                        _associations[key] = true;
                        Log(LogLevel.Info, logDepth, $"Association exists, skipped {pair}");
                        _associationsSkipped++;
                    }
                    else
                    {
                        Log(LogLevel.Warning, logDepth, $"Association failed {pair}: {ErrorText(ex)}");
                        _associationsFailed++;
                    }
                    ReportProgress();
                    return;
                }
                Log(LogLevel.Success, logDepth, $"Associated {pair}");
            }
            _associations[key] = true;
            _associationsCreated++;
            ReportProgress();
        }

        /// <summary>
        /// Is the pair associated in the destination? A QueryExpression on the destination intersect entity
        /// for both ids (TopCount 1), cached per pair for the run (an association made by the run counts).
        /// Null when the intersect cannot be queried: warned once per relationship, whose pairs are then
        /// associated without a check (the platform refuses a pair that exists: "Cannot insert duplicate key").
        /// </summary>
        private bool? AssociationExists(ManyToManyRelationship relationship, AssociationKey key, int logDepth)
        {
            if (_associations.TryGetValue(key, out bool known)) return known;
            if (_uncheckedRelationships.Contains(key.Relationship)) return null;

            string attribute1 = relationship.Entity1IntersectAttribute.Trim();
            string attribute2 = relationship.Entity2IntersectAttribute.Trim();
            var query = new QueryExpression(relationship.IntersectEntity.Trim().ToLowerInvariant())
            {
                ColumnSet = new ColumnSet(attribute1, attribute2),
                TopCount = 1
            };
            query.Criteria.AddCondition(attribute1, ConditionOperator.Equal, key.Side1);
            query.Criteria.AddCondition(attribute2, ConditionOperator.Equal, key.Side2);
            try
            {
                bool exists = (_destination.RetrieveMultiple(query)?.Entities?.Count ?? 0) > 0;
                _associations[key] = exists;
                return exists;
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                _uncheckedRelationships.Add(key.Relationship);
                Log(LogLevel.Warning, logDepth, $"Could not check the {relationship.SchemaName} associations in destination: {ErrorText(ex)}; associating without checking");
                return null;
            }
        }

        /// <summary>
        /// The AssociateRequest of one pair: Target the record, RelatedEntities the peer. A self-referential
        /// relationship needs the role of the target: Target is then the record on side 1 of the source row
        /// with PrimaryEntityRole Referencing (side 1), so the destination row keeps the source row's
        /// orientation.
        /// </summary>
        private static AssociateRequest BuildAssociateRequest(ManyToManyRelationship relationship, RecordKey record, RecordKey peer, bool recordIsSide1)
        {
            var schema = new Relationship(relationship.SchemaName.Trim());
            RecordKey target = record, related = peer;
            if (relationship.IsSelfReferential)
            {
                schema.PrimaryEntityRole = EntityRole.Referencing;
                if (!recordIsSide1)
                {
                    target = peer;
                    related = record;
                }
            }
            return new AssociateRequest
            {
                Target = new EntityReference(target.Entity, target.Id),
                Relationship = schema,
                RelatedEntities = new EntityReferenceCollection { new EntityReference(related.Entity, related.Id) }
            };
        }

        /// <summary>The platform refusing a pair that is associated already: "Cannot insert duplicate key" (0x80040237).</summary>
        internal static bool IsDuplicateKey(Exception ex)
        {
            if (!(ex is FaultException)) return false;
            OrganizationServiceFault detail = (ex as FaultException<OrganizationServiceFault>)?.Detail;
            if (detail != null && detail.ErrorCode == DuplicateRecordErrorCode) return true;
            string message = detail?.Message;
            if (string.IsNullOrEmpty(message)) message = ex.Message ?? string.Empty;
            return message.IndexOf("Cannot insert duplicate key", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // =====================================================================================
        // Reads
        // =====================================================================================

        /// <summary>
        /// Existence check in the destination, cached per entity+id for the run (SPEC 5.4): a
        /// QueryExpression on the primary id with TopCount 1, returning the primary name and state
        /// columns that exist. A virtual table is checked with a Retrieve by id instead (primary id and
        /// name columns; the record-not-found fault means "does not exist"): virtual-table providers may
        /// fail every query yet answer a Retrieve (SPEC 5.8). Without a schema (a never-create entity
        /// whose metadata is unavailable) the primary id is assumed to be "{entity}id" and only it is
        /// requested.
        /// </summary>
        private ExistenceInfo GetExistence(RecordKey key, EntitySchema destination, bool isVirtual)
        {
            if (_existence.TryGetValue(key, out ExistenceInfo cached)) return cached;

            string primaryId = !string.IsNullOrEmpty(destination?.PrimaryIdAttribute) ? destination.PrimaryIdAttribute : key.Entity + "id";
            string nameColumn = destination != null && !string.IsNullOrEmpty(destination.PrimaryNameAttribute) && destination.Attributes.ContainsKey(destination.PrimaryNameAttribute)
                ? destination.PrimaryNameAttribute
                : null;
            var columns = new List<string> { primaryId };
            if (nameColumn != null) columns.Add(nameColumn);

            Entity row;
            if (isVirtual)
            {
                try
                {
                    row = _destination.Retrieve(key.Entity, key.Id, new ColumnSet(columns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
                }
                catch (Exception ex) when (IsRecordNotFound(ex))
                {
                    row = null;
                }
            }
            else
            {
                if (destination != null && destination.Attributes.ContainsKey(StateCode)) columns.Add(StateCode);
                if (destination != null && destination.Attributes.ContainsKey(StatusCode)) columns.Add(StatusCode);
                var query = new QueryExpression(key.Entity)
                {
                    ColumnSet = new ColumnSet(columns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                    TopCount = 1
                };
                query.Criteria.AddCondition(primaryId, ConditionOperator.Equal, key.Id);
                row = _destination.RetrieveMultiple(query)?.Entities?.FirstOrDefault();
            }

            var info = new ExistenceInfo { Exists = row != null };
            if (row != null)
            {
                info.Name = nameColumn != null && row.Attributes.TryGetValue(nameColumn, out object name) ? CleanName(name as string) : null;
                info.StateCode = OptionValue(row, StateCode);
                info.StatusCode = OptionValue(row, StatusCode);
            }
            _existence[key] = info;
            return info;
        }

        /// <summary>
        /// The referenced entity in lower case. A reference without an entity name is resolved to the
        /// lookup's target when it has exactly one; otherwise null (it cannot be followed).
        /// </summary>
        private static string TargetEntity(EntityReference reference, AttributeSchema meta)
        {
            if (!string.IsNullOrEmpty(reference.LogicalName)) return reference.LogicalName.ToLowerInvariant();
            string[] targets = meta?.LookupTargets;
            return targets != null && targets.Length == 1 && !string.IsNullOrEmpty(targets[0]) ? targets[0].ToLowerInvariant() : null;
        }

        /// <summary>The entity's schema, or null when it does not exist or cannot be read (then <paramref name="failed"/> is true).</summary>
        private EntitySchema TryGetSchema(ISchemaProvider provider, string entity, out bool failed)
        {
            failed = false;
            try
            {
                return provider.GetEntity(entity);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                failed = true;
                return null;
            }
        }

        private Entity RetrieveSource(RecordKey key)
        {
            try
            {
                return _source.Retrieve(key.Entity, key.Id, new ColumnSet(true));
            }
            catch (Exception ex) when (IsRecordNotFound(ex))
            {
                return null;
            }
        }

        /// <summary>True for the "record does not exist" fault (0x80040217 or a "Does Not Exist" message).</summary>
        internal static bool IsRecordNotFound(Exception ex)
        {
            if (!(ex is FaultException)) return false;
            OrganizationServiceFault detail = (ex as FaultException<OrganizationServiceFault>)?.Detail;
            if (detail != null && detail.ErrorCode == ObjectDoesNotExistErrorCode) return true;
            string message = detail?.Message;
            if (string.IsNullOrEmpty(message)) message = ex.Message ?? string.Empty;
            return message.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        private bool IsCancellation(Exception ex) =>
            ex is OperationCanceledException && _cancellationToken.IsCancellationRequested;

        private static string PrimaryName(Entity source, RecordContext record)
        {
            string attribute = !string.IsNullOrEmpty(record.Source?.PrimaryNameAttribute)
                ? record.Source.PrimaryNameAttribute
                : record.Destination?.PrimaryNameAttribute;
            if (string.IsNullOrEmpty(attribute)) return null;
            return source.Attributes.TryGetValue(attribute, out object value) ? CleanName(value as string) : null;
        }

        private static int? OptionValue(Entity entity, string attribute)
        {
            if (entity == null || !entity.Attributes.TryGetValue(attribute, out object value) || value == null) return null;
            switch (value)
            {
                case OptionSetValue option: return option.Value;
                case int number: return number;
                default: return null;
            }
        }

        private static T ValueOf<T>(Entity entity, string attribute) where T : class =>
            entity != null && entity.Attributes.TryGetValue(attribute, out object value) ? value as T : null;

        private static int? DefaultStatus(EntitySchema schema, int state) =>
            schema?.DefaultStatusByState != null && schema.DefaultStatusByState.TryGetValue(state, out int status) ? status : (int?)null;

        private static Entity CloneWithout(Entity entity, ICollection<string> attributes)
        {
            var clone = new Entity(entity.LogicalName) { Id = entity.Id };
            foreach (KeyValuePair<string, object> pair in entity.Attributes)
            {
                if (!attributes.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) clone[pair.Key] = pair.Value;
            }
            return clone;
        }

        private void Remember(RecordKey key, string name)
        {
            if (!string.IsNullOrEmpty(name)) _names[key] = name;
        }

        private string NameOf(RecordKey key) => _names.TryGetValue(key, out string name) ? name : null;

        /// <summary>entity "name" (id), or entity (id) when the name is unknown.</summary>
        private static string Describe(RecordKey key, string name) =>
            string.IsNullOrEmpty(name) ? $"{key.Entity} ({key.Id})" : $"{key.Entity} \"{name}\" ({key.Id})";

        /// <summary>entity "name", or entity (id) when the name is unknown.</summary>
        private static string DescribeOwner(RecordKey key, string name) =>
            string.IsNullOrEmpty(name) ? $"{key.Entity} ({key.Id})" : $"{key.Entity} \"{name}\"";

        private static string FirstNonEmpty(string first, string second) => !string.IsNullOrEmpty(first) ? first : second;

        /// <summary>One-line, length-capped record name for log lines.</summary>
        private static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string single = OneLine(name);
            return single.Length <= MaxNameLength ? single : single.Substring(0, MaxNameLength - 3) + "...";
        }

        /// <summary>A one-line error message (fault detail preferred) for log lines and the summary.</summary>
        internal static string ErrorText(Exception ex)
        {
            if (ex == null) return string.Empty;
            string message = (ex as FaultException<OrganizationServiceFault>)?.Detail?.Message;
            if (string.IsNullOrWhiteSpace(message)) message = ex.Message;
            return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : OneLine(message);
        }

        private static string OneLine(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                builder.Append(c);
            }
            return builder.ToString();
        }

        private void Log(LogLevel level, int depth, string message) =>
            _logger.Log(level, new string(' ', Math.Max(0, depth) * 2) + message);

        private void ReportProgress()
        {
            _progress?.Report(new CopyProgress
            {
                SelectedIndex = _selectedIndex,
                SelectedTotal = _selectedTotal,
                Created = _created,
                Updated = _updated,
                SkippedExisting = _skippedExisting,
                Failed = _failed,
                LookupsBlanked = _lookupsBlanked,
                LookupsBackfilled = _lookupsBackfilled,
                ChildRecordsFound = _childRecordsFound,
                PeerRecordsFound = _peerRecordsFound,
                AssociationsCreated = _associationsCreated,
                AssociationsSkipped = _associationsSkipped,
                AssociationsFailed = _associationsFailed,
                CurrentRecord = _currentRecord
            });
        }

        private void ResetRun(int selectedTotal, IProgress<CopyProgress> progress, CancellationToken cancellationToken)
        {
            _states = new Dictionary<RecordKey, RecordState>();
            _existence = new Dictionary<RecordKey, ExistenceInfo>();
            _failedChecks = new Dictionary<RecordKey, string>();
            _targetKinds = new Dictionary<string, TargetKind>(StringComparer.OrdinalIgnoreCase);
            _names = new Dictionary<RecordKey, string>();
            _backfills = new Dictionary<RecordKey, List<Backfill>>();
            _deferredLookups = new Queue<DeferredLookup>();
            _pendingStates = new Queue<PendingState>();
            _warnedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _walked = new Dictionary<RecordKey, RelationshipContext>();
            _followed = new Dictionary<RecordKey, HashSet<string>>();
            _childRelationships = new Dictionary<string, IReadOnlyList<ChildRelationship>>(StringComparer.OrdinalIgnoreCase);
            _manyToManyRelationships = new Dictionary<string, IReadOnlyList<ManyToManyRelationship>>(StringComparer.OrdinalIgnoreCase);
            _warnedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _associations = new Dictionary<AssociationKey, bool>();
            _uncheckedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _errors = new List<string>();
            _selectedIndex = 0;
            _selectedTotal = selectedTotal;
            _created = _updated = _skippedExisting = _failed = _lookupsBlanked = _lookupsBackfilled = _stateChanges = _childRecordsFound = 0;
            _peerRecordsFound = _associationsCreated = _associationsSkipped = _associationsFailed = 0;
            _currentRecord = null;
            _progress = progress;
            _cancellationToken = cancellationToken;
        }

        private CopySummary BuildSummary(TimeSpan elapsed, bool cancelled) => new CopySummary
        {
            SelectedTotal = _selectedTotal,
            Created = _created,
            Updated = _updated,
            SkippedExisting = _skippedExisting,
            Failed = _failed,
            LookupsBlanked = _lookupsBlanked,
            LookupsBackfilled = _lookupsBackfilled,
            StateChanges = _stateChanges,
            ChildRecordsFound = _childRecordsFound,
            PeerRecordsFound = _peerRecordsFound,
            AssociationsCreated = _associationsCreated,
            AssociationsSkipped = _associationsSkipped,
            AssociationsFailed = _associationsFailed,
            Cancelled = cancelled,
            DryRun = _options.DryRun,
            Elapsed = elapsed,
            Errors = _errors.ToList().AsReadOnly()
        };

        // =====================================================================================
        // Private types
        // =====================================================================================

        private enum RecordState { Unknown, InProgress, Created, Updated, Exists, Failed, Missing }

        private enum EnsureResult { Available, Unavailable, Deferred }

        private readonly struct RecordKey : IEquatable<RecordKey>
        {
            public RecordKey(string entity, Guid id)
            {
                Entity = (entity ?? string.Empty).ToLowerInvariant();
                Id = id;
            }

            public string Entity { get; }
            public Guid Id { get; }

            public bool Equals(RecordKey other) => Id == other.Id && string.Equals(Entity, other.Entity, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is RecordKey other && Equals(other);
            public override int GetHashCode() => unchecked(((Entity ?? string.Empty).GetHashCode() * 397) ^ Id.GetHashCode());
        }

        /// <summary>One associated pair of an N:N relationship: the relationship, then the ids on side 1 and side 2.</summary>
        private readonly struct AssociationKey : IEquatable<AssociationKey>
        {
            public AssociationKey(string relationship, Guid side1, Guid side2)
            {
                Relationship = (relationship ?? string.Empty).Trim().ToLowerInvariant();
                Side1 = side1;
                Side2 = side2;
            }

            public string Relationship { get; }
            public Guid Side1 { get; }
            public Guid Side2 { get; }

            public bool Equals(AssociationKey other) =>
                Side1 == other.Side1 && Side2 == other.Side2 && string.Equals(Relationship, other.Relationship, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is AssociationKey other && Equals(other);
            public override int GetHashCode() =>
                unchecked((((Relationship ?? string.Empty).GetHashCode() * 397) ^ Side1.GetHashCode()) * 397 ^ Side2.GetHashCode());
        }

        /// <summary>A row of a source intersect: the peer, which side the record is on, and the pair.</summary>
        private sealed class Association
        {
            public Association(RecordKey peer, bool recordIsSide1, AssociationKey key)
            {
                Peer = peer;
                RecordIsSide1 = recordIsSide1;
                Key = key;
            }

            public RecordKey Peer { get; }
            public bool RecordIsSide1 { get; }
            public AssociationKey Key { get; }
        }

        private sealed class ExistenceInfo
        {
            public bool Exists;
            public string Name;
            public int? StateCode;
            public int? StatusCode;
        }

        /// <summary>How the records of one entity are treated as lookup targets (SPEC 5.8).</summary>
        private sealed class TargetKind
        {
            public bool NeverCreate;              // in the never-create list
            public bool IsVirtual;                // a virtual table
            public bool NotCopied;                // any other entity while lookups are not copied (SPEC 5.10)
            public bool MissingInDestination;     // virtual per the source schema (or NotCopied), and the destination has no such entity
            public EntitySchema Schema;           // for the existence check (null: "{entity}id" is assumed)

            public bool ExistenceOnly => NeverCreate || IsVirtual || NotCopied;
        }

        /// <summary>The answer of an existence check of a never-create or virtual lookup target.</summary>
        private sealed class TargetCheck
        {
            public static readonly TargetCheck Found = new TargetCheck { Exists = true };

            public static TargetCheck Missing(string problem) => new TargetCheck { Exists = false, Problem = problem };

            public static TargetCheck Unverified(string error, bool failedEarlier) =>
                new TargetCheck { Exists = null, Error = error, FailedEarlier = failedEarlier };

            public bool? Exists { get; private set; }        // null: the destination could not answer
            public string Problem { get; private set; }      // why the lookup is blanked (Exists == false)
            public string Error { get; private set; }        // why the check failed (Exists == null)
            public bool FailedEarlier { get; private set; }  // the failure is the cached result of an earlier check
        }

        private sealed class Backfill
        {
            public Backfill(RecordKey owner, string attribute)
            {
                Owner = owner;
                Attribute = attribute;
            }

            public RecordKey Owner { get; }       // the record whose lookup was omitted
            public string Attribute { get; }      // the omitted lookup attribute
        }

        /// <summary>
        /// A lookup left out of its record's write while lookups are not copied, because the record it
        /// points at was not in the destination yet: decided once the selected record's tree is done (SPEC 5.10).
        /// </summary>
        private sealed class DeferredLookup
        {
            public DeferredLookup(RecordContext owner, string attribute, RecordKey target)
            {
                Owner = owner.Key;
                OwnerName = owner.Name;
                Depth = owner.Depth;
                Attribute = attribute;
                Target = target;
            }

            public RecordKey Owner { get; }       // the record whose lookup was left out
            public string OwnerName { get; }
            public int Depth { get; }             // the owner's depth: its lookups are logged one level deeper
            public string Attribute { get; }      // the lookup attribute
            public RecordKey Target { get; }      // the record the lookup points at
        }

        /// <summary>A state change waiting for its selected record's tree to finish (SPEC 5.9).</summary>
        private sealed class PendingState
        {
            public PendingState(RecordContext record, Entity source, int state, int? status)
            {
                Key = record.Key;
                Name = record.Name;
                Depth = record.Depth;
                IsUpdate = record.IsUpdate;
                Destination = record.Destination;
                Current = record.Existing;
                Source = source;
                State = state;
                Status = status;
            }

            public RecordKey Key { get; }
            public string Name { get; }
            public int Depth { get; }
            public bool IsUpdate { get; }
            public EntitySchema Destination { get; }
            public ExistenceInfo Current { get; }     // the destination's state, updated once the change is applied
            public Entity Source { get; }             // the source record (the close messages read from it)
            public int State { get; }
            public int? Status { get; }
        }

        /// <summary>Everything known about the record one EnsureRecord call is working on.</summary>
        private sealed class RecordContext
        {
            public RecordContext(RecordKey key, int depth, bool isSelected, bool isChild, bool isPeer, string name)
            {
                Key = key;
                Depth = depth;
                IsSelected = isSelected;
                IsChild = isChild;
                IsPeer = isPeer;
                Name = name;
            }

            public RecordKey Key { get; }
            public int Depth { get; }
            public bool IsSelected { get; }
            public bool IsChild { get; }            // reached through a 1:N relationship (SPEC 5.10)
            public bool IsPeer { get; }             // reached through an N:N relationship (SPEC 5.10)

            /// <summary>How the record was reached: what its own relationships are chosen by.</summary>
            public RelationshipContext Context => IsPeer ? RelationshipContext.Peer : RelationshipContext.SelectedOrChild;

            public string Name { get; set; }
            public EntitySchema Destination { get; set; }
            public EntitySchema Source { get; set; }
            public ExistenceInfo Existing { get; set; }
            public bool IsUpdate { get; set; }

            /// <summary>Lookups kept although their target could not be checked (SPEC 5.8); a failing write is retried without them.</summary>
            public List<string> UnverifiedLookups { get; } = new List<string>();

            public void MarkUnverified(string attribute)
            {
                if (!UnverifiedLookups.Contains(attribute, StringComparer.OrdinalIgnoreCase)) UnverifiedLookups.Add(attribute);
            }
        }
    }
}
