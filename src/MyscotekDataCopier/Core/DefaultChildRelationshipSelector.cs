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
    /// The run's <see cref="IChildRelationshipSelector"/> (SPEC 5.10): for an entity configured in the
    /// relationship picker, its ticked relationships; for any other entity, the relationships shown as
    /// subgrids on its active main forms in the source - in both cases only the eligible ones
    /// (<see cref="ChildRelationshipEligibility"/>). The answer per entity is cached for the lifetime of
    /// the selector (one run); a failure (e.g. reading the forms) propagates and is not cached.
    /// </summary>
    public sealed class DefaultChildRelationshipSelector : IChildRelationshipSelector
    {
        private readonly ISchemaProvider _sourceSchema;
        private readonly Func<string, IReadOnlyCollection<string>> _subgridRelationshipNames;
        private readonly Dictionary<string, HashSet<string>> _configured = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly ISet<string> _neverCreate;
        private readonly Dictionary<string, IReadOnlyList<ChildRelationship>> _cache =
            new Dictionary<string, IReadOnlyList<ChildRelationship>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <param name="sourceSchema">Source metadata: the parent's 1:N relationships and the eligibility checks.</param>
        /// <param name="subgridRelationshipNames">The relationship names of an entity's main-form subgrids (null: none).</param>
        /// <param name="configuredSelections">Entity -> ticked relationship schema names; an entity present here is configured.</param>
        /// <param name="neverCreate">Entities whose records are never created (never followed as child entities).</param>
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

        public IReadOnlyList<ChildRelationship> GetChildRelationships(string parentEntity)
        {
            if (string.IsNullOrWhiteSpace(parentEntity)) return Array.Empty<ChildRelationship>();
            string entity = parentEntity.Trim();
            lock (_sync)
            {
                if (_cache.TryGetValue(entity, out IReadOnlyList<ChildRelationship> cached)) return cached;
            }

            ICollection<string> names = _configured.TryGetValue(entity, out HashSet<string> ticked)
                ? ticked
                : new HashSet<string>(_subgridRelationshipNames?.Invoke(entity) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<ChildRelationship> result = Array.Empty<ChildRelationship>();
            if (names.Count > 0)
            {
                IReadOnlyList<ChildRelationship> all = _sourceSchema.GetEntity(entity)?.OneToManyRelationships ?? Array.Empty<ChildRelationship>();
                result = all
                    .Where(r => r != null && r.SchemaName != null && names.Contains(r.SchemaName))
                    .Where(r => ChildRelationshipEligibility.IsEligible(_sourceSchema, r, _neverCreate))
                    .ToList()
                    .AsReadOnly();
            }

            lock (_sync)
            {
                _cache[entity] = result;
            }
            return result;
        }
    }
}
