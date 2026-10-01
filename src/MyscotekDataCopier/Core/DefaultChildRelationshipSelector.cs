using System;
using System.Collections.Generic;
using System.Linq;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Core
{
    /// <summary>
    /// Which 1:N relationships may be followed to child records at all (SPEC 5.10), for the picker and
    /// the run alike. A relationship is eligible unless its child entity is on the built-in system
    /// exclusion list, in the never-create list, missing from the source (or its metadata cannot be
    /// read), intersect, private or virtual; the child lookup must exist and be valid for create; an
    /// msdyn_ child entity must be creatable (its primary id valid for create). activitypointer is
    /// eligible whatever the create flag of its lookup: its rows are copied as their concrete activities.
    /// </summary>
    public static class ChildRelationshipEligibility
    {
        /// <summary>The activity pointer: listed through it, then copied as email, task, phonecall...</summary>
        public const string ActivityPointer = "activitypointer";

        /// <summary>System child entities that are never followed (the platform writes them itself, or they are internal).</summary>
        public static readonly IReadOnlyCollection<string> SystemExcludedChildEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "asyncoperation", "syncerror", "duplicaterecord", "processsession", "workflowlog", "bulkdeletefailure",
            "principalobjectattributeaccess", "userentityinstancedata", "mailboxtrackingfolder", "customeraddress",
            "actioncard", "postfollow", "postregarding", "recordcountsnapshot", "tracelog", "activityparty"
        };

        public static bool IsSystemExcluded(string entity) =>
            !string.IsNullOrWhiteSpace(entity) && SystemExcludedChildEntities.Contains(entity.Trim());

        /// <summary>
        /// The eligible 1:N relationships of <paramref name="parentEntity"/>, in metadata order (empty
        /// when the entity does not exist). Reads the source metadata of the parent and of each child
        /// entity; a failure to read the parent propagates, a child whose metadata cannot be read is
        /// not eligible.
        /// </summary>
        public static IReadOnlyList<ChildRelationship> GetEligible(ISchemaProvider sourceSchema, string parentEntity, ISet<string> neverCreate)
        {
            if (sourceSchema == null) throw new ArgumentNullException(nameof(sourceSchema));
            if (string.IsNullOrWhiteSpace(parentEntity)) return Array.Empty<ChildRelationship>();
            EntitySchema parent = sourceSchema.GetEntity(parentEntity.Trim());
            if (parent?.OneToManyRelationships == null) return Array.Empty<ChildRelationship>();
            return parent.OneToManyRelationships.Where(r => IsEligible(sourceSchema, r, neverCreate)).ToList().AsReadOnly();
        }

        public static bool IsEligible(ISchemaProvider sourceSchema, ChildRelationship relationship, ISet<string> neverCreate)
        {
            if (sourceSchema == null) throw new ArgumentNullException(nameof(sourceSchema));
            if (relationship == null || string.IsNullOrWhiteSpace(relationship.SchemaName)
                || string.IsNullOrWhiteSpace(relationship.ChildEntity) || string.IsNullOrWhiteSpace(relationship.ChildLookupAttribute))
            {
                return false;
            }

            string child = relationship.ChildEntity.Trim().ToLowerInvariant();
            if (IsSystemExcluded(child)) return false;
            if (neverCreate != null && neverCreate.Any(n => string.Equals(n, child, StringComparison.OrdinalIgnoreCase))) return false;

            EntitySchema schema;
            try
            {
                schema = sourceSchema.GetEntity(child);
            }
            catch (Exception)
            {
                return false;   // metadata unavailable: its records could not be copied anyway
            }
            if (schema == null || schema.IsIntersect || schema.IsPrivate || schema.IsVirtual) return false;

            if (schema.Attributes == null
                || !schema.Attributes.TryGetValue(relationship.ChildLookupAttribute.Trim(), out AttributeSchema lookup) || lookup == null)
            {
                return false;
            }
            if (child == ActivityPointer) return true;
            if (!lookup.IsValidForCreate) return false;
            return !child.StartsWith("msdyn_", StringComparison.Ordinal) || IsCreatable(schema);
        }

        /// <summary>A table whose rows can be created: its primary id is valid for create.</summary>
        private static bool IsCreatable(EntitySchema schema) =>
            !string.IsNullOrEmpty(schema.PrimaryIdAttribute)
            && schema.Attributes.TryGetValue(schema.PrimaryIdAttribute, out AttributeSchema id) && id != null && id.IsValidForCreate;
    }

    /// <summary>
    /// Which N:N relationships may be followed to associated records (peers) at all (SPEC 5.10), for
    /// the picker, the run's selector and the engine. A relationship is eligible for an entity when the
    /// entity is side 1 or side 2 of it (a self-referential relationship is both); its intersect entity
    /// is not a system one; the entity at the other end is not in the never-create list, not a security
    /// or queue table (role, privilege, fieldsecurityprofile, queue, position), its source metadata can
    /// be read and it is neither virtual nor private. When a destination schema is given, the
    /// relationship (by schema name, on the entity) and its intersect entity must also exist there.
    /// </summary>
    public static class ManyToManyEligibility
    {
        /// <summary>Intersect entities of system relationships that are never followed (security, membership, sharing).</summary>
        public static readonly IReadOnlyCollection<string> SystemIntersectEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "systemuserroles", "teamroles", "teamprofiles", "systemuserprofiles", "roleprivileges", "teammembership",
            "principalobjectaccess", "queuemembership", "roletemplateprivileges", "appmoduleroles"
        };

        /// <summary>Entities never reached as peers: an N:N relationship to one of them is not followed.</summary>
        public static readonly IReadOnlyCollection<string> ExcludedPeerEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "role", "privilege", "fieldsecurityprofile", "queue", "position"
        };

        public static bool IsSystemIntersect(string intersectEntity) =>
            !string.IsNullOrWhiteSpace(intersectEntity) && SystemIntersectEntities.Contains(intersectEntity.Trim());

        public static bool IsExcludedPeerEntity(string entity) =>
            !string.IsNullOrWhiteSpace(entity) && ExcludedPeerEntities.Contains(entity.Trim());

        /// <summary>
        /// The eligible N:N relationships of <paramref name="entity"/>, in metadata order (empty when the
        /// entity does not exist). Reads the source metadata of the entity and of each entity at the other
        /// end (and, with <paramref name="destinationSchema"/>, the destination metadata); a failure to read
        /// the entity itself propagates.
        /// </summary>
        public static IReadOnlyList<ManyToManyRelationship> GetEligible(ISchemaProvider sourceSchema, string entity, ISet<string> neverCreate,
                                                                       ISchemaProvider destinationSchema = null)
        {
            if (sourceSchema == null) throw new ArgumentNullException(nameof(sourceSchema));
            if (string.IsNullOrWhiteSpace(entity)) return Array.Empty<ManyToManyRelationship>();
            EntitySchema schema = sourceSchema.GetEntity(entity.Trim());
            if (schema?.ManyToManyRelationships == null) return Array.Empty<ManyToManyRelationship>();
            return schema.ManyToManyRelationships.Where(r => IsEligible(sourceSchema, entity, r, neverCreate, destinationSchema)).ToList().AsReadOnly();
        }

        /// <summary>Whether <paramref name="relationship"/> may be followed from the records of <paramref name="entity"/> (see the class).</summary>
        public static bool IsEligible(ISchemaProvider sourceSchema, string entity, ManyToManyRelationship relationship, ISet<string> neverCreate,
                                      ISchemaProvider destinationSchema = null)
        {
            if (sourceSchema == null) throw new ArgumentNullException(nameof(sourceSchema));
            if (string.IsNullOrWhiteSpace(entity) || relationship == null || string.IsNullOrWhiteSpace(relationship.SchemaName)
                || string.IsNullOrWhiteSpace(relationship.IntersectEntity)
                || string.IsNullOrWhiteSpace(relationship.Entity1IntersectAttribute) || string.IsNullOrWhiteSpace(relationship.Entity2IntersectAttribute))
            {
                return false;
            }

            string name = entity.Trim().ToLowerInvariant();
            string peer = relationship.OtherEntity(name);
            if (peer == null) return false;   // the entity is not a side of it
            if (IsSystemIntersect(relationship.IntersectEntity) || IsExcludedPeerEntity(peer)) return false;
            if (neverCreate != null && neverCreate.Any(n => string.Equals(n, peer, StringComparison.OrdinalIgnoreCase))) return false;

            EntitySchema schema;
            try
            {
                schema = sourceSchema.GetEntity(peer);
            }
            catch (Exception)
            {
                return false;   // metadata unavailable: its records could not be copied anyway
            }
            if (schema == null || schema.IsVirtual || schema.IsPrivate) return false;
            return destinationSchema == null || DestinationProblem(destinationSchema, name, relationship) == null;
        }

        /// <summary>
        /// Why <paramref name="relationship"/> cannot be followed from <paramref name="entity"/> in the
        /// destination - the entity, the relationship (by schema name) or its intersect entity is missing
        /// there - or null when it can. Metadata the destination cannot return (other than "does not
        /// exist") is not held against the relationship: null.
        /// </summary>
        public static string DestinationProblem(ISchemaProvider destinationSchema, string entity, ManyToManyRelationship relationship)
        {
            if (destinationSchema == null || string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(relationship?.SchemaName)) return null;
            string name = entity.Trim().ToLowerInvariant();

            EntitySchema schema;
            try
            {
                schema = destinationSchema.GetEntity(name);
            }
            catch (Exception)
            {
                return null;
            }
            if (schema == null) return $"{name} does not exist in destination";
            string schemaName = relationship.SchemaName.Trim();
            bool present = (schema.ManyToManyRelationships ?? Array.Empty<ManyToManyRelationship>())
                .Any(r => r != null && string.Equals(r.SchemaName?.Trim(), schemaName, StringComparison.OrdinalIgnoreCase));
            if (!present) return "it does not exist in destination";

            string intersect = relationship.IntersectEntity?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(intersect)) return null;
            EntitySchema intersectSchema;
            try
            {
                intersectSchema = destinationSchema.GetEntity(intersect);
            }
            catch (Exception)
            {
                return null;
            }
            return intersectSchema == null ? $"its intersect entity {intersect} does not exist in destination" : null;
        }
    }

    /// <summary>
    /// The run's <see cref="IRelationshipSelector"/> (SPEC 5.10). For an entity configured in the
    /// relationship picker: its ticked 1:N and N:N relationships, however its records are reached. For
    /// any other entity reached as a selected or child record: the relationships shown as subgrids on
    /// its active main forms in the source; reached as a peer (through an N:N relationship): nothing.
    /// In every case only the eligible relationships (<see cref="ChildRelationshipEligibility"/>,
    /// <see cref="ManyToManyEligibility"/> against the source), in metadata order, names compared
    /// case-insensitively. Answers are cached for the lifetime of the selector (one run) per entity and
    /// context, the main-form subgrids per entity; a failure (e.g. reading the forms) propagates and is
    /// not cached.
    /// </summary>
    public sealed class DefaultChildRelationshipSelector : IRelationshipSelector
    {
        private readonly ISchemaProvider _sourceSchema;
        private readonly Func<string, IReadOnlyCollection<string>> _subgridRelationshipNames;
        private readonly Dictionary<string, HashSet<string>> _configured = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly ISet<string> _neverCreate;
        private readonly Dictionary<string, IReadOnlyList<ChildRelationship>> _cache =
            new Dictionary<string, IReadOnlyList<ChildRelationship>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<ManyToManyRelationship>> _manyToManyCache =
            new Dictionary<string, IReadOnlyList<ManyToManyRelationship>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _subgrids = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <param name="sourceSchema">Source metadata: the entity's relationships and the eligibility checks.</param>
        /// <param name="subgridRelationshipNames">The relationship names of an entity's main-form subgrids (null: none).</param>
        /// <param name="configuredSelections">Entity -> ticked relationship schema names (1:N and N:N); an entity present here is configured.</param>
        /// <param name="neverCreate">Entities whose records are never created (never followed as child or peer entities).</param>
        public DefaultChildRelationshipSelector(ISchemaProvider sourceSchema, Func<string, IReadOnlyCollection<string>> subgridRelationshipNames,
                                                IReadOnlyDictionary<string, ISet<string>> configuredSelections, ISet<string> neverCreate)
        {
            _sourceSchema = sourceSchema ?? throw new ArgumentNullException(nameof(sourceSchema));
            _subgridRelationshipNames = subgridRelationshipNames;
            _neverCreate = neverCreate ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (configuredSelections != null)
            {
                foreach (KeyValuePair<string, ISet<string>> pair in configuredSelections)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    _configured[pair.Key.Trim()] = new HashSet<string>(
                        (pair.Value ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
                        StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>True when the relationships of the entity were chosen in the picker (else its main-form subgrids apply).</summary>
        public bool IsConfigured(string entity) => !string.IsNullOrWhiteSpace(entity) && _configured.ContainsKey(entity.Trim());

        /// <summary>The 1:N relationships of a selected or child record (<see cref="RelationshipContext.SelectedOrChild"/>).</summary>
        public IReadOnlyList<ChildRelationship> GetChildRelationships(string parentEntity) =>
            GetChildRelationships(parentEntity, RelationshipContext.SelectedOrChild);

        public IReadOnlyList<ChildRelationship> GetChildRelationships(string entity, RelationshipContext context)
        {
            if (string.IsNullOrWhiteSpace(entity)) return Array.Empty<ChildRelationship>();
            string name = entity.Trim();
            string key = CacheKey(name, context);
            lock (_sync)
            {
                if (_cache.TryGetValue(key, out IReadOnlyList<ChildRelationship> cached)) return cached;
            }

            ICollection<string> names = Chosen(name, context);
            IReadOnlyList<ChildRelationship> result = Array.Empty<ChildRelationship>();
            if (names.Count > 0)
            {
                IReadOnlyList<ChildRelationship> all = _sourceSchema.GetEntity(name)?.OneToManyRelationships ?? Array.Empty<ChildRelationship>();
                result = all
                    .Where(r => r != null && r.SchemaName != null && names.Contains(r.SchemaName))
                    .Where(r => ChildRelationshipEligibility.IsEligible(_sourceSchema, r, _neverCreate))
                    .ToList()
                    .AsReadOnly();
            }

            lock (_sync)
            {
                _cache[key] = result;
            }
            return result;
        }

        public IReadOnlyList<ManyToManyRelationship> GetManyToManyRelationships(string entity, RelationshipContext context)
        {
            if (string.IsNullOrWhiteSpace(entity)) return Array.Empty<ManyToManyRelationship>();
            string name = entity.Trim();
            string key = CacheKey(name, context);
            lock (_sync)
            {
                if (_manyToManyCache.TryGetValue(key, out IReadOnlyList<ManyToManyRelationship> cached)) return cached;
            }

            ICollection<string> names = Chosen(name, context);
            IReadOnlyList<ManyToManyRelationship> result = Array.Empty<ManyToManyRelationship>();
            if (names.Count > 0)
            {
                IReadOnlyList<ManyToManyRelationship> all = _sourceSchema.GetEntity(name)?.ManyToManyRelationships ?? Array.Empty<ManyToManyRelationship>();
                result = all
                    .Where(r => r != null && r.SchemaName != null && names.Contains(r.SchemaName))
                    .Where(r => ManyToManyEligibility.IsEligible(_sourceSchema, name, r, _neverCreate))
                    .ToList()
                    .AsReadOnly();
            }

            lock (_sync)
            {
                _manyToManyCache[key] = result;
            }
            return result;
        }

        /// <summary>
        /// The relationship names chosen for an entity: its ticks when it is configured; otherwise the
        /// subgrids on its active main forms (read once per entity) for a selected or child record, and
        /// nothing for a peer.
        /// </summary>
        private ICollection<string> Chosen(string entity, RelationshipContext context)
        {
            if (_configured.TryGetValue(entity, out HashSet<string> ticked)) return ticked;
            if (context == RelationshipContext.Peer) return Array.Empty<string>();
            lock (_sync)
            {
                if (_subgrids.TryGetValue(entity, out HashSet<string> known)) return known;
            }
            var names = new HashSet<string>(_subgridRelationshipNames?.Invoke(entity) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                _subgrids[entity] = names;
            }
            return names;
        }

        private static string CacheKey(string entity, RelationshipContext context) =>
            (context == RelationshipContext.Peer ? "peer|" : "record|") + entity;
    }
}
