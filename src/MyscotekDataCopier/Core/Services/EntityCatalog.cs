using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>An entity shown in the entity list.</summary>
    public sealed class EntityInfo
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }          // falls back to SchemaName
        public string SchemaName { get; set; }
        public int ObjectTypeCode { get; set; }
        public string PrimaryIdAttribute { get; set; }
        public string PrimaryNameAttribute { get; set; }
        public bool IsActivity { get; set; }

        /// <summary>A virtual table (rows in an external data source): listed, but its records cannot be copied.</summary>
        public bool IsVirtual { get; set; }

        public override string ToString() => $"{DisplayName} ({LogicalName})";
    }

    /// <summary>Lists the entities of an organisation for the entity picker.</summary>
    public static class EntityCatalog
    {
        /// <summary>
        /// RetrieveAllEntitiesRequest (Entity filter only); excludes intersect and private entities;
        /// sorted by display name.
        /// </summary>
        public static IList<EntityInfo> GetEntities(IOrganizationService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            OrganizationResponse response = service.Execute(new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            });
            EntityMetadata[] metadata = response is RetrieveAllEntitiesResponse typed
                ? typed.EntityMetadata
                : response?.Results != null && response.Results.Contains("EntityMetadata")
                    ? response.Results["EntityMetadata"] as EntityMetadata[]
                    : null;
            return FromMetadata(metadata);
        }

        /// <summary>The filtering, mapping and sorting behind <see cref="GetEntities"/>.</summary>
        public static IList<EntityInfo> FromMetadata(IEnumerable<EntityMetadata> metadata)
        {
            if (metadata == null) return new List<EntityInfo>();

            return metadata
                .Where(m => m != null && !string.IsNullOrEmpty(m.LogicalName) && m.IsIntersect != true && m.IsPrivate != true)
                .Select(m => new EntityInfo
                {
                    LogicalName = m.LogicalName,
                    SchemaName = m.SchemaName,
                    DisplayName = DataverseSchemaProvider.LabelText(m.DisplayName) ?? m.SchemaName ?? m.LogicalName,
                    ObjectTypeCode = m.ObjectTypeCode ?? 0,
                    PrimaryIdAttribute = m.PrimaryIdAttribute,
                    PrimaryNameAttribute = m.PrimaryNameAttribute,
                    IsActivity = m.IsActivity == true,
                    IsVirtual = DataverseSchemaProvider.IsVirtualTable(m)
                })
                .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
