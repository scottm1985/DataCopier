using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Core.Schema;
using MyscotekDataCopier.Tests.Fakes;
using Xunit;
using static MyscotekDataCopier.Tests.Fakes.TestData;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// SPEC 5.10: the relationship options - "Copy 1:N relationships (subgrids)" (child records,
    /// recursively, only downward from the selected records) and "Copy N:1 relationships (lookups)"
    /// switched off (lookups kept only when their target already exists).
    /// </summary>
    public class CopyEngineChildRecordTests
    {
        private static readonly Guid AccountA = new Guid("a0000000-0000-0000-0000-00000000000a");
        private static readonly Guid AccountB = new Guid("b0000000-0000-0000-0000-00000000000b");
        private static readonly Guid AccountX = new Guid("f0000000-0000-0000-0000-00000000000f");
        private static readonly Guid ContactC = new Guid("c0000000-0000-0000-0000-00000000000c");
        private static readonly Guid ContactD = new Guid("d0000000-0000-0000-0000-00000000000d");
        private static readonly Guid ContactG = new Guid("c9000000-0000-0000-0000-0000000000c9");
        private static readonly Guid TaskT = new Guid("7a000000-0000-0000-0000-0000000000a1");
        private static readonly Guid TaskU = new Guid("7a000000-0000-0000-0000-0000000000a2");
        private static readonly Guid EmailE = new Guid("e0000000-0000-0000-0000-00000000000e");
        private static readonly Guid User1 = new Guid("10000000-0000-0000-0000-000000000001");
        private static readonly Guid User2 = new Guid("20000000-0000-0000-0000-000000000002");
        private static readonly Guid ProjectP = new Guid("30000000-0000-0000-0000-000000000003");

        /// <summary>
        /// The standard schema plus the 1:N relationships of account and contact, task, activitypointer
        /// (whose regardingobjectid is not valid for create, as on the platform) and a few system and
        /// custom child entities.
        /// </summary>
        private static Harness NewHarness()
        {
            var h = new Harness();
            h.SourceSchema.Edit("account")
                .OneToMany("contact_customer_accounts", "contact", "parentcustomerid")
                .OneToMany("account_parent_account", "account", "parentaccountid")
                .OneToMany("Account_Tasks", "task", "regardingobjectid")
                .OneToMany("Account_Emails", "email", "regardingobjectid")
                .OneToMany("Account_ActivityPointers", "activitypointer", "regardingobjectid")
                .OneToMany("Account_AsyncOperations", "asyncoperation", "regardingobjectid")
                .OneToMany("account_activity_parties", "activityparty", "partyid")
                .OneToMany("Account_CustomerAddress", "customeraddress", "parentid")
                .OneToMany("new_account_widgets", "new_widget", "new_accountid", custom: true);
            h.SourceSchema.Edit("contact")
                .OneToMany("Contact_Tasks", "task", "regardingobjectid")
                .OneToMany("contact_customer_contacts", "contact", "parentcustomerid");
            h.SourceSchema
                .Entity("task", "subject", "activityid")
                    .Memo("description").Lookup("regardingobjectid", "account", "contact").Owner().State().SystemAttributes()
                .Entity("activitypointer", "subject", "activityid")
                    .EntityName("activitytypecode").Lookup("regardingobjectid", "account", "contact")
                .Entity("asyncoperation", "name").Lookup("regardingobjectid", "account", "contact")
                .Entity("activityparty", null, "activitypartyid").Lookup("partyid", "account", "contact")
                .Entity("customeraddress", "name").Lookup("parentid", "account", "contact")
                .Entity("new_widget", "new_name").Lookup("new_accountid", "account");
            h.SourceSchema.Edit("activitypointer").Attribute("regardingobjectid", Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Lookup,
                create: false, update: false, configure: a => a.LookupTargets = new[] { "account", "contact" });
            return h;
        }

        /// <summary>Copies child records with the default selector: the configured entities, else the given subgrids.</summary>
        private static List<string> UseChildren(Harness h, Dictionary<string, string[]> configured = null, Dictionary<string, string[]> subgrids = null)
        {
            var subgridRequests = new List<string>();
            h.Options.CopyChildren = true;
            h.Options.ChildRelationshipSelector = new DefaultChildRelationshipSelector(
                h.SourceSchema,
                entity =>
                {
                    subgridRequests.Add(entity);
                    return subgrids != null && subgrids.TryGetValue(entity, out string[] names) ? names : Array.Empty<string>();
                },
                (configured ?? new Dictionary<string, string[]>()).ToDictionary(p => p.Key, p => (ISet<string>)new HashSet<string>(p.Value)),
                h.Options.NeverCreateEntities);
            return subgridRequests;
        }

        private static Dictionary<string, string[]> Tick(params (string Entity, string[] Relationships)[] entities) =>
            entities.ToDictionary(e => e.Entity, e => e.Relationships);

        private static (string, Guid) Key(CreateRequest create) => (create.Target.LogicalName, create.Target.Id);

        private static FaultException<OrganizationServiceFault> Fault(string message) =>
            FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, message);

        // ------------------------------------------------------------------------------------
        // child records
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Children_are_copied_after_the_parent_with_the_parent_lookup_set()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("account", AccountB, ("name", "Other")));
            h.Source.Add(Record("contact", ContactD, ("fullname", "John Roe"), ("parentcustomerid", Ref("account", AccountA, "Contoso"))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA, "Contoso"))));
            h.Source.Add(Record("contact", ContactG, ("fullname", "Not a child"), ("parentcustomerid", Ref("account", AccountB))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC), ("contact", ContactD) }, h.Destination.Creates.Select(Key));
            Assert.All(h.Destination.Creates.Skip(1), c =>
            {
                var parent = (EntityReference)c.Target["parentcustomerid"];
                Assert.Equal(("account", AccountA), (parent.LogicalName, parent.Id));
            });
            Assert.Equal((3, 0, 0, 2), (summary.Created, summary.Failed, summary.LookupsBlanked, summary.ChildRecordsFound));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})",
                $"Children of account \"Contoso\" via contact_customer_accounts: 2 contact records",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"  Copying contact \"John Roe\" ({ContactD})...",
                $"  Created contact \"John Roe\" ({ContactD})"
            }, h.Log.Lines);

            // The children query: the child entity, its lookup Equal the parent, the primary id only, 500 a page.
            QueryExpression children = Assert.Single(h.Source.Queries);
            Assert.Equal("contact", children.EntityName);
            Assert.Equal(new[] { "contactid" }, children.ColumnSet.Columns);
            ConditionExpression condition = Assert.Single(children.Criteria.Conditions);
            Assert.Equal(("parentcustomerid", ConditionOperator.Equal, (object)AccountA), (condition.AttributeName, condition.Operator, condition.Values[0]));
            Assert.Equal((500, 1, (string)null), (children.PageInfo.Count, children.PageInfo.PageNumber, children.PageInfo.PagingCookie));
            Assert.Equal(2, h.Progress.Last().ChildRecordsFound);
        }

        [Fact]
        public void Children_of_children_are_copied_two_levels_deep()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" }), ("contact", new[] { "Contact_Tasks" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("task", TaskT, ("subject", "Call Jane"), ("regardingobjectid", Ref("contact", ContactC))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC), ("task", TaskT) }, h.Destination.Creates.Select(Key));
            Assert.Equal(ContactC, ((EntityReference)h.Destination.Get("task", TaskT)["regardingobjectid"]).Id);
            Assert.Equal((3, 2), (summary.Created, summary.ChildRecordsFound));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})",
                $"Children of account \"Contoso\" via contact_customer_accounts: 1 contact record",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"  Children of contact \"Jane Doe\" via Contact_Tasks: 1 task record",
                $"    Copying task \"Call Jane\" ({TaskT})...",
                $"    Created task \"Call Jane\" ({TaskT})"
            }, h.Log.Lines);
            // The task has no relationships chosen (not configured, no subgrids): its children are not listed.
            Assert.DoesNotContain(h.Source.Queries, q => q.EntityName != "contact" && q.EntityName != "task");
        }

        [Fact]
        public void An_unconfigured_deeper_entity_follows_its_main_form_subgrids()
        {
            var h = NewHarness();
            List<string> subgridRequests = UseChildren(h,
                configured: Tick(("account", new[] { "contact_customer_accounts" })),
                subgrids: new Dictionary<string, string[]> { ["account"] = new[] { "Account_Tasks" }, ["contact"] = new[] { "contact_tasks" } });
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("task", TaskU, ("subject", "Account task"), ("regardingobjectid", Ref("account", AccountA))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("task", TaskT, ("subject", "Call Jane"), ("regardingobjectid", Ref("contact", ContactC))));

            CopySummary summary = h.Run("account", AccountA);

            // account is configured (its subgrid Account_Tasks is not followed); contact follows its subgrids (names match case-insensitively).
            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC), ("task", TaskT) }, h.Destination.Creates.Select(Key));
            Assert.False(h.Destination.Contains("task", TaskU));
            Assert.Equal(new[] { "contact", "task" }, subgridRequests);   // never asked for the configured account
            Assert.Equal(2, summary.ChildRecordsFound);
        }

        [Fact]
        public void An_existing_child_is_skipped_but_its_children_are_still_copied()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" }), ("contact", new[] { "Contact_Tasks" })));
            h.Destination.Add(Record("contact", ContactC, ("fullname", "Jane (destination)"), ("lastname", "Old")));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "New"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("task", TaskT, ("subject", "Call Jane"), ("regardingobjectid", Ref("contact", ContactC))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("task", TaskT) }, h.Destination.Creates.Select(Key));
            Assert.Empty(h.Destination.Updates);   // the existing child is not updated
            Assert.Equal("Old", h.Destination.Get("contact", ContactC)["lastname"]);
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "contact");
            Assert.Equal((2, 1, 2), (summary.Created, summary.SkippedExisting, summary.ChildRecordsFound));
            Assert.Equal(new[]
            {
                $"Children of account \"Contoso\" via contact_customer_accounts: 1 contact record",
                $"  Exists, skipped contact \"Jane (destination)\" ({ContactC})",
                $"  Children of contact \"Jane (destination)\" via Contact_Tasks: 1 task record",
                $"    Copying task \"Call Jane\" ({TaskT})...",
                $"    Created task \"Call Jane\" ({TaskT})"
            }, h.Log.Lines.Skip(2));
        }

        [Fact]
        public void An_existing_selected_record_is_updated_and_its_children_copied()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Destination.Add(Record("account", AccountA, ("name", "Old")));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(AccountA, Assert.Single(h.Destination.Updates).Target.Id);
            Assert.Equal(("contact", ContactC), Key(Assert.Single(h.Destination.Creates)));
            Assert.Equal((1, 1, 1), (summary.Updated, summary.Created, summary.ChildRecordsFound));
        }

        [Fact]
        public void Children_of_a_lookup_target_are_not_copied()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" }), ("contact", new[] { "Contact_Tasks" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("primarycontactid", Ref("contact", ContactC))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe")));   // the primary contact, not a child of the account
            h.Source.Add(Record("task", TaskT, ("subject", "Call Jane"), ("regardingobjectid", Ref("contact", ContactC))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("contact", ContactC), ("account", AccountA) }, h.Destination.Creates.Select(Key));
            Assert.False(h.Destination.Contains("task", TaskT));
            Assert.DoesNotContain(h.Source.Queries, q => q.EntityName == "task");   // never even listed
            Assert.Equal(0, summary.ChildRecordsFound);
        }

        [Fact]
        public void A_selected_record_first_copied_as_a_lookup_target_still_gets_its_children()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "Account_Tasks" })));
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("parentaccountid", Ref("account", AccountA))));
            h.Source.Add(Record("task", TaskT, ("subject", "Alpha task"), ("regardingobjectid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountB, AccountA);

            // Beta pulls Alpha in as a lookup target (no children then); Alpha's own turn lists them.
            Assert.Equal(new[] { ("account", AccountA), ("account", AccountB), ("task", TaskT) }, h.Destination.Creates.Select(Key));
            Assert.Equal(new[] { "task", "task" }, h.Source.Queries.Select(q => q.EntityName));   // Beta's tasks (none), then Alpha's
            Assert.Equal((3, 1), (summary.Created, summary.ChildRecordsFound));
            Assert.Equal(new[]
            {
                $"Already copied earlier in this run: account \"Alpha\" ({AccountA})",
                $"Children of account \"Alpha\" via Account_Tasks: 1 task record",
                $"  Copying task \"Alpha task\" ({TaskT})...",
                $"  Created task \"Alpha task\" ({TaskT})"
            }, h.Log.Lines.Skip(4));
        }

        [Fact]
        public void A_self_referencing_hierarchy_terminates_and_each_record_is_walked_once()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "account_parent_account" })));
            // A <- B <- X <- A: a loop in the hierarchy.
            h.Source.Add(Record("account", AccountA, ("name", "Alpha"), ("parentaccountid", Ref("account", AccountX))));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("parentaccountid", Ref("account", AccountA))));
            h.Source.Add(Record("account", AccountX, ("name", "Xray"), ("parentaccountid", Ref("account", AccountB))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { AccountB, AccountX, AccountA }, h.Destination.Creates.Select(c => c.Target.Id));   // lookups first
            Assert.Equal(3, h.Source.Queries.Count(q => q.EntityName == "account"));   // A, B and X listed once each
            Assert.Equal((3, 3, 1, 0), (summary.Created, summary.ChildRecordsFound, summary.LookupsBackfilled, summary.Failed));
            Assert.Equal(new[]
            {
                $"Children of account \"Alpha\" via account_parent_account: 1 account record",
                $"  Already copied earlier in this run: account \"Beta\" ({AccountB})",
                $"  Children of account \"Beta\" via account_parent_account: 1 account record",
                $"    Already copied earlier in this run: account \"Xray\" ({AccountX})",
                $"    Children of account \"Xray\" via account_parent_account: 1 account record",
                $"      Already copied earlier in this run: account \"Alpha\" ({AccountA})"
            }, h.Log.Lines.SkipWhile(l => !l.StartsWith("Children of", StringComparison.Ordinal)));
        }

        [Fact]
        public void Activity_pointer_children_are_copied_as_their_concrete_activities_once()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "Account_ActivityPointers", "Account_Tasks" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("activitypointer", TaskT, ("subject", "Call"), ("activitytypecode", "task"), ("regardingobjectid", Ref("account", AccountA))));
            h.Source.Add(Record("activitypointer", EmailE, ("subject", "Hello"), ("activitytypecode", "email"), ("regardingobjectid", Ref("account", AccountA))));
            h.Source.Add(Record("task", TaskT, ("subject", "Call"), ("regardingobjectid", Ref("account", AccountA))));
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"), ("regardingobjectid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("task", TaskT), ("email", EmailE) }, h.Destination.Creates.Select(Key));
            Assert.DoesNotContain(h.Destination.Creates, c => c.Target.LogicalName == "activitypointer");
            QueryExpression pointers = Assert.Single(h.Source.Queries, q => q.EntityName == "activitypointer");
            Assert.Equal(new[] { "activityid", "activitytypecode" }, pointers.ColumnSet.Columns);
            Assert.Equal("regardingobjectid", Assert.Single(pointers.Criteria.Conditions).AttributeName);
            Assert.Equal((3, 3), (summary.Created, summary.ChildRecordsFound));   // 1 through Account_Tasks + 2 through the pointers
            Assert.Contains($"Children of account \"Contoso\" via Account_ActivityPointers: 2 activitypointer records", h.Log.Lines);
            Assert.Contains($"  Already copied earlier in this run: task \"Call\" ({TaskT})", h.Log.Lines);
        }

        [Fact]
        public void System_never_create_and_virtual_child_entities_are_never_queried_even_if_a_selector_names_them()
        {
            var h = NewHarness();
            h.SourceSchema.Entity("team", "name").Lookup("regardingobjectid", "account");
            h.SourceSchema.Entity("new_vatrate", "new_name").VirtualTable().Lookup("new_accountid", "account");
            h.Options.CopyChildren = true;
            h.Options.ChildRelationshipSelector = new StubSelector(entity => entity == "account"
                ? new[]
                {
                    Relationship("Account_AsyncOperations", "asyncoperation", "regardingobjectid"),
                    Relationship("account_activity_parties", "activityparty", "partyid"),
                    Relationship("Account_CustomerAddress", "customeraddress", "parentid"),
                    Relationship("account_teams", "team", "regardingobjectid"),
                    Relationship("new_account_vatrate", "new_vatrate", "new_accountid"),
                    Relationship("contact_customer_accounts", "contact", "parentcustomerid")
                }
                : Array.Empty<ChildRelationship>());
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { "contact" }, h.Source.Queries.Select(q => q.EntityName));
            Assert.Equal((1, 0), (summary.Created, summary.ChildRecordsFound));
            Assert.Empty(h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void The_default_selector_never_offers_excluded_system_child_entities()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "Account_AsyncOperations", "account_activity_parties", "Account_CustomerAddress", "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));

            h.Run("account", AccountA);

            Assert.Equal(new[] { "contact" }, h.Source.Queries.Select(q => q.EntityName));
        }

        [Fact]
        public void A_child_entity_missing_in_the_destination_is_not_followed_with_one_warning()
        {
            var h = NewHarness();
            h.DestinationSchema = h.SourceSchema.Clone().RemoveEntity("new_widget");
            UseChildren(h, Tick(("account", new[] { "new_account_widgets", "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta")));

            CopySummary summary = h.Run("account", AccountA, AccountB);

            Assert.Equal(new[] { "contact", "contact" }, h.Source.Queries.Select(q => q.EntityName));
            Assert.Equal("1:N relationship new_account_widgets of account not followed: new_widget does not exist in destination",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
            Assert.Equal(2, summary.Created);
        }

        [Fact]
        public void A_failing_children_query_is_a_warning_and_the_run_continues()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts", "Account_Tasks" })));
            h.Source.FailRetrieveMultiple["contact"] = Fault("Principal user is missing prvReadContact privilege");
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("task", TaskT, ("subject", "Call"), ("regardingobjectid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("task", TaskT) }, h.Destination.Creates.Select(Key));
            Assert.Equal((2, 0, 1), (summary.Created, summary.Failed, summary.ChildRecordsFound));
            Assert.Empty(summary.Errors);
            Assert.Equal($"Could not list contact_customer_accounts children of account \"Contoso\": Principal user is missing prvReadContact privilege",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void A_failing_selector_is_a_warning_once_per_entity_and_the_run_continues()
        {
            var h = NewHarness();
            int asked = 0;
            h.Options.CopyChildren = true;
            h.Options.ChildRelationshipSelector = new StubSelector(entity =>
            {
                asked++;
                throw Fault("systemform: Principal user is missing prvReadForm privilege");
            });
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta")));

            CopySummary summary = h.Run("account", AccountA, AccountB);

            Assert.Equal(2, summary.Created);
            Assert.Equal(1, asked);   // once per entity per run
            Assert.Equal("Could not determine the 1:N relationships of account: systemform: Principal user is missing prvReadForm privilege; its child records are not copied",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void An_unexpected_failure_while_walking_the_children_does_not_fail_the_written_record()
        {
            var h = NewHarness();
            h.Options.CopyChildren = true;
            h.Options.ChildRelationshipSelector = new StubSelector(entity => new BrokenList());
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal((1, 0), (summary.Created, summary.Failed));
            Assert.Empty(summary.Errors);
            Assert.Equal("Child records of account \"Contoso\" not all copied: broken list", Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void Children_are_paged_500_at_a_time_with_the_paging_cookie()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            for (int i = 0; i < 501; i++)
            {
                h.Source.Add(Record("contact", Guid.NewGuid(), ("fullname", "Contact " + i), ("parentcustomerid", Ref("account", AccountA))));
            }

            CopySummary summary = h.Run("account", AccountA);

            List<QueryExpression> pages = h.Source.Queries.Where(q => q.EntityName == "contact").ToList();
            Assert.Equal(new[] { (1, (string)null), (2, "<cookie page=\"1\" />") }, pages.Select(q => (q.PageInfo.PageNumber, q.PageInfo.PagingCookie)));
            Assert.All(pages, q => Assert.Equal(500, q.PageInfo.Count));
            Assert.Equal((502, 501), (summary.Created, summary.ChildRecordsFound));
            Assert.Contains("Children of account \"Contoso\" via contact_customer_accounts: 501 contact records", h.Log.Lines);
        }

        [Fact]
        public void Dry_run_lists_the_children_and_would_create_them_without_writing()
        {
            var h = NewHarness();
            h.Options.DryRun = true;
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" }), ("contact", new[] { "Contact_Tasks" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("task", TaskT, ("subject", "Call Jane"), ("regardingobjectid", Ref("contact", ContactC))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Empty(h.Destination.Writes);
            Assert.All(h.Destination.Executed, r => Assert.IsType<RetrieveMultipleRequest>(r));
            Assert.Equal(new[] { "contact", "task" }, h.Source.Queries.Select(q => q.EntityName));   // the children are still listed
            Assert.Equal((3, 2), (summary.Created, summary.ChildRecordsFound));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"[DRY RUN] Would create account \"Contoso\" ({AccountA})",
                $"Children of account \"Contoso\" via contact_customer_accounts: 1 contact record",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  [DRY RUN] Would create contact \"Jane Doe\" ({ContactC})",
                $"  Children of contact \"Jane Doe\" via Contact_Tasks: 1 task record",
                $"    Copying task \"Call Jane\" ({TaskT})...",
                $"    [DRY RUN] Would create task \"Call Jane\" ({TaskT})"
            }, h.Log.Lines);
        }

        [Fact]
        public void Cancellation_inside_the_children_loop_stops_before_the_next_child()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("contact", ContactD, ("fullname", "John Roe"), ("parentcustomerid", Ref("account", AccountA))));
            using var cts = new CancellationTokenSource();
            h.Destination.BeforeExecute = request =>
            {
                if (request is CreateRequest create && create.Target.LogicalName == "contact") cts.Cancel();
            };

            Assert.Throws<OperationCanceledException>(() => h.Run("account", cts.Token, AccountA));

            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC) }, h.Destination.Creates.Select(Key));
            CopySummary summary = h.Engine.LastSummary;
            Assert.True(summary.Cancelled);
            Assert.Equal((2, 2), (summary.Created, summary.ChildRecordsFound));
        }

        [Fact]
        public void State_changes_of_the_tree_are_applied_after_the_children()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            h.Run("account", AccountA);

            Assert.Collection(h.Destination.Writes,
                r => Assert.Equal(("account", AccountA), Key(Assert.IsType<CreateRequest>(r))),
                r => Assert.Equal(("contact", ContactC), Key(Assert.IsType<CreateRequest>(r))),
                r => Assert.Equal(1, ((OptionSetValue)Assert.IsType<UpdateRequest>(r).Target["statecode"]).Value));
        }

        [Fact]
        public void Without_the_1N_option_no_children_are_listed_even_with_a_selector()
        {
            var h = NewHarness();
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Options.CopyChildren = false;
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Empty(h.Source.Queries);
            Assert.Equal(("account", AccountA), Key(Assert.Single(h.Destination.Creates)));
            Assert.Equal(0, summary.ChildRecordsFound);
        }

        // ------------------------------------------------------------------------------------
        // lookups not copied (N:1 unticked)
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Lookups_off_keeps_existing_targets_blanks_missing_ones_never_creates_and_keeps_unverifiable_ones()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            h.SourceSchema.Edit("contact").Lookup("new_projectid", "new_project").Lookup("new_accountid", "account");
            h.SourceSchema.Entity("new_project", "new_name");
            h.Destination.FailRetrieveMultiple["new_project"] = Fault("Principal user is missing prvReadnew_project privilege");
            h.Destination.Add(new Entity("systemuser", User1));
            h.Destination.Add(Record("account", AccountA, ("name", "Contoso (destination)")));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("account", AccountX, ("name", "Missing")));
            h.Source.Add(Record("new_project", ProjectP, ("new_name", "Apollo")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe"),
                ("parentcustomerid", Ref("account", AccountA)),             // exists: kept
                ("new_accountid", Ref("account", AccountX)),                // missing: blanked, never created
                ("new_projectid", Ref("new_project", ProjectP)),            // cannot be checked: kept unverified
                ("ownerid", Ref("systemuser", User1)),                      // never-create, exists: kept (as always)
                ("preferredsystemuserid", Ref("systemuser", User2))));      // never-create, missing: the usual wording

            CopySummary summary = h.Run("contact", ContactC);

            CreateRequest create = Assert.Single(h.Destination.Creates);
            Assert.Equal(("contact", ContactC), Key(create));
            Assert.Equal(AccountA, ((EntityReference)create.Target["parentcustomerid"]).Id);
            Assert.Equal(ProjectP, ((EntityReference)create.Target["new_projectid"]).Id);
            Assert.Equal(User1, ((EntityReference)create.Target["ownerid"]).Id);
            Assert.False(create.Target.Contains("new_accountid"));
            Assert.False(create.Target.Contains("preferredsystemuserid"));
            Assert.Empty(h.Destination.Updates);   // the existing account is not updated
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName != "contact");   // no target is read from the source
            Assert.Equal((1, 0, 2), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Equal(new[]
            {
                $"  Kept new_projectid on contact \"Jane Doe\" unverified: new_project {ProjectP} could not be checked in destination: Principal user is missing prvReadnew_project privilege",
                $"  Blanked preferredsystemuserid on contact \"Jane Doe\": systemuser {User2} does not exist in destination",
                // The missing account is decided once the contact's tree is done (the tree might have copied it).
                $"  Blanked new_accountid on contact \"Jane Doe\": account {AccountX} does not exist in destination (lookups not copied)"
            }, h.Log.Messages(LogLevel.Warning));
            // Existence checks only: one query per target record, for its id, top 1.
            QueryExpression accountCheck = Assert.Single(h.Destination.Queries, q => q.EntityName == "account" && (Guid)q.Criteria.Conditions[0].Values[0] == AccountA);
            Assert.Equal(1, accountCheck.TopCount);
        }

        [Fact]
        public void Lookups_off_applies_to_party_list_members_too()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            h.Destination.Add(Record("contact", ContactC, ("fullname", "Jane Doe")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe")));
            h.Source.Add(Record("contact", ContactD, ("fullname", "John Roe")));
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"),
                ("to", Parties(Party(Ref("contact", ContactC), 2), Party(Ref("contact", ContactD), 2), Party(null, 2, "someone@example.com")))));

            CopySummary summary = h.Run("email", EmailE);

            Entity email = Assert.Single(h.Destination.Creates).Target;
            var to = (EntityCollection)email["to"];
            Assert.Equal(2, to.Entities.Count);
            Assert.Equal(ContactC, ((EntityReference)to.Entities[0]["partyid"]).Id);
            Assert.Equal("someone@example.com", to.Entities[1]["addressused"]);
            Assert.Equal(1, summary.LookupsBlanked);
            Assert.Equal($"  Dropped party contact {ContactD} from to on email \"Hello\": does not exist in destination (lookups not copied; party lists are not backfilled)",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void Lookups_off_backfills_a_self_reference_instead_of_blanking_it()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            h.Source.Add(Record("account", AccountA, ("name", "Loop"), ("parentaccountid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.False(Assert.Single(h.Destination.Creates).Target.Contains("parentaccountid"));
            Assert.Equal(AccountA, ((EntityReference)Assert.Single(h.Destination.Updates).Target["parentaccountid"]).Id);
            Assert.Equal((1, 0), (summary.LookupsBackfilled, summary.LookupsBlanked));
        }

        [Fact]
        public void Lookups_off_to_a_table_the_destination_does_not_have_is_blanked_without_asking()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            h.SourceSchema.Edit("contact").Lookup("new_projectid", "new_project");
            h.SourceSchema.Entity("new_project", "new_name");
            h.DestinationSchema = h.SourceSchema.Clone().RemoveEntity("new_project");
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("new_projectid", Ref("new_project", ProjectP))));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.False(Assert.Single(h.Destination.Creates).Target.Contains("new_projectid"));
            Assert.DoesNotContain(h.Destination.Queries, q => q.EntityName == "new_project");
            Assert.Equal($"  Blanked new_projectid on contact \"Jane Doe\": new_project {ProjectP} does not exist in destination (lookups not copied)",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
            Assert.Equal(1, summary.LookupsBlanked);
        }

        [Fact]
        public void Lookups_off_keeps_the_lookup_to_a_record_this_run_copied_as_a_child()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC) }, h.Destination.Creates.Select(Key));
            Assert.Equal(AccountA, ((EntityReference)h.Destination.Get("contact", ContactC)["parentcustomerid"]).Id);
            Assert.Equal(0, summary.LookupsBlanked);
        }

        [Fact]
        public void Lookups_off_backfills_a_lookup_to_a_record_the_tree_copies_later_as_a_child()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            h.Options.BypassCustomPluginExecution = true;
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            // The primary contact is not in the destination when the account is written, but it is one of its children.
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("primarycontactid", Ref("contact", ContactC)),
                ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            // The account is created without the lookup, the contact as its child, then the lookup is written with
            // an update carrying the usual request parameters - after the child's create, before the state change.
            Assert.Collection(h.Destination.Writes,
                r =>
                {
                    CreateRequest create = Assert.IsType<CreateRequest>(r);
                    Assert.Equal(("account", AccountA), Key(create));
                    Assert.False(create.Target.Contains("primarycontactid"));
                },
                r => Assert.Equal(("contact", ContactC), Key(Assert.IsType<CreateRequest>(r))),
                r =>
                {
                    UpdateRequest update = Assert.IsType<UpdateRequest>(r);
                    Assert.Equal(("account", AccountA), (update.Target.LogicalName, update.Target.Id));
                    Assert.Equal(new[] { "primarycontactid" }, update.Target.Attributes.Keys);
                    var contact = (EntityReference)update.Target["primarycontactid"];
                    Assert.Equal(("contact", ContactC), (contact.LogicalName, contact.Id));
                    Assert.True((bool)update.Parameters["SuppressDuplicateDetection"]);
                    Assert.True((bool)update.Parameters["BypassCustomPluginExecution"]);
                },
                r => Assert.Equal(1, ((OptionSetValue)Assert.IsType<UpdateRequest>(r).Target["statecode"]).Value));
            Assert.Equal(ContactC, ((EntityReference)h.Destination.Get("account", AccountA)["primarycontactid"]).Id);
            Assert.Equal(AccountA, ((EntityReference)h.Destination.Get("contact", ContactC)["parentcustomerid"]).Id);
            Assert.Equal((2, 1, 0, 1), (summary.Created, summary.LookupsBackfilled, summary.LookupsBlanked, summary.StateChanges));
            Assert.Empty(h.Log.Messages(LogLevel.Warning));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})",
                $"Children of account \"Contoso\" via contact_customer_accounts: 1 contact record",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"  Backfilled account.primarycontactid -> contact ({ContactC})",
                $"State applied to account \"Contoso\" ({AccountA}): statecode=1, statuscode=2"
            }, h.Log.Lines);
        }

        [Fact]
        public void Lookups_off_blanks_a_lookup_whose_record_the_tree_never_copies_once_the_tree_is_done()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("primarycontactid", Ref("contact", ContactC))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe")));   // not a child of the account: never copied
            h.Source.Add(Record("contact", ContactD, ("fullname", "John Roe"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("account", AccountB, ("name", "Next")));

            CopySummary summary = h.Run("account", AccountA, AccountB);

            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactD), ("account", AccountB) }, h.Destination.Creates.Select(Key));
            Assert.False(h.Destination.Creates[0].Target.Contains("primarycontactid"));
            Assert.Empty(h.Destination.Updates);
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.Id == ContactC);   // never read from the source
            Assert.Equal((3, 0, 1), (summary.Created, summary.LookupsBackfilled, summary.LookupsBlanked));
            // Decided - and counted - once the account's tree is done, before the next selected record.
            Assert.Equal(1, h.Progress.Last(p => p.SelectedIndex == 1).LookupsBlanked);
            Assert.Equal($"  Blanked primarycontactid on account \"Contoso\": contact {ContactC} does not exist in destination (lookups not copied)",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})",
                $"Children of account \"Contoso\" via contact_customer_accounts: 1 contact record",
                $"  Copying contact \"John Roe\" ({ContactD})...",
                $"  Created contact \"John Roe\" ({ContactD})",
                $"  Blanked primarycontactid on account \"Contoso\": contact {ContactC} does not exist in destination (lookups not copied)",
                $"Copying account \"Next\" ({AccountB})...",
                $"Created account \"Next\" ({AccountB})"
            }, h.Log.Lines);
        }

        [Fact]
        public void Lookups_off_dry_run_would_backfill_or_would_blank_the_deferred_lookups_at_the_same_point()
        {
            var dry = NewHarness();
            dry.Options.DryRun = true;
            SeedDeferredLookups(dry);
            CopySummary drySummary = dry.Run("account", AccountA);

            var real = NewHarness();
            SeedDeferredLookups(real);
            CopySummary realSummary = real.Run("account", AccountA);

            Assert.Empty(dry.Destination.Writes);
            Assert.All(dry.Destination.Executed, r => Assert.IsType<RetrieveMultipleRequest>(r));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"[DRY RUN] Would create account \"Contoso\" ({AccountA})",
                $"Children of account \"Contoso\" via contact_customer_accounts: 1 contact record",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  [DRY RUN] Would create contact \"Jane Doe\" ({ContactC})",
                $"  [DRY RUN] Would backfill account.primarycontactid -> contact ({ContactC})",
                $"  [DRY RUN] Would blank parentaccountid on account \"Contoso\": account {AccountX} does not exist in destination (lookups not copied)"
            }, dry.Log.Lines);
            Assert.Equal(new[] { LogLevel.Success, LogLevel.Warning }, dry.Log.Entries.Skip(5).Select(e => e.Level));

            // The real run writes the same lines at the same point, with the same counts.
            Assert.Equal(real.Log.Entries.Select(e => e.Level), dry.Log.Entries.Select(e => e.Level));
            Assert.Equal(real.Log.Lines, dry.Log.Lines.Select(l => l
                .Replace("[DRY RUN] Would create ", "Created ")
                .Replace("[DRY RUN] Would backfill ", "Backfilled ")
                .Replace("[DRY RUN] Would blank ", "Blanked ")));
            UpdateRequest backfill = Assert.Single(real.Destination.Updates);
            Assert.Equal(("account", AccountA, ContactC), (backfill.Target.LogicalName, backfill.Target.Id, ((EntityReference)backfill.Target["primarycontactid"]).Id));
            Assert.Equal((2, 1, 1), (drySummary.Created, drySummary.LookupsBackfilled, drySummary.LookupsBlanked));
            Assert.Equal((2, 1, 1), (realSummary.Created, realSummary.LookupsBackfilled, realSummary.LookupsBlanked));
        }

        [Fact]
        public void Lookups_off_cancellation_reports_the_unresolved_deferred_lookups_as_blanked()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("primarycontactid", Ref("contact", ContactC))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("contact", ContactD, ("fullname", "John Roe"), ("parentcustomerid", Ref("account", AccountA))));
            using var cts = new CancellationTokenSource();
            h.Destination.BeforeExecute = request =>
            {
                if (request is CreateRequest create && create.Target.LogicalName == "contact") cts.Cancel();   // stops before the next child
            };

            Assert.Throws<OperationCanceledException>(() => h.Run("account", cts.Token, AccountA));

            // The primary contact was created, but the tree never finished: the lookup is not written.
            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC) }, h.Destination.Creates.Select(Key));
            Assert.Empty(h.Destination.Updates);
            Assert.False(h.Destination.Get("account", AccountA).Contains("primarycontactid"));
            CopySummary summary = h.Engine.LastSummary;
            Assert.True(summary.Cancelled);
            Assert.Equal((2, 0, 1), (summary.Created, summary.LookupsBackfilled, summary.LookupsBlanked));
            Assert.Equal($"Deferred lookup account.primarycontactid -> contact ({ContactC}) not resolved: run cancelled, lookup left blank",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void Both_options_off_copy_only_the_selected_record()
        {
            var h = NewHarness();
            h.Options.CopyLookups = false;
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("primarycontactid", Ref("contact", ContactC))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            CreateRequest create = Assert.Single(h.Destination.Creates);
            Assert.Equal(("account", AccountA), Key(create));
            Assert.False(create.Target.Contains("primarycontactid"));
            Assert.Empty(h.Destination.Updates);
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "contact");
            Assert.Empty(h.Source.Queries);   // no child records are listed
            Assert.Equal((1, 1, 0), (summary.Created, summary.LookupsBlanked, summary.ChildRecordsFound));
            Assert.Equal($"  Blanked primarycontactid on account \"Contoso\": contact {ContactC} does not exist in destination (lookups not copied)",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        // ------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Lookups off, children on: account A points at contact C, one of its children (copied later in
        /// its tree), and at account X, which nothing copies.
        /// </summary>
        private static void SeedDeferredLookups(Harness h)
        {
            h.Options.CopyLookups = false;
            UseChildren(h, Tick(("account", new[] { "contact_customer_accounts" })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"),
                ("primarycontactid", Ref("contact", ContactC)), ("parentaccountid", Ref("account", AccountX))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            h.Source.Add(Record("account", AccountX, ("name", "Never copied")));
        }

        private static ChildRelationship Relationship(string schemaName, string child, string lookup) =>
            new ChildRelationship { SchemaName = schemaName, ParentEntity = "account", ChildEntity = child, ChildLookupAttribute = lookup };

        /// <summary>A relationship list that fails when it is read (an unexpected failure inside the walk).</summary>
        private sealed class BrokenList : IReadOnlyList<ChildRelationship>
        {
            public int Count => 1;
            public ChildRelationship this[int index] => throw new InvalidOperationException("broken list");
            public IEnumerator<ChildRelationship> GetEnumerator() => throw new InvalidOperationException("broken list");
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>A selector that answers with a function (no eligibility rules).</summary>
        private sealed class StubSelector : IChildRelationshipSelector
        {
            private readonly Func<string, IReadOnlyList<ChildRelationship>> _answer;
            public StubSelector(Func<string, IReadOnlyList<ChildRelationship>> answer) { _answer = answer; }
            public IReadOnlyList<ChildRelationship> GetChildRelationships(string parentEntity) => _answer(parentEntity);
        }
    }
}
