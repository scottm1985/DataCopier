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
    /// SPEC 5.10: the N:N relationships of "Create related records for 1:N and N:N relationships
    /// (subgrids)". A record reached through an N:N relationship is a PEER: copied like a related record
    /// (created when missing, skipped when it exists), then associated with the record unless the
    /// destination intersect holds the pair already; its own relationships are followed only when its
    /// entity is configured.
    /// </summary>
    public class CopyEngineManyToManyTests
    {
        private static readonly Guid AccountA = new Guid("a0000000-0000-0000-0000-00000000000a");
        private static readonly Guid AccountB = new Guid("b0000000-0000-0000-0000-00000000000b");
        private static readonly Guid AccountX = new Guid("f0000000-0000-0000-0000-00000000000f");
        private static readonly Guid ContactC = new Guid("c0000000-0000-0000-0000-00000000000c");
        private static readonly Guid LeadL = new Guid("1ead0000-0000-0000-0000-000000000001");
        private static readonly Guid LeadM = new Guid("1ead0000-0000-0000-0000-000000000002");
        private static readonly Guid LeadN = new Guid("1ead0000-0000-0000-0000-000000000003");
        private static readonly Guid MatterM = new Guid("3a770000-0000-0000-0000-000000000001");
        private static readonly Guid CompetitorP = new Guid("c0390000-0000-0000-0000-000000000001");
        private static readonly Guid TaskT = new Guid("7a000000-0000-0000-0000-0000000000a1");

        private const string Leads = "accountleads_association";             // account (side 1) - lead (side 2), intersect accountleads
        private const string Matters = "new_matter_contact";                 // new_matter (side 1) - contact (side 2), custom
        private const string Related = "new_account_account";                // account - account: self-referential, custom
        private const string Competitors = "leadcompetitors_association";    // lead (side 1) - competitor (side 2), intersect leadcompetitors

        private static readonly HashSet<string> IntersectEntities = new HashSet<string> { "accountleads", Matters, Related, "leadcompetitors" };

        /// <summary>
        /// The standard schema plus lead (a 1:N relationship to task), competitor, new_matter and task, the
        /// four N:N relationships above (in the schema, and declared on both fake services) and account's
        /// 1:N relationship to its contacts.
        /// </summary>
        private static Harness NewHarness()
        {
            var h = new Harness();
            h.SourceSchema.Edit("account").OneToMany("contact_customer_accounts", "contact", "parentcustomerid");
            h.SourceSchema
                .Entity("lead", "fullname").OneToMany("Lead_Tasks", "task", "regardingobjectid")
                .Entity("competitor", "name")
                .Entity("new_matter", "new_name")
                .Entity("task", "subject", "activityid").Lookup("regardingobjectid", "account", "contact", "lead");
            h.SourceSchema
                .ManyToMany(Leads, "accountleads", "account", "accountid", "lead", "leadid")
                .ManyToMany(Matters, Matters, "new_matter", "new_matterid", "contact", "contactid", custom: true)
                .ManyToMany(Related, Related, "account", "accountidone", "account", "accountidtwo", custom: true)
                .ManyToMany(Competitors, "leadcompetitors", "lead", "leadid", "competitor", "competitorid");
            foreach (FakeOrganizationService service in new[] { h.Source, h.Destination })
            {
                service.ManyToMany(Leads, "accountleads", "account", "accountid", "lead", "leadid")
                    .ManyToMany(Matters, Matters, "new_matter", "new_matterid", "contact", "contactid")
                    .ManyToMany(Related, Related, "account", "accountidone", "account", "accountidtwo")
                    .ManyToMany(Competitors, "leadcompetitors", "lead", "leadid", "competitor", "competitorid");
            }
            return h;
        }

        /// <summary>
        /// Follows relationships with the default selector: the ticks of the configured entities, else the
        /// given subgrids (for selected and child records; a peer follows nothing). Returns the entities
        /// whose main-form subgrids were asked for.
        /// </summary>
        private static List<string> UseRelationships(Harness h, Dictionary<string, string[]> configured = null, Dictionary<string, string[]> subgrids = null)
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

        /// <summary>account "Contoso" associated with lead "Lead One" through accountleads_association, in the source.</summary>
        private static void SeedAccountAndLead(Harness h)
        {
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("lead", LeadL, ("fullname", "Lead One")));
            h.Source.AddAssociation(Leads, AccountA, LeadL);
        }

        private static (string, Guid) Key(CreateRequest create) => (create.Target.LogicalName, create.Target.Id);

        /// <summary>Target, relationship, the one related record and the role of an AssociateRequest.</summary>
        private static (string, Guid, string, string, Guid, EntityRole?) Shape(AssociateRequest request)
        {
            EntityReference related = Assert.Single(request.RelatedEntities);
            return (request.Target.LogicalName, request.Target.Id, request.Relationship.SchemaName, related.LogicalName, related.Id, request.Relationship.PrimaryEntityRole);
        }

        /// <summary>The destination writes and intersect queries, in order (existence checks of records left out).</summary>
        private static IEnumerable<string> Steps(FakeOrganizationService service) =>
            service.Executed.Select(request =>
            {
                switch (request)
                {
                    case CreateRequest create: return "create " + create.Target.LogicalName;
                    case UpdateRequest update: return "update " + update.Target.LogicalName;
                    case AssociateRequest associate: return "associate " + associate.Relationship.SchemaName;
                    case RetrieveMultipleRequest query when query.Query is QueryExpression q && IntersectEntities.Contains(q.EntityName): return "query " + q.EntityName;
                    default: return null;
                }
            }).Where(step => step != null);

        private static IEnumerable<(Guid, Guid)> Pairs(FakeOrganizationService service, string relationship) =>
            service.Associations(relationship).OrderBy(p => p.Entity1Id).ThenBy(p => p.Entity2Id);

        private static FaultException<OrganizationServiceFault> Fault(string message) =>
            FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, message);

        // ------------------------------------------------------------------------------------
        // peers and associations
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_missing_peer_is_created_then_the_pair_is_associated()
        {
            var h = NewHarness();
            h.Options.BypassCustomPluginExecution = true;
            UseRelationships(h, Tick(("account", new[] { Leads })));
            SeedAccountAndLead(h);

            CopySummary summary = h.Run("account", AccountA);

            // The peer is created, the destination intersect is asked for the pair, THEN the pair is associated.
            Assert.Equal(new[] { "create account", "create lead", "query accountleads", $"associate {Leads}" }, Steps(h.Destination));
            AssociateRequest associate = Assert.Single(h.Destination.Associates);
            Assert.Equal(("account", AccountA, Leads, "lead", LeadL, (EntityRole?)null), Shape(associate));
            Assert.True((bool)associate.Parameters["BypassCustomPluginExecution"]);
            Assert.DoesNotContain("SuppressDuplicateDetection", associate.Parameters.Keys);   // for create and update only
            Assert.Equal(new[] { (AccountA, LeadL) }, Pairs(h.Destination, Leads));

            // The source intersect: the record's side Equal its id, the other side returned and ordered, 500 a page.
            QueryExpression listed = Assert.Single(h.Source.Queries, q => q.EntityName == "accountleads");
            Assert.Equal(new[] { "leadid" }, listed.ColumnSet.Columns);
            ConditionExpression condition = Assert.Single(listed.Criteria.Conditions);
            Assert.Equal(("accountid", ConditionOperator.Equal, (object)AccountA), (condition.AttributeName, condition.Operator, condition.Values[0]));
            Assert.Equal("leadid", Assert.Single(listed.Orders).AttributeName);
            Assert.Equal((500, 1, (string)null), (listed.PageInfo.Count, listed.PageInfo.PageNumber, listed.PageInfo.PagingCookie));
            // The destination check: both ids, the first row only.
            QueryExpression check = Assert.Single(h.Destination.Queries, q => q.EntityName == "accountleads");
            Assert.Equal(1, check.TopCount);
            Assert.Equal(new[] { ("accountid", (object)AccountA), ("leadid", (object)LeadL) }, check.Criteria.Conditions.Select(c => (c.AttributeName, c.Values[0])));

            Assert.Equal((2, 1, 1, 0, 0), (summary.Created, summary.PeerRecordsFound, summary.AssociationsCreated, summary.AssociationsSkipped, summary.AssociationsFailed));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})",
                $"Associated records of account \"Contoso\" via {Leads}: 1 lead record",
                $"  Copying lead \"Lead One\" ({LeadL})...",
                $"  Created lead \"Lead One\" ({LeadL})",
                $"  Associated account \"Contoso\" <-> lead \"Lead One\" via {Leads}"
            }, h.Log.Lines);
            Assert.Equal(new[] { LogLevel.Info, LogLevel.Success, LogLevel.Info, LogLevel.Info, LogLevel.Success, LogLevel.Success }, h.Log.Entries.Select(e => e.Level));
        }

        [Fact]
        public void An_existing_peer_is_skipped_never_read_from_the_source_and_only_associated()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            h.Destination.Add(Record("lead", LeadL, ("fullname", "Lead (destination)")));
            SeedAccountAndLead(h);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { "create account", "query accountleads", $"associate {Leads}" }, Steps(h.Destination));
            Assert.Empty(h.Destination.Updates);
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.LogicalName == "lead");
            Assert.Equal(new[] { (AccountA, LeadL) }, Pairs(h.Destination, Leads));
            Assert.Equal((1, 1, 1), (summary.Created, summary.SkippedExisting, summary.AssociationsCreated));
            Assert.Equal(new[]
            {
                $"Associated records of account \"Contoso\" via {Leads}: 1 lead record",
                $"  Exists, skipped lead \"Lead (destination)\" ({LeadL})",
                $"  Associated account \"Contoso\" <-> lead \"Lead (destination)\" via {Leads}"
            }, h.Log.Lines.Skip(2));
        }

        [Fact]
        public void An_association_the_destination_holds_is_skipped_without_an_AssociateRequest()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            h.Destination.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Destination.Add(Record("lead", LeadL, ("fullname", "Lead One")));
            h.Destination.AddAssociation(Leads, AccountA, LeadL);
            SeedAccountAndLead(h);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { "update account", "query accountleads" }, Steps(h.Destination));
            Assert.Empty(h.Destination.Associates);
            Assert.Equal(new[] { (AccountA, LeadL) }, Pairs(h.Destination, Leads));
            Assert.Equal((1, 0, 1, 0), (summary.Updated, summary.AssociationsCreated, summary.AssociationsSkipped, summary.AssociationsFailed));
            Assert.Equal($"  Association exists, skipped account \"Contoso\" <-> lead \"Lead One\" via {Leads}", h.Log.Lines.Last());
            Assert.Equal(LogLevel.Info, h.Log.Entries.Last().Level);
            Assert.Empty(h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void A_peer_that_cannot_be_copied_skips_its_association_with_a_warning()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            h.Destination.FailCreates.Add("lead");
            SeedAccountAndLead(h);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Empty(h.Destination.Associates);
            Assert.Equal((1, 1, 1, 0, 1), (summary.Created, summary.Failed, summary.PeerRecordsFound, summary.AssociationsCreated, summary.AssociationsFailed));
            Assert.Equal($"  Skipped association account \"Contoso\" <-> lead \"Lead One\" via {Leads}: lead {LeadL} could not be copied",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
            Assert.StartsWith($"FAILED to create lead \"Lead One\" ({LeadL}): Simulated create failure", Assert.Single(summary.Errors));
            Assert.Equal(1, h.Progress.Last().AssociationsFailed);
        }

        [Fact]
        public void A_self_referential_relationship_is_listed_from_both_sides_and_each_pair_keeps_its_orientation()
        {
            var h = NewHarness();
            UseRelationships(h, subgrids: new Dictionary<string, string[]> { ["account"] = new[] { Related } });
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta")));
            h.Source.Add(Record("account", AccountX, ("name", "Xray")));
            h.Source.AddAssociation(Related, AccountA, AccountB);   // Alpha on side 1
            h.Source.AddAssociation(Related, AccountX, AccountA);   // Alpha on side 2

            CopySummary summary = h.Run("account", AccountA);

            // Both sides of the intersect are listed for the record (the peers, unconfigured accounts, list nothing).
            Assert.Equal(new[] { ("accountidone", "accountidtwo"), ("accountidtwo", "accountidone") },
                h.Source.Queries.Where(q => q.EntityName == Related).Select(q => (Assert.Single(q.Criteria.Conditions).AttributeName, Assert.Single(q.ColumnSet.Columns))));
            // Each pair is associated as the source has it: Target is the side-1 record, with the Referencing role.
            Assert.Collection(h.Destination.Associates,
                r => Assert.Equal(("account", AccountA, Related, "account", AccountB, (EntityRole?)EntityRole.Referencing), Shape(r)),
                r => Assert.Equal(("account", AccountX, Related, "account", AccountA, (EntityRole?)EntityRole.Referencing), Shape(r)));
            Assert.Equal(new[] { (AccountA, AccountB), (AccountX, AccountA) }, Pairs(h.Destination, Related));
            Assert.Equal((3, 2, 2), (summary.Created, summary.PeerRecordsFound, summary.AssociationsCreated));
            Assert.Equal(new[]
            {
                $"Associated records of account \"Alpha\" via {Related}: 2 account records",
                $"  Copying account \"Beta\" ({AccountB})...",
                $"  Created account \"Beta\" ({AccountB})",
                $"  Associated account \"Alpha\" <-> account \"Beta\" via {Related}",
                $"  Copying account \"Xray\" ({AccountX})...",
                $"  Created account \"Xray\" ({AccountX})",
                $"  Associated account \"Alpha\" <-> account \"Xray\" via {Related}"
            }, h.Log.Lines.Skip(2));
        }

        [Fact]
        public void A_record_on_side_2_lists_its_own_side_and_is_associated_with_its_side_1_peer()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("contact", new[] { Matters })));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe")));
            h.Source.Add(Record("new_matter", MatterM, ("new_name", "Smith v Jones")));
            h.Source.AddAssociation(Matters, MatterM, ContactC);

            CopySummary summary = h.Run("contact", ContactC);

            QueryExpression listed = Assert.Single(h.Source.Queries, q => q.EntityName == Matters);
            Assert.Equal(("contactid", "new_matterid"), (Assert.Single(listed.Criteria.Conditions).AttributeName, Assert.Single(listed.ColumnSet.Columns)));
            Assert.Equal(("contact", ContactC, Matters, "new_matter", MatterM, (EntityRole?)null), Shape(Assert.Single(h.Destination.Associates)));
            Assert.Equal(new[] { (MatterM, ContactC) }, Pairs(h.Destination, Matters));
            Assert.Equal((2, 1, 1), (summary.Created, summary.PeerRecordsFound, summary.AssociationsCreated));
            Assert.Contains($"Associated records of contact \"Jane Doe\" via {Matters}: 1 new_matter record", h.Log.Lines);
            Assert.Contains($"  Associated contact \"Jane Doe\" <-> new_matter \"Smith v Jones\" via {Matters}", h.Log.Lines);
        }

        // ------------------------------------------------------------------------------------
        // what a peer follows
        // ------------------------------------------------------------------------------------

        /// <summary>Lead One has a task (Lead_Tasks) and a competitor (leadcompetitors_association) in the source.</summary>
        private static void SeedLeadRelations(Harness h)
        {
            SeedAccountAndLead(h);
            h.Source.Add(Record("task", TaskT, ("subject", "Call the lead"), ("regardingobjectid", Ref("lead", LeadL))));
            h.Source.Add(Record("competitor", CompetitorP, ("name", "Rival")));
            h.Source.AddAssociation(Competitors, LeadL, CompetitorP);
        }

        [Fact]
        public void An_unconfigured_peer_follows_none_of_its_relationships_not_even_its_subgrids()
        {
            var h = NewHarness();
            List<string> subgridRequests = UseRelationships(h,
                configured: Tick(("account", new[] { Leads })),
                subgrids: new Dictionary<string, string[]> { ["lead"] = new[] { "Lead_Tasks", Competitors } });
            SeedLeadRelations(h);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("lead", LeadL) }, h.Destination.Creates.Select(Key));
            Assert.Equal(new[] { "accountleads" }, h.Source.Queries.Select(q => q.EntityName));   // neither the tasks nor the competitors of the lead
            Assert.Empty(subgridRequests);   // the peer's main forms are not even read
            Assert.Equal((0, 1, 1), (summary.ChildRecordsFound, summary.PeerRecordsFound, summary.AssociationsCreated));
        }

        [Fact]
        public void A_configured_peer_follows_its_ticked_1N_and_N_N_relationships()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads }), ("lead", new[] { "Lead_Tasks", Competitors })));
            SeedLeadRelations(h);

            CopySummary summary = h.Run("account", AccountA);

            // The lead's own task (a child record of the peer) and competitor (a peer of the peer) come with it.
            Assert.Equal(new[] { ("account", AccountA), ("lead", LeadL), ("task", TaskT), ("competitor", CompetitorP) }, h.Destination.Creates.Select(Key));
            Assert.Equal(new[] { Competitors, Leads }, h.Destination.Associates.Select(a => a.Relationship.SchemaName));
            Assert.Equal(new[] { (LeadL, CompetitorP) }, Pairs(h.Destination, Competitors));
            Assert.Equal(LeadL, ((EntityReference)h.Destination.Get("task", TaskT)["regardingobjectid"]).Id);
            Assert.Equal((4, 1, 2, 2), (summary.Created, summary.ChildRecordsFound, summary.PeerRecordsFound, summary.AssociationsCreated));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})",
                $"Associated records of account \"Contoso\" via {Leads}: 1 lead record",
                $"  Copying lead \"Lead One\" ({LeadL})...",
                $"  Created lead \"Lead One\" ({LeadL})",
                $"  Children of lead \"Lead One\" via Lead_Tasks: 1 task record",
                $"    Copying task \"Call the lead\" ({TaskT})...",
                $"    Created task \"Call the lead\" ({TaskT})",
                $"  Associated records of lead \"Lead One\" via {Competitors}: 1 competitor record",
                $"    Copying competitor \"Rival\" ({CompetitorP})...",
                $"    Created competitor \"Rival\" ({CompetitorP})",
                $"    Associated lead \"Lead One\" <-> competitor \"Rival\" via {Competitors}",
                $"  Associated account \"Contoso\" <-> lead \"Lead One\" via {Leads}"
            }, h.Log.Lines);
        }

        [Fact]
        public void A_record_first_reached_as_a_peer_follows_its_relationships_once_reached_as_a_selected_record()
        {
            var h = NewHarness();
            UseRelationships(h, subgrids: new Dictionary<string, string[]> { ["account"] = new[] { "contact_customer_accounts", Related } });
            h.Source.Add(Record("account", AccountA, ("name", "Alpha")));
            h.Source.Add(Record("account", AccountB, ("name", "Beta")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountB))));
            h.Source.AddAssociation(Related, AccountA, AccountB);

            CopySummary summary = h.Run("account", AccountA, AccountB);

            // Beta is created as Alpha's peer (following nothing); as a selected record it then gets its child and its own associations.
            Assert.Equal(new[] { ("account", AccountA), ("account", AccountB), ("contact", ContactC) }, h.Destination.Creates.Select(Key));
            Assert.Equal(("account", AccountA, Related, "account", AccountB, (EntityRole?)EntityRole.Referencing), Shape(Assert.Single(h.Destination.Associates)));
            Assert.Equal((3, 1, 2, 1, 1), (summary.Created, summary.ChildRecordsFound, summary.PeerRecordsFound, summary.AssociationsCreated, summary.AssociationsSkipped));
            Assert.Equal(new[]
            {
                $"Already copied earlier in this run: account \"Beta\" ({AccountB})",
                $"Children of account \"Beta\" via contact_customer_accounts: 1 contact record",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"Associated records of account \"Beta\" via {Related}: 1 account record",
                $"  Already copied earlier in this run: account \"Alpha\" ({AccountA})",
                $"  Association exists, skipped account \"Beta\" <-> account \"Alpha\" via {Related}"
            }, h.Log.Lines.Skip(6));
        }

        [Fact]
        public void Child_records_come_before_the_peers_of_the_same_record()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads, "contact_customer_accounts" })));
            SeedAccountAndLead(h);
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { "create account", "create contact", "create lead", "query accountleads", $"associate {Leads}" }, Steps(h.Destination));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"Created account \"Contoso\" ({AccountA})",
                $"Children of account \"Contoso\" via contact_customer_accounts: 1 contact record",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"Associated records of account \"Contoso\" via {Leads}: 1 lead record",
                $"  Copying lead \"Lead One\" ({LeadL})...",
                $"  Created lead \"Lead One\" ({LeadL})",
                $"  Associated account \"Contoso\" <-> lead \"Lead One\" via {Leads}"
            }, h.Log.Lines);
            Assert.Equal((3, 1, 1, 1), (summary.Created, summary.ChildRecordsFound, summary.PeerRecordsFound, summary.AssociationsCreated));
        }

        [Fact]
        public void State_changes_of_the_tree_come_after_its_associations()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso"), ("statecode", Opt(1)), ("statuscode", Opt(2))));
            h.Source.Add(Record("lead", LeadL, ("fullname", "Lead One")));
            h.Source.AddAssociation(Leads, AccountA, LeadL);

            h.Run("account", AccountA);

            Assert.Equal(new[] { "create account", "create lead", "query accountleads", $"associate {Leads}", "update account" }, Steps(h.Destination));
            Assert.Equal(1, ((OptionSetValue)h.Destination.Updates.Last().Target["statecode"]).Value);
        }

        // ------------------------------------------------------------------------------------
        // what is never followed
        // ------------------------------------------------------------------------------------

        [Fact]
        public void System_intersects_and_never_create_excluded_or_virtual_peers_are_never_listed_even_if_a_selector_names_them()
        {
            var h = NewHarness();
            h.SourceSchema.Entity("role", "name").Entity("new_vatrate", "new_name").VirtualTable();
            h.Options.CopyChildren = true;
            h.Options.ChildRelationshipSelector = new StubRelationshipSelector(entity => entity == "account"
                ? new[]
                {
                    ManyToMany("new_account_users", "new_account_users", "account", "accountid", "systemuser", "systemuserid"),     // never-create peer
                    ManyToMany("new_account_roles", "new_account_roles", "account", "accountid", "role", "roleid"),                 // excluded peer
                    ManyToMany("new_account_vatrates", "new_account_vatrates", "account", "accountid", "new_vatrate", "new_vatrateid"),   // virtual peer
                    ManyToMany("new_account_members", "teammembership", "account", "accountid", "lead", "leadid"),                  // system intersect
                    ManyToMany("new_contact_matters", "new_contact_matters", "contact", "contactid", "new_matter", "new_matterid"),   // account is not a side
                    ManyToMany(Leads, "accountleads", "account", "accountid", "lead", "leadid")
                }
                : Array.Empty<ManyToManyRelationship>());
            SeedAccountAndLead(h);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { "accountleads" }, h.Source.Queries.Select(q => q.EntityName));
            Assert.Equal(1, summary.AssociationsCreated);
            Assert.Empty(h.Log.Messages(LogLevel.Warning));
        }

        [Fact]
        public void An_N_N_relationship_or_intersect_the_destination_lacks_is_not_followed_with_one_warning()
        {
            var missing = NewHarness();
            missing.DestinationSchema = missing.SourceSchema.Clone().RemoveManyToMany("account", Leads);
            UseRelationships(missing, Tick(("account", new[] { Leads })));
            SeedAccountAndLead(missing);
            missing.Source.Add(Record("account", AccountB, ("name", "Other")));
            missing.Source.AddAssociation(Leads, AccountB, LeadL);

            CopySummary summary = missing.Run("account", AccountA, AccountB);

            Assert.Empty(missing.Source.Queries);
            Assert.Equal($"N:N relationship {Leads} of account not followed: it does not exist in destination", Assert.Single(missing.Log.Messages(LogLevel.Warning)));
            Assert.Equal((2, 0, 0), (summary.Created, summary.PeerRecordsFound, summary.AssociationsCreated));

            var noIntersect = NewHarness();
            noIntersect.DestinationSchema = noIntersect.SourceSchema.Clone().RemoveEntity("accountleads");
            UseRelationships(noIntersect, Tick(("account", new[] { Leads })));
            SeedAccountAndLead(noIntersect);

            noIntersect.Run("account", AccountA);

            Assert.Empty(noIntersect.Source.Queries);
            Assert.Equal($"N:N relationship {Leads} of account not followed: its intersect entity accountleads does not exist in destination",
                Assert.Single(noIntersect.Log.Messages(LogLevel.Warning)));
        }

        // ------------------------------------------------------------------------------------
        // failures
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_failing_source_intersect_query_is_a_warning_and_the_run_continues()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads, "contact_customer_accounts" })));
            h.Source.FailRetrieveMultiple["accountleads"] = Fault("Principal user is missing prvReadLead privilege");
            SeedAccountAndLead(h);
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("parentcustomerid", Ref("account", AccountA))));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(new[] { ("account", AccountA), ("contact", ContactC) }, h.Destination.Creates.Select(Key));
            Assert.Empty(h.Destination.Associates);
            Assert.Equal((0, 0, 0), (summary.Failed, summary.PeerRecordsFound, summary.AssociationsFailed));
            Assert.Empty(summary.Errors);
            Assert.Equal($"Could not list {Leads} associations of account \"Contoso\": Principal user is missing prvReadLead privilege",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        [Fact]
        public void An_association_the_destination_refuses_is_a_warning_and_the_records_stay_copied()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            h.Destination.FailAssociates.Add(Leads);
            SeedAccountAndLead(h);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(2, h.Destination.Creates.Count);
            Assert.Empty(h.Destination.Associations(Leads));
            Assert.Equal((2, 0, 0, 1), (summary.Created, summary.Failed, summary.AssociationsCreated, summary.AssociationsFailed));
            Assert.Equal($"  Association failed account \"Contoso\" <-> lead \"Lead One\" via {Leads}: Simulated associate failure for {Leads}",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
            Assert.Empty(summary.Errors);
        }

        [Fact]
        public void Without_a_destination_check_a_pair_the_platform_refuses_as_a_duplicate_counts_as_existing()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            h.Destination.FailRetrieveMultiple["accountleads"] = Fault("Simulated intersect failure");
            h.Destination.Add(Record("lead", LeadL, ("fullname", "Lead One")));
            h.Destination.AddAssociation(Leads, AccountA, LeadL);
            SeedAccountAndLead(h);
            h.Source.Add(Record("lead", LeadM, ("fullname", "Lead Two")));
            h.Source.AddAssociation(Leads, AccountA, LeadM);

            CopySummary summary = h.Run("account", AccountA);

            // The intersect is asked once; both pairs are then sent: the existing one is refused as a duplicate.
            Assert.Equal(new[] { "create account", "query accountleads", $"associate {Leads}", "create lead", $"associate {Leads}" }, Steps(h.Destination));
            Assert.Equal(new[] { (AccountA, LeadL), (AccountA, LeadM) }, Pairs(h.Destination, Leads));
            Assert.Equal((1, 1, 0), (summary.AssociationsCreated, summary.AssociationsSkipped, summary.AssociationsFailed));
            Assert.Equal($"  Could not check the {Leads} associations in destination: Simulated intersect failure; associating without checking",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
            Assert.Contains($"  Association exists, skipped account \"Contoso\" <-> lead \"Lead One\" via {Leads}", h.Log.Lines);
            Assert.Contains($"  Associated account \"Contoso\" <-> lead \"Lead Two\" via {Leads}", h.Log.Lines);
            Assert.True(CopyEngine.IsDuplicateKey(FakeOrganizationService.Fault(0, "Cannot insert duplicate key.")));
            Assert.False(CopyEngine.IsDuplicateKey(Fault("Generic SQL error.")));
        }

        [Fact]
        public void A_failing_N_N_selector_is_a_warning_and_the_run_continues()
        {
            var h = NewHarness();
            h.Options.CopyChildren = true;
            h.Options.ChildRelationshipSelector = new StubRelationshipSelector(entity => throw Fault("systemform: Principal user is missing prvReadForm privilege"));
            SeedAccountAndLead(h);

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal(1, summary.Created);
            Assert.Equal("Could not determine the N:N relationships of account: systemform: Principal user is missing prvReadForm privilege; its associations are not copied",
                Assert.Single(h.Log.Messages(LogLevel.Warning)));
        }

        // ------------------------------------------------------------------------------------
        // counts, paging, dry run, cancellation
        // ------------------------------------------------------------------------------------

        [Fact]
        public void The_counts_reach_the_summary_and_the_progress()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            SeedAccountAndLead(h);                                                       // Lead One: created and associated
            h.Source.Add(Record("lead", LeadM, ("fullname", "Lead Two")));               // Lead Two: there, and so is the pair
            h.Source.Add(Record("lead", LeadN, ("fullname", "Lead Three")));             // Lead Three: fails
            h.Source.AddAssociation(Leads, AccountA, LeadM).AddAssociation(Leads, AccountA, LeadN);
            h.Destination.Add(Record("lead", LeadM, ("fullname", "Lead Two")));
            h.Destination.AddAssociation(Leads, AccountA, LeadM);
            h.Destination.FailCreates.Add("lead/" + LeadN.ToString("D"));

            CopySummary summary = h.Run("account", AccountA);

            Assert.Equal((2, 1, 1), (summary.Created, summary.SkippedExisting, summary.Failed));
            Assert.Equal((3, 1, 1, 1), (summary.PeerRecordsFound, summary.AssociationsCreated, summary.AssociationsSkipped, summary.AssociationsFailed));
            CopyProgress last = h.Progress.Last();
            Assert.Equal((3, 1, 1, 1), (last.PeerRecordsFound, last.AssociationsCreated, last.AssociationsSkipped, last.AssociationsFailed));
        }

        [Fact]
        public void Associated_records_are_listed_500_at_a_time_with_the_paging_cookie()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            for (int i = 0; i < 501; i++)
            {
                Guid lead = Guid.NewGuid();
                h.Source.Add(Record("lead", lead, ("fullname", "Lead " + i)));
                h.Source.AddAssociation(Leads, AccountA, lead);
            }

            CopySummary summary = h.Run("account", AccountA);

            List<QueryExpression> pages = h.Source.Queries.Where(q => q.EntityName == "accountleads").ToList();
            Assert.Equal(new[] { (1, (string)null), (2, "<cookie page=\"1\" />") }, pages.Select(q => (q.PageInfo.PageNumber, q.PageInfo.PagingCookie)));
            Assert.All(pages, q => Assert.Equal(500, q.PageInfo.Count));
            Assert.Equal((502, 501, 501), (summary.Created, summary.PeerRecordsFound, summary.AssociationsCreated));
            Assert.Equal(501, h.Destination.Associations(Leads).Count);
            Assert.Contains($"Associated records of account \"Contoso\" via {Leads}: 501 lead records", h.Log.Lines);
        }

        /// <summary>Lead One missing (created, then associated); Lead Two there with the pair (skipped).</summary>
        private static void SeedDryRun(Harness h)
        {
            UseRelationships(h, Tick(("account", new[] { Leads })));
            SeedAccountAndLead(h);
            h.Source.Add(Record("lead", LeadM, ("fullname", "Lead Two")));
            h.Source.AddAssociation(Leads, AccountA, LeadM);
            h.Destination.Add(Record("lead", LeadM, ("fullname", "Lead Two")));
            h.Destination.AddAssociation(Leads, AccountA, LeadM);
        }

        [Fact]
        public void Dry_run_lists_and_checks_the_associations_but_writes_nothing_and_logs_the_same()
        {
            var dry = NewHarness();
            dry.Options.DryRun = true;
            SeedDryRun(dry);
            CopySummary drySummary = dry.Run("account", AccountA);

            var real = NewHarness();
            SeedDryRun(real);
            CopySummary realSummary = real.Run("account", AccountA);

            Assert.Empty(dry.Destination.Writes);
            Assert.All(dry.Destination.Executed, r => Assert.IsType<RetrieveMultipleRequest>(r));
            Assert.Equal(new[] { "query accountleads", "query accountleads" }, Steps(dry.Destination));   // the pairs are still checked
            Assert.Equal(new[] { (AccountA, LeadM) }, Pairs(dry.Destination, Leads));
            Assert.Equal(new[]
            {
                $"Copying account \"Contoso\" ({AccountA})...",
                $"[DRY RUN] Would create account \"Contoso\" ({AccountA})",
                $"Associated records of account \"Contoso\" via {Leads}: 2 lead records",
                $"  Copying lead \"Lead One\" ({LeadL})...",
                $"  [DRY RUN] Would create lead \"Lead One\" ({LeadL})",
                $"  [DRY RUN] Would associate account \"Contoso\" <-> lead \"Lead One\" via {Leads}",
                $"  Exists, skipped lead \"Lead Two\" ({LeadM})",
                $"  Association exists, skipped account \"Contoso\" <-> lead \"Lead Two\" via {Leads}"
            }, dry.Log.Lines);

            // The real run writes the same lines, at the same levels, with the same counts.
            Assert.Equal(real.Log.Entries.Select(e => e.Level), dry.Log.Entries.Select(e => e.Level));
            Assert.Equal(real.Log.Lines, dry.Log.Lines.Select(l => l
                .Replace("[DRY RUN] Would create ", "Created ")
                .Replace("[DRY RUN] Would associate ", "Associated ")));
            Assert.Equal(new[] { (AccountA, LeadL), (AccountA, LeadM) }, Pairs(real.Destination, Leads));
            Assert.Equal((2, 1, 2, 1, 1), (drySummary.Created, drySummary.SkippedExisting, drySummary.PeerRecordsFound, drySummary.AssociationsCreated, drySummary.AssociationsSkipped));
            Assert.Equal((2, 1, 2, 1, 1), (realSummary.Created, realSummary.SkippedExisting, realSummary.PeerRecordsFound, realSummary.AssociationsCreated, realSummary.AssociationsSkipped));
        }

        [Fact]
        public void Cancellation_inside_the_peer_loop_stops_before_the_next_write()
        {
            var h = NewHarness();
            UseRelationships(h, Tick(("account", new[] { Leads })));
            SeedAccountAndLead(h);
            h.Source.Add(Record("lead", LeadM, ("fullname", "Lead Two")));
            h.Source.AddAssociation(Leads, AccountA, LeadM);
            using var cts = new CancellationTokenSource();
            h.Destination.BeforeExecute = request =>
            {
                if (request is CreateRequest create && create.Target.LogicalName == "lead") cts.Cancel();
            };

            Assert.Throws<OperationCanceledException>(() => h.Run("account", cts.Token, AccountA));

            // The first peer was created; neither its association nor the second peer followed.
            Assert.Equal(new[] { ("account", AccountA), ("lead", LeadL) }, h.Destination.Creates.Select(Key));
            Assert.Empty(h.Destination.Associates);
            CopySummary summary = h.Engine.LastSummary;
            Assert.True(summary.Cancelled);
            Assert.Equal((2, 2, 0), (summary.Created, summary.PeerRecordsFound, summary.AssociationsCreated));
        }

        // ------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------

        private static ManyToManyRelationship ManyToMany(string schemaName, string intersect, string entity1, string attribute1, string entity2, string attribute2) =>
            new ManyToManyRelationship
            {
                SchemaName = schemaName,
                IntersectEntity = intersect,
                Entity1LogicalName = entity1,
                Entity1IntersectAttribute = attribute1,
                Entity2LogicalName = entity2,
                Entity2IntersectAttribute = attribute2
            };

        /// <summary>An <see cref="IRelationshipSelector"/> that answers N:N relationships with a function (no eligibility rules) and no 1:N ones.</summary>
        private sealed class StubRelationshipSelector : IRelationshipSelector
        {
            private readonly Func<string, IReadOnlyList<ManyToManyRelationship>> _manyToMany;
            public StubRelationshipSelector(Func<string, IReadOnlyList<ManyToManyRelationship>> manyToMany) { _manyToMany = manyToMany; }
            public IReadOnlyList<ChildRelationship> GetChildRelationships(string parentEntity) => Array.Empty<ChildRelationship>();
            public IReadOnlyList<ChildRelationship> GetChildRelationships(string entity, RelationshipContext context) => Array.Empty<ChildRelationship>();
            public IReadOnlyList<ManyToManyRelationship> GetManyToManyRelationships(string entity, RelationshipContext context) => _manyToMany(entity);
        }
    }
}
