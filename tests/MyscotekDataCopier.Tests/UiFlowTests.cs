using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using MyscotekDataCopier.Tests.Fakes;
using MyscotekDataCopier.UI;
using XrmToolBox.Extensibility;
using Xunit;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// Drives the real control end to end against fake source and destination services (see
    /// <see cref="UiScenario"/>): connecting, the entity list, views, paged loading, the client-side
    /// filter, ticking, the destination, a dry-run copy, the never-create and same-organisation guards,
    /// errors and Cancel. The message pump is run explicitly (<see cref="UiTestHost.PumpUntil"/>).
    /// </summary>
    public class UiFlowTests
    {
        [Fact]
        public void Connect_load_filter_tick_and_copy_as_a_dry_run()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    // 1. Source connected: entities listed by display name, the last entity re-selected, its views loaded.
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the entities and the account views");

                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    Assert.Equal(new[] { "Account", "Contact", "User" }, entities.Items.Cast<ListViewItem>().Select(i => i.Text));
                    Assert.Equal("account", Assert.Single(entities.SelectedItems.Cast<ListViewItem>()).Name);
                    Assert.Equal("Active Accounts", views.Text);
                    Assert.Equal("Source: (unnamed connection)", UiTestHost.FindToolItem<ToolStripLabel>(control, "sourceLabel").Text);
                    Assert.Contains("3 entities loaded from the source.", UiTest.LogText(control));

                    // 2. Page 1 (page size 3 from the settings), then page 2 with page 1's paging cookie.
                    Button loadRecords = UiTestHost.Find<Button>(control, "loadRecordsButton");
                    Assert.True(loadRecords.Enabled);
                    loadRecords.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 3, "page 1");
                    Label count = UiTestHost.Find<Label>(control, "recordCountLabel");
                    Assert.Equal("3 records loaded (more available)", count.Text);

                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "recordGrid");
                    List<DataGridViewColumn> visible = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
                    Assert.Equal(new[] { "Copy", "Account Name", "Account Number" }, visible.Select(c => c.HeaderText));   // display names
                    Assert.Equal(new[] { "name", "accountnumber" }, visible.Skip(1).Select(c => c.ToolTipText));        // the raw names
                    Assert.Equal(new[] { 300, 120 }, visible.Skip(1).Select(c => c.Width));
                    Assert.All(visible.Skip(1), c => Assert.True(c.ReadOnly));

                    Button loadMore = UiTestHost.Find<Button>(control, "loadMoreButton");
                    Assert.True(loadMore.Enabled);
                    loadMore.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 5, "page 2");
                    Assert.Equal("5 records loaded (all)", count.Text);
                    Assert.False(loadMore.Enabled);
                    Assert.False(UiTestHost.Find<Button>(control, "loadAllButton").Enabled);
                    Assert.Equal(new[] { 1, 2 }, scenario.PagesRequested);
                    Assert.Equal(new[] { 3, 3 }, scenario.PageSizes);
                    Assert.Equal(new[] { null, "<cookie page=\"1\" />" }, scenario.Cookies);

                    // 3. Client-side filter (after the typing pause); wildcards and quotes match literally.
                    TextBox filter = UiTestHost.Find<TextBox>(control, "recordFilter");
                    Label selected = UiTestHost.Find<Label>(control, "selectedLabel");
                    filter.Text = "contoso";
                    UiTestHost.PumpUntil(() => grid.Rows.Count == 2, "the filter to apply");
                    UiTestHost.Find<Button>(control, "selectAllButton").PerformClick();   // acts on the filtered rows
                    Assert.Equal("Selected: 2", selected.Text);

                    SetFilter(control, filter, "[UK] 50%");
                    Assert.Equal("Contoso [UK] 50%", Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Cells[2].Value);
                    Assert.Equal("Selected: 2 (1 hidden by the filter)", selected.Text);
                    SetFilter(control, filter, "o'neil");
                    Assert.Equal("O'Neil's Bakery", Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Cells[2].Value);
                    SetFilter(control, filter, "wind*");
                    Assert.Equal("Northwind*", Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Cells[2].Value);
                    SetFilter(control, filter, "A-3");   // any visible column
                    Assert.Equal("Fabrikam", Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Cells[2].Value);
                    UiTestHost.Find<Button>(control, "selectNoneButton").PerformClick();   // only unticked rows are visible: no change
                    Assert.Equal("Selected: 2 (2 hidden by the filter)", selected.Text);
                    SetFilter(control, filter, string.Empty);
                    Assert.Equal(5, grid.Rows.Count);
                    Assert.Equal("Selected: 2", selected.Text);   // ticks survived the filtering

                    // A single click on a tick box counts at once.
                    ClickCopyCell(grid, 2);   // Fabrikam
                    Assert.Equal("Selected: 3", selected.Text);
                    ClickCopyCell(grid, 2);
                    Assert.Equal("Selected: 2", selected.Text);
                    Assert.Equal(new[] { scenario.Accounts[0].Id, scenario.Accounts[1].Id }, control.GetSelectedIds());

                    // 4. Destination: Copy becomes available; Service is still the source.
                    Button copy = UiTestHost.Find<Button>(control, "copyButton");
                    Assert.False(copy.Enabled);
                    control.UpdateConnection(scenario.Destination, null, DataCopierControl.DestinationActionName, DataCopierControl.DestinationParameter);
                    Assert.True(copy.Enabled);
                    Assert.Same(scenario.Source, control.Service);
                    Assert.Equal("Destination: (unnamed connection)", UiTestHost.FindToolItem<ToolStripLabel>(control, "destinationLabel").Text);

                    // 5. Dry run: the engine runs; header, engine lines and summary are logged; nothing is written.
                    UiTestHost.Find<CheckBox>(control, "dryRunCheckBox").Checked = true;
                    Assert.True(scenario.LastSavedDryRun);
                    copy.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTest.LogText(control).Contains("---- Summary"), "the copy");

                    string log = UiTest.LogText(control);
                    Assert.Contains("] Copying 2 account records from (unnamed connection) to (unnamed connection)", log);
                    Assert.Contains("] Options: dry run on, preserve created on off, bypass custom plugins off, copy N:1 relationships (lookups) on, " +
                                    "copy 1:N relationships (subgrids) off; never created: businessunit, organization, systemuser, team, transactioncurrency", log);
                    Assert.DoesNotContain("1:N relationships followed", log);
                    Assert.Contains("] DRY RUN: nothing will be written to the destination.", log);
                    Assert.Contains($"] [DRY RUN] Would create account \"Contoso Ltd\" ({scenario.Accounts[0].Id})", log);
                    Assert.Contains($"] [DRY RUN] Would create account \"Contoso [UK] 50%\" ({scenario.Accounts[1].Id})", log);
                    Assert.Contains("] ---- Summary (dry run - nothing was written): 2 selected records ----", log);
                    Assert.Contains("]   Created" + new string(' ', 13) + "2", log);
                    Assert.Empty(scenario.Destination.Writes);
                    Assert.Equal("Dry run done: 2 / 2 - created 2, failed 0", UiTestHost.Find<Label>(control, "progressLabel").Text);
                    Assert.False(UiTestHost.Find<Button>(control, "cancelButton").Enabled);
                    Assert.True(copy.Enabled);
                    Assert.Empty(scenario.Dialogs.Messages);
                    // The source metadata was read once, off the UI thread, for the headers; the copy reused it.
                    Assert.Equal(new[] { "account" }, scenario.MetadataRequests);
                    Assert.DoesNotContain("UI test", scenario.MetadataThreads);
                }
            });
        }

        [Fact]
        public void Copying_a_never_create_entity_is_refused()
        {
            var scenario = new UiScenario();
            scenario.Settings.LastEntity = "systemuser";
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the systemuser views");
                    Assert.Equal("(All records)", views.Text);   // no saved views: synthesised
                    Assert.Contains("systemuser is in the never-create list: its records can be listed but not copied.", UiTest.LogText(control));

                    UiTestHost.Find<Button>(control, "loadAllButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 1, "the users");
                    UiTestHost.Find<Button>(control, "selectAllButton").PerformClick();
                    control.UpdateConnection(scenario.Destination, null, DataCopierControl.DestinationActionName, DataCopierControl.DestinationParameter);

                    Button copy = UiTestHost.Find<Button>(control, "copyButton");
                    Assert.True(copy.Enabled);
                    copy.PerformClick();
                    UiTestHost.Pump();

                    Assert.StartsWith("systemuser is in the never-create list (businessunit, organization, systemuser, team, transactioncurrency).",
                        Assert.Single(scenario.Dialogs.Messages));
                    string log = UiTest.LogText(control);
                    Assert.Contains("Copy refused: systemuser is in the never-create list.", log);
                    Assert.DoesNotContain("Copying 1", log);
                    Assert.Empty(scenario.Destination.Executed);
                }
            });
        }

        [Fact]
        public void A_virtual_table_is_marked_in_the_entity_list_and_copying_it_is_refused()
        {
            var scenario = new UiScenario { IncludeVirtualEntity = true };
            scenario.Settings.LastEntity = "new_vatrate";
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the new_vatrate views");

                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    Assert.Equal(new[] { "Account", "Contact", "User", "VAT Rate" }, entities.Items.Cast<ListViewItem>().Select(i => i.Text));
                    Assert.Equal(new[] { "account", "contact", "systemuser", "new_vatrate (virtual)" },
                        entities.Items.Cast<ListViewItem>().Select(i => i.SubItems[1].Text));
                    Assert.Equal("new_vatrate", Assert.Single(entities.SelectedItems.Cast<ListViewItem>()).Name);
                    Assert.Contains("new_vatrate is a virtual table: its records can be listed but not copied.", UiTest.LogText(control));

                    UiTestHost.Find<Button>(control, "loadAllButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 1, "the VAT rates");
                    UiTestHost.Find<Button>(control, "selectAllButton").PerformClick();
                    control.UpdateConnection(scenario.Destination, null, DataCopierControl.DestinationActionName, DataCopierControl.DestinationParameter);

                    Button copy = UiTestHost.Find<Button>(control, "copyButton");
                    Assert.True(copy.Enabled);
                    copy.PerformClick();
                    UiTestHost.Pump();

                    Assert.StartsWith("new_vatrate is a virtual table: its rows live in an external data source and cannot be created here.",
                        Assert.Single(scenario.Dialogs.Messages));
                    string log = UiTest.LogText(control);
                    Assert.Contains("Copy refused: new_vatrate is a virtual table.", log);
                    Assert.DoesNotContain("Copying 1", log);
                    Assert.Empty(scenario.Destination.Executed);
                }
            });
        }

        [Fact]
        public void Copying_into_the_same_organisation_asks_first_and_No_copies_nothing()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ComboBox>(control, "viewCombo").Items.Count > 0, "the account views");
                    UiTestHost.Find<Button>(control, "loadAllButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 5, "all accounts");
                    UiTestHost.Find<Button>(control, "selectAllButton").PerformClick();

                    // The same connection picked as the destination: only a log line, no dialog yet.
                    control.UpdateConnection(scenario.Source, null, DataCopierControl.DestinationActionName, DataCopierControl.DestinationParameter);
                    UiTestHost.PumpUntil(() => UiTest.LogText(control).Contains("The destination looks like the same organisation as the source"), "the heads-up line");
                    Assert.Empty(scenario.Dialogs.Messages);

                    scenario.Dialogs.Answer = DialogResult.No;
                    UiTestHost.Find<Button>(control, "copyButton").PerformClick();
                    UiTestHost.Pump();

                    Assert.StartsWith("The destination appears to be the SAME organisation as the source", Assert.Single(scenario.Dialogs.Messages));
                    string log = UiTest.LogText(control);
                    Assert.Contains("Copy not started: same organisation not confirmed.", log);
                    Assert.DoesNotContain("] Copying 5 account records", log);
                    Assert.False(control.IsBusy);
                }
            });
        }

        [Fact]
        public void A_failure_is_logged_and_shown_and_Refresh_entities_retries()
        {
            var scenario = new UiScenario { FailEntityList = true };
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && scenario.Dialogs.Messages.Count == 1, "the error");

                    Assert.Equal("Simulated metadata failure", scenario.Dialogs.Messages[0]);
                    Assert.Contains("Loading entities failed: Simulated metadata failure", UiTest.LogText(control));
                    Assert.Equal(string.Empty, UiTestHost.Find<Label>(control, "progressLabel").Text);   // no stale "Loading entities..."
                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    Assert.Empty(entities.Items);

                    scenario.FailEntityList = false;
                    ToolStripButton refresh = UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton");
                    Assert.True(refresh.Enabled);
                    refresh.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && entities.Items.Count == 3 && UiTestHost.Find<ComboBox>(control, "viewCombo").Items.Count > 0,
                        "the entities after the retry");
                }
            });
        }

        [Fact]
        public void While_busy_the_inputs_are_disabled_and_Cancel_stops_Load_all_after_the_current_page()
        {
            var scenario = new UiScenario();
            using (var pageOneRequested = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                scenario.BeforePage = page =>
                {
                    if (page != 1) return;
                    pageOneRequested.Set();
                    release.Wait(TimeSpan.FromSeconds(20));
                };
                UiTestHost.Run(() =>
                {
                    using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                    {
                        control.UpdateConnection(scenario.Source, null, string.Empty, null);
                        UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ComboBox>(control, "viewCombo").Items.Count > 0, "the account views");

                        UiTestHost.Find<Button>(control, "loadAllButton").PerformClick();
                        UiTestHost.PumpUntil(() => pageOneRequested.IsSet, "page 1 to be requested");

                        Assert.True(control.IsBusy);
                        Button cancel = UiTestHost.Find<Button>(control, "cancelButton");
                        Assert.True(cancel.Enabled);
                        foreach (string input in new[] { "entityList", "viewCombo", "includePersonalViewsCheckBox", "loadRecordsButton", "loadMoreButton",
                                                         "loadAllButton", "dryRunCheckBox", "preserveCreatedOnCheckBox", "bypassPluginsCheckBox",
                                                         "copyLookupsCheckBox", "copyChildrenCheckBox", "relationshipsButton", "copyButton" })
                        {
                            Assert.False(UiTestHost.Find<Control>(control, input).Enabled, input + " should be disabled while busy");
                        }
                        Assert.False(UiTestHost.FindToolItem<ToolStripButton>(control, "selectDestinationButton").Enabled);

                        cancel.PerformClick();
                        release.Set();
                        UiTestHost.PumpUntil(() => !control.IsBusy, "the load to stop");

                        Assert.Equal(3, control.Records.Rows.Count);
                        Assert.Equal(new[] { 1 }, scenario.PagesRequested);
                        Assert.Contains("Loading stopped: 3 records loaded (more available).", UiTest.LogText(control));
                        Assert.False(cancel.Enabled);
                        Assert.True(UiTestHost.Find<Button>(control, "loadMoreButton").Enabled);
                        Assert.True(UiTestHost.Find<Control>(control, "entityList").Enabled);
                    }
                });
            }
        }

        [Fact]
        public void The_Relationships_button_follows_the_1N_option_and_the_selected_entity()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    Button relationships = UiTestHost.Find<Button>(control, "relationshipsButton");
                    CheckBox copyChildren = UiTestHost.Find<CheckBox>(control, "copyChildrenCheckBox");
                    copyChildren.Checked = true;
                    Assert.False(relationships.Enabled);   // no source, no entity yet
                    copyChildren.Checked = false;

                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the account views");
                    Assert.False(relationships.Enabled);   // the 1:N option is off

                    copyChildren.Checked = true;
                    Assert.True(relationships.Enabled);
                    Assert.True(control.Settings.CopyChildren);   // saved at once
                    copyChildren.Checked = false;
                    Assert.False(relationships.Enabled);
                    Assert.False(control.Settings.CopyChildren);

                    // A never-create entity cannot be copied, so there is nothing to choose for it.
                    copyChildren.Checked = true;
                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    entities.Items["systemuser"].Selected = true;
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Text == "(All records)", "the systemuser views");
                    Assert.False(relationships.Enabled);
                    entities.Items["account"].Selected = true;
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Text == "Active Accounts", "the account views again");
                    Assert.True(relationships.Enabled);
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void The_relationship_picker_saves_its_ticks_and_a_dry_run_copy_follows_them_down_two_levels()
        {
            var scenario = new UiScenario();
            scenario.Settings.CopyChildren = true;
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ComboBox>(control, "viewCombo").Items.Count > 0, "the account views");

                    // 1. The picker (driven here without its modal loop): the account's relationships, pre-ticked from its main form.
                    string rootText = null, contactsText = null, subContactsText = null;
                    bool contactsTicked = false, subContactsTicked = true;
                    control.ShowPicker = picker =>
                    {
                        picker.ShowMessage = (text, icon) =>
                        {
                            scenario.Dialogs.Messages.Add(text);
                            return DialogResult.OK;
                        };
                        picker.StartPosition = FormStartPosition.Manual;
                        picker.Location = new Point(-3000, -3000);
                        picker.Show();
                        UiTestHost.PumpUntil(() => !picker.IsLoading && picker.RootNode != null && picker.RootNode.Nodes.Count > 0
                                                   && picker.RootNode.Nodes[0].Text != RelationshipPickerForm.LoadingText, "the account relationships");
                        rootText = picker.RootNode.Text;
                        TreeNode contacts = Assert.Single(picker.RootNode.Nodes.Cast<TreeNode>());   // Account_Tasks: no task metadata, not eligible
                        contactsText = contacts.Text;
                        contactsTicked = contacts.Checked;

                        contacts.Expand();   // the child entity's own relationships, loaded lazily
                        UiTestHost.PumpUntil(() => !picker.IsLoading && contacts.Nodes.Count == 1 && contacts.Nodes[0].Text != RelationshipPickerForm.LoadingText,
                            "the contact relationships");
                        TreeNode subContacts = contacts.Nodes[0];
                        subContactsText = subContacts.Text;
                        subContactsTicked = subContacts.Checked;
                        subContacts.Checked = true;   // contact becomes configured

                        UiTestHost.Find<Button>(picker, "okButton").PerformClick();
                        return picker.DialogResult;
                    };
                    Button relationships = UiTestHost.Find<Button>(control, "relationshipsButton");
                    Assert.True(relationships.Enabled);
                    relationships.PerformClick();

                    Assert.Equal("Account (account)", rootText);
                    Assert.Equal("Contacts (contact) via parentcustomerid \u2014 contact_customer_accounts [subgrid]", contactsText);
                    Assert.True(contactsTicked);
                    Assert.Equal("Contacts (contact) via parentcustomerid \u2014 contact_customer_contacts", subContactsText);
                    Assert.False(subContactsTicked);
                    RelationshipSelection saved = Assert.Single(control.Settings.RelationshipSelections);   // only the entity whose ticks changed
                    Assert.Equal((string.Empty, "contact", true), (saved.Organization, saved.Entity, saved.Configured));
                    Assert.Equal(new[] { "contact_customer_contacts" }, saved.Relationships);
                    UiTestHost.PumpUntil(() => UiTest.LogText(control).Contains("1:N relationships saved - contact: contact_customer_contacts."), "the saved line");

                    // 2. A dry-run copy of one account: its contact (main-form subgrid), then that contact's contact (ticked).
                    UiTestHost.Find<Button>(control, "loadRecordsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 3, "page 1");
                    ClickCopyCell(UiTestHost.Find<DataGridView>(control, "recordGrid"), 0);   // Contoso Ltd
                    control.UpdateConnection(scenario.Destination, null, DataCopierControl.DestinationActionName, DataCopierControl.DestinationParameter);
                    UiTestHost.Find<CheckBox>(control, "dryRunCheckBox").Checked = true;
                    UiTestHost.Find<Button>(control, "copyButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTest.LogText(control).Contains("---- Summary"), "the copy");

                    string log = UiTest.LogText(control);
                    Assert.Contains("copy N:1 relationships (lookups) on, copy 1:N relationships (subgrids) on; never created:", log);
                    const string Followed = "] 1:N relationships followed from account (the subgrids on its active main forms): contact_customer_accounts (contact.parentcustomerid)";
                    Assert.Contains(Followed, log);
                    Assert.Contains("] Other entities reached as child records follow the relationships ticked for them in Relationships..., otherwise the subgrids on their active main forms.", log);
                    Assert.InRange(log.IndexOf(Followed, StringComparison.Ordinal), 0, log.IndexOf("] DRY RUN: nothing will be written", StringComparison.Ordinal));
                    Assert.Contains($"] [DRY RUN] Would create account \"Contoso Ltd\" ({scenario.Accounts[0].Id})", log);
                    Assert.Contains("] Children of account \"Contoso Ltd\" via contact_customer_accounts: 1 contact record", log);
                    Assert.Contains($"]   [DRY RUN] Would create contact \"Jane Child\" ({scenario.ChildContact.Id})", log);
                    Assert.Contains("]   Children of contact \"Jane Child\" via contact_customer_contacts: 1 contact record", log);
                    Assert.Contains($"]     [DRY RUN] Would create contact \"Joe Grandchild\" ({scenario.GrandchildContact.Id})", log);
                    Assert.Contains("]   Child records found 2", log);
                    Assert.Equal("Dry run done: 1 / 1 - created 3, children found 2, failed 0", UiTestHost.Find<Label>(control, "progressLabel").Text);
                    Assert.Empty(scenario.Destination.Writes);
                    Assert.Empty(scenario.Dialogs.Messages);
                    Assert.DoesNotContain("UI test", scenario.MetadataThreads);   // picker, header and copy read metadata off the UI thread
                }
            });
        }

        [Fact]
        public void The_picker_reopens_with_the_saved_selections_of_the_source_and_Cancel_saves_nothing()
        {
            var scenario = new UiScenario();
            scenario.Settings.CopyChildren = true;
            scenario.Settings.RelationshipSelections.Add(new RelationshipSelection
            {
                Organization = string.Empty, Entity = "account", Configured = true, Relationships = { "Account_Tasks" }
            });
            scenario.Settings.RelationshipSelections.Add(new RelationshipSelection
            {
                Organization = "https://other.crm4.dynamics.com", Entity = "contact", Configured = true, Relationships = { "x" }
            });
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ComboBox>(control, "viewCombo").Items.Count > 0, "the account views");

                    IReadOnlyDictionary<string, ISet<string>> handedIn = null;
                    control.ShowPicker = picker =>
                    {
                        handedIn = picker.ConfiguredSelections;   // what the dialog starts from
                        return DialogResult.Cancel;
                    };
                    UiTestHost.Find<Button>(control, "relationshipsButton").PerformClick();
                    UiTestHost.Pump();

                    Assert.Equal(new[] { "account" }, handedIn.Keys);   // this source only
                    Assert.Equal(new[] { "Account_Tasks" }, handedIn["account"]);
                    Assert.Equal(2, control.Settings.RelationshipSelections.Count);   // unchanged
                    Assert.DoesNotContain("1:N relationships saved", UiTest.LogText(control));
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Headers_of_a_view_with_a_linked_entity_column_show_display_names_and_the_linked_column_filters()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count == 2, "the account views");
                    views.SelectedIndex = 1;
                    Assert.Equal("Active Accounts and Primary Contacts", views.Text);   // name + pc.emailaddress1

                    Button loadRecords = UiTestHost.Find<Button>(control, "loadRecordsButton");
                    loadRecords.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 3, "page 1");

                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "recordGrid");
                    List<DataGridViewColumn> visible = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
                    Assert.Equal(new[] { "Copy", "Account Name", "Email (Primary Contact)" }, visible.Select(c => c.HeaderText));
                    Assert.Equal(new[] { "name", "pc.emailaddress1" }, visible.Skip(1).Select(c => c.ToolTipText));
                    Assert.Equal("sales@fabrikam.example", grid.Rows[2].Cells[visible[2].Index].Value);
                    // Metadata for both entities, read once each on a worker thread.
                    Assert.Equal(new[] { "account", "contact" }, scenario.MetadataRequests);
                    Assert.DoesNotContain("UI test", scenario.MetadataThreads);

                    // The linked column filters like any other (the DataTable's column names have no dots).
                    TextBox filter = UiTestHost.Find<TextBox>(control, "recordFilter");
                    SetFilter(control, filter, "fabrikam.example");
                    Assert.Equal("Fabrikam", Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Cells[visible[1].Index].Value);
                    SetFilter(control, filter, string.Empty);

                    // Loading the view again uses the cached metadata.
                    loadRecords.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 3, "page 1 again");
                    Assert.Equal(new[] { "Copy", "Account Name", "Email (Primary Contact)" },
                        grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).Select(c => c.HeaderText));
                    Assert.Equal(2, scenario.MetadataRequests.Count);
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Space_ticks_the_current_row_once_whichever_of_its_cells_is_current()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    control.UpdateConnection(scenario.Source, null, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ComboBox>(control, "viewCombo").Items.Count > 0, "the account views");
                    UiTestHost.Find<Button>(control, "loadRecordsButton").PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && control.Records.Rows.Count == 3, "page 1");
                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "recordGrid");
                    Label selected = UiTestHost.Find<Label>(control, "selectedLabel");

                    // On the Copy cell itself (its check box reacts to Space too).
                    grid.CurrentCell = grid.Rows[0].Cells[DataCopierControl.CopyColumn];
                    PressSpace(grid);
                    Assert.Equal("Selected: 1", selected.Text);
                    PressSpace(grid);
                    Assert.Equal("Selected: 0", selected.Text);

                    // After a mouse tick, Space on that Copy cell unticks it again.
                    ClickCopyCell(grid, 1);
                    Assert.Equal("Selected: 1", selected.Text);
                    PressSpace(grid);
                    Assert.Equal("Selected: 0", selected.Text);

                    // On another cell of the row.
                    grid.CurrentCell = grid.Rows[2].Cells[2];
                    PressSpace(grid);
                    Assert.Equal("Selected: 1", selected.Text);
                    Assert.Equal(new[] { scenario.Accounts[2].Id }, control.GetSelectedIds());
                }
            });
        }

        [Fact]
        public void Without_a_source_Refresh_entities_asks_XrmToolBox_for_one_and_the_callback_loads_the_entities_once()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var requests = new List<RequestConnectionEventArgs>();
                    control.OnRequestConnection += (sender, e) => requests.Add((RequestConnectionEventArgs)e);   // what XrmToolBox listens to

                    // No connection: the button works and asks XrmToolBox for one (its connection dialog).
                    ToolStripButton refresh = UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton");
                    Assert.True(refresh.Enabled);
                    refresh.PerformClick();

                    RequestConnectionEventArgs request = Assert.Single(requests);
                    Assert.Equal("RefreshEntities", request.ActionName);   // the method base.UpdateConnection invokes once connected
                    Assert.Same(control, request.Control);
                    Assert.Null(request.Parameter);
                    Assert.Null(control.Service);
                    Assert.Equal(0, scenario.EntityListLoads);
                    Assert.Empty(scenario.Dialogs.Messages);

                    // XrmToolBox, once the user has chosen the connection: the action runs once, against the new source.
                    control.UpdateConnection(scenario.Source, null, request.ActionName, request.Parameter);
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the entities and the account views");

                    Assert.Same(scenario.Source, control.Service);
                    Assert.Equal(1, scenario.EntityListLoads);   // not a second time by the connection change
                    Assert.Equal(3, UiTestHost.Find<ListView>(control, "entityList").Items.Count);
                    Assert.Equal("Active Accounts", views.Text);
                    Assert.Equal("Source: (unnamed connection)", UiTestHost.FindToolItem<ToolStripLabel>(control, "sourceLabel").Text);
                    string log = UiTest.LogText(control);
                    Assert.Equal(1, Occurrences(log, "] Source environment: (unnamed connection)"));
                    Assert.Equal(1, Occurrences(log, "] 3 entities loaded from the source."));
                    Assert.Single(requests);
                    Assert.Empty(scenario.Dialogs.Messages);

                    // Connected, the button reloads at once without asking again.
                    refresh.PerformClick();
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0 && scenario.EntityListLoads == 2, "the reload");
                    Assert.Single(requests);
                    Assert.Equal(1, Occurrences(UiTest.LogText(control), "] Source environment: "));
                }
            });
        }

        [Fact]
        public void With_a_destination_but_no_source_Copy_asks_for_the_source_and_the_destination_never_becomes_the_source()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var requests = new List<RequestConnectionEventArgs>();
                    control.OnRequestConnection += (sender, e) => requests.Add((RequestConnectionEventArgs)e);
                    ToolStripLabel sourceLabel = UiTestHost.FindToolItem<ToolStripLabel>(control, "sourceLabel");
                    ToolStripLabel destinationLabel = UiTestHost.FindToolItem<ToolStripLabel>(control, "destinationLabel");

                    // The destination first: its own request - an additional organisation, which XrmToolBox
                    // never makes the tab's connection - answered without touching Service.
                    UiTestHost.FindToolItem<ToolStripButton>(control, "selectDestinationButton").PerformClick();
                    RequestConnectionEventArgs destination = Assert.Single(requests);
                    Assert.Equal("AdditionalOrganization", destination.ActionName);
                    Assert.Equal("destination", destination.Parameter);
                    Assert.Same(control, destination.Control);
                    control.UpdateConnection(scenario.Destination, null, destination.ActionName, destination.Parameter);
                    UiTestHost.Pump();
                    Assert.Null(control.Service);
                    Assert.Null(control.ConnectionDetail);
                    Assert.Equal("Source: (none)", sourceLabel.Text);
                    Assert.Equal("Destination: (unnamed connection)", destinationLabel.Text);
                    Assert.Equal(0, scenario.EntityListLoads);

                    // Copy is disabled while there is nothing to copy; forced, its handler asks for the source instead of failing.
                    Button copy = UiTestHost.Find<Button>(control, "copyButton");
                    Assert.False(copy.Enabled);
                    copy.Enabled = true;
                    copy.PerformClick();
                    UiTestHost.Pump();

                    Assert.Equal(2, requests.Count);
                    Assert.Equal("CopySelectedRecords", requests[1].ActionName);
                    Assert.Same(control, requests[1].Control);
                    Assert.Empty(scenario.Dialogs.Messages);
                    Assert.False(control.IsBusy);
                    Assert.DoesNotContain(" failed", UiTest.LogText(control));

                    // The callback: the copy runs once against the new source - with nothing ticked yet it says
                    // what is missing - and the source's entities load once; the destination is kept.
                    control.UpdateConnection(scenario.Source, null, requests[1].ActionName, requests[1].Parameter);
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    UiTestHost.PumpUntil(() => !control.IsBusy && views.Items.Count > 0, "the entities and the account views");

                    Assert.Same(scenario.Source, control.Service);
                    Assert.Equal(new[] { "Select an entity first." }, scenario.Dialogs.Messages);
                    Assert.Equal(1, scenario.EntityListLoads);
                    Assert.Equal("Source: (unnamed connection)", sourceLabel.Text);
                    Assert.Equal("Destination: (unnamed connection)", destinationLabel.Text);
                    Assert.DoesNotContain("] Copying ", UiTest.LogText(control));
                    Assert.Empty(scenario.Destination.Executed);

                    // Connected, a new destination still leaves Service alone.
                    var other = new FakeOrganizationService();
                    control.UpdateConnection(other, null, DataCopierControl.DestinationActionName, DataCopierControl.DestinationParameter);
                    Assert.Same(scenario.Source, control.Service);
                    Assert.Equal(2, requests.Count);
                }
            });
        }

        [Fact]
        public void Selecting_the_destination_asks_for_an_additional_organisation_and_the_source_stays_the_tools_connection()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var sourceDetail = new ConnectionDetail { ConnectionName = "Source org" };
                    control.UpdateConnection(scenario.Source, sourceDetail, string.Empty, null);
                    UiTestHost.PumpUntil(() => !control.IsBusy && UiTestHost.Find<ComboBox>(control, "viewCombo").Items.Count > 0, "the account views");
                    var requests = new List<RequestConnectionEventArgs>();
                    control.OnRequestConnection += (sender, e) => requests.Add((RequestConnectionEventArgs)e);

                    UiTestHost.FindToolItem<ToolStripButton>(control, "selectDestinationButton").PerformClick();

                    // XrmToolBox's "additional organisation". For this action name the host leaves the tab's
                    // own connection alone - status bar connection, tab title and highlight, the connection it
                    // records for the tab - and passes ActionName and Parameter back with the chosen
                    // connection: read in the host's code, not testable here.
                    RequestConnectionEventArgs request = Assert.Single(requests);
                    Assert.Equal("AdditionalOrganization", request.ActionName);
                    Assert.Equal("destination", request.Parameter);
                    Assert.Same(control, request.Control);

                    var destinationDetail = new ConnectionDetail { ConnectionName = "Destination org" };
                    control.UpdateConnection(scenario.Destination, destinationDetail, request.ActionName, request.Parameter);
                    UiTestHost.Pump();

                    // The tool's own connection is still the source; nothing of it was reset or reloaded.
                    Assert.Same(scenario.Source, control.Service);
                    Assert.Same(sourceDetail, control.ConnectionDetail);
                    Assert.Equal("Source: Source org", UiTestHost.FindToolItem<ToolStripLabel>(control, "sourceLabel").Text);
                    Assert.Equal("Destination: Destination org", UiTestHost.FindToolItem<ToolStripLabel>(control, "destinationLabel").Text);
                    Assert.Equal(1, scenario.EntityListLoads);
                    Assert.Equal("Active Accounts", UiTestHost.Find<ComboBox>(control, "viewCombo").Text);
                    Assert.True(UiTestHost.Find<Button>(control, "loadRecordsButton").Enabled);
                    string log = UiTest.LogText(control);
                    Assert.Contains("] Destination environment: Destination org", log);
                    Assert.Equal(1, Occurrences(log, "] Source environment: "));
                    Assert.Single(requests);
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Without_a_connection_the_controls_work_and_those_that_need_the_source_ask_for_it()
        {
            var scenario = new UiScenario();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(scenario.Settings, scenario.Save, scenario.Dialogs))
                {
                    var requests = new List<string>();
                    control.OnRequestConnection += (sender, e) => requests.Add(((RequestConnectionEventArgs)e).ActionName);

                    // Controls that need no connection: no request, no error.
                    UiTestHost.Find<TextBox>(control, "entityFilter").Text = "acc";
                    UiTestHost.Find<TextBox>(control, "recordFilter").Text = "contoso";
                    control.ApplyRecordFilter();
                    foreach (string option in new[] { "includePersonalViewsCheckBox", "dryRunCheckBox", "preserveCreatedOnCheckBox",
                                                      "bypassPluginsCheckBox", "copyLookupsCheckBox", "copyChildrenCheckBox" })
                    {
                        CheckBox box = UiTestHost.Find<CheckBox>(control, option);
                        box.Checked = !box.Checked;
                        box.Checked = !box.Checked;
                    }
                    UiTestHost.Find<Button>(control, "clearLogButton").PerformClick();
                    UiTestHost.Pump();
                    Assert.Empty(requests);

                    // Controls that need the source: Refresh entities is enabled without one; the others are disabled
                    // until there is something to act on, so they are forced on to show that they ask for it too.
                    UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton").PerformClick();
                    foreach (string name in new[] { "loadRecordsButton", "loadMoreButton", "loadAllButton", "relationshipsButton", "copyButton" })
                    {
                        Button button = UiTestHost.Find<Button>(control, name);
                        Assert.False(button.Enabled, name + " should be disabled without a source");
                        button.Enabled = true;
                        button.PerformClick();
                    }
                    UiTestHost.Pump();

                    Assert.Equal(new[] { "RefreshEntities", "LoadRecords", "LoadMoreRecords", "LoadAllRecords", "ChooseRelationships", "CopySelectedRecords" }, requests);
                    foreach (string action in requests)
                    {
                        // What base.UpdateConnection looks up once connected: a unique instance method without parameters.
                        MethodInfo method = typeof(DataCopierControl).GetMethod(action, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        Assert.NotNull(method);
                        Assert.Empty(method.GetParameters());
                    }
                    Assert.Null(control.Service);
                    Assert.False(control.IsBusy);
                    Assert.Empty(control.Records.Rows);
                    Assert.Empty(scenario.Dialogs.Messages);
                    Assert.Equal(0, scenario.EntityListLoads);
                }
            });
        }

        private static int Occurrences(string text, string value)
        {
            int count = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
            return count;
        }

        /// <summary>Space pressed and released on the grid, through the message loop like real typing.</summary>
        private static void PressSpace(DataGridView grid)
        {
            PostMessage(grid.Handle, WmKeyDown, (IntPtr)VkSpace, (IntPtr)0x00390001);
            PostMessage(grid.Handle, WmKeyUp, (IntPtr)VkSpace, unchecked((IntPtr)(int)0xC0390001));
            UiTestHost.Pump(60);
        }

        private static void SetFilter(DataCopierControl control, TextBox filter, string text)
        {
            filter.Text = text;
            control.ApplyRecordFilter();   // what the typing-pause timer does
        }

        /// <summary>
        /// A real mouse click in the middle of row <paramref name="rowIndex"/>'s Copy cell: move, down, up
        /// (the check box cell only reacts to a press that a mouse move put inside its glyph).
        /// </summary>
        private static void ClickCopyCell(DataGridView grid, int rowIndex)
        {
            grid.PerformLayout();
            Rectangle cell = grid.GetCellDisplayRectangle(grid.Columns[DataCopierControl.CopyColumn].Index, rowIndex, cutOverflow: false);
            if (cell.IsEmpty) throw new InvalidOperationException($"The Copy cell of row {rowIndex} is not displayed.");
            var point = (IntPtr)(((cell.Top + cell.Height / 2) << 16) | (cell.Left + cell.Width / 2));
            SendMessage(grid.Handle, WmMouseMove, IntPtr.Zero, point);
            SendMessage(grid.Handle, WmLButtonDown, (IntPtr)MkLButton, point);
            SendMessage(grid.Handle, WmLButtonUp, IntPtr.Zero, point);
            UiTestHost.Pump(20);
        }

        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int MkLButton = 0x0001;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int VkSpace = 0x20;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>
    /// Fake source and destination for the UI flow tests. The source has three entities (Account,
    /// Contact, User), two system views for account ("Active Accounts": name + accountnumber, then
    /// "Active Accounts and Primary Contacts": name + pc.emailaddress1 through a link-entity), none for
    /// the others, five accounts and one user; FetchXML is paged by the fetch's page/count. The source
    /// answers RetrieveEntity for account and contact (recording each request and its thread), the
    /// destination for account and contact (the copy engine's metadata). For the 1:N relationships
    /// (SPEC 5.10) the source also holds an active account main form with a contact subgrid, a contact
    /// of the first account and a contact of that contact; other QueryExpressions (child records,
    /// systemform) are evaluated over the store.
    /// </summary>
    internal sealed class UiScenario
    {
        private static readonly Guid ActiveAccountsViewId = new Guid("5a9e1b1c-0000-0000-0000-000000000001");
        private static readonly Guid PrimaryContactsViewId = new Guid("5a9e1b1c-0000-0000-0000-000000000002");

        public UiScenario()
        {
            string[] names = { "Contoso Ltd", "Contoso [UK] 50%", "Fabrikam", "O'Neil's Bakery", "Northwind*" };
            string[] emails = { "info@contoso.example", "uk@contoso.example", "sales@fabrikam.example", "bakery@oneil.example", "north@wind.example" };
            for (int i = 0; i < names.Length; i++)
            {
                Entity account = TestData.Record("account", Guid.NewGuid(), ("name", names[i]), ("accountnumber", "A-" + (i + 1)));
                Accounts.Add(account);
                Source.Add(account);
                _primaryContactEmails[account.Id] = emails[i];
            }
            User = TestData.Record("systemuser", Guid.NewGuid(), ("fullname", "Jane Admin"));
            Source.Add(User);
            VatRate = TestData.Record("new_vatrate", Guid.NewGuid(), ("new_name", "20.0"));
            Source.Add(VatRate);
            ChildContact = TestData.Record("contact", Guid.NewGuid(), ("fullname", "Jane Child"), ("parentcustomerid", TestData.Ref("account", Accounts[0].Id)));
            Source.Add(ChildContact);
            GrandchildContact = TestData.Record("contact", Guid.NewGuid(), ("fullname", "Joe Grandchild"), ("parentcustomerid", TestData.Ref("contact", ChildContact.Id)));
            Source.Add(GrandchildContact);
            Source.Add(FormSubgridServiceTests.Form("account", 2, 1, FormSubgridServiceTests.Subgrid("contact_customer_accounts")));

            Source.ExecuteHandler = OnSourceRequest;
            Source.RetrieveMultipleHandler = OnSourceQuery;
            Destination.ExecuteHandler = request => !(request is RetrieveEntityRequest retrieve) ? null
                : retrieve.LogicalName == "account" ? AccountMetadata()
                : retrieve.LogicalName == "contact" ? Metadata(DataverseSchemaProviderTests.ContactMetadata())
                : null;
        }

        public FakeOrganizationService Source { get; } = new FakeOrganizationService();
        public FakeOrganizationService Destination { get; } = new FakeOrganizationService();
        public List<Entity> Accounts { get; } = new List<Entity>();
        public Entity User { get; }
        public Entity VatRate { get; }

        /// <summary>A contact of the first account (its parentcustomerid), and a contact of that contact.</summary>
        public Entity ChildContact { get; }
        public Entity GrandchildContact { get; }
        public DataCopierSettings Settings { get; } = new DataCopierSettings { LastEntity = "account", PageSize = 3 };

        /// <summary>Also list "VAT Rate" (new_vatrate), a virtual table: it has a data provider.</summary>
        public bool IncludeVirtualEntity { get; set; }
        public DialogRecorder Dialogs { get; } = new DialogRecorder();

        /// <summary>RetrieveAllEntities fails while this is set.</summary>
        public bool FailEntityList { get; set; }

        /// <summary>How many times the source was asked for its entity list (RetrieveAllEntities; counted on the worker thread).</summary>
        public int EntityListLoads => Volatile.Read(ref _entityListLoads);

        private int _entityListLoads;

        /// <summary>Called on the worker thread before a page is answered.</summary>
        public Action<int> BeforePage { get; set; }

        private readonly Dictionary<Guid, string> _primaryContactEmails = new Dictionary<Guid, string>();

        /// <summary>Entities whose metadata the source was asked for (RetrieveEntity), in order.</summary>
        public List<string> MetadataRequests { get; } = new List<string>();

        /// <summary>The name of the thread each of those requests ran on (null for a thread-pool thread).</summary>
        public List<string> MetadataThreads { get; } = new List<string>();

        public List<int> PagesRequested { get; } = new List<int>();
        public List<int> PageSizes { get; } = new List<int>();
        public List<string> Cookies { get; } = new List<string>();
        public bool LastSavedDryRun { get; private set; }

        public void Save(DataCopierSettings settings) => LastSavedDryRun = settings.DryRun;

        private OrganizationResponse OnSourceRequest(OrganizationRequest request)
        {
            switch (request)
            {
                case RetrieveAllEntitiesRequest _:
                    Interlocked.Increment(ref _entityListLoads);
                    if (FailEntityList) throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Simulated metadata failure");
                    var metadata = new List<EntityMetadata>
                    {
                        Meta("systemuser", "SystemUser", "User", "systemuserid", "fullname"),
                        Meta("contact", "Contact", "Contact", "contactid", "fullname"),
                        Meta("account", "Account", "Account", "accountid", "name")
                    };
                    if (IncludeVirtualEntity)
                    {
                        EntityMetadata vatRates = Meta("new_vatrate", "new_VatRate", "VAT Rate", "new_vatrateid", "new_name");
                        vatRates.DataProviderId = new Guid("c9a7f5b6-2e3d-4a1b-9f80-7d6e5c4b3a21");   // an external data provider
                        metadata.Add(vatRates);
                    }
                    var response = new RetrieveAllEntitiesResponse();
                    response.Results["EntityMetadata"] = metadata.ToArray();
                    return response;
                case RetrieveEntityRequest retrieve:
                    MetadataRequests.Add(retrieve.LogicalName);
                    MetadataThreads.Add(Thread.CurrentThread.Name);
                    if (retrieve.LogicalName == "account") return AccountMetadata();
                    if (retrieve.LogicalName == "contact") return Metadata(DataverseSchemaProviderTests.ContactMetadata());
                    throw FakeOrganizationService.Fault(FakeOrganizationService.ObjectDoesNotExist, "Could not find an entity with specified entity name: " + retrieve.LogicalName);
                default:
                    return null;
            }
        }

        private EntityCollection OnSourceQuery(QueryBase query)
        {
            switch (query)
            {
                case QueryExpression views when views.EntityName == "savedquery":
                    object entity = views.Criteria.Conditions.First(c => c.AttributeName == "returnedtypecode").Values[0];
                    return "account".Equals(entity) ? new EntityCollection(new List<Entity> { ActiveAccountsView(), PrimaryContactsView() }) : new EntityCollection();
                case QueryExpression personal when personal.EntityName == "userquery":
                    return new EntityCollection();   // no personal views
                case QueryExpression _:
                    return null;                     // systemform, child records: evaluated over the store
                case FetchExpression fetch:
                    return Page(XElement.Parse(fetch.Query));
                default:
                    return null;
            }
        }

        private EntityCollection Page(XElement fetch)
        {
            string entity = (string)fetch.Element("entity")?.Attribute("name");
            int page = (int)fetch.Attribute("page");
            int count = (int)fetch.Attribute("count");
            PagesRequested.Add(page);
            PageSizes.Add(count);
            Cookies.Add((string)fetch.Attribute("paging-cookie"));
            BeforePage?.Invoke(page);

            List<Entity> all = entity == "account" ? Accounts
                : entity == "systemuser" ? new List<Entity> { User }
                : entity == "new_vatrate" ? new List<Entity> { VatRate }
                : new List<Entity>();
            List<Entity> rows = all.Skip((page - 1) * count).Take(count).Select(Clone).ToList();
            // The fake does not join: a view linking the primary contact (alias "pc") gets its column as an aliased value.
            if (fetch.Descendants("link-entity").Any(link => (string)link.Attribute("alias") == "pc"))
            {
                foreach (Entity row in rows) row["pc.emailaddress1"] = new AliasedValue("contact", "emailaddress1", _primaryContactEmails[row.Id]);
            }
            return new EntityCollection(rows)
            {
                EntityName = entity,
                MoreRecords = page * count < all.Count,
                PagingCookie = $"<cookie page=\"{page}\" />"
            };
        }

        private static Entity Clone(Entity record)
        {
            var copy = new Entity(record.LogicalName, record.Id);
            foreach (KeyValuePair<string, object> pair in record.Attributes) copy[pair.Key] = pair.Value;
            return copy;
        }

        private static Entity ActiveAccountsView() => new Entity("savedquery", ActiveAccountsViewId)
        {
            ["name"] = "Active Accounts",
            ["fetchxml"] = "<fetch><entity name=\"account\"><attribute name=\"name\" /><attribute name=\"accountnumber\" /><order attribute=\"name\" /></entity></fetch>",
            ["layoutxml"] = "<grid name=\"resultset\" jump=\"name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"accountid\">" +
                            "<cell name=\"name\" width=\"300\" /><cell name=\"accountnumber\" width=\"120\" /></row></grid>"
        };

        private static Entity PrimaryContactsView() => new Entity("savedquery", PrimaryContactsViewId)
        {
            ["name"] = "Active Accounts and Primary Contacts",
            ["fetchxml"] = "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" />" +
                           "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" link-type=\"outer\" alias=\"pc\">" +
                           "<attribute name=\"emailaddress1\" /></link-entity></entity></fetch>",
            ["layoutxml"] = "<grid name=\"resultset\" jump=\"name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"accountid\">" +
                            "<cell name=\"name\" width=\"300\" /><cell name=\"pc.emailaddress1\" width=\"200\" /></row></grid>"
        };

        private static OrganizationResponse AccountMetadata() => Metadata(DataverseSchemaProviderTests.AccountMetadata());

        private static OrganizationResponse Metadata(EntityMetadata metadata)
        {
            var response = new RetrieveEntityResponse();
            response.Results["EntityMetadata"] = metadata;
            return response;
        }

        private static EntityMetadata Meta(string logicalName, string schemaName, string label, string primaryId, string primaryName) =>
            new EntityMetadata { LogicalName = logicalName, SchemaName = schemaName, DisplayName = new Microsoft.Xrm.Sdk.Label(label, 1033) }
                .With("IsIntersect", false)
                .With("IsPrivate", false)
                .With("PrimaryIdAttribute", primaryId)
                .With("PrimaryNameAttribute", primaryName);
    }
}
