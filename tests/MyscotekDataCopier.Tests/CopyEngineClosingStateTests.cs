using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Tests.Fakes;
using Xunit;
using static MyscotekDataCopier.Tests.Fakes.TestData;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// SPEC 5.9: state changes are applied once the selected record's whole tree is done (after every
    /// backfill into its records), with the close message the platform requires for a closing state.
    /// </summary>
    public class CopyEngineClosingStateTests
    {
        private static readonly Guid AccountA = new Guid("a0000000-0000-0000-0000-00000000000a");
        private static readonly Guid ContactC = new Guid("c0000000-0000-0000-0000-00000000000c");
        private static readonly Guid JobJ = new Guid("4d000000-0000-0000-0000-00000000004d");
        private static readonly Guid OpportunityO = new Guid("0b000000-0000-0000-0000-0000000000b0");
        private static readonly Guid Incident1 = new Guid("1c000000-0000-0000-0000-000000000001");
        private static readonly Guid Incident2 = new Guid("1c000000-0000-0000-0000-000000000002");
        private static readonly Guid Quote1 = new Guid("90000000-0000-0000-0000-000000000001");
        private static readonly Guid Quote2 = new Guid("90000000-0000-0000-0000-000000000002");
        private static readonly Guid Quote3 = new Guid("90000000-0000-0000-0000-000000000003");
        private static readonly Guid Order1 = new Guid("50000000-0000-0000-0000-000000000001");
        private static readonly Guid Order2 = new Guid("50000000-0000-0000-0000-000000000002");
        private static readonly Guid Order3 = new Guid("50000000-0000-0000-0000-000000000003");
        private static readonly DateTime ClosedOn = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The standard schema plus opportunity, incident, quote and salesorder (with their default
        /// status per state) and a custom job that points at an opportunity which points back at it.
        /// </summary>
        private static Harness NewHarness()
        {
            var h = new Harness();
            h.SourceSchema
                .Entity("opportunity", "name")
                    .Money("actualvalue").DateTime("actualclosedate").Money("estimatedvalue")
                    .Lookup("parentaccountid", "account").Lookup("new_jobid", "new_job")
                    .Owner().State(activeDefaultStatus: 1, inactiveDefaultStatus: 3).DefaultStatus(2, 4)
                .Entity("new_job", "new_name")
                    .Lookup("new_opportunityid", "opportunity")
                    .State()
                .Entity("incident", "title")
                    .Lookup("customerid", "account", "contact")
                    .State(activeDefaultStatus: 1, inactiveDefaultStatus: 5).DefaultStatus(2, 6)
                .Entity("quote", "name")
                    .State(activeDefaultStatus: 1, inactiveDefaultStatus: 2).DefaultStatus(2, 4).DefaultStatus(3, 5)
                .Entity("salesorder", "name")
                    .State(activeDefaultStatus: 1, inactiveDefaultStatus: 3).DefaultStatus(2, 4).DefaultStatus(3, 100001);
            return h;
        }

        // ------------------------------------------------------------------------------------
        // opportunity
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Won_opportunity_is_closed_with_WinOpportunity_after_the_backfill_into_it()
        {
            var h = NewHarness();
            h.Options.BypassCustomPluginExecution = true;
            h.Source.Add(Record("new_job", JobJ, ("new_name", "Job 1"), ("new_opportunityid", Ref("opportunity", OpportunityO))));
            h.Source.Add(Record("opportunity", OpportunityO, ("name", "Big deal"), ("new_jobid", Ref("new_job", JobJ)),
                ("actualvalue", new Money(1500m)), ("actualclosedate", ClosedOn), ("statecode", Opt(1)), ("statuscode", Opt(3))));

            CopySummary summary = h.Run("new_job", JobJ);

            // The backfill into the opportunity is written BEFORE WinOpportunity makes it read-only.
            Assert.Collection(h.Destination.Writes,
                r => Assert.Equal(("opportunity", OpportunityO), CreateKey(r)),
                r => Assert.Equal(("new_job", JobJ), CreateKey(r)),
                r =>
                {
                    var backfill = Assert.IsType<UpdateRequest>(r);
                    Assert.Equal(("opportunity", OpportunityO), (backfill.Target.LogicalName, backfill.Target.Id));
                    Assert.Equal(new[] { "new_jobid" }, backfill.Target.Attributes.Keys);
                },
                r =>
                {
                    var win = Assert.IsType<WinOpportunityRequest>(r);
                    Entity close = win.OpportunityClose;
                    Assert.Equal("opportunityclose", close.LogicalName);
                    Assert.Equal(new[] { "actualend", "actualrevenue", "opportunityid", "subject" }, close.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
                    var opportunity = (EntityReference)close["opportunityid"];
                    Assert.Equal(("opportunity", OpportunityO), (opportunity.LogicalName, opportunity.Id));
                    Assert.Equal("Closed by Data Copier", close["subject"]);
                    Assert.Equal(1500m, ((Money)close["actualrevenue"]).Value);   // the source's actual revenue (actualvalue)
                    Assert.Equal(ClosedOn, close["actualend"]);                    // the source's actualclosedate
                    Assert.Equal(3, win.Status.Value);
                    Assert.True((bool)win.Parameters["BypassCustomPluginExecution"]);
                    Assert.False(win.Parameters.ContainsKey("SuppressDuplicateDetection"));
                });

            List<OrganizationRequest> executed = h.Destination.Executed;
            int backfillAt = executed.FindIndex(r => r is UpdateRequest u && u.Target.Id == OpportunityO && u.Target.Contains("new_jobid"));
            int winAt = executed.FindIndex(r => r is WinOpportunityRequest);
            Assert.InRange(backfillAt, 0, winAt - 1);
            Assert.Equal(executed.Count - 1, winAt);   // WinOpportunity is the very last request

            Entity stored = h.Destination.Get("opportunity", OpportunityO);
            Assert.Equal((1, 3), StateOf(stored));
            Assert.Equal(JobJ, ((EntityReference)stored["new_jobid"]).Id);
            Assert.Equal((2, 1, 1, 0), (summary.Created, summary.LookupsBackfilled, summary.StateChanges, summary.Failed));
            Assert.Equal(new[]
            {
                $"Copying new_job \"Job 1\" ({JobJ})...",
                $"  Copying opportunity \"Big deal\" ({OpportunityO})...",
                $"    Deferred new_jobid on opportunity \"Big deal\": new_job {JobJ} is still being copied (circular reference), will backfill",
                $"  Created opportunity \"Big deal\" ({OpportunityO})",
                $"Created new_job \"Job 1\" ({JobJ})",
                $"  Backfilled opportunity.new_jobid -> new_job ({JobJ})",
                $"  State applied to opportunity \"Big deal\" ({OpportunityO}): statecode=1, statuscode=3 (WinOpportunity)"
            }, h.Log.Lines);
        }

        [Fact]
        public void Lost_opportunity_is_closed_with_LoseOpportunity()
        {
            var h = NewHarness();
            h.Source.Add(Record("opportunity", OpportunityO, ("name", "Lost deal"), ("actualclosedate", ClosedOn), ("statecode", Opt(2)), ("statuscode", Opt(5))));

            CopySummary summary = h.Run("opportunity", OpportunityO);

            var lose = Assert.IsType<LoseOpportunityRequest>(Assert.Single(h.Destination.CloseRequests));
            Assert.Equal("opportunityclose", lose.OpportunityClose.LogicalName);
            Assert.Equal(new[] { "actualend", "opportunityid", "subject" }, lose.OpportunityClose.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(5, lose.Status.Value);
            Assert.False(lose.Parameters.ContainsKey("BypassCustomPluginExecution"));   // the option is off
            Assert.DoesNotContain(h.Destination.Updates, u => u.Target.Contains("statecode"));
            Assert.Equal((2, 5), StateOf(h.Destination.Get("opportunity", OpportunityO)));
            Assert.Equal(1, summary.StateChanges);
            Assert.Equal($"State applied to opportunity \"Lost deal\" ({OpportunityO}): statecode=2, statuscode=5 (LoseOpportunity)", h.Log.Lines.Last());
        }

        [Fact]
        public void An_existing_won_opportunity_is_reopened_updated_then_won_again()
        {
            var h = NewHarness();
            h.Destination.Add(Record("opportunity", OpportunityO, ("name", "Old name"), ("statecode", Opt(1)), ("statuscode", Opt(3))));
            h.Source.Add(Record("opportunity", OpportunityO, ("name", "Big deal"), ("statecode", Opt(1)), ("statuscode", Opt(3))));

            CopySummary summary = h.Run("opportunity", OpportunityO);

            Assert.Collection(h.Destination.Writes,
                r => AssertStateUpdate(r, OpportunityO, 0, 1),                                  // reopened for the update
                r => Assert.Equal("Big deal", Assert.IsType<UpdateRequest>(r).Target["name"]),
                r => Assert.Equal(3, Assert.IsType<WinOpportunityRequest>(r).Status.Value));
            Assert.Equal((1, 3), StateOf(h.Destination.Get("opportunity", OpportunityO)));
            Assert.Equal((1, 1), (summary.Updated, summary.StateChanges));
            Assert.Equal($"State applied to opportunity \"Big deal\" ({OpportunityO}): statecode=1, statuscode=3 (WinOpportunity)", h.Log.Lines.Last());
        }

        // ------------------------------------------------------------------------------------
        // incident, quote, salesorder
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Resolved_incident_is_closed_with_CloseIncident_and_a_cancelled_one_with_a_plain_update()
        {
            var h = NewHarness();
            h.Source.Add(Record("incident", Incident1, ("title", "Printer on fire"), ("statecode", Opt(1)), ("statuscode", Opt(5))));
            h.Source.Add(Record("incident", Incident2, ("title", "Duplicate"), ("statecode", Opt(2)), ("statuscode", Opt(6))));

            CopySummary summary = h.Run("incident", Incident1, Incident2);

            Assert.Collection(h.Destination.Writes.Where(r => !(r is CreateRequest)),
                r => AssertClose<CloseIncidentRequest>(r, "IncidentResolution", "incidentresolution", "incidentid", "incident", Incident1, 5),
                r => AssertStateUpdate(r, Incident2, 2, 6));
            Assert.Equal((1, 5), StateOf(h.Destination.Get("incident", Incident1)));
            Assert.Equal((2, 6), StateOf(h.Destination.Get("incident", Incident2)));
            Assert.Equal(2, summary.StateChanges);
            Assert.Contains($"State applied to incident \"Printer on fire\" ({Incident1}): statecode=1, statuscode=5 (CloseIncident)", h.Log.Lines);
            Assert.Contains($"State applied to incident \"Duplicate\" ({Incident2}): statecode=2, statuscode=6", h.Log.Lines);
        }

        [Fact]
        public void Won_and_closed_quotes_are_activated_then_closed_with_WinQuote_and_CloseQuote()
        {
            var h = NewHarness();
            h.Source.Add(Record("quote", Quote1, ("name", "Q-1 won"), ("statecode", Opt(2)), ("statuscode", Opt(4))));
            h.Source.Add(Record("quote", Quote2, ("name", "Q-2 lost"), ("statecode", Opt(3)), ("statuscode", Opt(5))));
            h.Source.Add(Record("quote", Quote3, ("name", "Q-3 active"), ("statecode", Opt(1)), ("statuscode", Opt(3))));

            CopySummary summary = h.Run("quote", Quote1, Quote2, Quote3);

            Assert.Collection(h.Destination.Writes.Where(r => !(r is CreateRequest)),
                r => AssertStateUpdate(r, Quote1, 1, 2),                                                     // the draft is activated...
                r => AssertClose<WinQuoteRequest>(r, "QuoteClose", "quoteclose", "quoteid", "quote", Quote1, 4),   // ...then won
                r => AssertStateUpdate(r, Quote2, 1, 2),
                r => AssertClose<CloseQuoteRequest>(r, "QuoteClose", "quoteclose", "quoteid", "quote", Quote2, 5),
                r => AssertStateUpdate(r, Quote3, 1, 3));                                                    // active: a plain update
            Assert.Equal((2, 4), StateOf(h.Destination.Get("quote", Quote1)));
            Assert.Equal((3, 5), StateOf(h.Destination.Get("quote", Quote2)));
            Assert.Equal((1, 3), StateOf(h.Destination.Get("quote", Quote3)));
            Assert.Equal(3, summary.StateChanges);
            Assert.Contains($"State applied to quote \"Q-1 won\" ({Quote1}): statecode=2, statuscode=4 (activate + WinQuote)", h.Log.Lines);
            Assert.Contains($"State applied to quote \"Q-2 lost\" ({Quote2}): statecode=3, statuscode=5 (activate + CloseQuote)", h.Log.Lines);
            Assert.Contains($"State applied to quote \"Q-3 active\" ({Quote3}): statecode=1, statuscode=3", h.Log.Lines);
        }

        [Fact]
        public void Cancelled_and_fulfilled_orders_use_CancelSalesOrder_and_FulfillSalesOrder()
        {
            var h = NewHarness();
            h.Source.Add(Record("salesorder", Order1, ("name", "SO-1 cancelled"), ("statecode", Opt(2)), ("statuscode", Opt(4))));
            h.Source.Add(Record("salesorder", Order2, ("name", "SO-2 fulfilled"), ("statecode", Opt(3)), ("statuscode", Opt(100001))));
            h.Source.Add(Record("salesorder", Order3, ("name", "SO-3 submitted"), ("statecode", Opt(1)), ("statuscode", Opt(3))));

            CopySummary summary = h.Run("salesorder", Order1, Order2, Order3);

            Assert.Collection(h.Destination.Writes.Where(r => !(r is CreateRequest)),
                r => AssertClose<CancelSalesOrderRequest>(r, "OrderClose", "orderclose", "salesorderid", "salesorder", Order1, 4),
                r => AssertClose<FulfillSalesOrderRequest>(r, "OrderClose", "orderclose", "salesorderid", "salesorder", Order2, 100001),
                r => AssertStateUpdate(r, Order3, 1, 3));
            Assert.Equal((2, 4), StateOf(h.Destination.Get("salesorder", Order1)));
            Assert.Equal((3, 100001), StateOf(h.Destination.Get("salesorder", Order2)));
            Assert.Equal(3, summary.StateChanges);
            Assert.Contains($"State applied to salesorder \"SO-1 cancelled\" ({Order1}): statecode=2, statuscode=4 (CancelSalesOrder)", h.Log.Lines);
            Assert.Contains($"State applied to salesorder \"SO-2 fulfilled\" ({Order2}): statecode=3, statuscode=100001 (FulfillSalesOrder)", h.Log.Lines);
        }

        [Fact]
        public void The_fake_refuses_a_plain_update_into_a_closing_state_like_the_platform()
        {
            var service = new FakeOrganizationService();
            service.Add(new Entity("opportunity", OpportunityO));

            var ex = Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
                service.Update(new Entity("opportunity", OpportunityO) { ["statecode"] = Opt(1), ["statuscode"] = Opt(3) }));

            Assert.Equal("This message can not be used to set the state of opportunity to won. In order to set state of opportunity to won, use the won message instead.",
                ex.Detail.Message);
        }

        // ------------------------------------------------------------------------------------
        // the deferred queue: plain states, failures, cancellation, dry run
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Plain_state_changes_are_deferred_until_the_selected_record_tree_is_done()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("primarycontactid", Ref("contact", ContactC))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA)),
                ("statecode", Opt(1)), ("statuscode", Opt(2))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Collection(h.Destination.Writes,
                r => Assert.Equal(("contact", ContactC), CreateKey(r)),
                r => Assert.Equal(("account", AccountA), CreateKey(r)),
                r => Assert.Equal(new[] { "parentcustomerid" }, Assert.IsType<UpdateRequest>(r).Target.Attributes.Keys),   // the backfill into the contact...
                r => AssertStateUpdate(r, ContactC, 1, 2));                                                                  // ...before it is deactivated
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"    Deferred parentcustomerid on contact \"Jane Doe\": account {AccountA} is still being copied (circular reference), will backfill",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"Created account \"Contoso\" ({AccountA})",
                $"  Backfilled contact.parentcustomerid -> account ({AccountA})",
                $"  State applied to contact \"Jane Doe\" ({ContactC}): statecode=1, statuscode=2"
            }, h.Log.Lines);
            Assert.Equal((2, 1, 1), (summary.Created, summary.LookupsBackfilled, summary.StateChanges));
        }

        [Fact]
        public void The_state_changes_of_a_tree_are_applied_even_when_its_selected_record_fails()
        {
            var h = new Harness();
            h.Destination.FailCreates.Add("contact");
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("contact", ContactC);

            AssertStateUpdate(Assert.Single(h.Destination.Updates), AccountA, 1, 2);
            Assert.Equal((1, 1, 1), (summary.Created, summary.Failed, summary.StateChanges));
            Assert.Equal($"  State applied to account \"Contoso\" ({AccountA}): statecode=1, statuscode=2", h.Log.Lines.Last());
        }

        [Fact]
        public void A_close_message_that_fails_is_a_warning_and_the_record_stays_copied()
        {
            var h = NewHarness();
            h.Destination.FailStateChanges = true;
            h.Source.Add(Record("opportunity", OpportunityO, ("name", "Big deal"), ("statecode", Opt(1)), ("statuscode", Opt(3))));

            CopySummary summary = h.Run("opportunity", OpportunityO);

            Assert.True(h.Destination.Contains("opportunity", OpportunityO));
            Assert.IsType<WinOpportunityRequest>(Assert.Single(h.Destination.CloseRequests));
            Assert.Equal((1, 0, 0), (summary.Created, summary.Failed, summary.StateChanges));
            Assert.Empty(summary.Errors);
            Assert.Equal(
                $"Created opportunity \"Big deal\" ({OpportunityO}) but state not applied (WinOpportunity): Simulated state change failure for opportunity {OpportunityO}",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void Cancellation_inside_a_tree_reports_the_state_changes_that_were_not_applied()
        {
            var h = new Harness();
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));
            using var cts = new CancellationTokenSource();
            h.Destination.BeforeExecute = request => { if (request is CreateRequest) cts.Cancel(); };   // at the account create

            Assert.Throws<OperationCanceledException>(() => h.Run("contact", cts.Token, ContactC));

            Assert.Equal("account", Assert.Single(h.Destination.Creates).Target.LogicalName);
            Assert.Empty(h.Destination.Updates);   // the account's state change was never sent
            CopySummary summary = h.Engine.LastSummary;
            Assert.True(summary.Cancelled);
            Assert.Equal((1, 0), (summary.Created, summary.StateChanges));
            Assert.Equal(new[]
            {
                "1 pending state change not applied: run cancelled",
                $"  account \"Contoso\" ({AccountA}): statecode=1, statuscode=2"
            }, h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void Dry_run_names_the_close_message_it_would_use_and_sends_nothing()
        {
            var h = NewHarness();
            h.Options.DryRun = true;
            h.Source.Add(Record("opportunity", OpportunityO, ("name", "Big deal"), ("statecode", Opt(1)), ("statuscode", Opt(3))));
            h.Source.Add(Record("quote", Quote1, ("name", "Q-1 won"), ("statecode", Opt(2)), ("statuscode", Opt(4))));

            CopySummary opportunityRun = h.Run("opportunity", OpportunityO);
            CopySummary quoteRun = h.Run("quote", Quote1);

            Assert.Empty(h.Destination.Writes);
            Assert.Equal((1, 1), (opportunityRun.StateChanges, quoteRun.StateChanges));
            Assert.Contains($"[DRY RUN] Would apply state to opportunity \"Big deal\" ({OpportunityO}): statecode=1, statuscode=3 (WinOpportunity)", h.Log.Lines);
            Assert.Contains($"[DRY RUN] Would apply state to quote \"Q-1 won\" ({Quote1}): statecode=2, statuscode=4 (activate + WinQuote)", h.Log.Lines);
        }

        // ------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------

        private static (string Entity, Guid Id) CreateKey(OrganizationRequest request)
        {
            var create = Assert.IsType<CreateRequest>(request);
            return (create.Target.LogicalName, create.Target.Id);
        }

        private static (int State, int Status) StateOf(Entity entity) =>
            (((OptionSetValue)entity["statecode"]).Value, ((OptionSetValue)entity["statuscode"]).Value);

        /// <summary>A plain Update of exactly statecode + statuscode.</summary>
        private static void AssertStateUpdate(OrganizationRequest request, Guid id, int state, int status)
        {
            var update = Assert.IsType<UpdateRequest>(request);
            Assert.Equal(id, update.Target.Id);
            Assert.Equal(new[] { "statecode", "statuscode" }, update.Target.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal((state, status), StateOf(update.Target));
        }

        /// <summary>A close message whose close activity (the <paramref name="parameter"/>) points at the record with the copier's subject.</summary>
        private static void AssertClose<TRequest>(OrganizationRequest request, string parameter, string closeEntity, string lookup, string entity, Guid id, int status)
            where TRequest : OrganizationRequest
        {
            Assert.IsType<TRequest>(request);
            var close = (Entity)request.Parameters[parameter];
            Assert.Equal(closeEntity, close.LogicalName);
            Assert.Equal(new[] { lookup, "subject" }, close.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
            var record = (EntityReference)close[lookup];
            Assert.Equal((entity, id), (record.LogicalName, record.Id));
            Assert.Equal(CopyEngine.CloseActivitySubject, close["subject"]);
            Assert.Equal(status, ((OptionSetValue)request.Parameters["Status"]).Value);
        }
    }
}
