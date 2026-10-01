using System;
using System.Globalization;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>Turns a record's attribute value into the text shown in a grid cell.</summary>
    public static class CellFormatter
    {
        /// <summary>Shown for image (byte[]) values.</summary>
        public const string ImagePlaceholder = "(image)";

        /// <summary>
        /// FormattedValues[column] when present; otherwise the value formatted by type (see
        /// <see cref="FormatValue"/>). A missing column gives "".
        /// </summary>
        public static string Format(Entity e, string column)
        {
            if (e == null || string.IsNullOrEmpty(column)) return string.Empty;
            if (e.FormattedValues != null && e.FormattedValues.TryGetValue(column, out string formatted) && formatted != null) return formatted;
            return e.Attributes.TryGetValue(column, out object value) ? FormatValue(value) : string.Empty;
        }

        /// <summary>
        /// EntityReference: Name (fallback id); AliasedValue: unwrapped then formatted; Money: "N2";
        /// OptionSetValue: its value; OptionSetValueCollection: values joined; DateTime: local time "g";
        /// bool; byte[]: "(image)"; activity parties: party names joined; null: "".
        /// </summary>
        public static string FormatValue(object value)
        {
            switch (value)
            {
                case null:
                    return string.Empty;
                case AliasedValue aliased:
                    return FormatValue(aliased.Value);
                case EntityReference reference:
                    return !string.IsNullOrEmpty(reference.Name) ? reference.Name : reference.Id.ToString();
                case Money money:
                    return money.Value.ToString("N2", CultureInfo.CurrentCulture);
                case OptionSetValue option:
                    return option.Value.ToString(CultureInfo.CurrentCulture);
                case OptionSetValueCollection options:
                    return string.Join(", ", options.Where(o => o != null).Select(o => o.Value.ToString(CultureInfo.CurrentCulture)));
                case DateTime date:
                    return date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                case bool flag:
                    return flag.ToString(CultureInfo.CurrentCulture);
                case byte[] _:
                    return ImagePlaceholder;
                case EntityCollection parties:
                    return string.Join("; ", parties.Entities
                        .Select(p => p.Attributes.TryGetValue("partyid", out object party) && party != null
                            ? FormatValue(party)
                            : p.GetAttributeValue<string>("addressused"))
                        .Where(s => !string.IsNullOrEmpty(s)));
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.CurrentCulture);
                default:
                    return value.ToString();
            }
        }
    }
}
