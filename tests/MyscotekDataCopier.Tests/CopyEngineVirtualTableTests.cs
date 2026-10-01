using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Tests.Fakes;
using Xunit;
using static MyscotekDataCopier.Tests.Fakes.TestData;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// SPEC 5.8: lookups to virtual tables (rows in an external data source, served by a virtual
    /// table data provider) and lookups whose target cannot be checked.
    /// </summary>
    public class CopyEngineVirtualTableTests
    {
        private static readonly Guid AccountA = new Guid("a0000000-0000-0000-0000-00000000000a");
        private static readonly Guid AccountB = new Guid("b0000000-0000-0000-0000-00000000000b");
        private static readonly Guid EmailE = new Guid("e0000000-0000-0000-0000-00000000000e");
        private static readonly Guid User1 = new Guid("10000000-0000-0000-0000-000000000001");
        private static readonly Guid Vat20 = new Guid("7a700000-0000-0000-0000-000000000020");
        private static readonly Guid VatGone = new Guid("7a700000-0000-0000-0000-0000000000ff");
        private static readonly Guid TaxCodeT = new Guid("5c000000-0000-0000-0000-0000000000c5");
        private static readonly DateTime SourceCreatedOn = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        /// <summary>What a failing virtual table data provider answers to ANY query on its table (even an unfiltered top 1).</summary>
        private const string ProviderQueryError =
            "Error calling into the virtual table data provider to get new_vatrate items for query expression: Bad Request\r\nA task was canceled.";

        /// <summary>A provider that cannot answer a Retrieve either (the one-line form is what the log shows).</summary>
        private const string ProviderRetrieveError = "Error calling into the virtual table data provider to get new_vatrate item: Bad Request\r\nA task was canceled.";
        private const string ProviderRetrieveErrorLine = "Error calling into the virtual table data provider to get new_vatrate item: Bad Request A task was canceled.";

        /// <summary>
        /// account.new_vatrateid -> new_vatrate, a virtual table whose data provider fails every
        /// RetrieveMultiple while a Retrieve by id works (as some providers do).
        /// </summary>
        private static Harness NewHarness()
        {
            var h = new Harness();
            h.SourceSchema.Entity("new_vatrate", "new_name").VirtualTable().Decimal("new_rate");
            h.SourceSchema.Edit("account").Lookup("new_vatrateid", "new_vatrate");
            h.Source.FailRetrieveMultiple["new_vatrate"] = Fault(ProviderQueryError);
            h.Destination.FailRetrieveMultiple["new_vatrate"] = Fault(ProviderQueryError);
            return h;
        }

        private static FaultException<OrganizationServiceFault> Fault(string message) =>
            FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, message);

        // ------------------------------------------------------------------------------------
        // virtual lookup targets: checked by Retrieve, never read from the source, never created
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Virtual_lookup_target_is_checked_with_Retrieve_and_kept_without_reading_the_source_or_creating_it()
        {
            var h = NewHarness();
            h.Destination.Add(Record("new_vatrate", Vat20, ("new_name", "20.0"), ("new_rate", 20m)));
            h.Source.Add(Record("new_vatrate", Vat20, ("new_name", "20.0"), ("new_rate", 20m)));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("new_vatrateid", Ref("new_vatrate", Vat20, "20.0"))));

            CopySummary summary = h.Run("account", AccountA);

            CreateRequest create = Assert.Single(h.Destination.Creates);   // the account only
            Assert.Equal(("account", AccountA), (create.Target.LogicalName, create.Target.Id));
            var vat = (EntityReference)create.Target["new_vatrateid"];
            Assert.Equal(("new_vatrate", Vat20), (vat.LogicalName, vat.Id));

            RetrieveRequest check = Assert.Single(h.Destination.Retrieves);
            Assert.Equal(("new_vatrate", Vat20), (check.Target.LogicalName, check.Target.Id));
            Assert.Equal(new[] { "new_vatrateid", "new_name" }, check.ColumnSet.Columns);
            Assert.DoesNotContain(h.Destination.Queries, q => q.EntityName == "new_vatrate");   // queries fail on virtual tables
            Assert.DoesNotContain(h.Source.Executed, r => r is RetrieveRequest retrieve && retrieve.Target.LogicalName == "new_vatrate");

            Assert.Equal((1, 0, 0), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})"
            }, h.Log.Lines);   // a resolved lookup is not logged
        }

        [Fact]
        public void Virtual_lookup_target_missing_in_destination_is_blanked_with_the_virtual_wording_and_checked_once()
        {
            var h = NewHarness();
            h.Source.Add(Record("new_vatrate", VatGone, ("new_name", "17.5")));
            h.Source.Add(Record("account", AccountA, ("name", "Alpha"), ("new_vatrateid", Ref("new_vatrate", VatGone))));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("new_vatrateid", Ref("new_vatrate", VatGone))));

            CopySummary summary = h.Run("account", AccountA, AccountB);

            Assert.Equal(new[] { AccountA, AccountB }, h.Destination.Creates.Select(c => c.Target.Id));
            Assert.All(h.Destination.Creates, c => Assert.False(c.Target.Contains("new_vatrateid")));
            Assert.Single(h.Destination.Retrieves);   // the answer is cached for the run
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "new_vatrate");
            Assert.Equal((2, 0, 2), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Equal(new[]
            {
                $"  Blanked new_vatrateid on account \"Alpha\": new_vatrate {VatGone} does not exist in destination (virtual table, never created)",
                $"  Blanked new_vatrateid on account \"Beta\": new_vatrate {VatGone} does not exist in destination (virtual table, never created)"
            }, h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void Virtual_table_the_destination_does_not_have_blanks_the_lookup_without_any_request()
        {
            var h = new Harness();
            h.SourceSchema.Entity("new_vatrate", "new_name").VirtualTable();
            h.SourceSchema.Edit("email").Lookup("regardingobjectid", "account", "contact", "new_vatrate");
            h.DestinationSchema = h.SourceSchema.Clone().RemoveEntity("new_vatrate");
            h.Source.Add(Record("new_vatrate", Vat20, ("new_name", "20.0")));
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"), ("regardingobjectid", Ref("new_vatrate", Vat20))));

            CopySummary summary = h.Run("email", EmailE);

            Assert.False(Assert.Single(h.Destination.Creates).Target.Contains("regardingobjectid"));
            Assert.Empty(h.Destination.Retrieves);
            Assert.DoesNotContain(h.Destination.Queries, q => q.EntityName == "new_vatrate");
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "new_vatrate");
            Assert.Equal((1, 1), (summary.Created, summary.LookupsBlanked));
            Assert.Equal(
                $"  Blanked regardingobjectid on email \"Hello\": new_vatrate {Vat20} does not exist in destination (virtual table, the destination has no such table)",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void A_table_virtual_only_in_the_source_is_copied_when_the_destination_has_it_as_a_standard_table()
        {
            var h = NewHarness();
            h.DestinationSchema = h.SourceSchema.Clone();
            h.DestinationSchema.Edit("new_vatrate").VirtualTable(false);   // the destination schema decides
            h.Source.FailRetrieveMultiple.Clear();
            h.Destination.FailRetrieveMultiple.Clear();
            h.Source.Add(Record("new_vatrate", Vat20, ("new_name", "20.0"), ("new_rate", 20m)));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("new_vatrateid", Ref("new_vatrate", Vat20))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("new_vatrate", Vat20), ("account", AccountA) }, h.Destination.Creates.Select(c => (c.Target.LogicalName, c.Target.Id)));
            Assert.Equal(2, summary.Created);
        }

        // ------------------------------------------------------------------------------------
        // unverifiable lookups: kept, warned, cached; retried without them if the write fails
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Unverifiable_virtual_lookup_is_kept_with_a_warning_and_its_failed_check_is_cached_for_the_run()
        {
            var h = NewHarness();
            h.Destination.FailRetrieves["new_vatrate"] = Fault(ProviderRetrieveError);
            h.Source.Add(Record("account", AccountA, ("name", "Alpha"), ("new_vatrateid", Ref("new_vatrate", Vat20))));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("new_vatrateid", Ref("new_vatrate", Vat20))));

            CopySummary summary = h.Run("account", AccountA, AccountB);

            Assert.Equal(2, h.Destination.Creates.Count);
            Assert.All(h.Destination.Creates, c => Assert.Equal(Vat20, ((EntityReference)c.Target["new_vatrateid"]).Id));
            Assert.Single(h.Destination.Retrieves, r => r.Target.LogicalName == "new_vatrate");   // a broken provider is asked once per id
            Assert.Equal((2, 0, 0), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Empty(summary.Errors);
            Assert.Equal(new[]
            {
                $"  Kept new_vatrateid on account \"Alpha\" unverified: new_vatrate {Vat20} could not be checked in destination: {ProviderRetrieveErrorLine}",
                $"  Kept new_vatrateid on account \"Beta\" unverified (check failed earlier): new_vatrate {Vat20}"
            }, h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void A_not_found_fault_from_the_check_is_an_answer_so_the_lookup_is_blanked_not_kept()
        {
            var h = new Harness();
            h.Destination.FailRetrieveMultiple["systemuser"] =
                FakeOrganizationService.Fault(FakeOrganizationService.ObjectDoesNotExist, $"systemuser With Id = {User1} Does Not Exist");
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"), ("ownerid", Ref("systemuser", User1))));

            CopySummary summary = h.Run("email", EmailE);

            Assert.False(Assert.Single(h.Destination.Creates).Target.Contains("ownerid"));
            Assert.Equal((1, 1), (summary.Created, summary.LookupsBlanked));
            Assert.Equal($"  Blanked ownerid on email \"Hello\": systemuser {User1} does not exist in destination",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void A_create_that_fails_with_an_unverified_lookup_is_retried_once_without_it()
        {
            var h = NewHarness();
            h.Destination.FailRetrieves["new_vatrate"] = Fault(ProviderRetrieveError);
            h.Destination.BeforeExecute = request =>
            {
                if (request is CreateRequest create && create.Target.Contains("new_vatrateid")) throw Fault("Invalid VAT rate");
            };
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("accountnumber", "AC-1"), ("new_vatrateid", Ref("new_vatrate", Vat20))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Collection(h.Destination.Creates,
                first => Assert.Equal(Vat20, ((EntityReference)first.Target["new_vatrateid"]).Id),
                retry =>
                {
                    Assert.Equal(new[] { "accountnumber", "name" }, retry.Target.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
                    Assert.Equal(AccountA, retry.Target.Id);
                    Assert.True((bool)retry.Parameters["SuppressDuplicateDetection"]);
                });
            Assert.False(h.Destination.Get("account", AccountA).Contains("new_vatrateid"));
            Assert.Equal((1, 0, 1), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Empty(summary.Errors);
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"  Kept new_vatrateid on account \"Contoso\" unverified: new_vatrate {Vat20} could not be checked in destination: {ProviderRetrieveErrorLine}",
                "  Create failed, retrying without the unverified lookup new_vatrateid: Invalid VAT rate",
                "  Blanked new_vatrateid on account \"Contoso\": create failed with the unverified lookup (Invalid VAT rate)",
                $"Created account \"Contoso\" ({AccountA})"
            }, h.Log.Lines);
            Assert.Equal(
                new[] { LogLevel.Info, LogLevel.Warning, LogLevel.Info, LogLevel.Warning, LogLevel.Success },
                h.Log.Entries.Select(e => e.Level));
        }

        [Fact]
        public void Unverified_lookups_and_overriddencreatedon_are_left_out_in_turn_then_together()
        {
            var h = NewHarness();
            h.Options.PreserveCreatedOn = true;
            h.Destination.FailOverriddenCreatedOn = true;
            h.Destination.FailRetrieves["new_vatrate"] = Fault(ProviderRetrieveError);
            h.Destination.BeforeExecute = request =>
            {
                if (request is CreateRequest create && create.Target.Contains("new_vatrateid")) throw Fault("Invalid VAT rate");
            };
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("createdon", SourceCreatedOn), ("new_vatrateid", Ref("new_vatrate", Vat20))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[]
            {
                "name new_vatrateid overriddencreatedon",   // everything: the lookup fails it
                "name overriddencreatedon",                 // without the lookup: the override privilege is missing
                "name new_vatrateid",                       // without overriddencreatedon: the lookup again
                "name"                                      // without both: created
            }, h.Destination.Creates.Select(c => string.Join(" ", c.Target.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal))));
            Assert.True(h.Destination.Contains("account", AccountA));
            Assert.Equal((1, 0, 1), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Equal(new[]
            {
                "  Create failed, retrying without the unverified lookup new_vatrateid: Invalid VAT rate",
                "  Create failed, retrying without overriddencreatedon: Principal user is missing prvOverrideCreatedOnCreatedBy privilege",
                "  Create failed, retrying without the unverified lookup new_vatrateid and overriddencreatedon: Invalid VAT rate"
            }, h.Log.Messages(LogLevel.Info).Where(l => l.Contains("retrying")));
            // Each warning carries the error of an attempt that still had what it reports as left out.
            Assert.Equal(new[]
            {
                $"  Kept new_vatrateid on account \"Contoso\" unverified: new_vatrate {Vat20} could not be checked in destination: {ProviderRetrieveErrorLine}",
                "  Blanked new_vatrateid on account \"Contoso\": create failed with the unverified lookup (Invalid VAT rate)",
                $"  account \"Contoso\" ({AccountA}) created without overriddencreatedon: Principal user is missing prvOverrideCreatedOnCreatedBy privilege"
            }, h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void When_the_retry_without_the_unverified_lookup_fails_too_the_record_fails_with_the_last_error()
        {
            var h = NewHarness();
            h.Destination.FailRetrieves["new_vatrate"] = Fault(ProviderRetrieveError);
            h.Destination.FailCreates.Add("account");
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("new_vatrateid", Ref("new_vatrate", Vat20))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(2, h.Destination.Creates.Count);
            Assert.False(h.Destination.Creates[1].Target.Contains("new_vatrateid"));
            Assert.Equal((0, 1, 0), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Equal($"FAILED to create account \"Contoso\" ({AccountA}): Simulated create failure for account {AccountA}", Assert.Single(summary.Errors));
            Assert.Contains($"  Create failed, retrying without the unverified lookup new_vatrateid: Simulated create failure for account {AccountA}",
                h.Log.Messages(LogLevel.Info));
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains("Blanked"));
        }

        [Fact]
        public void An_update_that_fails_with_an_unverified_lookup_is_retried_without_it()
        {
            var h = NewHarness();
            h.Destination.FailRetrieves["new_vatrate"] = Fault(ProviderRetrieveError);
            h.Destination.Add(Record("account", AccountA, ("name", "Old")));
            h.Destination.BeforeExecute = request =>
            {
                if (request is UpdateRequest update && update.Target.Contains("new_vatrateid")) throw Fault("Invalid VAT rate");
            };
            h.Source.Add(Record("account", AccountA, ("name", "New"), ("new_vatrateid", Ref("new_vatrate", Vat20))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Collection(h.Destination.Updates,
                first => Assert.True(first.Target.Contains("new_vatrateid")),
                retry => Assert.Equal(new[] { "name" }, retry.Target.Attributes.Keys));
            Assert.Equal("New", h.Destination.Get("account", AccountA)["name"]);
            Assert.Equal((1, 0, 1), (summary.Updated, summary.Failed, summary.LookupsBlanked));
            Assert.Contains("  Update failed, retrying without the unverified lookup new_vatrateid: Invalid VAT rate", h.Log.Messages(LogLevel.Info));
            Assert.Contains("  Blanked new_vatrateid on account \"New\": update failed with the unverified lookup (Invalid VAT rate)", h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void A_party_list_with_an_unverified_party_is_left_out_as_a_whole_when_the_create_fails_with_it()
        {
            var h = new Harness();
            h.Destination.FailRetrieveMultiple["systemuser"] = Fault("Principal user is missing prvReadUser privilege");
            h.Destination.BeforeExecute = request =>
            {
                if (request is CreateRequest create && create.Target.Contains("to")) throw Fault("The party systemuser could not be resolved");
            };
            h.Source.Add(Record("email", EmailE, ("subject", "Hello"), ("description", "Body"),
                ("to", Parties(Party(Ref("systemuser", User1), 2), Party(null, 2, "someone@example.com")))));

            CopySummary summary = h.Run("email", EmailE);

            Assert.Collection(h.Destination.Creates,
                first => Assert.Equal(2, ((EntityCollection)first.Target["to"]).Entities.Count),   // the unverified party was kept
                retry =>
                {
                    Assert.False(retry.Target.Contains("to"));
                    Assert.Equal("Body", retry.Target["description"]);
                });
            Assert.Equal((1, 0, 1), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.Equal(new[]
            {
                $"  Kept party systemuser {User1} in to on email \"Hello\" unverified: could not be checked in destination: Principal user is missing prvReadUser privilege",
                "  Blanked to on email \"Hello\": create failed with the unverified lookup (The party systemuser could not be resolved)"
            }, h.Log.Messages(LogLevel.Warning));
        }

        // ------------------------------------------------------------------------------------
        // dry run, selected virtual table
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Dry_run_checks_virtual_targets_writes_nothing_and_would_keep_the_unverified_lookups()
        {
            var h = NewHarness();
            h.Options.DryRun = true;
            h.SourceSchema.Entity("new_taxcode", "new_name").VirtualTable();
            h.SourceSchema.Edit("account").Lookup("new_taxcodeid", "new_taxcode");
            h.Destination.FailRetrieves["new_taxcode"] = Fault("Error calling into the virtual table data provider: Bad Request");
            h.Destination.Add(Record("new_vatrate", Vat20, ("new_name", "20.0")));
            h.Source.Add(Record("account", AccountA, ("name", "Alpha"),
                ("new_vatrateid", Ref("new_vatrate", Vat20)), ("new_taxcodeid", Ref("new_taxcode", TaxCodeT))));
            h.Source.Add(Record("account", AccountB, ("name", "Beta"), ("new_vatrateid", Ref("new_vatrate", VatGone))));

            CopySummary summary = h.Run("account", AccountA, AccountB);

            Assert.Empty(h.Destination.Writes);
            Assert.All(h.Destination.Executed, r => Assert.True(r is RetrieveRequest || r is RetrieveMultipleRequest, r.GetType().Name));
            Assert.Equal(new[] { "new_vatrate", "new_taxcode", "new_vatrate" }, h.Destination.Retrieves.Select(r => r.Target.LogicalName));
            Assert.Equal(new[]
            {
                $"Copying account \"Alpha\" ({AccountA})...",
                $"  [DRY RUN] Would keep new_taxcodeid on account \"Alpha\" unverified: new_taxcode {TaxCodeT} could not be checked in destination: Error calling into the virtual table data provider: Bad Request",
                $"[DRY RUN] Would create account \"Alpha\" ({AccountA})",
                $"Copying account \"Beta\" ({AccountB})...",
                $"  Blanked new_vatrateid on account \"Beta\": new_vatrate {VatGone} does not exist in destination (virtual table, never created)",
                $"[DRY RUN] Would create account \"Beta\" ({AccountB})"
            }, h.Log.Lines);
            Assert.Equal((2, 0, 1), (summary.Created, summary.Failed, summary.LookupsBlanked));
            Assert.True(summary.DryRun);
        }

        [Theory]
        [InlineData("virtual in both")]
        [InlineData("virtual in the destination only")]
        [InlineData("virtual in the source, missing in the destination")]
        public void Selected_virtual_table_is_refused_up_front(string setup)
        {
            var h = new Harness();
            h.SourceSchema.Entity("new_vatrate", "new_name").VirtualTable(setup != "virtual in the destination only");
            h.DestinationSchema = h.SourceSchema.Clone();
            if (setup == "virtual in the destination only") h.DestinationSchema.Edit("new_vatrate").VirtualTable();
            if (setup.EndsWith("missing in the destination", StringComparison.Ordinal)) h.DestinationSchema.RemoveEntity("new_vatrate");
            h.Source.Add(Record("new_vatrate", Vat20, ("new_name", "20.0")));

            CopySummary summary = h.Run("new_vatrate", Vat20);

            const string Refusal = "new_vatrate is a virtual table: its rows live in an external data source and cannot be created here.";
            Assert.Empty(h.Source.Executed);
            Assert.Empty(h.Destination.Executed);
            Assert.Equal(Refusal, Assert.Single(summary.Errors));
            Assert.Equal((LogLevel.Error, Refusal), Assert.Single(h.Log.Entries));
            Assert.Equal((0, 0), (summary.Created, summary.Failed));
            Assert.Same(summary, h.Engine.LastSummary);
        }
    }
}
