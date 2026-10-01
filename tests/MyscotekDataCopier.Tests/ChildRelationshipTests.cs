using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Core.Schema;
using MyscotekDataCopier.Core.Services;
using MyscotekDataCopier.Tests.Fakes;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    /// <summary>SPEC 5.10: which 1:N and N:N relationships may be followed, and which are chosen for a run.</summary>
    public class ChildRelationshipSelectorTests
    {
        private static readonly ISet<string> NeverCreate = new CopyOptions().NeverCreateEntities;

        /// <summary>
        /// account with one 1:N relationship and one N:N relationship per N:N eligibility rule, in this
        /// order: accountleads_association (account on side 1: eligible), new_matter_account (account on
        /// side 2, custom: eligible), new_account_account (self-referential, custom: eligible), then a
        /// never-create peer, two excluded peers (role, queue), a virtual and a private peer, a peer whose
        /// metadata fails, one without metadata and one through a system intersect entity.
        /// </summary>
        internal static FakeSchemaProvider ManyToManySchema()
        {
            var schema = new FakeSchemaProvider();
            schema.Entity("account", "name").EntityDisplayName("Account", "Accounts")
                    .OneToMany("contact_customer_accounts", "contact", "parentcustomerid")
                .Entity("contact", "fullname").EntityDisplayName("Contact", "Contacts").Customer("parentcustomerid")
                .Entity("lead", "fullname").EntityDisplayName("Lead", "Leads")
                .Entity("new_matter", "new_name").EntityDisplayName("Matter", "Matters")
                .Entity("systemuser", "fullname")
                .Entity("role", "name")
                .Entity("queue", "name")
                .Entity("new_vatrate", "new_name").VirtualTable()
                .Entity("new_private", "new_name").Private()
                .Entity("new_broken", "new_name");
            schema.ManyToMany("accountleads_association", "accountleads", "account", "accountid", "lead", "leadid")
                .ManyToMany("new_matter_account", "new_matter_account", "new_matter", "new_matterid", "account", "accountid", custom: true)
                .ManyToMany("new_account_account", "new_account_account", "account", "accountidone", "account", "accountidtwo", custom: true)
                .ManyToMany("new_account_systemuser", "new_account_systemuser", "account", "accountid", "systemuser", "systemuserid")
                .ManyToMany("new_account_role", "new_account_role", "account", "accountid", "role", "roleid")
                .ManyToMany("new_account_queue", "new_account_queue", "account", "accountid", "queue", "queueid")
                .ManyToMany("new_account_vatrate", "new_account_vatrate", "account", "accountid", "new_vatrate", "new_vatrateid")
                .ManyToMany("new_account_private", "new_account_private", "account", "accountid", "new_private", "new_privateid")
                .ManyToMany("new_account_broken", "new_account_broken", "account", "accountid", "new_broken", "new_brokenid")
                .ManyToMany("new_account_gone", "new_account_gone", "account", "accountid", "new_gone", "new_goneid")
                .ManyToMany("new_account_members", "teammembership", "account", "accountid", "contact", "contactid");
            schema.Failures["new_broken"] = new InvalidOperationException("metadata unavailable");
            return schema;
        }

        /// <summary>account with one 1:N relationship per eligibility rule; the child entities to match.</summary>
        internal static FakeSchemaProvider EligibilitySchema()
        {
            var schema = new FakeSchemaProvider();
            schema.Entity("account", "name")
                    .Attribute("masterid", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "account" })
                    .OneToMany("contact_customer_accounts", "contact", "parentcustomerid")                  // eligible
                    .OneToMany("Account_AsyncOperations", "asyncoperation", "regardingobjectid")            // system exclusion list
                    .OneToMany("account_teams", "team", "regardingobjectid")                                // never-create
                    .OneToMany("new_account_vatrate", "new_vatrate", "new_accountid")                     // virtual
                    .OneToMany("new_account_private", "new_private", "new_accountid")                       // private
                    .OneToMany("new_account_links", "new_link", "new_accountid")                            // intersect
                    .OneToMany("account_master_account", "account", "masterid")                             // lookup not valid for create
                    .OneToMany("msdyn_account_readonly", "msdyn_readonly", "msdyn_accountid")               // msdyn_, not creatable
                    .OneToMany("msdyn_account_notes", "msdyn_creatable", "msdyn_accountid", custom: true)   // msdyn_, creatable: eligible
                    .OneToMany("Account_ActivityPointers", "activitypointer", "regardingobjectid")          // eligible despite its lookup
                    .OneToMany("new_account_gone", "new_gone", "new_accountid")                             // no metadata
                    .OneToMany("new_account_broken", "new_broken", "new_accountid")                         // metadata fails
                    .OneToMany("new_account_nolookup", "new_nolookup", "new_accountid")                     // the lookup is missing
                    .OneToMany("Account_Annotation", "annotation", "objectid")                              // notes: eligible
                .Entity("contact", "fullname").Customer("parentcustomerid")
                .Entity("asyncoperation", "name").Lookup("regardingobjectid", "account")
                .Entity("team", "name").Lookup("regardingobjectid", "account")
                .Entity("new_vatrate", "new_name").VirtualTable().Lookup("new_accountid", "account")
                .Entity("new_private", "new_name").Private().Lookup("new_accountid", "account")
                .Entity("new_link", "new_name").Intersect().Lookup("new_accountid", "account")
                .Entity("msdyn_readonly", "msdyn_name").Lookup("msdyn_accountid", "account")
                    .Attribute("msdyn_readonlyid", AttributeTypeCode.Uniqueidentifier, create: false, update: false)
                .Entity("msdyn_creatable", "msdyn_name").Lookup("msdyn_accountid", "account")
                .Entity("activitypointer", "subject", "activityid")
                    .Attribute("regardingobjectid", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "account" })
                .Entity("new_nolookup", "new_name")
                .Entity("new_broken", "new_name").Lookup("new_accountid", "account")
                .Entity("annotation", "subject").Lookup("objectid", "account", "contact");
            schema.Failures["new_broken"] = new InvalidOperationException("metadata unavailable");
            return schema;
        }

        [Fact]
        public void Eligible_relationships_exclude_system_never_create_virtual_private_intersect_and_non_creatable_children()
        {
            IReadOnlyList<ChildRelationship> eligible = ChildRelationshipEligibility.GetEligible(EligibilitySchema(), "account", NeverCreate);

            Assert.Equal(new[] { "contact_customer_accounts", "msdyn_account_notes", "Account_ActivityPointers", "Account_Annotation" },
                eligible.Select(r => r.SchemaName));
            ChildRelationship contacts = eligible[0];
            Assert.Equal(("account", "contact", "parentcustomerid", false),
                (contacts.ParentEntity, contacts.ChildEntity, contacts.ChildLookupAttribute, contacts.IsCustomRelationship));
            Assert.True(eligible[1].IsCustomRelationship);
            Assert.Equal("contact_customer_accounts (contact.parentcustomerid)", contacts.ToString());
        }

        [Fact]
        public void The_system_exclusion_list_holds_the_platform_written_entities_but_not_notes()
        {
            foreach (string entity in new[] { "asyncoperation", "syncerror", "duplicaterecord", "processsession", "workflowlog", "bulkdeletefailure",
                                              "principalobjectattributeaccess", "userentityinstancedata", "mailboxtrackingfolder", "customeraddress",
                                              "actioncard", "postfollow", "postregarding", "recordcountsnapshot", "tracelog", "activityparty" })
            {
                Assert.True(ChildRelationshipEligibility.IsSystemExcluded(entity), entity);
            }
            Assert.True(ChildRelationshipEligibility.IsSystemExcluded(" AsyncOperation "));
            Assert.False(ChildRelationshipEligibility.IsSystemExcluded("annotation"));
            Assert.False(ChildRelationshipEligibility.IsSystemExcluded("activitypointer"));
            Assert.False(ChildRelationshipEligibility.IsSystemExcluded(null));
        }

        [Fact]
        public void An_unknown_entity_has_no_eligible_relationships()
        {
            Assert.Empty(ChildRelationshipEligibility.GetEligible(EligibilitySchema(), "new_unknown", NeverCreate));
            Assert.Empty(ChildRelationshipEligibility.GetEligible(EligibilitySchema(), " ", NeverCreate));
        }

        [Fact]
        public void A_configured_selection_overrides_the_main_form_subgrids()
        {
            var subgridRequests = new List<string>();
            var selector = new DefaultChildRelationshipSelector(EligibilitySchema(),
                entity => { subgridRequests.Add(entity); return new[] { "msdyn_account_notes" }; },
                new Dictionary<string, ISet<string>>
                {
                    ["Account"] = new HashSet<string> { "account_annotation", "Account_AsyncOperations", "contact_customer_accounts" }
                },
                NeverCreate);

            IReadOnlyList<ChildRelationship> chosen = selector.GetChildRelationships("account");

            // Metadata order, names matched case-insensitively, the ineligible system relationship left out.
            Assert.Equal(new[] { "contact_customer_accounts", "Account_Annotation" }, chosen.Select(r => r.SchemaName));
            Assert.Empty(subgridRequests);
            Assert.True(selector.IsConfigured("ACCOUNT"));
            Assert.False(selector.IsConfigured("contact"));
        }

        [Fact]
        public void An_unconfigured_entity_follows_its_eligible_main_form_subgrids()
        {
            var selector = new DefaultChildRelationshipSelector(EligibilitySchema(),
                entity => entity == "account"
                    ? new[] { "CONTACT_CUSTOMER_ACCOUNTS", "account_teams", "accountleads_association" }   // + never-create, + an N:N name
                    : Array.Empty<string>(),
                new Dictionary<string, ISet<string>> { ["contact"] = new HashSet<string>() },
                NeverCreate);

            Assert.Equal(new[] { "contact_customer_accounts" }, selector.GetChildRelationships("account").Select(r => r.SchemaName));
            Assert.False(selector.IsConfigured("account"));
        }

        [Fact]
        public void A_configured_empty_selection_means_no_children_without_reading_metadata()
        {
            FakeSchemaProvider schema = EligibilitySchema();
            var selector = new DefaultChildRelationshipSelector(schema, entity => new[] { "contact_customer_accounts" },
                new Dictionary<string, ISet<string>> { ["account"] = new HashSet<string>() }, NeverCreate);

            Assert.Empty(selector.GetChildRelationships("account"));
            Assert.Empty(schema.Requests);
        }

        [Fact]
        public void Eligible_N_N_relationships_have_the_entity_on_a_side_and_a_creatable_peer_outside_the_system_intersects()
        {
            FakeSchemaProvider schema = ManyToManySchema();

            IReadOnlyList<ManyToManyRelationship> eligible = ManyToManyEligibility.GetEligible(schema, "account", NeverCreate);

            // Never-create, role, queue, virtual, private, unreadable and missing peers, and a system intersect, are left out.
            Assert.Equal(new[] { "accountleads_association", "new_matter_account", "new_account_account" }, eligible.Select(r => r.SchemaName));
            Assert.Equal(("accountleads", "account", "accountid", "lead", "leadid", false),
                (eligible[0].IntersectEntity, eligible[0].Entity1LogicalName, eligible[0].Entity1IntersectAttribute,
                 eligible[0].Entity2LogicalName, eligible[0].Entity2IntersectAttribute, eligible[0].IsCustomRelationship));
            Assert.Equal("lead", eligible[0].OtherEntity("account"));        // account on side 1
            Assert.Equal("new_matter", eligible[1].OtherEntity("Account"));  // account on side 2: the peer is side 1
            Assert.True(eligible[1].IsCustomRelationship);
            Assert.True(eligible[2].IsSelfReferential);                      // account on both sides: allowed
            Assert.Equal("account", eligible[2].OtherEntity("account"));
            Assert.Equal("accountleads_association (account <-> lead)", eligible[0].ToString());

            // The same relationships from the other side; an entity that is not a side of one is not eligible for it.
            Assert.Equal(new[] { "accountleads_association" }, ManyToManyEligibility.GetEligible(schema, "lead", NeverCreate).Select(r => r.SchemaName));
            Assert.Equal(new[] { "new_matter_account" }, ManyToManyEligibility.GetEligible(schema, "new_matter", NeverCreate).Select(r => r.SchemaName));
            Assert.False(ManyToManyEligibility.IsEligible(schema, "contact", eligible[0], NeverCreate));
            Assert.Null(eligible[0].OtherEntity("contact"));
            Assert.Empty(ManyToManyEligibility.GetEligible(schema, "new_unknown", NeverCreate));
            Assert.Empty(ManyToManyEligibility.GetEligible(schema, " ", NeverCreate));

            // The never-create list is the one passed in.
            Assert.Contains("new_account_systemuser",
                ManyToManyEligibility.GetEligible(schema, "account", new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Select(r => r.SchemaName));
        }

        [Fact]
        public void The_system_intersects_and_the_excluded_peer_entities()
        {
            foreach (string intersect in new[] { "systemuserroles", "teamroles", "teamprofiles", "systemuserprofiles", "roleprivileges",
                                                 "teammembership", "principalobjectaccess" })
            {
                Assert.True(ManyToManyEligibility.IsSystemIntersect(intersect), intersect);
            }
            foreach (string peer in new[] { "role", "privilege", "fieldsecurityprofile", "queue", "position" })
            {
                Assert.True(ManyToManyEligibility.IsExcludedPeerEntity(peer), peer);
            }
            Assert.True(ManyToManyEligibility.IsSystemIntersect(" TeamMembership "));
            Assert.False(ManyToManyEligibility.IsSystemIntersect("accountleads"));
            Assert.False(ManyToManyEligibility.IsSystemIntersect(null));
            Assert.False(ManyToManyEligibility.IsExcludedPeerEntity("contact"));
            Assert.False(ManyToManyEligibility.IsExcludedPeerEntity(null));
        }

        [Fact]
        public void An_N_N_relationship_or_intersect_the_destination_lacks_is_not_eligible_against_that_destination()
        {
            FakeSchemaProvider source = ManyToManySchema();
            FakeSchemaProvider destination = source.Clone().RemoveManyToMany("account", "new_matter_account").RemoveEntity("accountleads");
            ManyToManyRelationship leads = source.GetEntity("account").ManyToManyRelationships[0];
            ManyToManyRelationship matters = source.GetEntity("account").ManyToManyRelationships[1];

            Assert.Equal(new[] { "new_account_account" },
                ManyToManyEligibility.GetEligible(source, "account", NeverCreate, destination).Select(r => r.SchemaName));
            Assert.Equal("its intersect entity accountleads does not exist in destination", ManyToManyEligibility.DestinationProblem(destination, "account", leads));
            Assert.Equal("it does not exist in destination", ManyToManyEligibility.DestinationProblem(destination, "account", matters));
            Assert.Equal("account does not exist in destination",
                ManyToManyEligibility.DestinationProblem(source.Clone().RemoveEntity("account"), "account", leads));
            Assert.Null(ManyToManyEligibility.DestinationProblem(source, "account", leads));       // all there
            Assert.Null(ManyToManyEligibility.DestinationProblem(null, "account", leads));         // no destination: nothing to check

            // Metadata the destination cannot return is not held against the relationship.
            destination.Failures["account"] = new InvalidOperationException("metadata unavailable");
            Assert.Null(ManyToManyEligibility.DestinationProblem(destination, "account", matters));
        }

        [Fact]
        public void A_configured_entity_follows_its_N_N_ticks_however_reached_and_an_unconfigured_one_its_subgrids_except_as_a_peer()
        {
            var subgridRequests = new List<string>();
            var selector = new DefaultChildRelationshipSelector(ManyToManySchema(),
                entity =>
                {
                    subgridRequests.Add(entity);
                    return entity == "lead" ? new[] { "accountleads_association" } : new[] { "new_account_account" };
                },
                new Dictionary<string, ISet<string>>
                {
                    ["account"] = new HashSet<string> { "ACCOUNTLEADS_ASSOCIATION", "new_account_systemuser", "contact_customer_accounts" }
                },
                NeverCreate);

            // Configured: its eligible ticks, as a selected or child record and as a peer alike; the forms are not read.
            Assert.Equal(new[] { "accountleads_association" },
                selector.GetManyToManyRelationships("account", RelationshipContext.SelectedOrChild).Select(r => r.SchemaName));
            Assert.Equal(new[] { "accountleads_association" }, selector.GetManyToManyRelationships("account", RelationshipContext.Peer).Select(r => r.SchemaName));
            Assert.Equal(new[] { "contact_customer_accounts" }, selector.GetChildRelationships("account", RelationshipContext.Peer).Select(r => r.SchemaName));
            Assert.Empty(subgridRequests);

            // Unconfigured: nothing as a peer (without reading the forms), its subgrids otherwise - read once for both kinds.
            Assert.Empty(selector.GetManyToManyRelationships("lead", RelationshipContext.Peer));
            Assert.Empty(selector.GetChildRelationships("lead", RelationshipContext.Peer));
            Assert.Empty(subgridRequests);
            IReadOnlyList<ManyToManyRelationship> leads = selector.GetManyToManyRelationships("lead", RelationshipContext.SelectedOrChild);
            Assert.Equal(new[] { "accountleads_association" }, leads.Select(r => r.SchemaName));
            Assert.Empty(selector.GetChildRelationships("lead"));
            Assert.Equal(new[] { "lead" }, subgridRequests);
            Assert.Same(leads, selector.GetManyToManyRelationships("LEAD", RelationshipContext.SelectedOrChild));   // cached per entity and context
            Assert.False(selector.IsConfigured("lead"));
        }

        [Fact]
        public void The_answer_per_entity_is_cached_and_a_failure_is_not()
        {
            int asked = 0;
            bool fail = true;
            var selector = new DefaultChildRelationshipSelector(EligibilitySchema(),
                entity =>
                {
                    asked++;
                    if (fail) throw new InvalidOperationException("systemform unavailable");
                    return new[] { "contact_customer_accounts" };
                },
                null, NeverCreate);

            Assert.Throws<InvalidOperationException>(() => selector.GetChildRelationships("account"));
            fail = false;
            IReadOnlyList<ChildRelationship> first = selector.GetChildRelationships("account");
            IReadOnlyList<ChildRelationship> second = selector.GetChildRelationships("Account");

            Assert.Equal("contact_customer_accounts", Assert.Single(first).SchemaName);
            Assert.Same(first, second);
            Assert.Equal(2, asked);
        }
    }

    public class FormSubgridServiceTests
    {
        /// <summary>A trimmed but realistic account main form: two subgrids (one with a lower-case class id) and a lookup control.</summary>
        internal const string AccountMainForm =
            "<form headerdensity=\"HighWithControls\"><tabs>" +
            "<tab name=\"SUMMARY_TAB\" id=\"{b3e51b5a-0bbd-4d5b-9d2e-0c7a5d3b8d10}\" IsUserDefined=\"0\" locklevel=\"0\" showlabel=\"true\" expanded=\"true\">" +
            "<labels><label description=\"Summary\" languagecode=\"1033\" /></labels><columns>" +
            "<column width=\"34%\"><sections><section name=\"ACCOUNT_INFORMATION\" showlabel=\"true\" id=\"{0a1b2c3d-0000-0000-0000-000000000001}\" columns=\"1\">" +
            "<labels><label description=\"ACCOUNT INFORMATION\" languagecode=\"1033\" /></labels><rows><row>" +
            "<cell id=\"{0a1b2c3d-0000-0000-0000-000000000002}\" showlabel=\"true\"><labels><label description=\"Primary Contact\" languagecode=\"1033\" /></labels>" +
            "<control id=\"primarycontactid\" classid=\"{270BD3DB-D9AF-4782-9025-509E298DEC0A}\" datafieldname=\"primarycontactid\" disabled=\"false\" uniqueid=\"{0a1b2c3d-0000-0000-0000-000000000003}\">" +
            "<parameters><AutoResolve>true</AutoResolve><DisableMru>false</DisableMru><AvailableViewIds>{a2d479c5-53e3-4c69-addd-802327e67a0d}</AvailableViewIds></parameters>" +
            "</control></cell></row></rows></section></sections></column>" +
            "<column width=\"33%\"><sections><section name=\"Contacts\" showlabel=\"false\" id=\"{0a1b2c3d-0000-0000-0000-000000000004}\" columns=\"1\"><rows><row>" +
            "<cell id=\"{0a1b2c3d-0000-0000-0000-000000000005}\" rowspan=\"12\" auto=\"false\"><labels><label description=\"Contacts\" languagecode=\"1033\" /></labels>" +
            "<control id=\"Contacts\" classid=\"{E7A81278-8635-4D9E-8D4D-59480B391C5B}\" indicationOfSubgrid=\"true\" uniqueid=\"{0a1b2c3d-0000-0000-0000-000000000006}\">" +
            "<parameters><ViewId>{00000000-0000-0000-00AA-000010001004}</ViewId><IsUserView>false</IsUserView>" +
            "<RelationshipName>contact_customer_accounts</RelationshipName><TargetEntityType>contact</TargetEntityType>" +
            "<AutoExpand>Fixed</AutoExpand><EnableQuickFind>false</EnableQuickFind><EnableViewPicker>false</EnableViewPicker><ViewIds />" +
            "<EnableJumpBar>false</EnableJumpBar><ChartGridMode>Grid</ChartGridMode><VisualizationId /><IsUserChart>false</IsUserChart>" +
            "<EnableChartPicker>false</EnableChartPicker><RecordsPerPage>10</RecordsPerPage></parameters>" +
            "</control></cell></row></rows></section></sections></column>" +
            "<column width=\"33%\"><sections><section name=\"Opportunities\" showlabel=\"false\" id=\"{0a1b2c3d-0000-0000-0000-000000000007}\" columns=\"1\"><rows><row>" +
            "<cell id=\"{0a1b2c3d-0000-0000-0000-000000000008}\" rowspan=\"8\" auto=\"false\"><labels><label description=\"Opportunities\" languagecode=\"1033\" /></labels>" +
            "<control id=\"Opportunities\" classid=\"{e7a81278-8635-4d9e-8d4d-59480b391c5b}\" indicationOfSubgrid=\"true\" uniqueid=\"{0a1b2c3d-0000-0000-0000-000000000009}\">" +
            "<parameters><ViewId>{00000000-0000-0000-00AA-000010003001}</ViewId><IsUserView>false</IsUserView>" +
            "<RelationshipName>opportunity_customer_accounts</RelationshipName><TargetEntityType>opportunity</TargetEntityType>" +
            "<AutoExpand>Fixed</AutoExpand><RecordsPerPage>5</RecordsPerPage></parameters>" +
            "</control></cell></row></rows></section></sections></column>" +
            "</columns></tab></tabs></form>";

        [Fact]
        public void A_main_form_yields_the_relationship_names_of_its_two_subgrids_and_not_the_other_control()
        {
            Assert.Equal(new[] { "contact_customer_accounts", "opportunity_customer_accounts" }, FormSubgridService.ParseSubgridRelationships(AccountMainForm));
        }

        [Fact]
        public void Custom_subgrid_controls_count_while_empty_names_duplicates_and_bad_xml_do_not()
        {
            const string form =
                "<form><tabs><tab><columns><column><sections><section><rows>" +
                "<row><cell><control id=\"Widgets\" classid=\"{F9A8A302-114E-466A-B582-6771B2AE0D92}\" uniqueid=\"{1}\">" +
                "<parameters><RelationshipName>new_account_widgets</RelationshipName></parameters></control></cell></row>" +
                "<row><cell><control id=\"Records\" classid=\"{E7A81278-8635-4D9E-8D4D-59480B391C5B}\"><parameters><RelationshipName /><TargetEntityType>task</TargetEntityType></parameters></control></cell></row>" +
                "<row><cell><control id=\"Contacts2\" classid=\"E7A81278-8635-4D9E-8D4D-59480B391C5B\"><parameters><RelationshipName> contact_customer_accounts </RelationshipName></parameters></control></cell></row>" +
                "<row><cell><control id=\"Contacts3\" classid=\"{E7A81278-8635-4D9E-8D4D-59480B391C5B}\"><parameters><RelationshipName>CONTACT_CUSTOMER_ACCOUNTS</RelationshipName></parameters></control></cell></row>" +
                "<row><cell><control id=\"name\" classid=\"{4273EDBD-AC1D-40d3-9FB2-095C621B552D}\" datafieldname=\"name\" /></cell></row>" +
                "</rows></section></sections></column></columns></tab></tabs></form>";

            Assert.Equal(new[] { "new_account_widgets", "contact_customer_accounts" }, FormSubgridService.ParseSubgridRelationships(form));
            Assert.Empty(FormSubgridService.ParseSubgridRelationships("<form><tabs>"));
            Assert.Empty(FormSubgridService.ParseSubgridRelationships(null));
            Assert.Empty(FormSubgridService.ParseSubgridRelationships("  "));
        }

        [Fact]
        public void Only_the_active_main_forms_of_the_entity_are_read_once_bad_forms_skipped_and_names_distinct()
        {
            var source = new FakeOrganizationService();
            source.Add(Form("account", 2, 1, AccountMainForm));
            source.Add(Form("account", 2, 1, "<form><control classid=\"{E7A81278-8635-4D9E-8D4D-59480B391C5B}\"><parameters>" +
                                               "<RelationshipName>contact_customer_accounts</RelationshipName></parameters></control>" +
                                               "<control classid=\"{E7A81278-8635-4D9E-8D4D-59480B391C5B}\"><parameters>" +
                                               "<RelationshipName>Account_Tasks</RelationshipName></parameters></control></form>"));
            source.Add(Form("account", 2, 1, "<form><not closed>"));                                                   // bad XML: skipped
            source.Add(Form("account", 2, 0, Subgrid("new_inactive_form")));                                         // inactive
            source.Add(Form("account", 7, 1, Subgrid("new_quick_create")));                                          // quick create
            source.Add(Form("contact", 2, 1, Subgrid("contact_customer_contacts")));                                 // another entity
            var service = new FormSubgridService();

            IReadOnlyList<string> names = service.GetMainFormSubgridRelationships(source, "Account");
            IReadOnlyList<string> again = service.GetMainFormSubgridRelationships(source, "account");

            Assert.Equal(new[] { "contact_customer_accounts", "opportunity_customer_accounts", "Account_Tasks" }, names);
            Assert.Same(names, again);   // cached per entity
            QueryExpression query = Assert.Single(source.Queries);
            Assert.Equal("systemform", query.EntityName);
            Assert.Equal(new[] { "formxml" }, query.ColumnSet.Columns);
            Assert.Equal(new[] { ("objecttypecode", (object)"account"), ("type", 2), ("formactivationstate", 1) },
                query.Criteria.Conditions.Select(c => (c.AttributeName, c.Values[0])));
            Assert.All(query.Criteria.Conditions, c => Assert.Equal(ConditionOperator.Equal, c.Operator));

            Assert.Equal(new[] { "contact_customer_contacts" }, new FormSubgridService().GetMainFormSubgridRelationships(source, "contact"));
            Assert.Empty(service.GetMainFormSubgridRelationships(source, " "));
            Assert.Equal(2, source.Queries.Count);
        }

        [Fact]
        public void A_failing_form_query_propagates_and_is_not_cached()
        {
            var source = new FakeOrganizationService();
            source.FailRetrieveMultiple["systemform"] = FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "missing prvReadSystemForm");
            source.Add(Form("account", 2, 1, AccountMainForm));
            var service = new FormSubgridService();

            Assert.ThrowsAny<Exception>(() => service.GetMainFormSubgridRelationships(source, "account"));
            source.FailRetrieveMultiple.Clear();
            Assert.Equal(2, service.GetMainFormSubgridRelationships(source, "account").Count);
        }

        internal static Entity Form(string entity, int type, int activationState, string formXml) =>
            new Entity("systemform", Guid.NewGuid())
            {
                ["objecttypecode"] = entity,
                ["type"] = new OptionSetValue(type),
                ["formactivationstate"] = new OptionSetValue(activationState),
                ["formxml"] = formXml
            };

        internal static string Subgrid(string relationship) =>
            "<form><control classid=\"{E7A81278-8635-4D9E-8D4D-59480B391C5B}\"><parameters><RelationshipName>" + relationship +
            "</RelationshipName></parameters></control></form>";
    }
}
