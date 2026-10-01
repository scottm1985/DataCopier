using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Core.Schema;
using MyscotekDataCopier.Tests.Fakes;
using MyscotekDataCopier.UI;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// The relationship picker (SPEC 5.10) on its own, against a fake schema: lazy loading level by
    /// level, ticks per entity (synchronised wherever the entity appears), the buttons, saved
    /// selections re-applied, and failures. The dialog is shown modeless and off-screen.
    /// </summary>
    public class RelationshipPickerFormTests
    {
        private const string Dash = " — ";

        private static FakeSchemaProvider Schema()
        {
            var schema = new FakeSchemaProvider();
            schema.Entity("account", "name").EntityDisplayName("Account", "Accounts")
                    .OneToMany("contact_customer_accounts", "contact", "parentcustomerid")
                    .OneToMany("Account_Tasks", "task", "regardingobjectid")
                    .OneToMany("new_account_widgets", "new_widget", "new_accountid", custom: true)
                    .OneToMany("Account_AsyncOperations", "asyncoperation", "regardingobjectid")   // system-excluded: never shown
                .Entity("contact", "fullname").EntityDisplayName("Contact", "Contacts").Customer("parentcustomerid")
                    .OneToMany("Contact_Tasks", "task", "regardingobjectid")
                    .OneToMany("contact_customer_contacts", "contact", "parentcustomerid")
                    .OneToMany("new_contact_widgets", "new_widget", "new_contactid", custom: true)
                .Entity("task", "subject", "activityid").EntityDisplayName("Task", "Tasks").Lookup("regardingobjectid", "account", "contact")
                .Entity("new_widget", "new_name").EntityDisplayName("Widget").Lookup("new_accountid", "account").Lookup("new_contactid", "contact")
                .Entity("asyncoperation", "name").Lookup("regardingobjectid", "account");
            return schema;
        }

        private static readonly Dictionary<string, string[]> Subgrids = new Dictionary<string, string[]>
        {
            ["account"] = new[] { "contact_customer_accounts", "Account_Tasks" },
            ["contact"] = new[] { "Contact_Tasks" }
        };

        private sealed class Picker : IDisposable
        {
            public Picker(FakeSchemaProvider schema, Dictionary<string, ISet<string>> configured = null,
                          Func<string, IReadOnlyCollection<string>> subgrids = null)
            {
                SubgridRequests = new List<string>();
                Form = new RelationshipPickerForm("account", "Account", schema,
                    subgrids ?? (entity =>
                    {
                        lock (SubgridRequests) SubgridRequests.Add(entity);
                        return Subgrids.TryGetValue(entity, out string[] names) ? names : Array.Empty<string>();
                    }),
                    new CopyOptions().NeverCreateEntities, configured)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -3000)
                };
                Form.ShowMessage = (text, icon) =>
                {
                    Messages.Add((icon, text));
                    return DialogResult.OK;
                };
                Form.Show();
                WaitIdle("the account relationships");
            }

            public RelationshipPickerForm Form { get; }
            public List<(MessageBoxIcon Icon, string Text)> Messages { get; } = new List<(MessageBoxIcon Icon, string Text)>();
            public List<string> SubgridRequests { get; }
            public TreeNode Root => Form.RootNode;

            public void WaitIdle(string what) =>
                UiTestHost.PumpUntil(() => !Form.IsLoading && Root != null && !Root.Nodes.Cast<TreeNode>().Any(n => n.Text == RelationshipPickerForm.LoadingText), what);

            public TreeNode Expand(TreeNode node)
            {
                node.Expand();
                UiTestHost.PumpUntil(() => !Form.IsLoading && !(node.Nodes.Count == 1 && node.Nodes[0].Text == RelationshipPickerForm.LoadingText),
                    "the relationships under " + node.Text);
                return node;
            }

            public void Click(string button)
            {
                UiTestHost.Find<Button>(Form, button).PerformClick();
                UiTestHost.PumpUntil(() => !Form.IsLoading, button);
                UiTestHost.Pump(20);
            }

            public void Dispose() => Form.Dispose();
        }

        private static TreeNode Node(TreeNode parent, string relationship) =>
            parent.Nodes.Cast<TreeNode>().Single(n => n.Name == relationship);

        private static string[] Texts(TreeNode parent) => parent.Nodes.Cast<TreeNode>().Select(n => n.Text).ToArray();

        private static string[] Ticked(TreeNode parent) => parent.Nodes.Cast<TreeNode>().Where(n => n.Checked).Select(n => n.Name).ToArray();

        /// <summary>The native state image of a node: 0 none (no check box), 1 unticked, 2 ticked.</summary>
        private static int StateImage(TreeNode node) =>
            (int)(((long)SendMessage(node.TreeView.Handle, TvmGetItemState, node.Handle, (IntPtr)TvisStateImageMask) & TvisStateImageMask) >> 12);

        private const int TvmGetItemState = 0x1100 + 39;
        private const int TvisStateImageMask = 0xF000;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [Fact]
        public void The_root_lists_the_eligible_relationships_subgrids_first_and_pre_ticked()
        {
            UiTestHost.Run(() =>
            {
                using (var picker = new Picker(Schema()))
                {
                    Assert.Equal("1:N relationships to copy - Account (account)", picker.Form.Text);
                    Assert.Equal(new Size(700, 500), picker.Form.Size);
                    Assert.Equal(new Font("Segoe UI", 9f), picker.Form.Font);
                    Assert.Equal(FormBorderStyle.Sizable, picker.Form.FormBorderStyle);
                    Assert.Equal("Account (account)", picker.Root.Text);
                    Assert.True(picker.Root.IsExpanded);
                    Assert.Equal(new[]
                    {
                        "Contacts (contact) via parentcustomerid" + Dash + "contact_customer_accounts [subgrid]",
                        "Tasks (task) via regardingobjectid" + Dash + "Account_Tasks [subgrid]",
                        "Widget (new_widget) via new_accountid" + Dash + "new_account_widgets [custom]"
                    }, Texts(picker.Root));
                    Assert.Equal(new[] { "contact_customer_accounts", "Account_Tasks" }, Ticked(picker.Root));
                    Assert.All(picker.Root.Nodes.Cast<TreeNode>(), n => Assert.Equal(RelationshipPickerForm.LoadingText, Assert.Single(n.Nodes.Cast<TreeNode>()).Text));

                    // The root and the "Loading..." lines have no check box (state image 0) and cannot be ticked.
                    Assert.Equal(0, StateImage(picker.Root));
                    Assert.Equal(0, StateImage(picker.Root.Nodes[0].Nodes[0]));
                    Assert.Equal(2, StateImage(picker.Root.Nodes[0]));   // ticked
                    Assert.Equal(1, StateImage(picker.Root.Nodes[2]));   // unticked
                    picker.Root.Checked = true;
                    picker.Root.Nodes[0].Nodes[0].Checked = true;
                    Assert.False(picker.Root.Checked);
                    Assert.False(picker.Root.Nodes[0].Nodes[0].Checked);
                    Assert.Equal(0, StateImage(picker.Root));

                    Assert.Empty(picker.Form.ConfiguredSelections);   // nothing changed: every entity follows its subgrids
                    Assert.Equal("No entity configured: every entity follows the subgrids on its main forms.", picker.Form.StatusText);
                    Assert.Equal(new[] { "account" }, picker.SubgridRequests);   // the child entities are read only when expanded
                    Assert.Empty(picker.Messages);
                }
            });
        }

        [Fact]
        public void Relationships_expand_lazily_into_the_child_entity_and_its_ticks_follow_it_everywhere()
        {
            UiTestHost.Run(() =>
            {
                FakeSchemaProvider schema = Schema();
                using (var picker = new Picker(schema))
                {
                    TreeNode contacts = picker.Expand(Node(picker.Root, "contact_customer_accounts"));
                    Assert.True(contacts.IsExpanded);
                    Assert.Equal(new[]
                    {
                        "Tasks (task) via regardingobjectid" + Dash + "Contact_Tasks [subgrid]",
                        "Contacts (contact) via parentcustomerid" + Dash + "contact_customer_contacts",
                        "Widget (new_widget) via new_contactid" + Dash + "new_contact_widgets [custom]"
                    }, Texts(contacts));
                    Assert.Equal(new[] { "Contact_Tasks" }, Ticked(contacts));

                    // The same entity one level deeper: loaded once, shown again.
                    int requests = schema.RequestsSoFar().Count;
                    TreeNode deeper = picker.Expand(Node(contacts, "contact_customer_contacts"));
                    Assert.True(deeper.IsExpanded);
                    Assert.Equal(Texts(contacts), Texts(deeper));
                    Assert.Equal(requests, schema.RequestsSoFar().Count);
                    Assert.Equal(new[] { "account", "contact" }, picker.SubgridRequests);

                    // Ticking under one contact node ticks it under the other: the ticks belong to the entity.
                    Node(deeper, "new_contact_widgets").Checked = true;
                    Assert.Equal(new[] { "Contact_Tasks", "new_contact_widgets" }, Ticked(contacts));
                    Assert.Equal(new[] { "Contact_Tasks", "new_contact_widgets" }, Ticked(deeper));
                    Node(contacts, "Contact_Tasks").Checked = false;
                    Assert.Equal(new[] { "new_contact_widgets" }, Ticked(deeper));

                    IReadOnlyDictionary<string, ISet<string>> configured = picker.Form.ConfiguredSelections;
                    Assert.Equal(new[] { "contact" }, configured.Keys);   // account was not changed
                    Assert.Equal(new[] { "new_contact_widgets" }, configured["contact"]);
                    Assert.Equal("Configured: contact (1 ticked). Every other entity follows its main-form subgrids.", picker.Form.StatusText);

                    // Unticking a root relationship configures the root entity with the rest of its ticks.
                    Node(picker.Root, "contact_customer_accounts").Checked = false;
                    Assert.Equal(new[] { "Account_Tasks" }, picker.Form.ConfiguredSelections["account"]);
                    Assert.True(Node(picker.Root, "contact_customer_accounts").IsExpanded);   // unticking does not hide the level below
                    Assert.Empty(picker.Messages);
                }
            });
        }

        [Fact]
        public void The_buttons_act_on_the_selected_relationship_child_entity_or_the_root()
        {
            UiTestHost.Run(() =>
            {
                using (var picker = new Picker(Schema()))
                {
                    TreeNode contacts = Node(picker.Root, "contact_customer_accounts");
                    picker.Form.Tree.SelectedNode = contacts;   // not expanded yet: the buttons load it first

                    picker.Click("untickAllButton");
                    Assert.True(contacts.IsExpanded);
                    Assert.Empty(Ticked(contacts));
                    picker.Click("tickCustomButton");
                    Assert.Equal(new[] { "new_contact_widgets" }, Ticked(contacts));
                    picker.Click("tickSubgridsButton");
                    Assert.Equal(new[] { "Contact_Tasks", "new_contact_widgets" }, Ticked(contacts));
                    Assert.Equal(new[] { "Account_Tasks", "contact_customer_accounts" }, Ticked(picker.Root).OrderBy(n => n, StringComparer.Ordinal));   // the root: untouched

                    // The "Loading..." line of a selected node stands for that node's entity; the root otherwise.
                    picker.Form.Tree.SelectedNode = picker.Root;
                    picker.Click("untickAllButton");
                    Assert.Empty(Ticked(picker.Root));
                    picker.Click("tickCustomButton");
                    Assert.Equal(new[] { "new_account_widgets" }, Ticked(picker.Root));

                    // An entity without eligible relationships: an explanatory line, and configured with nothing ticked.
                    TreeNode tasks = Node(picker.Root, "Account_Tasks");
                    picker.Form.Tree.SelectedNode = tasks.Nodes[0];
                    picker.Click("tickSubgridsButton");
                    Assert.Equal(RelationshipPickerForm.NoRelationshipsText, Assert.Single(tasks.Nodes.Cast<TreeNode>()).Text);
                    tasks.Nodes[0].Checked = true;
                    Assert.False(tasks.Nodes[0].Checked);

                    IReadOnlyDictionary<string, ISet<string>> configured = picker.Form.ConfiguredSelections;
                    Assert.Equal(new[] { "account", "contact", "task" }, configured.Keys.OrderBy(k => k, StringComparer.Ordinal));
                    Assert.Equal(new[] { "new_account_widgets" }, configured["account"]);
                    Assert.Empty(configured["task"]);
                    Assert.Empty(picker.Messages);

                    UiTestHost.Find<Button>(picker.Form, "okButton").PerformClick();
                    Assert.Equal(DialogResult.OK, picker.Form.DialogResult);
                }
            });
        }

        [Fact]
        public void Saved_selections_are_re_applied_instead_of_the_subgrids()
        {
            UiTestHost.Run(() =>
            {
                var saved = new Dictionary<string, ISet<string>>
                {
                    ["account"] = new HashSet<string> { "new_account_widgets", "relationship_that_is_gone" },
                    ["contact"] = new HashSet<string>()
                };
                using (var picker = new Picker(Schema(), saved))
                {
                    Assert.Equal(new[] { "new_account_widgets" }, Ticked(picker.Root));
                    Assert.Empty(Ticked(picker.Expand(Node(picker.Root, "contact_customer_accounts"))));
                    Assert.Equal("Configured: account (2 ticked), contact (none). Every other entity follows its main-form subgrids.", picker.Form.StatusText);

                    IReadOnlyDictionary<string, ISet<string>> configured = picker.Form.ConfiguredSelections;
                    Assert.Equal(new[] { "new_account_widgets", "relationship_that_is_gone" }, configured["account"].OrderBy(n => n, StringComparer.Ordinal));
                    Assert.NotSame(saved["account"], configured["account"]);   // a copy

                    UiTestHost.Find<Button>(picker.Form, "cancelButton").PerformClick();
                    Assert.Equal(DialogResult.Cancel, picker.Form.DialogResult);
                }
            });
        }

        [Fact]
        public void A_failed_load_is_shown_and_expanding_again_retries()
        {
            UiTestHost.Run(() =>
            {
                FakeSchemaProvider schema = Schema();
                using (var picker = new Picker(schema))
                {
                    TreeNode contacts = Node(picker.Root, "contact_customer_accounts");
                    schema.Failures["contact"] = new InvalidOperationException("metadata unavailable");
                    contacts.Expand();
                    UiTestHost.PumpUntil(() => !picker.Form.IsLoading && picker.Messages.Count == 1, "the error");

                    Assert.Equal((MessageBoxIcon.Error, "The 1:N relationships of contact could not be loaded: metadata unavailable"), picker.Messages[0]);
                    Assert.False(contacts.IsExpanded);
                    Assert.Equal(RelationshipPickerForm.LoadingText, Assert.Single(contacts.Nodes.Cast<TreeNode>()).Text);

                    schema.Failures.Clear();
                    picker.Expand(contacts);
                    Assert.Equal(3, contacts.Nodes.Count);
                    Assert.True(contacts.IsExpanded);   // shown expanded once loaded
                    Assert.Single(picker.Messages);
                }
            });
        }

        [Fact]
        public void Main_forms_that_cannot_be_read_leave_nothing_pre_ticked_with_a_warning()
        {
            UiTestHost.Run(() =>
            {
                using (var picker = new Picker(Schema(), subgrids: entity => throw new InvalidOperationException("missing prvReadSystemForm")))
                {
                    Assert.Equal(3, picker.Root.Nodes.Count);
                    Assert.Empty(Ticked(picker.Root));
                    Assert.DoesNotContain(Texts(picker.Root), t => t.Contains("[subgrid]"));
                    Assert.Equal((MessageBoxIcon.Warning, "The main forms of account could not be read, so none of its relationships is pre-ticked: missing prvReadSystemForm"),
                        Assert.Single(picker.Messages));
                }
            });
        }

        [Fact]
        public void A_root_entity_that_cannot_be_read_is_reported()
        {
            UiTestHost.Run(() =>
            {
                FakeSchemaProvider schema = Schema();
                schema.Failures["account"] = new InvalidOperationException("Principal user is missing prvReadEntity privilege");
                var messages = new List<string>();
                using (var form = new RelationshipPickerForm("account", "Account", schema, entity => Array.Empty<string>(), null, null)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -3000)
                })
                {
                    form.ShowMessage = (text, icon) =>
                    {
                        messages.Add(text);
                        return DialogResult.OK;
                    };
                    form.Show();
                    UiTestHost.PumpUntil(() => !form.IsLoading && messages.Count == 1, "the error");

                    Assert.Equal("The 1:N relationships of account could not be loaded: Principal user is missing prvReadEntity privilege", messages[0]);
                    Assert.Equal(RelationshipPickerForm.LoadingText, Assert.Single(form.RootNode.Nodes.Cast<TreeNode>()).Text);
                    Assert.Equal(messages[0], form.StatusText);
                }
            });
        }

        [Fact]
        public void Relationship_text_names_the_child_entity_the_lookup_and_the_relationship_with_its_tags()
        {
            var relationship = new ChildRelationship
            {
                SchemaName = "contact_customer_accounts", ParentEntity = "account", ChildEntity = "Contact", ChildLookupAttribute = "parentcustomerid"
            };
            Assert.Equal("Contacts (contact) via parentcustomerid — contact_customer_accounts [subgrid]",
                RelationshipPickerForm.RelationshipText(relationship, "Contacts", isSubgrid: true));
            relationship.IsCustomRelationship = true;
            Assert.Equal("contact (contact) via parentcustomerid — contact_customer_accounts [subgrid] [custom]",
                RelationshipPickerForm.RelationshipText(relationship, null, isSubgrid: true));
        }
    }
}
