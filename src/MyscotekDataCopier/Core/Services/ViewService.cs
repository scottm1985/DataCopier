using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>A system view (savedquery) or personal view (userquery).</summary>
    public sealed class ViewInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public bool IsPersonal { get; set; }
        public string FetchXml { get; set; }
        public string LayoutXml { get; set; }

        /// <summary>The name as shown in the view picker: personal views are suffixed " (personal)".</summary>
        public string DisplayName => IsPersonal ? Name + " (personal)" : Name;

        public override string ToString() => DisplayName;
    }

    /// <summary>Reads the views of an entity for the view picker.</summary>
    public static class ViewService
    {
        /// <summary>Name of the view the UI synthesises when an entity has no usable views.</summary>
        public const string AllRecordsViewName = "(All records)";

        /// <summary>
        /// System views (savedquery: returnedtypecode = entity, querytype = 0, statecode = 0, fetchxml
        /// not null, by name), then - when <paramref name="includePersonal"/> - the personal views the
        /// current user can read (userquery, same filters). A failing query is swallowed so it can never
        /// hide the other list; use the overload with a logger to see why.
        /// </summary>
        public static IList<ViewInfo> GetViews(IOrganizationService service, string entity, bool includePersonal) =>
            GetViews(service, entity, includePersonal, null);

        /// <summary>As <see cref="GetViews(IOrganizationService, string, bool)"/>, logging a Warning for each query that fails.</summary>
        public static IList<ViewInfo> GetViews(IOrganizationService service, string entity, bool includePersonal, ICopyLogger logger)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentException("An entity logical name is required.", nameof(entity));

            var views = new List<ViewInfo>();
            try
            {
                views.AddRange(Query(service, "savedquery", "savedqueryid", entity, isPersonal: false));
            }
            catch (Exception ex)
            {
                logger?.Log(LogLevel.Warning, $"Could not read the system views of {entity}: {ex.Message}");
            }

            if (includePersonal)
            {
                try
                {
                    views.AddRange(Query(service, "userquery", "userqueryid", entity, isPersonal: true));
                }
                catch (Exception ex)
                {
                    logger?.Log(LogLevel.Warning, $"Could not read the personal views of {entity}: {ex.Message}");
                }
            }
            return views;
        }

        /// <summary>
        /// The "(All records)" view for an entity without usable views:
        /// fetch = primary name + primary id + createdon ordered by primary name; layout = primary name
        /// and createdon columns.
        /// </summary>
        public static ViewInfo CreateAllRecordsView(string entity, string primaryIdAttribute, string primaryNameAttribute)
        {
            if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentException("An entity logical name is required.", nameof(entity));
            string primaryId = string.IsNullOrWhiteSpace(primaryIdAttribute) ? entity + "id" : primaryIdAttribute;
            bool hasName = !string.IsNullOrWhiteSpace(primaryNameAttribute);

            var entityElement = new XElement("entity", new XAttribute("name", entity));
            if (hasName) entityElement.Add(Attribute(primaryNameAttribute));
            entityElement.Add(Attribute(primaryId), Attribute("createdon"));
            entityElement.Add(hasName
                ? new XElement("order", new XAttribute("attribute", primaryNameAttribute))
                : new XElement("order", new XAttribute("attribute", "createdon"), new XAttribute("descending", "true")));

            var row = new XElement("row", new XAttribute("name", "result"), new XAttribute("id", primaryId));
            if (hasName) row.Add(Cell(primaryNameAttribute, 300));
            row.Add(Cell("createdon", 125));
            var grid = new XElement("grid",
                new XAttribute("name", "resultset"),
                new XAttribute("jump", hasName ? primaryNameAttribute : primaryId),
                new XAttribute("select", "1"),
                new XAttribute("icon", "1"),
                new XAttribute("preview", "1"),
                row);

            return new ViewInfo
            {
                Id = Guid.Empty,
                Name = AllRecordsViewName,
                IsPersonal = false,
                FetchXml = new XElement("fetch", entityElement).ToString(SaveOptions.DisableFormatting),
                LayoutXml = grid.ToString(SaveOptions.DisableFormatting)
            };
        }

        private static IEnumerable<ViewInfo> Query(IOrganizationService service, string viewEntity, string idAttribute, string entity, bool isPersonal)
        {
            var query = new QueryExpression(viewEntity)
            {
                ColumnSet = new ColumnSet(idAttribute, "name", "fetchxml", "layoutxml")
            };
            query.Criteria.AddCondition("returnedtypecode", ConditionOperator.Equal, entity);
            query.Criteria.AddCondition("querytype", ConditionOperator.Equal, 0);
            query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            query.Criteria.AddCondition("fetchxml", ConditionOperator.NotNull);
            query.AddOrder("name", OrderType.Ascending);

            EntityCollection result = service.RetrieveMultiple(query);
            return (result?.Entities ?? Enumerable.Empty<Entity>())
                .Select(e => new ViewInfo
                {
                    Id = e.Id,
                    Name = e.GetAttributeValue<string>("name"),
                    IsPersonal = isPersonal,
                    FetchXml = e.GetAttributeValue<string>("fetchxml"),
                    LayoutXml = e.GetAttributeValue<string>("layoutxml")
                })
                .Where(v => !string.IsNullOrWhiteSpace(v.FetchXml))
                .ToList();
        }

        private static XElement Attribute(string name) => new XElement("attribute", new XAttribute("name", name));

        private static XElement Cell(string name, int width) =>
            new XElement("cell", new XAttribute("name", name), new XAttribute("width", width));
    }
}
