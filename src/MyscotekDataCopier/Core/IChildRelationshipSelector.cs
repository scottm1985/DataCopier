using System.Collections.Generic;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Core
{
    /// <summary>
    /// Chooses the 1:N relationships whose child records are copied with a record (SPEC 5.10). The
    /// engine asks once per parent entity per run; an empty list means "no child records".
    /// </summary>
    public interface IChildRelationshipSelector
    {
        IReadOnlyList<ChildRelationship> GetChildRelationships(string parentEntity);
    }
}
