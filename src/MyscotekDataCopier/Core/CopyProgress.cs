namespace MyscotekDataCopier.Core
{
    /// <summary>
    /// Progress snapshot reported through <see cref="System.IProgress{T}"/> after every create/update
    /// and after every selected record. A new instance is reported each time (safe to keep).
    /// </summary>
    public sealed class CopyProgress
    {
        public int SelectedIndex, SelectedTotal;      // 1-based index of the selected record being processed
        public int Created, Updated, SkippedExisting, Failed, LookupsBlanked, LookupsBackfilled;
        public int ChildRecordsFound;                 // child records listed through 1:N relationships (SPEC 5.10)
        public string CurrentRecord;                  // e.g. contact "Jane Doe"
    }
}
