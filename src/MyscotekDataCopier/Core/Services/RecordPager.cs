using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace MyscotekDataCopier.Core.Services
{
    /// <summary>One page of view results.</summary>
    public sealed class RecordPage
    {
        public EntityCollection Entities { get; set; }
        public bool MoreRecords { get; set; }
        public string PagingCookie { get; set; }
    }

    /// <summary>Pages through a view's FetchXML.</summary>
    public static class RecordPager
    {
        /// <summary>
        /// Retrieves page <paramref name="page"/> (1-based) of <paramref name="pageSize"/> records with a
        /// FetchExpression; pass the previous page's <see cref="RecordPage.PagingCookie"/> for page 2 onwards.
        /// </summary>
        public static RecordPage Fetch(IOrganizationService service, string fetchXml, int page, int pageSize, string cookie)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            string paged = FetchXmlHelper.ApplyPaging(fetchXml, page, pageSize, cookie);
            EntityCollection result = service.RetrieveMultiple(new FetchExpression(paged)) ?? new EntityCollection();
            return new RecordPage
            {
                Entities = result,
                MoreRecords = result.MoreRecords,
                PagingCookie = result.PagingCookie
            };
        }
    }
}
