using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>
    /// The grid column headers of a view, as a Dynamics view shows them: the attribute's display name,
    /// and for a column of a linked entity (<c>alias.attribute</c>, or an attribute aliased in the
    /// fetch) that entity's attribute display name followed by the lookup it is reached through, e.g.
    /// "Email (Primary Contact)". A column that cannot be resolved keeps its raw name. Reads metadata
    /// through the schema provider (one GetEntity per entity involved), so call it off the UI thread.
    /// </summary>
    public static class ColumnHeaderResolver
    {
        /// <summary>
        /// One header per column, in order. <paramref name="entityLogicalName"/> is the view's entity
        /// (the fetch's main &lt;entity&gt; when null). Never throws for a metadata or FetchXML problem:
        /// the affected headers keep their raw column names.
        /// </summary>
        public static IList<string> Resolve(string fetchXml, string entityLogicalName, IList<ViewColumn> columns, ISchemaProvider schema)
        {
            var headers = new List<string>();
            if (columns == null) return headers;

            XElement main = MainEntity(fetchXml);
            string mainEntity = !string.IsNullOrWhiteSpace(entityLogicalName) ? entityLogicalName.Trim() : (string)main?.Attribute("name");
            var schemas = new Dictionary<string, EntitySchema>(StringComparer.OrdinalIgnoreCase);

            EntitySchema SchemaOf(string entity)
            {
                if (schema == null || string.IsNullOrWhiteSpace(entity)) return null;
                if (schemas.TryGetValue(entity, out EntitySchema known)) return known;
                EntitySchema found;
                try
                {
                    found = schema.GetEntity(entity);
                }
                catch (Exception)
                {
                    found = null;   // metadata unavailable: raw column names
                }
                schemas[entity] = found;
                return found;
            }

            foreach (ViewColumn column in columns)
            {
                string name = column?.Name ?? string.Empty;
                headers.Add(Header(name, main, mainEntity, SchemaOf) ?? name);
            }
            return headers;
        }

        private static string Header(string column, XElement main, string mainEntity, Func<string, EntitySchema> schemaOf)
        {
            if (column.Length == 0) return null;

            // Where the column comes from: the entity or link-entity element that owns it, and the attribute.
            XElement owner;
            string attribute;
            XElement aliased = main?.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "attribute" && string.Equals((string)e.Attribute("alias"), column, StringComparison.OrdinalIgnoreCase));
            int dot = column.IndexOf('.');
            if (aliased != null)
            {
                owner = aliased.Parent;                       // <attribute name="x" alias="column"/>
                attribute = (string)aliased.Attribute("name");
            }
            else if (dot > 0 && dot < column.Length - 1)
            {
                string alias = column.Substring(0, dot);      // alias.attribute: a linked entity's column
                owner = main?.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "link-entity" && string.Equals((string)e.Attribute("alias"), alias, StringComparison.OrdinalIgnoreCase));
                if (owner == null) return null;
                attribute = column.Substring(dot + 1);
            }
            else
            {
                owner = main;
                attribute = column;
            }

            bool linked = owner != null && owner.Name.LocalName == "link-entity";
            string label = AttributeLabel(schemaOf(linked ? (string)owner.Attribute("name") : mainEntity), attribute);
            if (label == null || !linked) return label;

            string via = LinkLabel(owner, mainEntity, schemaOf);
            return via == null ? label : label + " (" + via + ")";
        }

        /// <summary>
        /// What a linked entity is reached through: the display name of the parent's "to" attribute when
        /// that is a lookup (an N:1 link, e.g. primarycontactid: "Primary Contact"), else the linked
        /// entity's display name (e.g. a 1:N link to contacts: "Contact").
        /// </summary>
        private static string LinkLabel(XElement link, string mainEntity, Func<string, EntitySchema> schemaOf)
        {
            XElement parent = link.Parent;
            string parentEntity = parent != null && parent.Name.LocalName == "link-entity" ? (string)parent.Attribute("name") : mainEntity;
            EntitySchema parentSchema = schemaOf(parentEntity);
            string to = (string)link.Attribute("to");
            if (!string.IsNullOrEmpty(to) && parentSchema?.Attributes != null
                && parentSchema.Attributes.TryGetValue(to, out AttributeSchema lookup) && lookup != null && IsLookup(lookup.AttributeType)
                && !string.IsNullOrWhiteSpace(lookup.DisplayName))
            {
                return lookup.DisplayName;
            }

            string linkedDisplayName = schemaOf((string)link.Attribute("name"))?.DisplayName;
            return string.IsNullOrWhiteSpace(linkedDisplayName) ? null : linkedDisplayName;
        }

        private static bool IsLookup(AttributeTypeCode type) =>
            type == AttributeTypeCode.Lookup || type == AttributeTypeCode.Customer || type == AttributeTypeCode.Owner;

        private static string AttributeLabel(EntitySchema entity, string attribute)
        {
            if (entity?.Attributes == null || string.IsNullOrEmpty(attribute)) return null;
            return entity.Attributes.TryGetValue(attribute, out AttributeSchema meta) && meta != null && !string.IsNullOrWhiteSpace(meta.DisplayName)
                ? meta.DisplayName
                : null;
        }

        /// <summary>The fetch's main &lt;entity&gt; element, or null when the FetchXML is missing or invalid.</summary>
        private static XElement MainEntity(string fetchXml)
        {
            if (string.IsNullOrWhiteSpace(fetchXml)) return null;
            try
            {
                XElement root = XDocument.Parse(fetchXml).Root;
                return root != null && root.Name.LocalName == "fetch"
                    ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "entity")
                    : null;
            }
            catch (XmlException)
            {
                return null;
            }
        }
    }
}
