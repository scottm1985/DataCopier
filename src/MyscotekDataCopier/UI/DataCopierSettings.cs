using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using MyscotekDataCopier.Core;
using XrmToolBox.Extensibility;

namespace MyscotekDataCopier.UI
{
    /// <summary>
    /// The tool's persisted settings (SPEC section 6). XrmToolBox's SettingsManager stores them as XML
    /// in %AppData%\MscrmTools\XrmToolBox\Settings\MyscotekDataCopier.xml, so the class is public with
    /// public read/write properties (XmlSerializer). Saved when the tool closes, whenever an option
    /// checkbox changes and when the relationship picker is confirmed; <see cref="NeverCreateEntities"/>
    /// and <see cref="PageSize"/> are only edited in that file (close XrmToolBox first: the tool
    /// rewrites the file when it closes). Elements that no longer exist are ignored when the file is
    /// read, such as IncludePersonalViews before 1.2026.10.2 (personal views are now always listed).
    /// </summary>
    public class DataCopierSettings
    {
        public const int DefaultPageSize = 500;

        /// <summary>The largest page Dataverse returns for a FetchXML query.</summary>
        public const int MaxPageSize = 5000;

        public bool DryRun { get; set; }
        public bool PreserveCreatedOn { get; set; }
        public bool BypassCustomPlugins { get; set; }

        /// <summary>"Create related records for N:1 relationships (lookups)" (SPEC 5.10): the lookup targets are created first. Default on.</summary>
        public bool CopyLookups { get; set; } = true;

        /// <summary>"Create related records for 1:N relationships (subgrids)" (SPEC 5.10): the child records are copied too. Default off.</summary>
        public bool CopyChildren { get; set; }

        /// <summary>Records per page for Load records / Load more / Load all.</summary>
        public int PageSize { get; set; } = DefaultPageSize;

        /// <summary>
        /// Comma-separated entities whose records are never created (lookups to them are kept only when
        /// the same GUID exists in the destination). Blank restores the defaults.
        /// </summary>
        public string NeverCreateEntities { get; set; } = string.Join(",", CopyOptions.DefaultNeverCreateEntities);

        /// <summary>The entity selected last time; re-selected when the entity list loads.</summary>
        public string LastEntity { get; set; }

        /// <summary>
        /// The 1:N relationships chosen in the relationship picker, per source organisation (its URL)
        /// and entity (SPEC 5.10). An entity without a configured entry follows the subgrids on its
        /// active main forms.
        /// </summary>
        public List<RelationshipSelection> RelationshipSelections { get; set; } = new List<RelationshipSelection>();

        /// <summary><see cref="PageSize"/> limited to 1..5000; the default when unset or invalid.</summary>
        internal int EffectivePageSize => PageSize <= 0 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize);

        /// <summary>
        /// The configured entities of <paramref name="organization"/> and their ticked relationship
        /// schema names (keys and names case-insensitive). Entries of other organisations, and entries
        /// that are not configured, are ignored.
        /// </summary>
        internal Dictionary<string, ISet<string>> GetRelationshipSelections(string organization)
        {
            string org = OrganizationKey(organization);
            var selections = new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationshipSelection selection in RelationshipSelections ?? new List<RelationshipSelection>())
            {
                if (selection == null || !selection.Configured || string.IsNullOrWhiteSpace(selection.Entity)
                    || !string.Equals(OrganizationKey(selection.Organization), org, StringComparison.Ordinal))
                {
                    continue;
                }
                selections[selection.Entity.Trim().ToLowerInvariant()] = new HashSet<string>(
                    (selection.Relationships ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
                    StringComparer.OrdinalIgnoreCase);
            }
            return selections;
        }

        /// <summary>
        /// Replaces every entry of <paramref name="organization"/> with <paramref name="selections"/>
        /// (configured entity -> ticked relationship schema names); other organisations are kept.
        /// </summary>
        internal void SetRelationshipSelections(string organization, IReadOnlyDictionary<string, ISet<string>> selections)
        {
            string org = OrganizationKey(organization);
            if (RelationshipSelections == null) RelationshipSelections = new List<RelationshipSelection>();
            RelationshipSelections.RemoveAll(s => s == null || string.Equals(OrganizationKey(s.Organization), org, StringComparison.Ordinal));
            if (selections == null) return;
            foreach (KeyValuePair<string, ISet<string>> pair in selections.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                RelationshipSelections.Add(new RelationshipSelection
                {
                    Organization = org,
                    Entity = pair.Key.Trim().ToLowerInvariant(),
                    Configured = true,
                    Relationships = (pair.Value ?? (ISet<string>)new HashSet<string>())
                        .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
        }

        /// <summary>How an organisation is keyed: trimmed, without a trailing slash, lower case ("" when unknown).</summary>
        internal static string OrganizationKey(string organization) =>
            (organization ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

        /// <summary>Reads the settings file; defaults when there is none. May throw if the store is unusable.</summary>
        internal static DataCopierSettings LoadFromXrmToolBox()
        {
            // TryLoad<T> needs the concrete type: with object it would hand back something else and
            // the settings would silently reset to their defaults.
            return SettingsManager.Instance.TryLoad(typeof(DataCopierSettings), out DataCopierSettings settings) && settings != null
                ? settings
                : new DataCopierSettings();
        }

        /// <summary>Writes the settings file. May throw if the store is unusable.</summary>
        internal static void SaveToXrmToolBox(DataCopierSettings settings) =>
            SettingsManager.Instance.Save(typeof(DataCopierSettings), settings);
    }

    /// <summary>
    /// The 1:N relationships chosen for one entity of one source organisation in the relationship
    /// picker (SPEC 5.10), as stored in the settings file.
    /// </summary>
    public class RelationshipSelection
    {
        /// <summary>The source organisation: its URL, lower case, without a trailing slash.</summary>
        public string Organization { get; set; }

        /// <summary>The entity logical name.</summary>
        public string Entity { get; set; }

        /// <summary>
        /// True when the relationships of the entity were chosen in the picker: <see cref="Relationships"/>
        /// is then followed (empty: no child records). False: the entry is ignored and the entity follows
        /// the subgrids on its active main forms.
        /// </summary>
        public bool Configured { get; set; }

        /// <summary>The ticked relationship schema names.</summary>
        [XmlArrayItem("Relationship")]
        public List<string> Relationships { get; set; } = new List<string>();
    }
}
