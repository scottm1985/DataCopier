using System.Collections.Generic;
using MyscotekDataCopier.Core.Schema;

namespace MyscotekDataCopier.Core
{
    /// <summary>
    /// Chooses the 1:N relationships whose child records are copied with a record (SPEC 5.10). The
    /// engine asks once per parent entity per run; an empty list means "no child records". A selector
    /// that also implements <see cref="IRelationshipSelector"/> chooses N:N relationships too, and the
    /// relationships of records reached as peers.
    /// </summary>
    public interface IChildRelationshipSelector
    {
        /// <summary>The 1:N relationships followed from a selected or child record of <paramref name="parentEntity"/>.</summary>
        IReadOnlyList<ChildRelationship> GetChildRelationships(string parentEntity);
    }

    /// <summary>How a record was reached in a run, which decides the relationships followed from it (SPEC 5.10).</summary>
    public enum RelationshipContext
    {
        /// <summary>
        /// A selected record, or a child record reached through a 1:N relationship: an entity not
        /// configured in the picker follows the subgrids on its active main forms.
        /// </summary>
        SelectedOrChild,

        /// <summary>
        /// A peer, reached through an N:N relationship: only an entity configured in the picker follows
        /// anything (its ticked relationships); an unconfigured one follows nothing.
        /// </summary>
        Peer
    }

    /// <summary>
    /// Chooses the 1:N and N:N relationships followed from a record by how the record was reached
    /// (SPEC 5.10). <see cref="IChildRelationshipSelector.GetChildRelationships(string)"/> is the
    /// <see cref="RelationshipContext.SelectedOrChild"/> answer. The engine asks once per entity and
    /// context per run; an empty list means "nothing followed".
    /// </summary>
    public interface IRelationshipSelector : IChildRelationshipSelector
    {
        /// <summary>The 1:N relationships followed from a record of <paramref name="entity"/> reached as <paramref name="context"/>.</summary>
        IReadOnlyList<ChildRelationship> GetChildRelationships(string entity, RelationshipContext context);

        /// <summary>The N:N relationships followed from a record of <paramref name="entity"/> reached as <paramref name="context"/>.</summary>
        IReadOnlyList<ManyToManyRelationship> GetManyToManyRelationships(string entity, RelationshipContext context);
    }
}
