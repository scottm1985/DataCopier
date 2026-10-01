using System;

namespace MyscotekDataCopier.Core.Schema
{
    /// <summary>
    /// An N:N relationship (SPEC 5.10): the records of <see cref="Entity1LogicalName"/> and
    /// <see cref="Entity2LogicalName"/> associated through the rows of <see cref="IntersectEntity"/>,
    /// whose <see cref="Entity1IntersectAttribute"/> and <see cref="Entity2IntersectAttribute"/> hold
    /// the ids of the two records. Built from ManyToManyRelationshipMetadata; listed on both entities
    /// (once on a self-referential relationship, where both sides are the same entity).
    /// </summary>
    public sealed class ManyToManyRelationship
    {
        /// <summary>The relationship schema name, e.g. accountleads_association (what a form subgrid names).</summary>
        public string SchemaName { get; set; }

        /// <summary>The intersect entity holding one row per associated pair, e.g. accountleads.</summary>
        public string IntersectEntity { get; set; }

        /// <summary>The entity of side 1, e.g. account.</summary>
        public string Entity1LogicalName { get; set; }

        /// <summary>The intersect attribute holding the id of the side-1 record, e.g. accountid.</summary>
        public string Entity1IntersectAttribute { get; set; }

        /// <summary>The entity of side 2, e.g. lead.</summary>
        public string Entity2LogicalName { get; set; }

        /// <summary>The intersect attribute holding the id of the side-2 record, e.g. leadid.</summary>
        public string Entity2IntersectAttribute { get; set; }

        /// <summary>A custom (not system) relationship.</summary>
        public bool IsCustomRelationship { get; set; }

        /// <summary>Both sides are the same entity (e.g. account to account).</summary>
        public bool IsSelfReferential => SameEntity(Entity1LogicalName, Entity2LogicalName);

        /// <summary>True when <paramref name="entity"/> is side 1 or side 2 of the relationship.</summary>
        public bool Involves(string entity) => SameEntity(entity, Entity1LogicalName) || SameEntity(entity, Entity2LogicalName);

        /// <summary>
        /// The entity at the other end from <paramref name="entity"/>, in lower case: side 2 for side 1 and
        /// side 1 for side 2 (the entity itself for a self-referential relationship); null when the
        /// relationship does not involve <paramref name="entity"/>.
        /// </summary>
        public string OtherEntity(string entity)
        {
            if (SameEntity(entity, Entity1LogicalName)) return Normalize(Entity2LogicalName);
            if (SameEntity(entity, Entity2LogicalName)) return Normalize(Entity1LogicalName);
            return null;
        }

        public override string ToString() => $"{SchemaName} ({Normalize(Entity1LogicalName)} <-> {Normalize(Entity2LogicalName)})";

        private static bool SameEntity(string first, string second) =>
            !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
            && string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string entity) => entity?.Trim().ToLowerInvariant();
    }
}
