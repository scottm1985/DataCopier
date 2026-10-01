using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Tests.Fakes
{
    /// <summary>
    /// In-memory <see cref="ISchemaProvider"/> with a fluent builder:
    /// <c>new FakeSchemaProvider().Entity("account").Lookup("primarycontactid", "contact").String("name")...</c>
    /// Unknown entities return null (like a missing entity); <see cref="Failures"/> throw. Every request
    /// is recorded (thread-safe: the relationship picker reads metadata on worker threads).
    /// </summary>
    public sealed class FakeSchemaProvider : ISchemaProvider
    {
        private readonly Dictionary<string, EntitySchemaBuilder> _entities =
            new Dictionary<string, EntitySchemaBuilder>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <summary>Logical names passed to <see cref="GetEntity"/>, in order.</summary>
        public List<string> Requests { get; } = new List<string>();

        /// <summary>GetEntity of one of these entities throws the given exception (metadata that cannot be read).</summary>
        public Dictionary<string, Exception> Failures { get; } = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        public EntitySchema GetEntity(string logicalName)
        {
            lock (_sync)
            {
                Requests.Add(logicalName);
                if (logicalName != null && Failures.TryGetValue(logicalName, out Exception failure)) throw failure;
                return logicalName != null && _entities.TryGetValue(logicalName, out EntitySchemaBuilder builder) ? builder.Schema : null;
            }
        }

        /// <summary>The requests made so far (a copy, safe to enumerate while worker threads ask).</summary>
        public IReadOnlyList<string> RequestsSoFar()
        {
            lock (_sync)
            {
                return Requests.ToList();
            }
        }

        /// <summary>Starts (or replaces) an entity. The primary id attribute is added automatically, and
        /// the primary name attribute as a String when one is given.</summary>
        public EntitySchemaBuilder Entity(string logicalName, string primaryName = "name", string primaryId = null)
        {
            var builder = new EntitySchemaBuilder(this, logicalName, primaryId ?? logicalName + "id", primaryName);
            _entities[logicalName] = builder;
            return builder;
        }

        /// <summary>Continues building an entity already defined.</summary>
        public EntitySchemaBuilder Edit(string logicalName) => _entities[logicalName];

        public FakeSchemaProvider RemoveEntity(string logicalName)
        {
            _entities.Remove(logicalName);
            return this;
        }

        public FakeSchemaProvider RemoveAttribute(string logicalName, string attribute)
        {
            _entities[logicalName].Remove(attribute);
            return this;
        }

        /// <summary>A deep copy (e.g. a destination schema that then differs from the source).</summary>
        public FakeSchemaProvider Clone()
        {
            var copy = new FakeSchemaProvider();
            foreach (KeyValuePair<string, EntitySchemaBuilder> pair in _entities) copy._entities[pair.Key] = pair.Value.CloneFor(copy);
            return copy;
        }
    }

    /// <summary>Fluent builder for one <see cref="EntitySchema"/> of a <see cref="FakeSchemaProvider"/>.</summary>
    public sealed class EntitySchemaBuilder
    {
        private readonly FakeSchemaProvider _owner;
        private readonly Dictionary<string, AttributeSchema> _attributes = new Dictionary<string, AttributeSchema>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _defaultStatusByState = new Dictionary<int, int>();
        private readonly List<ChildRelationship> _relationships = new List<ChildRelationship>();

        internal EntitySchemaBuilder(FakeSchemaProvider owner, string logicalName, string primaryId, string primaryName)
        {
            _owner = owner;
            Schema = new EntitySchema
            {
                LogicalName = logicalName,
                PrimaryIdAttribute = primaryId,
                PrimaryNameAttribute = primaryName,
                DisplayName = logicalName,
                Attributes = _attributes,
                DefaultStatusByState = _defaultStatusByState,
                OneToManyRelationships = _relationships
            };
            Attribute(primaryId, AttributeTypeCode.Uniqueidentifier, create: true, update: false);
            if (primaryName != null) String(primaryName);
        }

        public EntitySchema Schema { get; }

        /// <summary>Adds (or replaces) an attribute; <paramref name="configure"/> can set the other flags.</summary>
        public EntitySchemaBuilder Attribute(string name, AttributeTypeCode type, bool create = true, bool update = true, Action<AttributeSchema> configure = null)
        {
            var attribute = new AttributeSchema
            {
                LogicalName = name,
                AttributeType = type,
                IsValidForCreate = create,
                IsValidForUpdate = update
            };
            configure?.Invoke(attribute);
            _attributes[name] = attribute;
            return this;
        }

        public EntitySchemaBuilder String(string name, bool create = true, bool update = true) => Attribute(name, AttributeTypeCode.String, create, update);
        public EntitySchemaBuilder Memo(string name, bool create = true, bool update = true) => Attribute(name, AttributeTypeCode.Memo, create, update);
        public EntitySchemaBuilder Int(string name) => Attribute(name, AttributeTypeCode.Integer);
        public EntitySchemaBuilder Decimal(string name) => Attribute(name, AttributeTypeCode.Decimal);
        public EntitySchemaBuilder Money(string name) => Attribute(name, AttributeTypeCode.Money);
        public EntitySchemaBuilder Bool(string name) => Attribute(name, AttributeTypeCode.Boolean);
        public EntitySchemaBuilder Picklist(string name) => Attribute(name, AttributeTypeCode.Picklist);
        public EntitySchemaBuilder Guid(string name) => Attribute(name, AttributeTypeCode.Uniqueidentifier);
        public EntitySchemaBuilder DateTime(string name, bool create = true, bool update = true) => Attribute(name, AttributeTypeCode.DateTime, create, update);
        public EntitySchemaBuilder EntityName(string name) => Attribute(name, AttributeTypeCode.EntityName);

        public EntitySchemaBuilder MultiSelect(string name) => Attribute(name, AttributeTypeCode.Virtual, configure: a => a.IsMultiSelect = true);
        public EntitySchemaBuilder Image(string name) => Attribute(name, AttributeTypeCode.Virtual, configure: a => a.IsImage = true);
        public EntitySchemaBuilder File(string name) => Attribute(name, AttributeTypeCode.Virtual, configure: a => a.IsFile = true);
        public EntitySchemaBuilder Virtual(string name) => Attribute(name, AttributeTypeCode.Virtual);
        public EntitySchemaBuilder Calculated(string name) => Attribute(name, AttributeTypeCode.String, configure: a => a.SourceType = 1);
        public EntitySchemaBuilder Rollup(string name) => Attribute(name, AttributeTypeCode.Decimal, configure: a => a.SourceType = 2);
        public EntitySchemaBuilder Derived(string name, string attributeOf, AttributeTypeCode type = AttributeTypeCode.String) =>
            Attribute(name, type, configure: a => a.AttributeOf = attributeOf);

        public EntitySchemaBuilder Lookup(string name, params string[] targets) =>
            Attribute(name, AttributeTypeCode.Lookup, configure: a => a.LookupTargets = targets);

        public EntitySchemaBuilder Customer(string name) =>
            Attribute(name, AttributeTypeCode.Customer, configure: a => a.LookupTargets = new[] { "account", "contact" });

        public EntitySchemaBuilder PartyList(string name, params string[] targets) =>
            Attribute(name, AttributeTypeCode.PartyList, configure: a => a.LookupTargets = targets);

        /// <summary>ownerid (Owner: systemuser/team) plus the read-only owning* lookups.</summary>
        public EntitySchemaBuilder Owner() =>
            Attribute("ownerid", AttributeTypeCode.Owner, configure: a => a.LookupTargets = new[] { "systemuser", "team" })
            .Attribute("owninguser", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("owningteam", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "team" })
            .Attribute("owningbusinessunit", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "businessunit" });

        /// <summary>statecode + statuscode with the default status of state 0 and state 1.</summary>
        public EntitySchemaBuilder State(int activeDefaultStatus = 1, int inactiveDefaultStatus = 2)
        {
            Attribute("statecode", AttributeTypeCode.State, create: false, update: true);
            Attribute("statuscode", AttributeTypeCode.Status, create: true, update: true);
            _defaultStatusByState[0] = activeDefaultStatus;
            _defaultStatusByState[1] = inactiveDefaultStatus;
            return this;
        }

        public EntitySchemaBuilder WithoutDefaultStatuses()
        {
            _defaultStatusByState.Clear();
            return this;
        }

        /// <summary>Sets the default status of a state (e.g. opportunity: 1 Won -> 3, 2 Lost -> 4).</summary>
        public EntitySchemaBuilder DefaultStatus(int state, int status)
        {
            _defaultStatusByState[state] = status;
            return this;
        }

        /// <summary>Marks the entity as a virtual table (rows in an external data source).</summary>
        public EntitySchemaBuilder VirtualTable(bool isVirtual = true)
        {
            Schema.IsVirtual = isVirtual;
            return this;
        }

        /// <summary>Sets the display name of an attribute already added (null by default, like a label-less attribute).</summary>
        public EntitySchemaBuilder Label(string attribute, string displayName)
        {
            _attributes[attribute].DisplayName = displayName;
            return this;
        }

        /// <summary>Sets the entity's display name (the logical name by default) and, optionally, its plural.</summary>
        public EntitySchemaBuilder EntityDisplayName(string displayName, string collectionName = null)
        {
            Schema.DisplayName = displayName;
            Schema.DisplayCollectionName = collectionName;
            return this;
        }

        /// <summary>
        /// Adds a 1:N relationship in which this entity is the parent: its child records are the
        /// <paramref name="childEntity"/> records whose <paramref name="childLookup"/> points at it.
        /// </summary>
        public EntitySchemaBuilder OneToMany(string schemaName, string childEntity, string childLookup, bool custom = false)
        {
            _relationships.Add(new ChildRelationship
            {
                SchemaName = schemaName,
                ParentEntity = Schema.LogicalName,
                ChildEntity = childEntity,
                ChildLookupAttribute = childLookup,
                IsCustomRelationship = custom
            });
            return this;
        }

        /// <summary>Marks the entity as private.</summary>
        public EntitySchemaBuilder Private(bool isPrivate = true)
        {
            Schema.IsPrivate = isPrivate;
            return this;
        }

        /// <summary>Marks the entity as an intersect entity.</summary>
        public EntitySchemaBuilder Intersect(bool isIntersect = true)
        {
            Schema.IsIntersect = isIntersect;
            return this;
        }

        /// <summary>The audit/system attributes every Dataverse entity carries.</summary>
        public EntitySchemaBuilder SystemAttributes() =>
            DateTime("createdon", create: false, update: false)
            .DateTime("modifiedon", create: false, update: false)
            .DateTime("overriddencreatedon", create: true, update: false)
            .Attribute("createdby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("modifiedby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("createdonbehalfby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("modifiedonbehalfby", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "systemuser" })
            .Attribute("versionnumber", AttributeTypeCode.BigInt, create: false, update: false)
            .Attribute("importsequencenumber", AttributeTypeCode.Integer, create: true, update: false)
            .Attribute("processid", AttributeTypeCode.Uniqueidentifier)
            .Attribute("stageid", AttributeTypeCode.Uniqueidentifier)
            .String("traversedpath");

        /// <summary>Continues with another entity of the same provider.</summary>
        public EntitySchemaBuilder Entity(string logicalName, string primaryName = "name", string primaryId = null) =>
            _owner.Entity(logicalName, primaryName, primaryId);

        public FakeSchemaProvider Done() => _owner;

        internal void Remove(string attribute) => _attributes.Remove(attribute);

        internal EntitySchemaBuilder CloneFor(FakeSchemaProvider owner)
        {
            var copy = new EntitySchemaBuilder(owner, Schema.LogicalName, Schema.PrimaryIdAttribute, Schema.PrimaryNameAttribute);
            copy._attributes.Clear();
            foreach (AttributeSchema a in _attributes.Values)
            {
                copy._attributes[a.LogicalName] = new AttributeSchema
                {
                    LogicalName = a.LogicalName,
                    DisplayName = a.DisplayName,
                    AttributeType = a.AttributeType,
                    IsValidForCreate = a.IsValidForCreate,
                    IsValidForUpdate = a.IsValidForUpdate,
                    AttributeOf = a.AttributeOf,
                    SourceType = a.SourceType,
                    IsFile = a.IsFile,
                    IsMultiSelect = a.IsMultiSelect,
                    IsImage = a.IsImage,
                    LookupTargets = a.LookupTargets?.ToArray()
                };
            }
            foreach (KeyValuePair<int, int> pair in _defaultStatusByState) copy._defaultStatusByState[pair.Key] = pair.Value;
            foreach (ChildRelationship r in _relationships)
            {
                copy._relationships.Add(new ChildRelationship
                {
                    SchemaName = r.SchemaName,
                    ParentEntity = r.ParentEntity,
                    ChildEntity = r.ChildEntity,
                    ChildLookupAttribute = r.ChildLookupAttribute,
                    IsCustomRelationship = r.IsCustomRelationship
                });
            }
            copy.Schema.DisplayName = Schema.DisplayName;
            copy.Schema.DisplayCollectionName = Schema.DisplayCollectionName;
            copy.Schema.IsIntersect = Schema.IsIntersect;
            copy.Schema.IsPrivate = Schema.IsPrivate;
            copy.Schema.IsVirtual = Schema.IsVirtual;
            return copy;
        }
    }
}
