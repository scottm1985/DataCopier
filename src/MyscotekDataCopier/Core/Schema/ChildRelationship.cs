namespace MyscotekDataCopier.Core.Schema
{
    /// <summary>
    /// A 1:N relationship of an entity (SPEC 5.10): the records of <see cref="ChildEntity"/> whose
    /// <see cref="ChildLookupAttribute"/> points at a record of <see cref="ParentEntity"/> are its child
    /// records. Built from OneToManyRelationshipMetadata (ReferencedEntity = parent, ReferencingEntity
    /// = child, ReferencingAttribute = the child's lookup).
    /// </summary>
    public sealed class ChildRelationship
    {
        /// <summary>The relationship schema name, e.g. contact_customer_accounts (what a form subgrid names).</summary>
        public string SchemaName { get; set; }

        /// <summary>The referenced (parent) entity, e.g. account.</summary>
        public string ParentEntity { get; set; }

        /// <summary>The referencing (child) entity, e.g. contact.</summary>
        public string ChildEntity { get; set; }

        /// <summary>The child's lookup that points at the parent, e.g. parentcustomerid.</summary>
        public string ChildLookupAttribute { get; set; }

        /// <summary>A custom (not system) relationship.</summary>
        public bool IsCustomRelationship { get; set; }

        public override string ToString() => $"{SchemaName} ({ChildEntity}.{ChildLookupAttribute})";
    }
}
