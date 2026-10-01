using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Tests.Fakes;
using Xunit;
using static MyscotekDataCopier.Tests.Fakes.TestData;

namespace MyscotekDataCopier.Tests
{
    public class CopyEngineTests
    {
        private static readonly Guid AccountA = new Guid("a0000000-0000-0000-0000-00000000000a");
        private static readonly Guid AccountB = new Guid("b0000000-0000-0000-0000-00000000000b");
        private static readonly Guid AccountX = new Guid("f0000000-0000-0000-0000-00000000000f");
        private static readonly Guid ContactC = new Guid("c0000000-0000-0000-0000-00000000000c");
        private static readonly Guid ContactD = new Guid("d0000000-0000-0000-0000-00000000000d");
        private static readonly Guid EmailE = new Guid("e0000000-0000-0000-0000-00000000000e");
        private static readonly Guid User1 = new Guid("10000000-0000-0000-0000-000000000001");
        private static readonly Guid User2 = new Guid("20000000-0000-0000-0000-000000000002");
        private static readonly Guid ProjectP = new Guid("30000000-0000-0000-0000-000000000003");
        private static readonly DateTime SourceCreatedOn = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        // ------------------------------------------------------------------------------------
        // 1. simple record copied with same GUID and only copyable attributes
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Simple_record_is_created_with_the_same_guid_and_only_copyable_attributes()
        {
            var h = new Harness();
            h.SourceSchema.Edit("account")
                .MultiSelect("new_tags").Image("entityimage").File("new_document").Virtual("new_virtual")
                .Calculated("new_calc").Rollup("new_rollup").String("new_readonly", create: false, update: false)
                .EntityName("new_entitytype")
                .String("entityimage_url").Attribute("entityimage_timestamp", AttributeTypeCode.BigInt).Guid("entityimageid");
            h.Source.Add(Record("account", AccountA,
                ("name", "Contoso"),
                ("accountnumber", "AC-1"),
                ("revenue", new Money(1234.5m)),
                ("numberofemployees", 42),
                ("creditonhold", true),
                ("industrycode", Opt(3)),
                ("new_tags", new OptionSetValueCollection { Opt(1), Opt(2) }),
                ("entityimage", new byte[] { 1, 2, 3 }),
                ("importsequencenumber", 7),
                // Everything below must NOT be copied.
                ("revenue_base", new Money(1234.5m)),                 // derived (AttributeOf)
                ("new_calc", "calculated"),                          // calculated
                ("new_rollup", 5m),                                  // rollup
                ("new_document", Guid.NewGuid()),                    // file column
                ("new_virtual", "virtual"),                          // plain virtual
                ("new_readonly", "read-only"),                       // not valid for create
                ("new_entitytype", "account"),                       // EntityName type
                ("createdon", SourceCreatedOn), ("modifiedon", SourceCreatedOn),
                ("createdby", Ref("systemuser", User1)), ("modifiedby", Ref("systemuser", User1)),
                ("owningbusinessunit", Ref("businessunit", Guid.NewGuid())),
                ("versionnumber", 123456L), ("exchangerate", 1.0m),
                ("statecode", Opt(0)), ("statuscode", Opt(1)),
                ("processid", Guid.NewGuid()), ("stageid", Guid.NewGuid()), ("traversedpath", "a,b"),
                ("entityimage_url", "/image"), ("entityimage_timestamp", 99L), ("entityimageid", Guid.NewGuid()),
                ("overriddencreatedon", SourceCreatedOn)));

            CopySummary summary = h.Run("account", AccountA);

            CreateRequest create = Assert.Single(h.Destination.Creates);
            Assert.Equal("account", create.Target.LogicalName);
            Assert.Equal(AccountA, create.Target.Id);
            Assert.Equal(
                new[] { "accountnumber", "creditonhold", "entityimage", "importsequencenumber", "industrycode", "name", "new_tags", "numberofemployees", "revenue" },
                create.Target.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal("Contoso", create.Target["name"]);
            Assert.Equal(1234.5m, ((Money)create.Target["revenue"]).Value);
            Assert.True(h.Destination.Contains("account", AccountA));
            Assert.Equal(1, summary.Created);
            Assert.Equal(0, summary.Failed);
            Assert.Empty(summary.Errors);
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})"
            }, h.Log.Lines);
        }

        // ------------------------------------------------------------------------------------
        // 2. parent lookup created before the child, same GUIDs, log order
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Parent_lookup_is_created_before_the_child_with_the_same_guids_and_the_log_shows_the_tree()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(0)), ("statuscode", Opt(1))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("firstname", "Jane"), ("lastname", "Doe"),
                ("parentcustomerid", Ref("account", AccountA, "Contoso")), ("statecode", Opt(0)), ("statuscode", Opt(1))));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC) },
                h.Destination.Creates.Select(c => (c.Target.LogicalName, c.Target.Id)));
            var parent = Assert.IsType<EntityReference>(h.Destination.Get("contact", ContactC)["parentcustomerid"]);
            Assert.Equal("account", parent.LogicalName);
            Assert.Equal(AccountA, parent.Id);
            Assert.Equal(2, summary.Created);
            Assert.Equal(new[]
            {
                $"Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Copying account \"Contoso\" ({AccountA})...",
                $"  Created account \"Contoso\" ({AccountA})",
                $"Created contact \"Jane Doe\" ({ContactC})"
            }, h.Log.Lines);
        }

        // ------------------------------------------------------------------------------------
        // 3. related record that already exists is skipped and not updated
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Related_record_that_already_exists_is_skipped_and_not_updated()
        {
            var h = new Harness();
            h.Destination.Add(Record("account", AccountA, ("name", "Contoso (destination)"), ("statecode", Opt(0)), ("statuscode", Opt(1))));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(0)), ("statuscode", Opt(1))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA, "Contoso"))));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Empty(h.Destination.Updates);
            CreateRequest create = Assert.Single(h.Destination.Creates);
            Assert.Equal(ContactC, create.Target.Id);
            Assert.Equal(AccountA, ((EntityReference)create.Target["parentcustomerid"]).Id);
            Assert.Equal("Contoso (destination)", h.Destination.Get("account", AccountA)["name"]);
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "account");   // not even for the name
            Assert.Contains($"  Exists, skipped account \"Contoso (destination)\" ({AccountA})", h.Log.Messages(LogLevel.Info));
            Assert.Equal(1, summary.SkippedExisting);
            Assert.Equal(1, summary.Created);
            Assert.Equal(0, summary.Updated);
        }

        // ------------------------------------------------------------------------------------
        // 4. selected record that already exists is updated (and reactivated first if inactive)
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Selected_record_that_already_exists_is_reactivated_then_updated()
        {
            var h = new Harness();
            h.Options.PreserveCreatedOn = true;   // must not matter for an update
            h.Destination.Add(Record("account", AccountA, ("name", "Old name"), ("numberofemployees", 1), ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("account", AccountA, ("name", "New name"), ("numberofemployees", 10), ("createdon", SourceCreatedOn),
                ("statecode", Opt(0)), ("statuscode", Opt(1))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Empty(h.Destination.Creates);
            Assert.Collection(h.Destination.Updates,
                reactivate =>
                {
                    Assert.Equal(new[] { "statecode", "statuscode" }, reactivate.Target.Attributes.Keys.OrderBy(k => k).ToArray());
                    Assert.Equal(0, ((OptionSetValue)reactivate.Target["statecode"]).Value);
                    Assert.Equal(1, ((OptionSetValue)reactivate.Target["statuscode"]).Value);
                },
                update =>
                {
                    Assert.Equal(AccountA, update.Target.Id);
                    Assert.Equal(new[] { "name", "numberofemployees" }, update.Target.Attributes.Keys.OrderBy(k => k).ToArray());
                });
            Entity stored = h.Destination.Get("account", AccountA);
            Assert.Equal("New name", stored["name"]);
            Assert.Equal(0, ((OptionSetValue)stored["statecode"]).Value);
            Assert.Equal(1, summary.Updated);
            Assert.Equal(0, summary.Created);
            Assert.Equal(0, summary.StateChanges);
            Assert.Equal(new[]
            {
                $"Copying account \"New name\" ({AccountA})...",
                $"  Reactivated account \"New name\" ({AccountA}) for update",
                $"Updated account \"New name\" ({AccountA})"
            }, h.Log.Lines);
        }

        [Fact]
        public void Selected_active_record_that_already_exists_is_updated_with_a_single_request()
        {
            var h = new Harness();
            h.Destination.Add(Record("account", AccountA, ("name", "Old name"), ("statecode", Opt(0)), ("statuscode", Opt(1))));
            h.Source.Add(Record("account", AccountA, ("name", "New name"), ("statecode", Opt(0)), ("statuscode", Opt(1))));

            CopySummary summary = h.Run("account", AccountA);

            UpdateRequest update = Assert.Single(h.Destination.Updates);
            Assert.Equal("New name", update.Target["name"]);
            Assert.Empty(h.Destination.Creates);
            Assert.Equal(1, summary.Updated);
        }

        [Fact]
        public void Selected_record_first_met_as_an_existing_lookup_target_is_still_updated_on_its_turn()
        {
            var h = new Harness();
            h.Destination.Add(Record("account", AccountA, ("name", "Alpha (old)")));
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("parentaccountid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountB, AccountA);

            Assert.Equal(AccountB, Assert.Single(h.Destination.Creates).Target.Id);
            UpdateRequest update = Assert.Single(h.Destination.Updates);
            Assert.Equal(AccountA, update.Target.Id);
            Assert.Equal("Alpha", h.Destination.Get("account", AccountA)["name"]);
            Assert.Equal(1, summary.Created);
            Assert.Equal(1, summary.Updated);
            Assert.Equal(1, summary.SkippedExisting);
        }

        [Fact]
        public void Selected_record_already_created_as_a_related_record_is_not_copied_twice()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("parentaccountid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountB, AccountA);

            Assert.Equal(new[] { AccountA, AccountB }, h.Destination.Creates.Select(c => c.Target.Id));
            Assert.Empty(h.Destination.Updates);
            Assert.Equal(2, summary.Created);
            Assert.Equal($"Already copied earlier in this run: account \"Alpha\" ({AccountA})", h.Log.Lines.Last());
        }

        [Fact]
        public void Duplicate_selected_ids_are_processed_once()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));

            CopySummary summary = h.Run("account", AccountA, AccountA);

            Assert.Single(h.Destination.Creates);
            Assert.Equal(1, summary.Created);
            Assert.Equal(2, summary.SelectedTotal);
            Assert.Equal($"Skipped duplicate selection account ({AccountA})", h.Log.Lines.Last());
        }

        [Fact]
        public void Update_failure_of_a_selected_record_is_logged_counted_and_does_not_stop_the_run()
        {
            var h = new Harness();
            h.Destination.FailUpdates.Add("account/" + AccountA);
            h.Destination.Add(Record("account", AccountA, ("name", "Old")));
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta")));

            CopySummary summary = h.Run("account", AccountA, AccountB);

            Assert.Equal(1, summary.Failed);
            Assert.Equal(0, summary.Updated);
            Assert.Equal(1, summary.Created);
            Assert.StartsWith($"FAILED to update account \"Alpha\" ({AccountA}): Simulated update failure", Assert.Single(summary.Errors));
            Assert.True(h.Destination.Contains("account", AccountB));
        }

        // ------------------------------------------------------------------------------------
        // 5. failed related create: logged, lookup blanked, selected record still created
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Failed_related_create_is_logged_the_lookup_blanked_and_the_selected_record_still_created()
        {
            var h = new Harness();
            h.Destination.FailCreates.Add("account");
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.True(h.Destination.Contains("contact", ContactC));
            Assert.False(h.Destination.Contains("account", AccountA));
            Assert.False(h.Destination.Get("contact", ContactC).Contains("parentcustomerid"));
            Assert.Equal("Doe", h.Destination.Get("contact", ContactC)["lastname"]);
            Assert.Equal(1, summary.Created);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(1, summary.LookupsBlanked);
            Assert.StartsWith($"FAILED to create account \"Contoso\" ({AccountA}): Simulated create failure", Assert.Single(summary.Errors));
            Assert.Equal(new[]
            {
                $"Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Copying account \"Contoso\" ({AccountA})...",
                $"  FAILED to create account \"Contoso\" ({AccountA}): Simulated create failure for account {AccountA}",
                $"  Blanked parentcustomerid on contact \"Jane Doe\": related account {AccountA} could not be copied",
                $"Created contact \"Jane Doe\" ({ContactC})"
            }, h.Log.Lines);
            Assert.Equal(LogLevel.Error, h.Log.Entries[2].Level);
            Assert.Equal(LogLevel.Warning, h.Log.Entries[3].Level);
        }

        [Fact]
        public void Related_record_missing_in_source_blanks_the_lookup_with_a_warning()
        {
            var h = new Harness();
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountX))));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.False(Assert.Single(h.Destination.Creates).Target.Contains("parentcustomerid"));
            Assert.Equal(1, summary.LookupsBlanked);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(new[]
            {
                $"  account {AccountX} not found in source",
                $"  Blanked parentcustomerid on contact \"Jane Doe\": related account {AccountX} could not be copied"
            }, h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void Source_read_failure_other_than_not_found_fails_only_that_related_record()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe"), ("parentcustomerid", Ref("account", AccountA, "Contoso"))));
            h.Source.BeforeExecute = request =>
            {
                if (request is RetrieveRequest retrieve && retrieve.Target.LogicalName == "account")
                    throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Principal user is missing prvReadAccount privilege");
            };

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Equal("contact", Assert.Single(h.Destination.Creates).Target.LogicalName);
            Assert.False(h.Destination.Get("contact", ContactC).Contains("parentcustomerid"));
            Assert.Equal((1, 1, 1), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Equal($"FAILED to copy account \"Contoso\" ({AccountA}): Principal user is missing prvReadAccount privilege", Assert.Single(summary.Errors));
        }

        [Fact]
        public void Reference_without_an_entity_name_follows_a_single_target_lookup_and_is_otherwise_blanked_with_a_warning()
        {
            var h = new Harness();
            h.Destination.Add(new Entity("systemuser", User1));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe")));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"),
                ("primarycontactid", new EntityReference { Id = ContactC })));                                // one target: contact
            h.Source.Add(Record("contact", ContactD, ("fullname", "John Roe"),
                ("parentcustomerid", new EntityReference { Id = AccountX }),                                  // Customer: account or contact
                ("preferredsystemuserid", new EntityReference { Id = User1, LogicalName = string.Empty })));  // one target: systemuser

            CopySummary accountRun = h.Run("account", AccountA);
            CopySummary contactRun = h.Run("contact", ContactD);

            Assert.Equal(new[] { ("contact", ContactC), ("account", AccountA), ("contact", ContactD) },
                h.Destination.Creates.Select(c => (c.Target.LogicalName, c.Target.Id)));
            var primaryContact = (EntityReference)h.Destination.Creates[1].Target["primarycontactid"];
            Assert.Equal(("contact", ContactC), (primaryContact.LogicalName, primaryContact.Id));
            Entity john = h.Destination.Creates[2].Target;
            Assert.False(john.Contains("parentcustomerid"));
            var user = (EntityReference)john["preferredsystemuserid"];
            Assert.Equal(("systemuser", User1), (user.LogicalName, user.Id));
            Assert.Equal((2, 0, 0), (accountRun.Created, accountRun.Failed, accountRun.LookupsBlanked));
            Assert.Equal((1, 0, 1), (contactRun.Created, contactRun.Failed, contactRun.LookupsBlanked));
            Assert.Equal($"  Blanked parentcustomerid on contact \"John Roe\": the reference to {AccountX} has no entity name",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void Related_entity_missing_in_destination_blanks_the_lookup_without_reading_the_source()
        {
            var h = new Harness();
            h.SourceSchema.Edit("contact").Lookup("new_projectid", "new_project");
            h.SourceSchema.Entity("new_project", "new_name");
            h.DestinationSchema = h.SourceSchema.Clone().RemoveEntity("new_project");
            h.Source.Add(Record("new_project", ProjectP, ("new_name", "Apollo")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("new_projectid", Ref("new_project", ProjectP))));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.False(Assert.Single(h.Destination.Creates).Target.Contains("new_projectid"));
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "new_project");
            Assert.Equal(1, summary.LookupsBlanked);
            Assert.Equal(new[]
            {
                $"  Entity new_project does not exist in destination: new_project {ProjectP} not copied",
                $"  Blanked new_projectid on contact \"Jane Doe\": related new_project {ProjectP} could not be copied"
            }, h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void MaxDepth_stops_the_recursion_with_a_warning_and_blanks_the_lookup()
        {
            var h = new Harness();
            h.Options.MaxDepth = 1;
            h.Source.Add(Record("account", AccountA, ("name", "Level 0"), ("parentaccountid", Ref("account", AccountB))));
            h.Source.Add(Record("account", AccountB, ("name", "Level 1"), ("parentaccountid", Ref("account", AccountX))));
            h.Source.Add(Record("account", AccountX, ("name", "Level 2")));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { AccountB, AccountA }, h.Destination.Creates.Select(c => c.Target.Id));
            Assert.False(h.Destination.Get("account", AccountB).Contains("parentaccountid"));
            Assert.Equal(AccountB, ((EntityReference)h.Destination.Get("account", AccountA)["parentaccountid"]).Id);
            Assert.Equal(1, summary.LookupsBlanked);
            Assert.Contains($"    Maximum depth (1) reached at account {AccountX}: not copied", h.Log.Messages(LogLevel.Warning));
        }

        // ------------------------------------------------------------------------------------
        // 6. never-create entities
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Never_create_lookup_is_kept_when_it_exists_blanked_when_not_and_never_retrieved_or_created()
        {
            var h = new Harness();
            h.Destination.Add(new Entity("systemuser", User1) { ["fullname"] = "Existing User" });
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"),
                ("ownerid", Ref("systemuser", User1, "Owner")),
                ("preferredsystemuserid", Ref("systemuser", User2, "Missing User"))));

            CopySummary summary = h.Run("contact", ContactC);

            CreateRequest create = Assert.Single(h.Destination.Creates);
            var owner = (EntityReference)create.Target["ownerid"];
            Assert.Equal(("systemuser", User1), (owner.LogicalName, owner.Id));
            Assert.False(create.Target.Contains("preferredsystemuserid"));
            Assert.Equal(1, summary.LookupsBlanked);
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "systemuser");
            Assert.DoesNotContain(h.Destination.Creates, c => c.Target.LogicalName == "systemuser");
            // No systemuser schema in the fake: the existence query asks for {entity}id only.
            Assert.All(h.Destination.Queries.Where(q => q.EntityName == "systemuser"),
                q => Assert.Equal(new[] { "systemuserid" }, q.ColumnSet.Columns));
            Assert.Equal($"  Blanked preferredsystemuserid on contact \"Jane Doe\": systemuser {User2} does not exist in destination",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
            Assert.DoesNotContain(h.Log.Lines, line => line.Contains(User1.ToString()));   // resolved lookups are not logged
        }

        [Fact]
        public void Never_create_existence_query_uses_the_destination_schema_when_it_is_available()
        {
            var h = new Harness();
            h.SourceSchema.Entity("systemuser", "fullname").State();
            h.Destination.Add(new Entity("systemuser", User1) { ["fullname"] = "Existing User" });
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("ownerid", Ref("systemuser", User1))));

            h.Run("contact", ContactC);

            QueryExpression query = Assert.Single(h.Destination.Queries, q => q.EntityName == "systemuser");
            Assert.Equal(new[] { "systemuserid", "fullname", "statecode", "statuscode" }, query.ColumnSet.Columns);
            Assert.Equal(1, query.TopCount);
            ConditionExpression condition = Assert.Single(query.Criteria.Conditions);
            Assert.Equal(("systemuserid", ConditionOperator.Equal, (object)User1), (condition.AttributeName, condition.Operator, condition.Values[0]));
        }

        [Fact]
        public void Existence_checks_are_cached_for_the_run()
        {
            var h = new Harness();
            h.Destination.Add(new Entity("systemuser", User1));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane"), ("ownerid", Ref("systemuser", User1)), ("preferredsystemuserid", Ref("systemuser", User1))));
            h.Source.Add(Record("contact", ContactD, ("fullname", "John"), ("ownerid", Ref("systemuser", User1))));

            h.Run("contact", ContactC, ContactD);

            Assert.Single(h.Destination.Queries, q => q.EntityName == "systemuser");
            Assert.Equal(2, h.Destination.Creates.Count);
            Assert.All(h.Destination.Creates, c => Assert.Equal(User1, ((EntityReference)c.Target["ownerid"]).Id));
        }

        [Fact]
        public void Never_create_check_that_fails_keeps_the_lookup_and_the_party_unverified_and_is_asked_once()
        {
            var h = new Harness();
            h.Destination.FailRetrieveMultiple["systemuser"] =
                FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Principal user is missing prvReadUser privilege");
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"), ("ownerid", Ref("systemuser", User1)),
                ("from", Parties(Party(Ref("systemuser", User1), 1)))));

            CopySummary summary = h.Run("email", EmailE);

            CreateRequest create = Assert.Single(h.Destination.Creates);   // the create worked first time: no retry
            Assert.Equal(EmailE, create.Target.Id);
            var owner = (EntityReference)create.Target["ownerid"];
            Assert.Equal(("systemuser", User1), (owner.LogicalName, owner.Id));
            Entity party = Assert.Single(((EntityCollection)create.Target["from"]).Entities);
            Assert.Equal(User1, ((EntityReference)party["partyid"]).Id);
            Assert.Equal((1, 0, 0), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Empty(summary.Errors);
            Assert.Equal(new[]
            {
                $"  Kept ownerid on email \"Hello\" unverified: systemuser {User1} could not be checked in destination: Principal user is missing prvReadUser privilege",
                $"  Kept party systemuser {User1} in from on email \"Hello\" unverified (check failed earlier)"
            }, h.Log.Messages(LogLevel.Warning));
            Assert.Single(h.Destination.Queries, q => q.EntityName == "systemuser");   // the failed check is cached for the run
        }

        [Fact]
        public void Selected_entity_in_the_never_create_list_is_refused_up_front()
        {
            var h = new Harness();

            CopySummary summary = h.Run("systemuser", User1);

            Assert.Empty(h.Source.Executed);
            Assert.Empty(h.Destination.Executed);
            Assert.Contains("never-create", Assert.Single(summary.Errors));
            Assert.Equal(LogLevel.Error, Assert.Single(h.Log.Entries).Level);
            Assert.Equal(0, summary.Created);
            Assert.Same(summary, h.Engine.LastSummary);
        }

        [Fact]
        public void Configured_never_create_entity_is_resolved_by_existence_only()
        {
            var h = new Harness();
            h.Options.NeverCreateEntities.Add("account");
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Equal("contact", Assert.Single(h.Destination.Creates).Target.LogicalName);
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "account");
            Assert.Equal(1, summary.LookupsBlanked);
        }

        [Fact]
        public void Selected_entity_missing_in_destination_throws_after_logging_the_error()
        {
            var h = new Harness();
            h.DestinationSchema = h.SourceSchema.Clone().RemoveEntity("account");

            var ex = Assert.Throws<InvalidOperationException>(() => h.Run("account", AccountA));

            Assert.Contains("does not exist in the destination", ex.Message);
            Assert.Equal(ex.Message, Assert.Single(h.Log.Messages(LogLevel.Error)));
            Assert.NotNull(h.Engine.LastSummary);
            Assert.False(h.Engine.LastSummary.Cancelled);
            Assert.Empty(h.Destination.Writes);
        }

        // ------------------------------------------------------------------------------------
        // 7. cycles and backfill
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Account_contact_cycle_creates_both_records_and_backfills_the_deferred_lookup_once()
        {
            var h = new Harness();
            SeedCycle(h.Source);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("contact", ContactC), ("account", AccountA) },
                h.Destination.Creates.Select(c => (c.Target.LogicalName, c.Target.Id)));
            Assert.False(h.Destination.Creates[0].Target.Contains("parentcustomerid"));
            Assert.Equal(ContactC, ((EntityReference)h.Destination.Creates[1].Target["primarycontactid"]).Id);
            UpdateRequest backfill = Assert.Single(h.Destination.Updates);
            Assert.Equal(("contact", ContactC), (backfill.Target.LogicalName, backfill.Target.Id));
            Assert.Equal(new[] { "parentcustomerid" }, backfill.Target.Attributes.Keys);
            Assert.Equal(AccountA, ((EntityReference)h.Destination.Get("contact", ContactC)["parentcustomerid"]).Id);
            Assert.Equal(2, summary.Created);
            Assert.Equal(1, summary.LookupsBackfilled);
            Assert.Equal(0, summary.LookupsBlanked);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"    Deferred parentcustomerid on contact \"Jane Doe\": account {AccountA} is still being copied (circular reference), will backfill",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"Created account \"Contoso\" ({AccountA})",
                $"  Backfilled contact.parentcustomerid -> account ({AccountA})"
            }, h.Log.Lines);
        }

        [Fact]
        public void Cycle_backfill_is_dropped_and_counted_as_blanked_when_the_target_create_fails()
        {
            var h = new Harness();
            SeedCycle(h.Source);
            h.Destination.FailCreates.Add("account/" + AccountA);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Empty(h.Destination.Updates);
            Assert.True(h.Destination.Contains("contact", ContactC));
            Assert.Equal(1, summary.Created);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(1, summary.LookupsBlanked);
            Assert.Equal(0, summary.LookupsBackfilled);
            Assert.Contains($"  Backfill of contact.parentcustomerid -> account ({AccountA}) dropped: account could not be copied, lookup left blank",
                h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void Self_reference_is_backfilled_after_the_record_is_created()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Loop"), ("parentaccountid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.False(Assert.Single(h.Destination.Creates).Target.Contains("parentaccountid"));
            UpdateRequest backfill = Assert.Single(h.Destination.Updates);
            Assert.Equal(AccountA, ((EntityReference)backfill.Target["parentaccountid"]).Id);
            Assert.Equal(1, summary.LookupsBackfilled);
        }

        // ------------------------------------------------------------------------------------
        // 8. dry run
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Dry_run_issues_no_writes_but_produces_the_same_log_shape_and_counts()
        {
            var real = new Harness();
            SeedMixedScenario(real);
            CopySummary realSummary = real.Run("account", AccountA, AccountB);

            var dry = new Harness();
            dry.Options.DryRun = true;
            SeedMixedScenario(dry);
            CopySummary drySummary = dry.Run("account", AccountA, AccountB);

            // The real run exercised every kind of write...
            Assert.Equal(2, real.Destination.Creates.Count);
            Assert.Equal(4, real.Destination.Updates.Count);   // backfill, state (after the tree), reactivation, update
            // ...the dry run none of them (reads only).
            Assert.Empty(dry.Destination.Writes);
            Assert.All(dry.Destination.Executed, r => Assert.IsType<RetrieveMultipleRequest>(r));
            Assert.False(dry.Destination.Contains("account", AccountA));

            Assert.Equal(real.Log.Entries.Select(e => e.Level), dry.Log.Entries.Select(e => e.Level));
            Assert.Equal(real.Log.Lines, dry.Log.Lines.Select(AsRealRunLine));
            Assert.Contains(dry.Log.Lines, l => l.StartsWith("[DRY RUN] Would create account", StringComparison.Ordinal));
            Assert.Contains(dry.Log.Lines, l => l.TrimStart().StartsWith("[DRY RUN] Would backfill contact.parentcustomerid", StringComparison.Ordinal));
            Assert.Contains($"[DRY RUN] Would apply state to account \"Contoso\" ({AccountA}): statecode=1, statuscode=2", dry.Log.Lines);
            Assert.Contains(dry.Log.Lines, l => l.TrimStart().StartsWith("[DRY RUN] Would reactivate account", StringComparison.Ordinal));
            Assert.Contains(dry.Log.Lines, l => l.StartsWith("[DRY RUN] Would update account", StringComparison.Ordinal));

            Assert.True(drySummary.DryRun);
            Assert.False(realSummary.DryRun);
            Assert.Equal(
                (realSummary.Created, realSummary.Updated, realSummary.SkippedExisting, realSummary.Failed, realSummary.LookupsBlanked, realSummary.LookupsBackfilled, realSummary.StateChanges),
                (drySummary.Created, drySummary.Updated, drySummary.SkippedExisting, drySummary.Failed, drySummary.LookupsBlanked, drySummary.LookupsBackfilled, drySummary.StateChanges));
            Assert.Equal((2, 1, 1, 1, 1), (drySummary.Created, drySummary.Updated, drySummary.LookupsBackfilled, drySummary.StateChanges, drySummary.LookupsBlanked));
        }

        // ------------------------------------------------------------------------------------
        // 9. attribute missing in destination: skipped and warned once
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Attribute_missing_in_destination_is_skipped_and_warned_once_per_run()
        {
            var h = new Harness();
            h.SourceSchema.Edit("contact").String("new_extra");
            h.DestinationSchema = h.SourceSchema.Clone().RemoveAttribute("contact", "new_extra");
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane"), ("lastname", "Doe"), ("new_extra", "x")));
            h.Source.Add(Record("contact", ContactD, ("fullname", "John"), ("lastname", "Roe"), ("new_extra", "y")));

            CopySummary summary = h.Run("contact", ContactC, ContactD);

            Assert.Equal(2, summary.Created);
            Assert.All(h.Destination.Creates, c => Assert.False(c.Target.Contains("new_extra")));
            Assert.All(h.Destination.Creates, c => Assert.True(c.Target.Contains("lastname")));
            Assert.Equal("  Skipped attribute new_extra on contact: not present in destination", Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        // ------------------------------------------------------------------------------------
        // 10. PreserveCreatedOn
        // ------------------------------------------------------------------------------------

        [Fact]
        public void PreserveCreatedOn_maps_createdon_to_overriddencreatedon_on_create()
        {
            var h = new Harness();
            h.Options.PreserveCreatedOn = true;
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane"), ("lastname", "Doe"), ("createdon", SourceCreatedOn), ("overriddencreatedon", DateTime.UtcNow)));

            h.Run("contact", ContactC);

            CreateRequest create = Assert.Single(h.Destination.Creates);
            Assert.Equal(SourceCreatedOn, create.Target["overriddencreatedon"]);
            Assert.False(create.Target.Contains("createdon"));
        }

        [Fact]
        public void Without_PreserveCreatedOn_neither_createdon_nor_overriddencreatedon_is_written()
        {
            var h = new Harness();
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane"), ("lastname", "Doe"), ("createdon", SourceCreatedOn)));

            h.Run("contact", ContactC);

            CreateRequest create = Assert.Single(h.Destination.Creates);
            Assert.False(create.Target.Contains("overriddencreatedon"));
            Assert.False(create.Target.Contains("createdon"));
        }

        [Fact]
        public void PreserveCreatedOn_is_never_sent_on_update()
        {
            var h = new Harness();
            h.Options.PreserveCreatedOn = true;
            h.Destination.Add(Record("contact", ContactC, ("lastname", "Old")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane"), ("lastname", "Doe"), ("createdon", SourceCreatedOn)));

            h.Run("contact", ContactC);

            UpdateRequest update = Assert.Single(h.Destination.Updates);
            Assert.False(update.Target.Contains("overriddencreatedon"));
            Assert.False(update.Target.Contains("createdon"));
            Assert.Equal("Doe", update.Target["lastname"]);
        }

        [Fact]
        public void Create_is_retried_once_without_overriddencreatedon_when_it_fails()
        {
            var h = new Harness();
            h.Options.PreserveCreatedOn = true;
            h.Destination.FailOverriddenCreatedOn = true;
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe"), ("createdon", SourceCreatedOn)));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Collection(h.Destination.Creates,
                first => Assert.Equal(SourceCreatedOn, first.Target["overriddencreatedon"]),
                retry =>
                {
                    Assert.False(retry.Target.Contains("overriddencreatedon"));
                    Assert.Equal("Doe", retry.Target["lastname"]);
                    Assert.Equal(ContactC, retry.Target.Id);
                });
            Assert.True(h.Destination.Contains("contact", ContactC));
            Assert.Equal(1, summary.Created);
            Assert.Equal(0, summary.Failed);
            Assert.Empty(summary.Errors);
            Assert.Equal(
                $"  contact \"Jane Doe\" ({ContactC}) created without overriddencreatedon: Principal user is missing prvOverrideCreatedOnCreatedBy privilege",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void Create_that_also_fails_without_overriddencreatedon_is_a_failure()
        {
            var h = new Harness();
            h.Options.PreserveCreatedOn = true;
            h.Destination.FailCreates.Add("contact");
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("createdon", SourceCreatedOn)));

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Equal(2, h.Destination.Creates.Count);
            Assert.Equal(0, summary.Created);
            Assert.Equal(1, summary.Failed);
            Assert.StartsWith($"FAILED to create contact \"Jane Doe\" ({ContactC})", Assert.Single(summary.Errors));
        }

        // ------------------------------------------------------------------------------------
        // 11. request parameters
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Every_create_and_update_carries_SuppressDuplicateDetection_and_the_bypass_flag_when_set(bool bypass)
        {
            var h = new Harness();
            h.Options.BypassCustomPluginExecution = bypass;
            SeedMixedScenario(h);   // creates, a state change, a backfill, a reactivation and an update

            h.Run("account", AccountA, AccountB);

            Assert.Equal(6, h.Destination.Writes.Count);
            Assert.All(h.Destination.Writes, request =>
            {
                Assert.True((bool)request.Parameters["SuppressDuplicateDetection"]);
                if (bypass) Assert.True((bool)request.Parameters["BypassCustomPluginExecution"]);
                else Assert.False(request.Parameters.ContainsKey("BypassCustomPluginExecution"));
            });
        }

        // ------------------------------------------------------------------------------------
        // 12. state / status
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0, 1, false)]   // active with the default status: nothing to apply
        [InlineData(1, 2, true)]    // inactive
        [InlineData(0, 5, true)]    // active but with a non-default status
        public void State_is_applied_after_create_only_when_it_differs_from_the_default(int state, int status, bool expectStateChange)
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(state)), ("statuscode", Opt(status))));

            CopySummary summary = h.Run("account", AccountA);

            CreateRequest create = Assert.Single(h.Destination.Creates);
            Assert.False(create.Target.Contains("statecode"));
            Assert.False(create.Target.Contains("statuscode"));
            if (expectStateChange)
            {
                UpdateRequest update = Assert.Single(h.Destination.Updates);
                Assert.Equal(new[] { "statecode", "statuscode" }, update.Target.Attributes.Keys.OrderBy(k => k).ToArray());
                Assert.Equal(state, ((OptionSetValue)update.Target["statecode"]).Value);
                Assert.Equal(status, ((OptionSetValue)update.Target["statuscode"]).Value);
                Assert.Equal(1, summary.StateChanges);
                // Applied once the selected record's tree is done: the line names the record, at its depth.
                Assert.Equal($"State applied to account \"Contoso\" ({AccountA}): statecode={state}, statuscode={status}", h.Log.Lines.Last());
            }
            else
            {
                Assert.Empty(h.Destination.Updates);
                Assert.Equal(0, summary.StateChanges);
            }
        }

        [Fact]
        public void State_is_applied_after_an_update_when_the_source_is_inactive()
        {
            var h = new Harness();
            h.Destination.Add(Record("account", AccountA, ("name", "Old"), ("statecode", Opt(0)), ("statuscode", Opt(1))));
            h.Source.Add(Record("account", AccountA, ("name", "New"), ("statecode", Opt(1)), ("statuscode", Opt(2))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Collection(h.Destination.Updates,
                update => Assert.Equal("New", update.Target["name"]),
                state => Assert.Equal(1, ((OptionSetValue)state.Target["statecode"]).Value));
            Assert.Equal(1, summary.Updated);
            Assert.Equal(1, summary.StateChanges);
        }

        [Fact]
        public void State_that_cannot_be_applied_is_a_warning_not_a_failure()
        {
            var h = new Harness();
            h.Destination.FailStateChanges = true;
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(1)), ("statuscode", Opt(2))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.True(h.Destination.Contains("account", AccountA));
            Assert.Equal(1, summary.Created);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(0, summary.StateChanges);
            Assert.Empty(summary.Errors);
            Assert.StartsWith($"Created account \"Contoso\" ({AccountA}) but state not applied: Simulated state change failure",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        // ------------------------------------------------------------------------------------
        // 13. party lists
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Party_lists_are_rebuilt_and_parties_whose_target_is_unavailable_are_dropped()
        {
            var h = new Harness();
            h.Destination.Add(new Entity("systemuser", User1));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe")));   // ContactD is not in the source
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"),
                ("to", Parties(
                    Party(Ref("contact", ContactC, "Jane Doe"), 2, "jane@example.com"),
                    Party(Ref("contact", ContactD, "Gone"), 2),
                    Party(null, 2, "someone@example.com"))),
                ("from", Parties(Party(Ref("systemuser", User1), 1))),
                ("cc", Parties(Party(Ref("systemuser", User2), 3)))));

            CopySummary summary = h.Run("email", EmailE);

            Assert.Equal(new[] { ("contact", ContactC), ("email", EmailE) },
                h.Destination.Creates.Select(c => (c.Target.LogicalName, c.Target.Id)));
            Entity email = h.Destination.Creates[1].Target;
            var to = (EntityCollection)email["to"];
            Assert.Collection(to.Entities,
                party =>
                {
                    Assert.Equal(new[] { "addressused", "participationtypemask", "partyid" }, party.Attributes.Keys.OrderBy(k => k).ToArray());
                    Assert.Equal(ContactC, ((EntityReference)party["partyid"]).Id);
                    Assert.Equal(2, ((OptionSetValue)party["participationtypemask"]).Value);
                    Assert.Equal("jane@example.com", party["addressused"]);
                },
                party =>
                {
                    Assert.Equal(new[] { "addressused", "participationtypemask" }, party.Attributes.Keys.OrderBy(k => k).ToArray());
                    Assert.Equal("someone@example.com", party["addressused"]);
                });
            Assert.All(to.Entities, party => Assert.Equal("activityparty", party.LogicalName));
            var from = (EntityCollection)email["from"];
            Assert.Equal(User1, ((EntityReference)Assert.Single(from.Entities)["partyid"]).Id);
            Assert.False(email.Contains("cc"));   // its only party was dropped
            Assert.Equal(2, summary.Created);
            Assert.Equal(2, summary.LookupsBlanked);
            Assert.Equal(new[]
            {
                $"  contact {ContactD} not found in source",
                $"  Dropped party contact {ContactD} from to on email \"Hello\": could not be copied",
                $"  Dropped party systemuser {User2} from cc on email \"Hello\": does not exist in destination"
            }, h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void Party_without_an_entity_name_is_dropped_with_a_warning_and_its_address_is_kept()
        {
            var h = new Harness();
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"),
                ("to", Parties(
                    Party(new EntityReference { Id = ContactC }, 2, "jane@example.com"),
                    Party(new EntityReference { Id = ContactD }, 2)))));

            CopySummary summary = h.Run("email", EmailE);

            Entity email = Assert.Single(h.Destination.Creates).Target;
            Entity party = Assert.Single(((EntityCollection)email["to"]).Entities);
            Assert.Equal(new[] { "addressused", "participationtypemask" }, party.Attributes.Keys.OrderBy(k => k).ToArray());
            Assert.Equal("jane@example.com", party["addressused"]);
            Assert.Equal(2, summary.LookupsBlanked);
            Assert.Equal(new[]
            {
                $"  Dropped party {ContactC} from to on email \"Hello\": the party reference has no entity name, its address jane@example.com is kept",
                $"  Dropped party {ContactD} from to on email \"Hello\": the party reference has no entity name"
            }, h.Log.Messages(LogLevel.Warning));
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "contact");
        }

        // ------------------------------------------------------------------------------------
        // 14. cancellation
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Cancellation_stops_the_run_and_the_last_summary_says_cancelled()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta")));
            using var cts = new CancellationTokenSource();
            h.Destination.BeforeExecute = request => { if (request is CreateRequest) cts.Cancel(); };

            Assert.Throws<OperationCanceledException>(() => h.Run("account", cts.Token, AccountA, AccountB));

            CopySummary summary = h.Engine.LastSummary;
            Assert.True(summary.Cancelled);
            Assert.Equal(1, summary.Created);
            Assert.Equal(2, summary.SelectedTotal);
            Assert.Equal(AccountA, Assert.Single(h.Destination.Creates).Target.Id);
        }

        [Fact]
        public void Cancellation_is_checked_inside_the_recursion_before_each_create()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            using var cts = new CancellationTokenSource();
            h.Destination.BeforeExecute = request => { if (request is CreateRequest) cts.Cancel(); };

            Assert.Throws<OperationCanceledException>(() => h.Run("contact", cts.Token, ContactC));

            Assert.Equal("account", Assert.Single(h.Destination.Creates).Target.LogicalName);
            Assert.True(h.Engine.LastSummary.Cancelled);
            Assert.Equal(1, h.Engine.LastSummary.Created);
        }

        [Fact]
        public void Already_cancelled_token_throws_before_any_request()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => h.Run("account", cts.Token, AccountA));

            Assert.Empty(h.Source.Executed);
            Assert.Empty(h.Destination.Executed);
            Assert.True(h.Engine.LastSummary.Cancelled);
        }

        [Fact]
        public void Pending_backfills_are_reported_as_blanked_when_the_run_is_cancelled()
        {
            var h = new Harness();
            SeedCycle(h.Source);
            using var cts = new CancellationTokenSource();
            h.Destination.BeforeExecute = request => { if (request is CreateRequest) cts.Cancel(); };   // at the contact create

            Assert.Throws<OperationCanceledException>(() => h.Run("account", cts.Token, AccountA));

            Assert.Equal("contact", Assert.Single(h.Destination.Creates).Target.LogicalName);
            Assert.Equal(1, h.Engine.LastSummary.LookupsBlanked);
            Assert.Contains(h.Log.Messages(LogLevel.Warning), m => m.Contains("not applied: run cancelled, lookup left blank"));
        }

        // ------------------------------------------------------------------------------------
        // progress
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Progress_is_reported_after_every_write_and_every_selected_record()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountX, ("name", "Parent")));
            h.Source.Add(Record("account", AccountA, ("name", "Alpha"), ("parentaccountid", Ref("account", AccountX))));
            h.Source.Add(Record("account", AccountB, ("name", "Beta")));

            h.Run("account", AccountA, AccountB);

            // parent create, Alpha create, Alpha done, Beta create, Beta done
            Assert.Equal(new[] { (1, 1), (1, 2), (1, 2), (2, 3), (2, 3) }, h.Progress.Select(p => (p.SelectedIndex, p.Created)));
            Assert.All(h.Progress, p => Assert.Equal(2, p.SelectedTotal));
            Assert.Equal("account \"Beta\"", h.Progress.Last().CurrentRecord);
        }

        // ------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Record_not_found_detection_uses_the_fault_code_or_message()
        {
            Assert.True(CopyEngine.IsRecordNotFound(FakeOrganizationService.Fault(FakeOrganizationService.ObjectDoesNotExist, "whatever")));
            Assert.True(CopyEngine.IsRecordNotFound(FakeOrganizationService.Fault(0, "account With Id = x Does Not Exist")));
            Assert.False(CopyEngine.IsRecordNotFound(FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Principal user is missing prvReadAccount privilege")));
            Assert.False(CopyEngine.IsRecordNotFound(new InvalidOperationException("Does Not Exist")));
        }

        private static void SeedCycle(FakeOrganizationService source)
        {
            source.Add(Record("account", AccountA, ("name", "Contoso"), ("primarycontactid", Ref("contact", ContactC, "Jane Doe"))));
            source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA, "Contoso"))));
        }

        /// <summary>
        /// Account A (inactive, owned by an existing user) <-> contact C cycle, C pointing at a missing
        /// user; account B exists inactive in the destination (reactivate + update).
        /// </summary>
        private static void SeedMixedScenario(Harness h)
        {
            h.Destination.Add(new Entity("systemuser", User1));
            h.Destination.Add(Record("account", AccountB, ("name", "Beta (old)"), ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("ownerid", Ref("systemuser", User1)),
                ("primarycontactid", Ref("contact", ContactC)), ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA)),
                ("preferredsystemuserid", Ref("systemuser", User2))));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("numberofemployees", 5), ("statecode", Opt(0)), ("statuscode", Opt(1))));
        }

        private static readonly (string Dry, string Real)[] DryRunLineMappings =
        {
            ("[DRY RUN] Would create ", "Created "),
            ("[DRY RUN] Would update ", "Updated "),
            ("[DRY RUN] Would reactivate ", "Reactivated "),
            ("[DRY RUN] Would apply state to ", "State applied to "),
            ("[DRY RUN] Would backfill ", "Backfilled "),
            ("[DRY RUN] Would keep ", "Kept ")
        };

        /// <summary>Turns a dry-run log line into the line the real run writes.</summary>
        private static string AsRealRunLine(string line)
        {
            string text = line.TrimStart(' ');
            string indent = line.Substring(0, line.Length - text.Length);
            foreach ((string dry, string real) in DryRunLineMappings)
            {
                if (text.StartsWith(dry, StringComparison.Ordinal)) return indent + real + text.Substring(dry.Length);
            }
            return line;
        }
    }
}
