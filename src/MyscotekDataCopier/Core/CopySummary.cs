using System;
using System.Collections.Generic;

namespace MyscotekDataCopier.Core
{
    /// <summary>
    /// Result of a <see cref="CopyEngine.Copy"/> run. Also available (with partial counts and
    /// <see cref="Cancelled"/> = true) from <see cref="CopyEngine.LastSummary"/> when the run was
    /// cancelled or a setup failure was thrown.
    /// </summary>
    public sealed class CopySummary
    {
        public int SelectedTotal, Created, Updated, SkippedExisting, Failed, LookupsBlanked, LookupsBackfilled, StateChanges;
        public int ChildRecordsFound;                                  // child records listed through 1:N relationships (SPEC 5.10)
        public int PeerRecordsFound;                                   // associated records (peers) listed through N:N relationships (SPEC 5.10)
        public int AssociationsCreated, AssociationsSkipped, AssociationsFailed;   // N:N associations (SPEC 5.10)
        public bool Cancelled, DryRun;
        public TimeSpan Elapsed;
        public IReadOnlyList<string> Errors = Array.Empty<string>();   // one line per failure, for the summary block
    }
}
