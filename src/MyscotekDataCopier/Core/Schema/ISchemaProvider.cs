namespace MyscotekDataCopier.Core.Schema
{
    /// <summary>Supplies entity metadata to the copy engine (so it is unit-testable without Dataverse).</summary>
    public interface ISchemaProvider { EntitySchema GetEntity(string logicalName); /* null if the entity does not exist */ }
}
