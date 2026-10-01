using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Core.Services;
using MyscotekDataCopier.Tests.Fakes;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    public class EntityCatalogTests
    {
        [Fact]
        public void GetEntities_excludes_intersect_and_private_entities_and_sorts_by_display_name()
        {
            var service = new FakeOrganizationService
            {
                ExecuteHandler = request =>
                {
                    var all = (RetrieveAllEntitiesRequest)request;
                    Assert.Equal(EntityFilters.Entity, all.EntityFilters);
                    var response = new RetrieveAllEntitiesResponse();
                    response.Results["EntityMetadata"] = new[]
                    {
                        Meta("contact", "Contact", "Contact", 2),
                        Meta("new_thing", "new_Thing", null, 10001),
                        Meta("account", "Account", "Account", 1),
                        Meta("accountleads", "AccountLeads", "Account Leads", 16, isIntersect: true),
                        Meta("new_private", "new_Private", "Private", 10002, isPrivate: true),
                        Meta("email", "Email", "Email", 4202, isActivity: true)
                    };
                    return response;
                }
            };

            IList<EntityInfo> entities = EntityCatalog.GetEntities(service);

            Assert.Equal(new[] { "account", "contact", "email", "new_thing" }, entities.Select(e => e.LogicalName));
            EntityInfo account = entities[0];
            Assert.Equal(("Account", "Account", 1, "accountid", "name", false),
                (account.DisplayName, account.SchemaName, account.ObjectTypeCode, account.PrimaryIdAttribute, account.PrimaryNameAttribute, account.IsActivity));
            Assert.True(entities[2].IsActivity);
            Assert.Equal("new_Thing", entities[3].DisplayName);   // no label: falls back to the schema name
            Assert.All(entities, e => Assert.False(e.IsVirtual));
        }

        [Fact]
        public void FromMetadata_marks_virtual_tables_but_not_elastic_ones()
        {
            EntityMetadata virtualByProvider = Meta("new_vatrate", "new_VatRate", "VAT Rate", 10010);
            virtualByProvider.DataProviderId = new Guid("c9a7f5b6-2e3d-4a1b-9f80-7d6e5c4b3a21");
            EntityMetadata virtualByType = Meta("new_taxcode", "new_TaxCode", "Tax Code", 10011);
            virtualByType.TableType = "Virtual";
            EntityMetadata elastic = Meta("new_log", "new_Log", "Log", 10012);
            elastic.DataProviderId = new Guid("1d9bde74-9ebd-4da9-8ff5-aa74945b9f74");
            elastic.TableType = "Elastic";

            IList<EntityInfo> entities = EntityCatalog.FromMetadata(new[] { Meta("account", "Account", "Account", 1), virtualByProvider, virtualByType, elastic });

            Assert.Equal(new[] { ("account", false), ("new_log", false), ("new_taxcode", true), ("new_vatrate", true) },   // by display name
                entities.Select(e => (e.LogicalName, e.IsVirtual)));
        }

        private static EntityMetadata Meta(string logicalName, string schemaName, string label, int objectTypeCode,
                                           bool isIntersect = false, bool isPrivate = false, bool isActivity = false) =>
            new EntityMetadata
            {
                LogicalName = logicalName,
                SchemaName = schemaName,
                DisplayName = label == null ? new Label() : new Label(label, 1033),
                IsActivity = isActivity
            }
            .With("IsIntersect", isIntersect)
            .With("IsPrivate", isPrivate)
            .With("ObjectTypeCode", objectTypeCode)
            .With("PrimaryIdAttribute", isActivity ? "activityid" : logicalName + "id")
            .With("PrimaryNameAttribute", isActivity ? "subject" : "name");
    }

    public class ViewServiceTests
    {
        private static readonly Guid ActiveAccounts = new Guid("11111111-0000-0000-0000-000000000001");
        private static readonly Guid AllAccounts = new Guid("11111111-0000-0000-0000-000000000002");
        private static readonly Guid MyAccounts = new Guid("22222222-0000-0000-0000-000000000001");

        [Fact]
        public void GetViews_returns_system_views_then_personal_views_with_the_spec_filters()
        {
            var queries = new List<QueryExpression>();
            var service = new FakeOrganizationService
            {
                RetrieveMultipleHandler = query =>
                {
                    var expression = (QueryExpression)query;
                    queries.Add(expression);
                    return expression.EntityName == "savedquery"
                        ? Views(View("savedquery", ActiveAccounts, "Active Accounts"), View("savedquery", AllAccounts, "All Accounts"))
                        : Views(View("userquery", MyAccounts, "My Accounts"));
                }
            };

            IList<ViewInfo> views = ViewService.GetViews(service, "account", includePersonal: true);

            Assert.Equal(new[] { "Active Accounts", "All Accounts", "My Accounts (personal)" }, views.Select(v => v.DisplayName));
            Assert.Equal(new[] { false, false, true }, views.Select(v => v.IsPersonal));
            Assert.Equal(new[] { ActiveAccounts, AllAccounts, MyAccounts }, views.Select(v => v.Id));
            Assert.Equal("My Accounts (personal)", views[2].ToString());
            Assert.All(views, v => Assert.StartsWith("<fetch", v.FetchXml));
            Assert.All(views, v => Assert.StartsWith("<grid", v.LayoutXml));

            Assert.Equal(new[] { "savedquery", "userquery" }, queries.Select(q => q.EntityName));
            foreach (QueryExpression query in queries)
            {
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "returnedtypecode" && c.Operator == ConditionOperator.Equal && (string)c.Values[0] == "account");
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "querytype" && c.Operator == ConditionOperator.Equal && (int)c.Values[0] == 0);
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "statecode" && c.Operator == ConditionOperator.Equal && (int)c.Values[0] == 0);
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "fetchxml" && c.Operator == ConditionOperator.NotNull);
                OrderExpression order = Assert.Single(query.Orders);
                Assert.Equal(("name", OrderType.Ascending), (order.AttributeName, order.OrderType));
            }
        }

        [Fact]
        public void GetViews_without_personal_views_does_not_query_userquery()
        {
            var service = new FakeOrganizationService { RetrieveMultipleHandler = query => Views(View("savedquery", ActiveAccounts, "Active Accounts")) };

            IList<ViewInfo> views = ViewService.GetViews(service, "account", includePersonal: false);

            Assert.Equal("Active Accounts", Assert.Single(views).Name);
            Assert.Single(service.Executed);
        }

        [Fact]
        public void A_failing_personal_view_query_is_logged_and_does_not_hide_the_system_views()
        {
            var service = new FakeOrganizationService
            {
                RetrieveMultipleHandler = query => ((QueryExpression)query).EntityName == "userquery"
                    ? throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "userquery is not available")
                    : Views(View("savedquery", ActiveAccounts, "Active Accounts"), View("savedquery", AllAccounts, "All Accounts"))
            };
            var logger = new ListLogger();

            IList<ViewInfo> views = ViewService.GetViews(service, "account", true, logger);

            Assert.Equal(2, views.Count);
            Assert.All(views, v => Assert.False(v.IsPersonal));
            Assert.Equal("Could not read the personal views of account: userquery is not available", Assert.Single(logger.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void A_failing_system_view_query_still_returns_the_personal_views()
        {
            var service = new FakeOrganizationService
            {
                RetrieveMultipleHandler = query => ((QueryExpression)query).EntityName == "savedquery"
                    ? throw new TimeoutException("timed out")
                    : Views(View("userquery", MyAccounts, "My Accounts"))
            };

            IList<ViewInfo> views = ViewService.GetViews(service, "account", includePersonal: true);

            Assert.Equal("My Accounts", Assert.Single(views).Name);
        }

        [Fact]
        public void CreateAllRecordsView_builds_the_spec_fetch_and_a_matching_layout()
        {
            ViewInfo view = ViewService.CreateAllRecordsView("account", "accountid", "name");

            Assert.Equal(ViewService.AllRecordsViewName, view.Name);
            Assert.False(view.IsPersonal);
            Assert.Equal(
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><attribute name=\"accountid\" /><attribute name=\"createdon\" /><order attribute=\"name\" /></entity></fetch>",
                view.FetchXml);
            Assert.Equal(new[] { "name", "createdon" }, LayoutParser.Parse(view.LayoutXml).Select(c => c.Name));
        }

        [Fact]
        public void CreateAllRecordsView_without_a_primary_name_orders_by_createdon()
        {
            ViewInfo view = ViewService.CreateAllRecordsView("new_log", "new_logid", null);

            XElement entity = XElement.Parse(view.FetchXml).Element("entity");
            Assert.Equal(new[] { "new_logid", "createdon" }, entity.Elements("attribute").Select(a => (string)a.Attribute("name")));
            Assert.Equal("createdon", (string)entity.Element("order").Attribute("attribute"));
            Assert.Equal(new[] { "createdon" }, LayoutParser.Parse(view.LayoutXml).Select(c => c.Name));
        }

        private static Entity View(string entity, Guid id, string name) =>
            new Entity(entity, id)
            {
                ["name"] = name,
                ["fetchxml"] = "<fetch><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>",
                ["layoutxml"] = "<grid name=\"resultset\"><row name=\"result\" id=\"accountid\"><cell name=\"name\" width=\"300\" /></row></grid>"
            };

        private static EntityCollection Views(params Entity[] views) => new EntityCollection(views.ToList());
    }

    public class RecordPagerTests
    {
        [Fact]
        public void Fetch_applies_paging_to_the_fetch_and_returns_the_page()
        {
            const string cookie = "<cookie page=\"1\"><accountid last=\"{A}\" /></cookie>";
            string executedFetch = null;
            var service = new FakeOrganizationService
            {
                RetrieveMultipleHandler = query =>
                {
                    executedFetch = ((FetchExpression)query).Query;
                    return new EntityCollection(new List<Entity> { new Entity("account", Guid.NewGuid()), new Entity("account", Guid.NewGuid()) })
                    {
                        MoreRecords = true,
                        PagingCookie = "<cookie page=\"2\" />"
                    };
                }
            };

            RecordPage page = RecordPager.Fetch(service,
                "<fetch top=\"10\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>", 2, 50, cookie);

            Assert.True(page.MoreRecords);
            Assert.Equal("<cookie page=\"2\" />", page.PagingCookie);
            Assert.Equal(2, page.Entities.Entities.Count);
            XElement fetch = XElement.Parse(executedFetch);
            Assert.Equal(("2", "50", cookie), ((string)fetch.Attribute("page"), (string)fetch.Attribute("count"), (string)fetch.Attribute("paging-cookie")));
            Assert.Null(fetch.Attribute("top"));
        }
    }

    public class ColumnHeaderResolverTests
    {
        private const string LinkedFetch =
            "<fetch><entity name=\"account\">" +
            "<attribute name=\"name\" /><attribute name=\"accountnumber\" /><attribute name=\"new_unlabelled\" />" +
            // N:1 link through the primarycontactid lookup, with an attribute aliased in the fetch and a nested link
            "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" link-type=\"outer\" alias=\"pc\">" +
            "<attribute name=\"emailaddress1\" /><attribute name=\"fullname\" alias=\"contactname\" />" +
            "<link-entity name=\"systemuser\" from=\"systemuserid\" to=\"ownerid\" alias=\"pco\"><attribute name=\"fullname\" /></link-entity>" +
            "</link-entity>" +
            // 1:N link (to = the account's primary key, not a lookup): named after the linked entity
            "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"kids\"><attribute name=\"fullname\" /></link-entity>" +
            "</entity></fetch>";

        [Fact]
        public void Columns_show_display_names_like_a_Dynamics_view_and_unresolved_columns_keep_their_name()
        {
            FakeSchemaProvider schema = Schema();

            IList<string> headers = ColumnHeaderResolver.Resolve(LinkedFetch, "account",
                Columns("name", "accountnumber", "new_unlabelled", "new_unknown", "pc.emailaddress1", "contactname", "pco.fullname", "kids.fullname", "zz.name"),
                schema);

            Assert.Equal(new[]
            {
                "Account Name",
                "Account Number",
                "new_unlabelled",                 // attribute without a display name
                "new_unknown",                    // not in the metadata
                "Email (Primary Contact)",        // alias.attribute through the lookup's display name
                "Full Name (Primary Contact)",    // attribute aliased in the fetch
                "Full Name (Owner)",              // nested link: the lookup is on the parent link's entity
                "Full Name (Contact)",            // 1:N link: the linked entity's display name
                "zz.name"                         // alias that is not in the fetch
            }, headers);
            // Each entity's metadata is read once however many columns use it.
            Assert.Equal(new[] { "account", "contact", "systemuser" }, schema.Requests.OrderBy(r => r, StringComparer.Ordinal));
        }

        [Fact]
        public void Main_entity_columns_still_resolve_without_usable_FetchXML()
        {
            IList<string> headers = ColumnHeaderResolver.Resolve("not xml", "account", Columns("name", "pc.emailaddress1"), Schema());

            Assert.Equal(new[] { "Account Name", "pc.emailaddress1" }, headers);
        }

        [Fact]
        public void Metadata_failures_keep_the_raw_column_names_and_never_throw()
        {
            var failing = new ThrowingSchemaProvider();

            Assert.Equal(new[] { "name", "pc.emailaddress1" },
                ColumnHeaderResolver.Resolve(LinkedFetch, "account", Columns("name", "pc.emailaddress1"), failing));
            Assert.Equal(new[] { "name" }, ColumnHeaderResolver.Resolve(LinkedFetch, "account", Columns("name"), null));
            Assert.Empty(ColumnHeaderResolver.Resolve(LinkedFetch, "account", null, Schema()));
            Assert.Equal(new[] { "Account Name" }, ColumnHeaderResolver.Resolve(LinkedFetch, null, Columns("name"), Schema()));   // entity from the fetch
        }

        private static FakeSchemaProvider Schema()
        {
            var schema = new FakeSchemaProvider();
            schema.Entity("account", "name").Label("name", "Account Name").EntityDisplayName("Account")
                    .String("accountnumber").Label("accountnumber", "Account Number")
                    .String("new_unlabelled")
                    .Lookup("primarycontactid", "contact").Label("primarycontactid", "Primary Contact")
                .Entity("contact", "fullname").Label("fullname", "Full Name").EntityDisplayName("Contact")
                    .String("emailaddress1").Label("emailaddress1", "Email")
                    .Customer("parentcustomerid").Label("parentcustomerid", "Company Name")
                    .Owner().Label("ownerid", "Owner")
                .Entity("systemuser", "fullname").Label("fullname", "Full Name").EntityDisplayName("User");
            return schema;
        }

        // Qualified: Microsoft.Xrm.Sdk.Metadata has a ViewColumn type too.
        private static IList<MyscotekDataCopier.Core.Services.ViewColumn> Columns(params string[] names) =>
            names.Select(n => new MyscotekDataCopier.Core.Services.ViewColumn { Name = n, Width = 100 }).ToList();

        private sealed class ThrowingSchemaProvider : MyscotekDataCopier.Core.Schema.ISchemaProvider
        {
            public MyscotekDataCopier.Core.Schema.EntitySchema GetEntity(string logicalName) =>
                throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Principal user is missing prvReadEntity privilege");
        }
    }

    public class CopyOptionsTests
    {
        [Fact]
        public void Defaults_match_the_owner_decisions()
        {
            var options = new CopyOptions();

            Assert.False(options.DryRun);
            Assert.False(options.PreserveCreatedOn);
            Assert.False(options.BypassCustomPluginExecution);
            Assert.True(options.UpdateExistingSelectedRecords);
            Assert.Equal(100, options.MaxDepth);
            Assert.Equal(new[] { "businessunit", "organization", "systemuser", "team", "transactioncurrency" },
                options.NeverCreateEntities.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Contains("SystemUser", options.NeverCreateEntities);   // case-insensitive
        }

        [Fact]
        public void SetNeverCreateEntities_parses_a_settings_list_and_a_blank_list_restores_the_defaults()
        {
            var options = new CopyOptions();

            options.SetNeverCreateEntities(" systemuser, Team ;queue ");
            Assert.Equal(new[] { "queue", "systemuser", "team" }, options.NeverCreateEntities.OrderBy(n => n, StringComparer.Ordinal));

            options.SetNeverCreateEntities("  ");
            Assert.Equal(CopyOptions.DefaultNeverCreateEntities.OrderBy(n => n, StringComparer.Ordinal),
                options.NeverCreateEntities.OrderBy(n => n, StringComparer.Ordinal));
        }
    }
}
