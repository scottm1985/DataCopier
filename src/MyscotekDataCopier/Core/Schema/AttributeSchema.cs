using System;
using Microsoft.Xrm.Sdk.Metadata;

namespace MyscotekDataCopier.Core.Schema
{
    /// <summary>The slice of an attribute's metadata the copy engine needs.</summary>
    public sealed class AttributeSchema
    {
        public string LogicalName;
        public string DisplayName;                  // the user-localised label (grid headers); the logical name when there is none
        public AttributeTypeCode AttributeType;     // Microsoft.Xrm.Sdk.Metadata
        public bool IsValidForCreate, IsValidForUpdate;
        public string AttributeOf;                  // non-null => derived attribute (e.g. *_base, *name): never copy
        public int SourceType;                      // 0 simple, 1 calculated, 2 rollup: copy only 0
        public bool IsFile;                         // FileAttributeMetadata: never copy
        public bool IsMultiSelect;                  // MultiSelectPicklistAttributeMetadata (Virtual type but copyable)
        public bool IsImage;                        // ImageAttributeMetadata (Virtual type but copyable: byte[] image, SPEC 5.3 rule 9)
        public string[] LookupTargets = Array.Empty<string>();   // for Lookup/Customer/Owner/PartyList
    }
}
