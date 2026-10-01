using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>
    /// The 1:N relationships an entity shows as subgrids on its active main forms (SPEC 5.10): the
    /// default choice of child records to copy with a record. One instance caches the answer per
    /// entity for its lifetime (the UI makes one per run and one per picker session). Thread-safe.
    /// </summary>
    public sealed class FormSubgridService
    {
        /// <summary>The class id of the classic subgrid control in form XML.</summary>
        public static readonly Guid SubgridControlClassId = new Guid("E7A81278-8635-4D9E-8D4D-59480B391C5B");

        /// <summary>systemform.type of a main form.</summary>
        public const int MainFormType = 2;

        /// <summary>systemform.formactivationstate of an active form.</summary>
        public const int ActiveForm = 1;

        private readonly Dictionary<string, IReadOnlyList<string>> _cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        /// <summary>
        /// The distinct relationship schema names of the subgrids on the active main forms of
        /// <paramref name="entity"/> in <paramref name="source"/> (systemform: objecttypecode = entity,
        /// type = 2, formactivationstate = 1), in form order. A form whose XML cannot be read is
        /// skipped; a failing query propagates (and is not cached).
        /// </summary>
        public IReadOnlyList<string> GetMainFormSubgridRelationships(IOrganizationService source, string entity)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(entity)) return Array.Empty<string>();
            string name = entity.Trim().ToLowerInvariant();
            lock (_sync)
            {
                if (_cache.TryGetValue(name, out IReadOnlyList<string> cached)) return cached;
            }

            var query = new QueryExpression("systemform") { ColumnSet = new ColumnSet("formxml") };
            query.Criteria.AddCondition("objecttypecode", ConditionOperator.Equal, name);
            query.Criteria.AddCondition("type", ConditionOperator.Equal, MainFormType);
            query.Criteria.AddCondition("formactivationstate", ConditionOperator.Equal, ActiveForm);
            EntityCollection forms = source.RetrieveMultiple(query);

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Entity form in forms?.Entities ?? Enumerable.Empty<Entity>())
            {
                string formXml = form.Attributes.TryGetValue("formxml", out object value) ? value as string : null;
                foreach (string relationship in ParseSubgridRelationships(formXml))
                {
                    if (seen.Add(relationship)) names.Add(relationship);
                }
            }

            IReadOnlyList<string> result = names.AsReadOnly();
            lock (_sync)
            {
                _cache[name] = result;
            }
            return result;
        }

        /// <summary>
        /// The relationship names of the subgrids in one form XML, in document order, without
        /// duplicates: every &lt;control&gt; with the subgrid class id, and any other &lt;control&gt;
        /// (a modern or custom subgrid) whose &lt;parameters&gt; hold a &lt;RelationshipName&gt;. Empty
        /// names (a subgrid of unrelated records) are ignored; missing or invalid XML yields nothing.
        /// </summary>
        public static IReadOnlyList<string> ParseSubgridRelationships(string formXml)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(formXml)) return names;

            XDocument document;
            try
            {
                document = XDocument.Parse(formXml);
            }
            catch (XmlException)
            {
                return names;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement control in document.Descendants().Where(e => Is(e, "control")))
            {
                IEnumerable<XElement> parameters = control.Elements().Where(e => Is(e, "parameters"));
                IEnumerable<XElement> relationshipElements = IsSubgridClass(control)
                    ? control.Descendants().Where(e => Is(e, "RelationshipName"))
                    : parameters.SelectMany(p => p.Elements()).Where(e => Is(e, "RelationshipName"));
                foreach (XElement element in relationshipElements)
                {
                    string relationship = element.Value?.Trim();
                    if (!string.IsNullOrEmpty(relationship) && seen.Add(relationship)) names.Add(relationship);
                }
            }
            return names;
        }

        private static bool Is(XElement element, string localName) =>
            string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

        private static bool IsSubgridClass(XElement control)
        {
            string classId = control.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, "classid", StringComparison.OrdinalIgnoreCase))?.Value;
            return Guid.TryParse(classId?.Trim(), out Guid id) && id == SubgridControlClassId;
        }
    }
}
