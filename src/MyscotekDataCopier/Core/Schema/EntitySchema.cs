using System;
using System.Collections.Generic;

namespace MyscotekDataCopier.Core.Schema
{
    /// <summary>The slice of an entity's metadata the copy engine needs (built from EntityMetadata).</summary>
    public sealed class EntitySchema
    {
        public string LogicalName, PrimaryIdAttribute, PrimaryNameAttribute, DisplayName;

        /// <summary>The plural display name (e.g. "Contacts"); null when there is none.</summary>
        public string DisplayCollectionName;

        public bool IsIntersect;

        /// <summary>A private (internal system) entity: never followed as a 1:N child entity (SPEC 5.10).</summary>
        public bool IsPrivate;

        /// <summary>
        /// A virtual table: its rows live in an external data source. The copier never retrieves them
        /// from the source, never creates or updates them, and checks them by id (SPEC 5.8).
        /// </summary>
        public bool IsVirtual;

        /// <summary>Keyed by attribute logical name, OrdinalIgnoreCase.</summary>
        public IReadOnlyDictionary<string, AttributeSchema> Attributes =
            new Dictionary<string, AttributeSchema>(StringComparer.OrdinalIgnoreCase);

        /// <summary>statecode -> default statuscode (from StateOptionMetadata.DefaultStatus); may be empty.</summary>
        public IReadOnlyDictionary<int, int> DefaultStatusByState = new Dictionary<int, int>();

        /// <summary>
        /// The 1:N relationships in which this entity is the parent (referenced) entity, in metadata
        /// order: where its child records are found (SPEC 5.10). May be empty.
        /// </summary>
        public IReadOnlyList<ChildRelationship> OneToManyRelationships = Array.Empty<ChildRelationship>();

        public bool HasStateCode => Attributes != null && Attributes.ContainsKey("statecode");
    }
}
