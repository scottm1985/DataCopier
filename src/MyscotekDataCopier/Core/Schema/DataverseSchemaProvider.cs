using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace MyscotekDataCopier.Core.Schema
{
    /// <summary>
    /// <see cref="ISchemaProvider"/> over a live organisation: one RetrieveEntityRequest
    /// (Entity | Attributes | Relationships, published metadata) per entity, cached for the lifetime
    /// of the provider. Thread-safe. An entity that does not exist is cached as null too, so it is
    /// asked for only once; any other failure (network, privileges) propagates and is not cached.
    /// </summary>
    public sealed class DataverseSchemaProvider : ISchemaProvider
    {
        /// <summary>
        /// The data provider of elastic tables: they carry a DataProviderId too, but their rows are
        /// stored in Dataverse, so they are not virtual.
        /// </summary>
        public static readonly Guid ElasticTableDataProviderId = new Guid("1d9bde74-9ebd-4da9-8ff5-aa74945b9f74");

        private readonly IOrganizationService _service;
        private readonly Dictionary<string, EntitySchema> _cache = new Dictionary<string, EntitySchema>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        public DataverseSchemaProvider(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public EntitySchema GetEntity(string logicalName)
        {
            if (string.IsNullOrWhiteSpace(logicalName)) return null;
            string key = logicalName.Trim();

            lock (_sync)
            {
                if (_cache.TryGetValue(key, out EntitySchema cached)) return cached;
            }

            // Retrieved outside the lock so a slow metadata call does not block readers of other
            // entities; if two threads race, the first result stored wins.
            EntitySchema schema;
            try
            {
                OrganizationResponse response = _service.Execute(new RetrieveEntityRequest
                {
                    LogicalName = key,
                    // Relationships: the 1:N relationships a record's child records are found through and
                    // the N:N relationships its associated records are found through (SPEC 5.10).
                    EntityFilters = EntityFilters.Entity | EntityFilters.Attributes | EntityFilters.Relationships,
                    RetrieveAsIfPublished = false
                });
                EntityMetadata metadata = response is RetrieveEntityResponse typed
                    ? typed.EntityMetadata
                    : response?.Results != null && response.Results.Contains("EntityMetadata")
                        ? response.Results["EntityMetadata"] as EntityMetadata
                        : null;
                schema = metadata == null ? null : FromMetadata(metadata);
            }
            catch (Exception ex) when (IsEntityMissing(ex, key))
            {
                schema = null;
            }

            lock (_sync)
            {
                if (_cache.TryGetValue(key, out EntitySchema raced)) return raced;
                _cache[key] = schema;
            }
            return schema;
        }

        /// <summary>Converts SDK metadata into an <see cref="EntitySchema"/>.</summary>
        public static EntitySchema FromMetadata(EntityMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            var attributes = new Dictionary<string, AttributeSchema>(StringComparer.OrdinalIgnoreCase);
            var defaultStatusByState = new Dictionary<int, int>();

            foreach (AttributeMetadata attribute in metadata.Attributes ?? Array.Empty<AttributeMetadata>())
            {
                if (attribute == null || string.IsNullOrEmpty(attribute.LogicalName)) continue;

                attributes[attribute.LogicalName] = new AttributeSchema
                {
                    LogicalName = attribute.LogicalName,
                    DisplayName = LabelText(attribute.DisplayName) ?? attribute.LogicalName,
                    // A missing type is treated as Virtual, i.e. "not copyable" unless flagged below.
                    AttributeType = attribute.AttributeType ?? AttributeTypeCode.Virtual,
                    IsValidForCreate = attribute.IsValidForCreate == true,
                    IsValidForUpdate = attribute.IsValidForUpdate == true,
                    AttributeOf = string.IsNullOrEmpty(attribute.AttributeOf) ? null : attribute.AttributeOf,
                    SourceType = attribute.SourceType ?? 0,
                    IsFile = attribute is FileAttributeMetadata,
                    IsMultiSelect = attribute is MultiSelectPicklistAttributeMetadata,
                    IsImage = attribute is ImageAttributeMetadata,
                    LookupTargets = (attribute as LookupAttributeMetadata)?.Targets?.Where(t => !string.IsNullOrEmpty(t)).ToArray()
                                    ?? Array.Empty<string>()
                };

                if (attribute is StateAttributeMetadata state && state.OptionSet?.Options != null)
                {
                    foreach (OptionMetadata option in state.OptionSet.Options)
                    {
                        if (option is StateOptionMetadata stateOption && stateOption.Value.HasValue && stateOption.DefaultStatus.HasValue)
                        {
                            defaultStatusByState[stateOption.Value.Value] = stateOption.DefaultStatus.Value;
                        }
                    }
                }
            }

            return new EntitySchema
            {
                LogicalName = metadata.LogicalName,
                PrimaryIdAttribute = metadata.PrimaryIdAttribute,
                PrimaryNameAttribute = metadata.PrimaryNameAttribute,
                DisplayName = LabelText(metadata.DisplayName) ?? metadata.SchemaName ?? metadata.LogicalName,
                DisplayCollectionName = LabelText(metadata.DisplayCollectionName),
                IsIntersect = metadata.IsIntersect == true,
                IsPrivate = metadata.IsPrivate == true,
                IsVirtual = IsVirtualTable(metadata),
                Attributes = attributes,
                DefaultStatusByState = defaultStatusByState,
                OneToManyRelationships = ChildRelationships(metadata),
                ManyToManyRelationships = ManyToManyRelationships(metadata)
            };
        }

        /// <summary>
        /// The entity's 1:N relationships (it is the referenced entity) as <see cref="ChildRelationship"/>s,
        /// in metadata order; entries without a schema name, child entity or child lookup are left out.
        /// </summary>
        private static IReadOnlyList<ChildRelationship> ChildRelationships(EntityMetadata metadata)
        {
            OneToManyRelationshipMetadata[] relationships = metadata.OneToManyRelationships;
            if (relationships == null || relationships.Length == 0) return Array.Empty<ChildRelationship>();

            return relationships
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.SchemaName)
                            && !string.IsNullOrWhiteSpace(r.ReferencingEntity) && !string.IsNullOrWhiteSpace(r.ReferencingAttribute))
                .Select(r => new ChildRelationship
                {
                    SchemaName = r.SchemaName,
                    ParentEntity = string.IsNullOrWhiteSpace(r.ReferencedEntity) ? metadata.LogicalName : r.ReferencedEntity,
                    ChildEntity = r.ReferencingEntity,
                    ChildLookupAttribute = r.ReferencingAttribute,
                    IsCustomRelationship = r.IsCustomRelationship == true
                })
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// The N:N relationships the entity takes part in, in metadata order; entries without a schema
        /// name, intersect entity, or entity and intersect attribute on either side are left out.
        /// </summary>
        private static IReadOnlyList<ManyToManyRelationship> ManyToManyRelationships(EntityMetadata metadata)
        {
            ManyToManyRelationshipMetadata[] relationships = metadata.ManyToManyRelationships;
            if (relationships == null || relationships.Length == 0) return Array.Empty<ManyToManyRelationship>();

            return relationships
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.SchemaName) && !string.IsNullOrWhiteSpace(r.IntersectEntityName)
                            && !string.IsNullOrWhiteSpace(r.Entity1LogicalName) && !string.IsNullOrWhiteSpace(r.Entity1IntersectAttribute)
                            && !string.IsNullOrWhiteSpace(r.Entity2LogicalName) && !string.IsNullOrWhiteSpace(r.Entity2IntersectAttribute))
                .Select(r => new ManyToManyRelationship
                {
                    SchemaName = r.SchemaName,
                    IntersectEntity = r.IntersectEntityName,
                    Entity1LogicalName = r.Entity1LogicalName,
                    Entity1IntersectAttribute = r.Entity1IntersectAttribute,
                    Entity2LogicalName = r.Entity2LogicalName,
                    Entity2IntersectAttribute = r.Entity2IntersectAttribute,
                    IsCustomRelationship = r.IsCustomRelationship == true
                })
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// True for a virtual table (its rows live in an external data source). The table type
        /// decides when the server reports one ("Virtual"; "Standard", "Elastic" and anything else are
        /// not virtual); older servers do not, and then a table is virtual when it has a data provider
        /// other than the elastic-table one.
        /// </summary>
        public static bool IsVirtualTable(EntityMetadata metadata)
        {
            if (metadata == null) return false;
            string tableType = metadata.TableType;
            if (!string.IsNullOrWhiteSpace(tableType))
                return string.Equals(tableType.Trim(), "Virtual", StringComparison.OrdinalIgnoreCase);

            Guid? provider = metadata.DataProviderId;
            return provider.HasValue && provider.Value != Guid.Empty && provider.Value != ElasticTableDataProviderId;
        }

        /// <summary>
        /// True when a metadata request failed because the entity does not exist: a service fault
        /// whose message says so. Dataverse and on-premises 9.x word it differently, e.g. "Could not
        /// find an entity with specified entity name: x", "Entity 'x' does not exist", "The entity with a
        /// name = 'x' ... was not found in the MetadataCache" or "Entity 'x' not found". The generic
        /// "not found" must also name the entity (when <paramref name="logicalName"/> is given): a
        /// missing entity is cached for the provider's lifetime, so an unrelated fault must not look like one.
        /// </summary>
        internal static bool IsEntityMissing(Exception ex, string logicalName = null)
        {
            if (!(ex is FaultException)) return false;
            string message = (ex as FaultException<OrganizationServiceFault>)?.Detail?.Message;
            if (string.IsNullOrEmpty(message)) message = ex.Message ?? string.Empty;

            return Contains(message, "Could not find")
                || Contains(message, "does not exist")
                || (Contains(message, "not found") && (string.IsNullOrEmpty(logicalName) || Contains(message, logicalName)));
        }

        private static bool Contains(string text, string fragment) =>
            text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The user-localised text of a label, else its first localised text, else null.</summary>
        internal static string LabelText(Label label)
        {
            string text = label?.UserLocalizedLabel?.Label;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = label?.LocalizedLabels?.Select(l => l?.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            }
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
