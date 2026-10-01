using System;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>FetchXML manipulation for paging the record grid.</summary>
    public static class FetchXmlHelper
    {
        /// <summary>
        /// Returns a new fetch string with <c>count</c>, <c>page</c> and <c>paging-cookie</c> set on the
        /// root &lt;fetch&gt; (the cookie is XML-escaped; a null/empty cookie removes the attribute) and
        /// any <c>top</c> attribute removed (top cannot be combined with paging).
        /// </summary>
        public static string ApplyPaging(string fetchXml, int page, int count, string pagingCookie)
        {
            if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), page, "The page number starts at 1.");
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "The page size must be at least 1.");

            XElement fetch = ParseFetch(fetchXml);
            fetch.SetAttributeValue("top", null);
            fetch.SetAttributeValue("count", count.ToString(CultureInfo.InvariantCulture));
            fetch.SetAttributeValue("page", page.ToString(CultureInfo.InvariantCulture));
            fetch.SetAttributeValue("paging-cookie", string.IsNullOrEmpty(pagingCookie) ? null : pagingCookie);
            return fetch.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// Makes sure the main &lt;entity&gt; returns <paramref name="attributeName"/> (e.g. the primary
        /// id, so every grid row has its id): adds &lt;attribute name=".."/&gt; unless the entity uses
        /// &lt;all-attributes/&gt; or already lists it. Returns the input unchanged when nothing is added.
        /// </summary>
        public static string EnsureAttribute(string fetchXml, string attributeName)
        {
            if (string.IsNullOrWhiteSpace(attributeName)) return fetchXml;

            XElement fetch = ParseFetch(fetchXml);
            XElement entity = fetch.Elements().FirstOrDefault(e => e.Name.LocalName == "entity");
            if (entity == null) return fetchXml;

            if (entity.Elements().Any(e => e.Name.LocalName == "all-attributes")) return fetchXml;
            var attributes = entity.Elements().Where(e => e.Name.LocalName == "attribute").ToList();
            if (attributes.Any(a => string.Equals((string)a.Attribute("name"), attributeName, StringComparison.OrdinalIgnoreCase))) return fetchXml;

            var added = new XElement(entity.Name.Namespace + "attribute", new XAttribute("name", attributeName));
            if (attributes.Count > 0) attributes[attributes.Count - 1].AddAfterSelf(added);
            else entity.AddFirst(added);
            return fetch.ToString(SaveOptions.DisableFormatting);
        }

        private static XElement ParseFetch(string fetchXml)
        {
            if (string.IsNullOrWhiteSpace(fetchXml)) throw new ArgumentException("FetchXML is required.", nameof(fetchXml));
            XDocument document;
            try
            {
                document = XDocument.Parse(fetchXml);
            }
            catch (XmlException ex)
            {
                throw new ArgumentException("The FetchXML is not valid XML: " + ex.Message, nameof(fetchXml), ex);
            }
            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "fetch")
                throw new ArgumentException("The FetchXML root element must be <fetch>.", nameof(fetchXml));
            return root;
        }
    }
}
