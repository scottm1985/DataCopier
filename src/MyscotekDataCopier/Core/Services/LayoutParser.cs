using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>A grid column from a view's layoutxml.</summary>
    public sealed class ViewColumn
    {
        /// <summary>Attribute logical name, or alias.attribute for a linked-entity column.</summary>
        public string Name { get; set; }
        public int Width { get; set; }

        public override string ToString() => $"{Name} ({Width})";
    }

    /// <summary>Reads the columns of a view from its layoutxml.</summary>
    public static class LayoutParser
    {
        /// <summary>Width used when a cell has no (valid) width.</summary>
        public const int DefaultWidth = 100;

        /// <summary>
        /// The &lt;cell name=".." width=".."/&gt; elements in document order. Cells without a name are
        /// ignored, as are repeated names (a grid cannot hold two columns with the same name).
        /// Missing or invalid XML yields an empty list.
        /// </summary>
        public static IList<ViewColumn> Parse(string layoutXml)
        {
            var columns = new List<ViewColumn>();
            if (string.IsNullOrWhiteSpace(layoutXml)) return columns;

            XDocument document;
            try
            {
                document = XDocument.Parse(layoutXml);
            }
            catch (XmlException)
            {
                return columns;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement cell in document.Descendants().Where(e => e.Name.LocalName == "cell"))
            {
                string name = ((string)cell.Attribute("name"))?.Trim();
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                columns.Add(new ViewColumn { Name = name, Width = ParseWidth((string)cell.Attribute("width")) });
            }
            return columns;
        }

        /// <summary>Leading digits of the width ("150" or "150px"); <see cref="DefaultWidth"/> otherwise.</summary>
        private static int ParseWidth(string width)
        {
            if (string.IsNullOrWhiteSpace(width)) return DefaultWidth;
            string digits = new string(width.Trim().TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
                ? value
                : DefaultWidth;
        }
    }
}
