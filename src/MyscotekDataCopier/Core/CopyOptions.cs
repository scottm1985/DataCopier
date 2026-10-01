using System;
using System.Collections.Generic;

namespace MyscotekDataCopier.Core
{
    /// <summary>Options for one <see cref="CopyEngine"/> run.</summary>
    public sealed class CopyOptions
    {
        /// <summary>The entities that are never created or retrieved from the source (SPEC section 1).</summary>
        public static readonly IReadOnlyList<string> DefaultNeverCreateEntities =
            new[] { "systemuser", "team", "businessunit", "organization", "transactioncurrency" };

        public CopyOptions()
        {
            NeverCreateEntities = new HashSet<string>(DefaultNeverCreateEntities, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Resolve everything and log what would happen, but write nothing.</summary>
        public bool DryRun { get; set; }

        /// <summary>On create, write the source <c>createdon</c> to <c>overriddencreatedon</c>.</summary>
        public bool PreserveCreatedOn { get; set; }

        /// <summary>Set <c>BypassCustomPluginExecution = true</c> on create/update requests (online only, needs prvBypassCustomPlugins).</summary>
        public bool BypassCustomPluginExecution { get; set; }

        /// <summary>Decision: always true (selected records that already exist are overwritten). The UI does not expose it.</summary>
        public bool UpdateExistingSelectedRecords { get; set; } = true;

        /// <summary>
        /// Entities whose records are never created: a lookup to one is kept only when the same GUID
        /// already exists in the destination. Case-insensitive. Defaults to
        /// <see cref="DefaultNeverCreateEntities"/>.
        /// </summary>
        public HashSet<string> NeverCreateEntities { get; }

        /// <summary>Safety cap on lookup recursion depth; a warning is logged when it is hit.</summary>
        public int MaxDepth { get; set; } = 100;

        /// <summary>
        /// "Create related records for N:1 relationships (lookups)" (SPEC 5.10). True (default): the
        /// records the copied records point at through lookups are created first, recursively. False: no
        /// lookup target is ever created or updated - a lookup is kept if its target exists in the
        /// destination by the end of the selected record's copy (one the copy brings along later, e.g. as
        /// a child record, is filled in with an update) and left blank otherwise. Never-create entities
        /// and virtual tables behave the same either way.
        /// </summary>
        public bool CopyLookups { get; set; } = true;

        /// <summary>
        /// "Create related records for 1:N relationships (subgrids)" (SPEC 5.10). When true, the child
        /// records of every selected record - and, recursively, of those children - are copied too,
        /// through the relationships <see cref="ChildRelationshipSelector"/> chooses. Default false.
        /// </summary>
        public bool CopyChildren { get; set; }

        /// <summary>Chooses the 1:N relationships followed when <see cref="CopyChildren"/> is on; null means no child records.</summary>
        public IChildRelationshipSelector ChildRelationshipSelector { get; set; }

        /// <summary>
        /// Replaces <see cref="NeverCreateEntities"/> with the entries of a comma/semicolon/space
        /// separated list (the settings format, e.g. "systemuser,team,businessunit"). Entries are
        /// trimmed and lower-cased. A null or blank list restores <see cref="DefaultNeverCreateEntities"/>.
        /// </summary>
        public void SetNeverCreateEntities(string list)
        {
            NeverCreateEntities.Clear();
            string[] parts = (list ?? string.Empty).Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string name = part.Trim().ToLowerInvariant();
                if (name.Length > 0) NeverCreateEntities.Add(name);
            }
            if (NeverCreateEntities.Count == 0)
            {
                foreach (string name in DefaultNeverCreateEntities) NeverCreateEntities.Add(name);
            }
        }
    }
}
