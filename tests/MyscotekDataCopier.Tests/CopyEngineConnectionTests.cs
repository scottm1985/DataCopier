using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Tests.Fakes;
using Xunit;
using static MyscotekDataCopier.Tests.Fakes.TestData;
using AttributeTypeCode = Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// SPEC 5.5, connections: Dataverse stores every connection as a pair - the master (ismaster true) and
    /// the mirror the platform creates with it (record1/record2 swapped, ismaster false), cross-linked
    /// through relatedconnectionid. The destination platform creates the mirror of every connection the
    /// copier creates, so a mirror is never written: its master is copied in its place. The fake does what
    /// the platform does on create, so the tests count the connection rows of the destination.
    /// </summary>
    public class CopyEngineConnectionTests
    {
        private static readonly Guid AccountA = new Guid("a0000000-0000-0000-0000-00000000000a");
        private static readonly Guid ContactC = new Guid("c0000000-0000-0000-0000-00000000000c");
        private static readonly Guid ContactD = new Guid("d0000000-0000-0000-0000-00000000000d");
        private static readonly Guid MasterM = new Guid("c0aa0000-0000-0000-0000-000000000001");   // contact C (record1) - account A (record2)
        private static readonly Guid MirrorR = new Guid("c0bb0000-0000-0000-0000-000000000001");   // its mirror: account A - contact C
        private static readonly Guid MasterN = new Guid("c0aa0000-0000-0000-0000-000000000002");   // contact C - contact D
        private static readonly Guid MirrorS = new Guid("c0bb0000-0000-0000-0000-000000000002");   // its mirror: contact D - contact C
        private static readonly Guid OrphanO = new Guid("c0bb0000-0000-0000-0000-0000000000ff");   // a mirror that names no master
        private static readonly Guid RoleFriend = new Guid("70e10000-0000-0000-0000-000000000001");
        private static readonly Guid RoleEmployer = new Guid("70e10000-0000-0000-0000-000000000002");
        private static readonly Guid WidgetW = new Guid("3d000000-0000-0000-0000-000000000001");

        /// <summary>
        /// The standard schema plus connection (its two records and roles writable; ismaster and
        /// relatedconnectionid read-only, as on the platform), connectionrole, a custom new_widget with a
        /// lookup to connection, and contact's two 1:N relationships to connection (record1id, record2id).
        /// </summary>
        private static Harness NewHarness()
        {
            var h = new Harness();
            h.SourceSchema
                .Entity("connection", "name")
                    .Lookup("record1id", "account", "contact")
                    .Lookup("record1roleid", "connectionrole")
                    .Lookup("record2id", "account", "contact")
                    .Lookup("record2roleid", "connectionrole")
                    .Attribute("ismaster", AttributeTypeCode.Boolean, create: false, update: false)
                    .Attribute("relatedconnectionid", AttributeTypeCode.Lookup, create: false, update: false, configure: a => a.LookupTargets = new[] { "connection" })
                    .Memo("description")
                .Entity("connectionrole", "name")
                .Entity("new_widget", "new_name").Lookup("new_connectionid", "connection");
            h.SourceSchema.Edit("contact")
                .OneToMany("contact_connections1", "connection", "record1id")
                .OneToMany("contact_connections2", "connection", "record2id");
            return h;
        }

        /// <summary>Copies child records: contact's connections through both of its relationships (connection itself follows nothing).</summary>
        private static void FollowConnections(Harness h)
        {
            h.Options.CopyChildren = true;
            h.Options.ChildRelationshipSelector = new DefaultChildRelationshipSelector(
                h.SourceSchema,
                entity => Array.Empty<string>(),
                new Dictionary<string, ISet<string>> { ["contact"] = new HashSet<string> { "contact_connections1", "contact_connections2" } },
                h.Options.NeverCreateEntities);
        }

        /// <summary>A source connection pair as the platform stores it: the master and its mirror (records swapped), cross-linked.</summary>
        private static void AddPair(FakeOrganizationService service, Guid master, string masterName, Guid mirror, string mirrorName,
                                    EntityReference record1, EntityReference record2)
        {
            service.Add(Record("connection", master, ("name", masterName), ("record1id", record1), ("record2id", record2),
                ("ismaster", true), ("relatedconnectionid", Ref("connection", mirror))));
            service.Add(Record("connection", mirror, ("name", mirrorName), ("record1id", record2), ("record2id", record1),
                ("ismaster", false), ("relatedconnectionid", Ref("connection", master))));
        }

        /// <summary>contact "Jane Doe", account "Contoso" and their connection: master M (Jane Doe to Contoso), mirror R.</summary>
        private static void SeedJaneAndContoso(Harness h)
        {
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe")));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            AddPair(h.Source, MasterM, "Jane Doe to Contoso", MirrorR, "Contoso to Jane Doe", Ref("contact", ContactC, "Jane Doe"), Ref("account", AccountA, "Contoso"));
        }

        private static string MirrorSkipped(Guid mirror, Guid master) =>
            $"Skipped connection {mirror}: mirror of {master}; the platform creates mirrors with their master";

        private static (string, Guid) Key(CreateRequest create) => (create.Target.LogicalName, create.Target.Id);

        private static Guid Id(Entity entity, string lookup) => ((EntityReference)entity[lookup]).Id;

        // ------------------------------------------------------------------------------------
        // masters
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_master_is_copied_as_before_without_its_read_only_columns_and_the_platform_adds_its_mirror()
        {
            var h = NewHarness();
            h.Destination.Add(Record("connectionrole", RoleFriend, ("name", "Friend")));
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe")));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("connectionrole", RoleFriend, ("name", "Friend")));
            h.Source.Add(Record("connectionrole", RoleEmployer, ("name", "Employer")));
            h.Source.Add(Record("connection", MasterM, ("name", "Jane Doe to Contoso"),
                ("record1id", Ref("contact", ContactC, "Jane Doe")), ("record1roleid", Ref("connectionrole", RoleFriend, "Friend")),
                ("record2id", Ref("account", AccountA, "Contoso")), ("record2roleid", Ref("connectionrole", RoleEmployer, "Employer")),
                ("ismaster", true), ("relatedconnectionid", Ref("connection", MirrorR))));
            h.Source.Add(Record("connection", MirrorR, ("name", "Contoso to Jane Doe"),
                ("record1id", Ref("account", AccountA, "Contoso")), ("record1roleid", Ref("connectionrole", RoleEmployer, "Employer")),
                ("record2id", Ref("contact", ContactC, "Jane Doe")), ("record2roleid", Ref("connectionrole", RoleFriend, "Friend")),
                ("ismaster", false), ("relatedconnectionid", Ref("connection", MasterM))));

            CopySummary summary = h.Run("connection", MasterM);

            // Same GUID; both records and both roles followed like any lookup; never ismaster or relatedconnectionid (rule 6).
            Entity created = Assert.Single(h.Destination.Creates, c => c.Target.LogicalName == "connection").Target;
            Assert.Equal(MasterM, created.Id);
            Assert.Equal(new[] { "name", "record1id", "record1roleid", "record2id", "record2roleid" }, created.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.DoesNotContain(h.Source.Retrieves, r => r.Target.Id == MirrorR);   // relatedconnectionid is never followed
            Assert.Equal((4, 1, 0, 0), (summary.Created, summary.SkippedExisting, summary.Failed, summary.ConnectionMirrorsSkipped));
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains("mirror"));

            // The destination platform made the mirror: a new GUID, records and roles swapped, the pair cross-linked.
            IReadOnlyList<Entity> rows = h.Destination.Records("connection");
            Assert.Equal(2, rows.Count);
            Entity master = h.Destination.Get("connection", MasterM);
            Entity mirror = Assert.Single(rows, r => r.Id != MasterM);
            Assert.NotEqual(MirrorR, mirror.Id);
            Assert.True((bool)master["ismaster"]);
            Assert.False((bool)mirror["ismaster"]);
            Assert.Equal((mirror.Id, MasterM), (Id(master, "relatedconnectionid"), Id(mirror, "relatedconnectionid")));
            Assert.Equal((AccountA, ContactC, RoleEmployer, RoleFriend),
                (Id(mirror, "record1id"), Id(mirror, "record2id"), Id(mirror, "record1roleid"), Id(mirror, "record2roleid")));
        }

        [Fact]
        public void A_connection_whose_ismaster_is_missing_or_null_is_copied_like_a_master()
        {
            var h = NewHarness();
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe")));
            h.Source.Add(Record("account", AccountA, ("name", "Contoso")));
            h.Source.Add(Record("connection", MasterM, ("name", "No flag"), ("record1id", Ref("contact", ContactC)), ("record2id", Ref("account", AccountA))));
            h.Source.Add(Record("connection", MasterN, ("name", "Null flag"), ("record1id", Ref("contact", ContactC)), ("record2id", Ref("account", AccountA)),
                ("ismaster", null)));

            CopySummary summary = h.Run("connection", MasterM, MasterN);

            Assert.Equal(new[] { MasterM, MasterN }, h.Destination.Creates.Where(c => c.Target.LogicalName == "connection").Select(c => c.Target.Id));
            Assert.Equal((4, 0), (summary.Created, summary.ConnectionMirrorsSkipped));
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains("mirror"));
        }

        // ------------------------------------------------------------------------------------
        // mirrors
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_contact_reaching_both_sides_of_a_connection_creates_the_master_once_and_skips_the_mirror()
        {
            var h = NewHarness();
            SeedJaneAndContoso(h);
            FollowConnections(h);

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Equal(new[] { ("contact", ContactC), ("account", AccountA), ("connection", MasterM) }, h.Destination.Creates.Select(Key));
            Assert.Equal(2, h.Destination.Records("connection").Count);   // the master and the mirror the platform made: one pair, not two
            Assert.False(h.Destination.Contains("connection", MirrorR));
            Assert.Equal((3, 2, 1, 0, 0), (summary.Created, summary.ChildRecordsFound, summary.ConnectionMirrorsSkipped, summary.SkippedExisting, summary.Failed));
            Assert.Equal(1, h.Progress.Last().ConnectionMirrorsSkipped);
            Assert.Equal(new[]
            {
                $"Copying contact \"Jane Doe\" ({ContactC})...",
                $"Created contact \"Jane Doe\" ({ContactC})",
                "Children of contact \"Jane Doe\" via contact_connections1: 1 connection record",
                $"  Copying connection \"Jane Doe to Contoso\" ({MasterM})...",
                $"    Copying account \"Contoso\" ({AccountA})...",
                $"    Created account \"Contoso\" ({AccountA})",
                $"  Created connection \"Jane Doe to Contoso\" ({MasterM})",
                "Children of contact \"Jane Doe\" via contact_connections2: 1 connection record",
                "  " + MirrorSkipped(MirrorR, MasterM),
                $"  Already copied earlier in this run: connection \"Jane Doe to Contoso\" ({MasterM})"
            }, h.Log.Lines);
            Assert.Equal(LogLevel.Info, h.Log.Entries[8].Level);
        }

        [Fact]
        public void A_selected_mirror_copies_its_master_as_a_selected_record_which_a_rerun_updates()
        {
            var h = NewHarness();
            SeedJaneAndContoso(h);

            CopySummary first = h.Run("connection", MirrorR);

            Assert.Equal(new[] { ("contact", ContactC), ("account", AccountA), ("connection", MasterM) }, h.Destination.Creates.Select(Key));
            Assert.Equal(2, h.Destination.Records("connection").Count);
            Assert.Equal((3, 0, 1), (first.Created, first.Updated, first.ConnectionMirrorsSkipped));
            Assert.Equal(new[]
            {
                MirrorSkipped(MirrorR, MasterM),
                $"Copying connection \"Jane Doe to Contoso\" ({MasterM})...",
                $"  Copying contact \"Jane Doe\" ({ContactC})...",
                $"  Created contact \"Jane Doe\" ({ContactC})",
                $"  Copying account \"Contoso\" ({AccountA})...",
                $"  Created account \"Contoso\" ({AccountA})",
                $"Created connection \"Jane Doe to Contoso\" ({MasterM})"
            }, h.Log.Lines);

            // Again: the master is found by its GUID and, standing in for a selected record, updated (decision: always update).
            int before = h.Log.Lines.Count;
            CopySummary second = h.Run("connection", MirrorR);

            UpdateRequest update = Assert.Single(h.Destination.Updates);
            Assert.Equal(("connection", MasterM), (update.Target.LogicalName, update.Target.Id));
            Assert.DoesNotContain("ismaster", update.Target.Attributes.Keys);
            Assert.DoesNotContain("relatedconnectionid", update.Target.Attributes.Keys);
            Assert.Equal(3, h.Destination.Creates.Count);   // nothing created the second time
            Assert.Equal(2, h.Destination.Records("connection").Count);
            Assert.Equal((0, 1, 2, 1), (second.Created, second.Updated, second.SkippedExisting, second.ConnectionMirrorsSkipped));
            Assert.Equal(new[]
            {
                MirrorSkipped(MirrorR, MasterM),
                $"Copying connection \"Jane Doe to Contoso\" ({MasterM})...",
                $"  Exists, skipped contact \"Jane Doe\" ({ContactC})",
                $"  Exists, skipped account \"Contoso\" ({AccountA})",
                $"Updated connection \"Jane Doe to Contoso\" ({MasterM})"
            }, h.Log.Lines.Skip(before));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Selecting_a_master_and_its_mirror_writes_the_master_once(bool masterFirst)
        {
            var h = NewHarness();
            SeedJaneAndContoso(h);

            CopySummary summary = masterFirst ? h.Run("connection", MasterM, MirrorR) : h.Run("connection", MirrorR, MasterM);

            Assert.Equal(("connection", MasterM), Key(Assert.Single(h.Destination.Creates, c => c.Target.LogicalName == "connection")));
            Assert.Empty(h.Destination.Updates);
            Assert.Equal(2, h.Destination.Records("connection").Count);
            Assert.Equal((3, 0, 1), (summary.Created, summary.Updated, summary.ConnectionMirrorsSkipped));
            Assert.Single(h.Log.Lines, l => l == MirrorSkipped(MirrorR, MasterM));
            Assert.Single(h.Log.Lines, l => l == $"Already copied earlier in this run: connection \"Jane Doe to Contoso\" ({MasterM})");
        }

        [Fact]
        public void A_rerun_finds_the_master_by_guid_skips_the_mirror_again_and_leaves_two_rows()
        {
            var h = NewHarness();
            SeedJaneAndContoso(h);
            FollowConnections(h);
            h.Run("contact", ContactC);
            int before = h.Log.Lines.Count;

            CopySummary summary = h.Run("contact", ContactC);

            Assert.Equal(3, h.Destination.Creates.Count);   // all from the first run
            UpdateRequest update = Assert.Single(h.Destination.Updates);   // the selected contact only: an existing child is skipped
            Assert.Equal(("contact", ContactC), (update.Target.LogicalName, update.Target.Id));
            Assert.Equal(2, h.Destination.Records("connection").Count);
            Assert.Equal((0, 1, 1, 1), (summary.Created, summary.Updated, summary.SkippedExisting, summary.ConnectionMirrorsSkipped));
            Assert.Equal(new[]
            {
                $"Copying contact \"Jane Doe\" ({ContactC})...",
                $"Updated contact \"Jane Doe\" ({ContactC})",
                "Children of contact \"Jane Doe\" via contact_connections1: 1 connection record",
                $"  Exists, skipped connection \"Jane Doe to Contoso\" ({MasterM})",
                "Children of contact \"Jane Doe\" via contact_connections2: 1 connection record",
                "  " + MirrorSkipped(MirrorR, MasterM)
            }, h.Log.Lines.Skip(before));
        }

        [Fact]
        public void A_mirror_met_again_is_read_logged_and_counted_once()
        {
            var h = NewHarness();
            FollowConnections(h);
            h.Source.Add(Record("contact", ContactC, ("fullname", "Jane Doe"), ("lastname", "Doe")));
            h.Source.Add(Record("contact", ContactD, ("fullname", "John Roe"), ("lastname", "Roe")));
            AddPair(h.Source, MasterN, "Jane Doe to John Roe", MirrorS, "John Roe to Jane Doe", Ref("contact", ContactC, "Jane Doe"), Ref("contact", ContactD, "John Roe"));

            CopySummary summary = h.Run("contact", ContactC, ContactD);

            Assert.Equal(new[] { ("contact", ContactC), ("contact", ContactD), ("connection", MasterN) }, h.Destination.Creates.Select(Key));
            Assert.Equal(2, h.Destination.Records("connection").Count);
            Assert.Single(h.Source.Retrieves, r => r.Target.Id == MirrorS);   // read from the source once
            Assert.Equal((3, 4, 1), (summary.Created, summary.ChildRecordsFound, summary.ConnectionMirrorsSkipped));
            Assert.Equal(new[]
            {
                $"Copying contact \"Jane Doe\" ({ContactC})...",
                $"Created contact \"Jane Doe\" ({ContactC})",
                "Children of contact \"Jane Doe\" via contact_connections1: 1 connection record",
                $"  Copying connection \"Jane Doe to John Roe\" ({MasterN})...",
                $"    Copying contact \"John Roe\" ({ContactD})...",
                $"    Created contact \"John Roe\" ({ContactD})",
                $"  Created connection \"Jane Doe to John Roe\" ({MasterN})",
                "Children of contact \"Jane Doe\" via contact_connections2: 1 connection record",
                "  " + MirrorSkipped(MirrorS, MasterN),
                $"  Already copied earlier in this run: connection \"Jane Doe to John Roe\" ({MasterN})",
                $"Already copied earlier in this run: contact \"John Roe\" ({ContactD})",
                "Children of contact \"John Roe\" via contact_connections1: 1 connection record",
                $"  Already handled earlier in this run: connection {MirrorS} (mirror of {MasterN})",
                $"  Already copied earlier in this run: connection \"Jane Doe to John Roe\" ({MasterN})",
                "Children of contact \"John Roe\" via contact_connections2: 1 connection record",
                $"  Already copied earlier in this run: connection \"Jane Doe to John Roe\" ({MasterN})"
            }, h.Log.Lines);
        }

        [Fact]
        public void A_lookup_to_a_mirror_is_blanked_and_its_master_copied_as_a_related_record()
        {
            var h = NewHarness();
            SeedJaneAndContoso(h);
            h.Source.Add(Record("new_widget", WidgetW, ("new_name", "Widget"), ("new_connectionid", Ref("connection", MirrorR))));

            CopySummary summary = h.Run("new_widget", WidgetW);

            // The mirror's GUID cannot be kept (the destination's mirror has another one): the lookup is blanked.
            Assert.Equal(new[] { ("contact", ContactC), ("account", AccountA), ("connection", MasterM), ("new_widget", WidgetW) },
                h.Destination.Creates.Select(Key));
            Assert.False(h.Destination.Creates[3].Target.Contains("new_connectionid"));
            Assert.Equal(2, h.Destination.Records("connection").Count);
            Assert.Equal((4, 1, 1, 0), (summary.Created, summary.LookupsBlanked, summary.ConnectionMirrorsSkipped, summary.Failed));
            Assert.Equal(new[]
            {
                $"Copying new_widget \"Widget\" ({WidgetW})...",
                "  " + MirrorSkipped(MirrorR, MasterM),
                $"  Copying connection \"Jane Doe to Contoso\" ({MasterM})...",
                $"    Copying contact \"Jane Doe\" ({ContactC})...",
                $"    Created contact \"Jane Doe\" ({ContactC})",
                $"    Copying account \"Contoso\" ({AccountA})...",
                $"    Created account \"Contoso\" ({AccountA})",
                $"  Created connection \"Jane Doe to Contoso\" ({MasterM})",
                $"  Blanked new_connectionid on new_widget \"Widget\": related connection {MirrorR} could not be copied",
                $"Created new_widget \"Widget\" ({WidgetW})"
            }, h.Log.Lines);
        }

        [Fact]
        public void A_mirror_without_relatedconnectionid_is_a_warning_and_nothing_is_written()
        {
            var h = NewHarness();
            SeedJaneAndContoso(h);
            h.Source.Add(Record("connection", OrphanO, ("name", "Orphan"), ("record1id", Ref("account", AccountA)), ("record2id", Ref("contact", ContactC)),
                ("ismaster", false)));

            CopySummary summary = h.Run("connection", OrphanO);

            Assert.Empty(h.Destination.Writes);
            Assert.Equal(new[] { OrphanO }, h.Source.Retrieves.Select(r => r.Target.Id));   // its records are not even read
            Assert.Equal((0, 0, 1), (summary.Created, summary.Failed, summary.ConnectionMirrorsSkipped));
            Assert.Empty(summary.Errors);
            Assert.Equal((LogLevel.Warning, $"Skipped connection {OrphanO}: mirror without relatedconnectionid, its master is unknown; not copied"),
                Assert.Single(h.Log.Entries));
        }

        [Fact]
        public void Two_mirrors_naming_each_other_are_a_warning_without_looping()
        {
            var h = NewHarness();
            SeedJaneAndContoso(h);
            // Corrupt data: R's master claims to be a mirror too, of R.
            h.Source.Add(Record("connection", MasterM, ("name", "Jane Doe to Contoso"), ("record1id", Ref("contact", ContactC)), ("record2id", Ref("account", AccountA)),
                ("ismaster", false), ("relatedconnectionid", Ref("connection", MirrorR))));

            CopySummary summary = h.Run("connection", MirrorR);

            Assert.Empty(h.Destination.Writes);
            Assert.Equal((0, 0, 2), (summary.Created, summary.Failed, summary.ConnectionMirrorsSkipped));
            Assert.Equal(new[]
            {
                (LogLevel.Info, MirrorSkipped(MirrorR, MasterM)),
                (LogLevel.Warning, $"Skipped connection {MasterM}: mirror of {MirrorR}, which is a mirror too; not copied")
            }, h.Log.Entries);
        }

        [Fact]
        public void Dry_run_skips_the_mirror_with_the_dry_run_prefix_and_counts_it()
        {
            var dry = NewHarness();
            dry.Options.DryRun = true;
            SeedJaneAndContoso(dry);
            FollowConnections(dry);
            CopySummary drySummary = dry.Run("contact", ContactC);

            var real = NewHarness();
            SeedJaneAndContoso(real);
            FollowConnections(real);
            CopySummary realSummary = real.Run("contact", ContactC);

            Assert.Empty(dry.Destination.Writes);
            Assert.All(dry.Destination.Executed, r => Assert.IsType<RetrieveMultipleRequest>(r));
            Assert.Contains("  [DRY RUN] " + MirrorSkipped(MirrorR, MasterM), dry.Log.Lines);
            Assert.Equal(real.Log.Entries.Select(e => e.Level), dry.Log.Entries.Select(e => e.Level));
            Assert.Equal(real.Log.Lines, dry.Log.Lines.Select(l => l
                .Replace("[DRY RUN] Would create ", "Created ")
                .Replace("[DRY RUN] Skipped connection ", "Skipped connection ")));
            Assert.Equal((3, 2, 1), (drySummary.Created, drySummary.ChildRecordsFound, drySummary.ConnectionMirrorsSkipped));
            Assert.Equal((3, 2, 1), (realSummary.Created, realSummary.ChildRecordsFound, realSummary.ConnectionMirrorsSkipped));
            Assert.Equal(1, dry.Progress.Last().ConnectionMirrorsSkipped);
        }
    }
}
