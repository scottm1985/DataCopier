using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Core.Schema;
using MyscotekDataCopier.Tests.Fakes;
using MyscotekDataCopier.UI;
using Xunit;
using Label = System.Windows.Forms.Label;
using LogLevel = MyscotekDataCopier.Core.LogLevel;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// Builds the real <see cref="DataCopierControl"/> outside XrmToolBox (STA thread, toast
    /// notifications stubbed - see <see cref="UiTestHost"/>) through its internal constructor with
    /// in-memory settings and no mirroring to XrmToolBox's log, so nothing outside the test run is
    /// touched. The public constructor differs only in reading/writing XrmToolBox's settings store.
    /// </summary>
    public class UiSmokeTests
    {
        [Fact]
        public void Control_builds_without_the_XrmToolBox_host_and_starts_idle_with_nothing_loaded()
        {
            var dialogs = new DialogRecorder();
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(new DataCopierSettings(), dialogs: dialogs))
                {
                    Assert.True(control.IsHandleCreated);
                    Assert.Equal(new Font("Segoe UI", 9f), control.Font);

                    // toolbar
                    Assert.Equal("Select destination environment...", UiTestHost.FindToolItem<ToolStripButton>(control, "selectDestinationButton").Text);
                    Assert.True(UiTestHost.FindToolItem<ToolStripButton>(control, "selectDestinationButton").Enabled);
                    Assert.Equal("Refresh entities", UiTestHost.FindToolItem<ToolStripButton>(control, "refreshEntitiesButton").Text);
                    ToolStripButton close = UiTestHost.FindToolItem<ToolStripButton>(control, "closeButton");
                    Assert.Equal(ToolStripItemAlignment.Right, close.Alignment);
                    Assert.Equal("Source: (none)", UiTestHost.FindToolItem<ToolStripLabel>(control, "sourceLabel").Text);
                    Assert.Equal("Destination: (none)", UiTestHost.FindToolItem<ToolStripLabel>(control, "destinationLabel").Text);
                    Assert.Equal(ToolStripGripStyle.Hidden, UiTestHost.Find<ToolStrip>(control, "toolbar").GripStyle);

                    // entity list: empty, details view
                    ListView entities = UiTestHost.Find<ListView>(control, "entityList");
                    Assert.Empty(entities.Items);
                    Assert.Equal(View.Details, entities.View);
                    Assert.True(entities.FullRowSelect);
                    Assert.False(entities.HideSelection);
                    Assert.Equal(new[] { "Display name", "Logical name" }, entities.Columns.Cast<ColumnHeader>().Select(c => c.Text));
                    Assert.NotNull(UiTestHost.Find<TextBox>(control, "entityFilter"));

                    // views and loading: nothing to load yet
                    ComboBox views = UiTestHost.Find<ComboBox>(control, "viewCombo");
                    Assert.Equal(ComboBoxStyle.DropDownList, views.DropDownStyle);
                    Assert.Empty(views.Items);
                    // No personal views option: the personal views are always listed, after the system views.
                    Assert.Equal(new[] { "viewLabel", "viewCombo", "loadRecordsButton", "loadMoreButton", "loadAllButton" },
                        UiTestHost.Find<FlowLayoutPanel>(control, "viewRow").Controls.Cast<Control>().Select(c => c.Name));
                    Assert.DoesNotContain(UiTestHost.Descendants(control), c => c is CheckBox && c.Text.IndexOf("personal", StringComparison.OrdinalIgnoreCase) >= 0);
                    Assert.False(UiTestHost.Find<Button>(control, "loadRecordsButton").Enabled);
                    Assert.False(UiTestHost.Find<Button>(control, "loadMoreButton").Enabled);
                    Assert.False(UiTestHost.Find<Button>(control, "loadAllButton").Enabled);
                    Assert.Equal("0 records loaded", UiTestHost.Find<Label>(control, "recordCountLabel").Text);
                    Assert.Equal(new[] { "recordFilter", "recordCountLabel" },   // the loaded count at the right of the filter
                        UiTestHost.Find<TableLayoutPanel>(control, "filterRow").Controls.Cast<Control>().Select(c => c.Name));

                    // grid: Copy tick box, hidden id, read-only apart from the tick box, no new-row line
                    DataGridView grid = UiTestHost.Find<DataGridView>(control, "recordGrid");
                    Assert.IsType<BindingSource>(grid.DataSource);
                    Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
                    Assert.IsType<DataGridViewCheckBoxColumn>(grid.Columns[0]);
                    Assert.Equal("Copy", grid.Columns[0].HeaderText);
                    Assert.False(grid.Columns[0].ReadOnly);
                    Assert.False(grid.Columns["__id"].Visible);
                    Assert.False(grid.AllowUserToAddRows);
                    Assert.False(grid.RowHeadersVisible);
                    Assert.Equal(DataGridViewSelectionMode.FullRowSelect, grid.SelectionMode);
                    Assert.Equal(DataGridViewAutoSizeColumnsMode.None, grid.AutoSizeColumnsMode);

                    Assert.False(UiTestHost.Find<Button>(control, "selectAllButton").Enabled);
                    Assert.False(UiTestHost.Find<Button>(control, "selectNoneButton").Enabled);
                    Assert.Equal("Selected: 0", UiTestHost.Find<Label>(control, "selectedLabel").Text);

                    // options enabled and off; Copy and Cancel disabled
                    foreach (string option in new[] { "dryRunCheckBox", "preserveCreatedOnCheckBox", "bypassPluginsCheckBox" })
                    {
                        CheckBox box = UiTestHost.Find<CheckBox>(control, option);
                        Assert.True(box.Enabled, option);
                        Assert.False(box.Checked, option);
                    }
                    Assert.Equal("Dry run (write nothing)", UiTestHost.Find<CheckBox>(control, "dryRunCheckBox").Text);
                    Assert.Equal("Preserve created on (overriddencreatedon)", UiTestHost.Find<CheckBox>(control, "preserveCreatedOnCheckBox").Text);
                    Assert.Equal("Bypass custom plugins (online only)", UiTestHost.Find<CheckBox>(control, "bypassPluginsCheckBox").Text);

                    // relationship options (SPEC 5.10): lookups on, children off, Relationships... needs both an entity and the 1:N option
                    CheckBox copyLookups = UiTestHost.Find<CheckBox>(control, "copyLookupsCheckBox");
                    CheckBox copyChildren = UiTestHost.Find<CheckBox>(control, "copyChildrenCheckBox");
                    Button relationships = UiTestHost.Find<Button>(control, "relationshipsButton");
                    Assert.Equal("Create related records for N:1 relationships (lookups)", copyLookups.Text);
                    Assert.Equal("Create related records for 1:N relationships (subgrids)", copyChildren.Text);
                    Assert.Equal("Relationships...", relationships.Text);
                    Assert.True(copyLookups.Enabled && copyLookups.Checked);
                    Assert.True(copyChildren.Enabled);
                    Assert.False(copyChildren.Checked);
                    Assert.False(relationships.Enabled);
                    Assert.Same(copyChildren.Parent, relationships.Parent);   // in the options row, next to the 1:N option
                    Assert.Equal("optionsRow", relationships.Parent.Name);
                    Button copy = UiTestHost.Find<Button>(control, "copyButton");
                    Assert.Equal("Copy selected records", copy.Text);
                    Assert.True(copy.Font.Bold);
                    Assert.False(copy.Enabled);
                    Assert.False(UiTestHost.Find<Button>(control, "cancelButton").Enabled);
                    Assert.Equal(string.Empty, UiTestHost.Find<Label>(control, "progressLabel").Text);
                    Assert.False(control.IsBusy);

                    // log: read-only, no wrapping, no links, Consolas 9; tells the user to connect
                    RichTextBox log = UiTestHost.Find<RichTextBox>(control, "logBox");
                    Assert.True(log.ReadOnly);
                    Assert.False(log.WordWrap);
                    Assert.False(log.DetectUrls);
                    Assert.Equal(Color.White, log.BackColor);
                    Assert.Equal(new Font("Consolas", 9f), log.Font);
                    foreach (string button in new[] { "copyLogButton", "saveLogButton", "clearLogButton" })
                        Assert.True(UiTestHost.Find<Button>(control, button).Enabled, button);
                    UiTestHost.PumpUntil(() => log.Text.Contains(DataCopierControl.NoSourceMessage), "the no-connection message");
                    Assert.Matches(@"^\[\d\d:\d\d:\d\d\] Not connected", log.Text);

                    // the entity list got its ~320 px once the splitter had a real size
                    SplitContainer main = UiTestHost.Find<SplitContainer>(control, "mainSplit");
                    Assert.Equal(Orientation.Vertical, main.Orientation);
                    Assert.Equal(320, main.SplitterDistance);
                    Assert.Equal(Orientation.Horizontal, UiTestHost.Find<SplitContainer>(control, "rightSplit").Orientation);
                }
            });

            Assert.Empty(dialogs.Messages);
            Assert.True(ToastNotificationsStub.IsStubLoaded);   // the real toast assembly was never loaded
        }

        [Fact]
        public void Settings_are_applied_to_the_options_and_saved_only_when_an_option_changes()
        {
            UiTestHost.Run(() =>
            {
                var settings = new DataCopierSettings
                {
                    DryRun = true, PreserveCreatedOn = false, BypassCustomPlugins = true, CopyLookups = false, CopyChildren = true
                };
                var saved = new List<(bool DryRun, bool Preserve, bool Bypass, bool Lookups, bool Children)>();
                using (DataCopierControl control = UiTest.NewControl(settings,
                           s => saved.Add((s.DryRun, s.PreserveCreatedOn, s.BypassCustomPlugins, s.CopyLookups, s.CopyChildren))))
                {
                    Assert.True(UiTestHost.Find<CheckBox>(control, "dryRunCheckBox").Checked);
                    Assert.False(UiTestHost.Find<CheckBox>(control, "preserveCreatedOnCheckBox").Checked);
                    Assert.True(UiTestHost.Find<CheckBox>(control, "bypassPluginsCheckBox").Checked);
                    Assert.False(UiTestHost.Find<CheckBox>(control, "copyLookupsCheckBox").Checked);
                    Assert.True(UiTestHost.Find<CheckBox>(control, "copyChildrenCheckBox").Checked);
                    Assert.Empty(saved);   // applying the settings does not write them back

                    UiTestHost.Find<CheckBox>(control, "preserveCreatedOnCheckBox").Checked = true;
                    UiTestHost.Find<CheckBox>(control, "copyLookupsCheckBox").Checked = true;
                    UiTestHost.Find<CheckBox>(control, "copyChildrenCheckBox").Checked = false;

                    Assert.Equal(new[] { (true, true, true, false, true), (true, true, true, true, true), (true, true, true, true, false) }, saved);
                    Assert.True(control.Settings.PreserveCreatedOn);
                    Assert.True(control.Settings.CopyLookups);
                    Assert.False(control.Settings.CopyChildren);
                }
            });
        }

        [Fact]
        public void A_settings_store_that_cannot_be_read_is_not_fatal()
        {
            UiTestHost.Run(() =>
            {
                using (var control = new DataCopierControl(() => throw new IOException("settings store unavailable"), _ => { }, mirrorToXrmToolBoxLog: false))
                {
                    var dialogs = new DialogRecorder();
                    control.ShowMessage = dialogs.Show;
                    control.CreateControl();

                    Assert.NotNull(control.Settings);
                    Assert.Equal(DataCopierSettings.DefaultPageSize, control.Settings.EffectivePageSize);
                    Assert.True(UiTestHost.Find<CheckBox>(control, "copyLookupsCheckBox").Checked);   // the defaults
                    RichTextBox log = UiTestHost.Find<RichTextBox>(control, "logBox");
                    UiTestHost.PumpUntil(() => log.Text.Contains("Settings could not be loaded, the defaults are used: settings store unavailable"), "the settings warning");
                    Assert.Empty(dialogs.Messages);
                }
            });
        }

        [Fact]
        public void Settings_defaults_match_the_spec()
        {
            var settings = new DataCopierSettings();

            Assert.False(settings.DryRun || settings.PreserveCreatedOn || settings.BypassCustomPlugins);
            Assert.True(settings.CopyLookups);
            Assert.False(settings.CopyChildren);
            Assert.Empty(settings.RelationshipSelections);
            Assert.Equal(500, settings.PageSize);
            Assert.Equal("systemuser,team,businessunit,organization,transactioncurrency", settings.NeverCreateEntities);
            Assert.Null(settings.LastEntity);
            Assert.Equal(500, new DataCopierSettings { PageSize = 0 }.EffectivePageSize);
            Assert.Equal(5000, new DataCopierSettings { PageSize = 90000 }.EffectivePageSize);
        }

        [Fact]
        public void The_relationship_options_and_selections_survive_an_XmlSerializer_round_trip()
        {
            var settings = new DataCopierSettings { CopyLookups = false, CopyChildren = true };
            settings.SetRelationshipSelections("https://dev.crm4.dynamics.com/", new Dictionary<string, ISet<string>>
            {
                ["account"] = new HashSet<string> { "contact_customer_accounts", "Account_Tasks" },
                ["contact"] = new HashSet<string>()
            });
            settings.SetRelationshipSelections("https://test.crm4.dynamics.com", new Dictionary<string, ISet<string>>
            {
                ["account"] = new HashSet<string> { "Account_Emails" }
            });

            var serializer = new XmlSerializer(typeof(DataCopierSettings));
            string xml;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, settings);
                xml = writer.ToString();
            }
            DataCopierSettings loaded;
            using (var reader = new StringReader(xml))
            {
                loaded = (DataCopierSettings)serializer.Deserialize(reader);
            }

            Assert.Contains("<CopyLookups>false</CopyLookups>", xml);
            Assert.Contains("<CopyChildren>true</CopyChildren>", xml);
            Assert.DoesNotContain("IncludePersonalViews", xml);   // gone in 1.2026.10.2: personal views are always listed
            Assert.Contains("<Relationship>Account_Tasks</Relationship>", xml);
            Assert.False(loaded.CopyLookups);
            Assert.True(loaded.CopyChildren);
            Assert.Equal(3, loaded.RelationshipSelections.Count);
            Dictionary<string, ISet<string>> dev = loaded.GetRelationshipSelections("HTTPS://DEV.CRM4.DYNAMICS.COM");
            Assert.Equal(new[] { "account", "contact" }, dev.Keys.OrderBy(k => k));
            Assert.Equal(new[] { "Account_Tasks", "contact_customer_accounts" }, dev["ACCOUNT"].OrderBy(n => n, StringComparer.Ordinal));
            Assert.Empty(dev["contact"]);   // configured with nothing ticked: no child records
            Assert.Equal(new[] { "Account_Emails" }, loaded.GetRelationshipSelections("https://test.crm4.dynamics.com/")["account"]);
            Assert.Empty(loaded.GetRelationshipSelections("https://prod.crm4.dynamics.com"));

            // An old settings file without the new elements keeps the defaults, and an element that no
            // longer exists (IncludePersonalViews, before 1.2026.10.2) is skipped without an error.
            DataCopierSettings old;
            using (var reader = new StringReader("<?xml version=\"1.0\"?><DataCopierSettings><DryRun>true</DryRun>" +
                                                 "<IncludePersonalViews>false</IncludePersonalViews><PageSize>250</PageSize></DataCopierSettings>"))
            {
                old = (DataCopierSettings)serializer.Deserialize(reader);
            }
            Assert.True(old.DryRun && old.CopyLookups && !old.CopyChildren);
            Assert.Equal(250, old.PageSize);   // read after the unknown element
            Assert.Empty(old.RelationshipSelections);
        }

        [Fact]
        public void Setting_the_selections_of_one_organisation_keeps_the_others_and_unconfigured_entries_are_ignored()
        {
            var settings = new DataCopierSettings();
            settings.RelationshipSelections.Add(new RelationshipSelection { Organization = "https://other", Entity = "account", Configured = true, Relationships = { "a" } });
            settings.RelationshipSelections.Add(new RelationshipSelection { Organization = "https://dev", Entity = "lead", Configured = false, Relationships = { "b" } });
            settings.RelationshipSelections.Add(null);

            Assert.Empty(settings.GetRelationshipSelections("https://dev"));   // not configured: follows its subgrids

            settings.SetRelationshipSelections("https://dev/", new Dictionary<string, ISet<string>> { [" Contact "] = new HashSet<string> { " x ", "" } });

            Assert.Equal(new[] { ("https://other", "account"), ("https://dev", "contact") },
                settings.RelationshipSelections.Select(s => (s.Organization, s.Entity)));
            Assert.Equal(new[] { "x" }, settings.RelationshipSelections[1].Relationships);
            Assert.True(settings.RelationshipSelections[1].Configured);
            Assert.Equal("https://other", DataCopierSettings.OrganizationKey(" HTTPS://Other/ "));
            Assert.Equal(string.Empty, DataCopierSettings.OrganizationKey(null));
        }
    }

    /// <summary>
    /// The public constructor - the one XrmToolBox calls - with XrmToolBox's settings and log folders
    /// redirected (Paths.OverrideRootPath) to a folder in the test output, which is deleted afterwards.
    /// </summary>
    public class UiHostIntegrationTests
    {
        [Fact]
        public void Public_constructor_persists_settings_with_SettingsManager_and_mirrors_errors_to_the_XrmToolBox_log()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XrmToolBoxRoot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            FieldInfo rootPath = typeof(XrmToolBox.Extensibility.Paths).GetField("rootPath", BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                XrmToolBox.Extensibility.Paths.OverrideRootPath(root);
                var dialogs = new DialogRecorder();
                UiTestHost.Run(() =>
                {
                    using (var control = new DataCopierControl())
                    {
                        control.ShowMessage = dialogs.Show;
                        control.Size = new Size(1200, 800);
                        control.CreateControl();
                        CheckBox dryRun = UiTestHost.Find<CheckBox>(control, "dryRunCheckBox");
                        Assert.False(dryRun.Checked);   // no settings file yet: the defaults
                        dryRun.Checked = true;          // saved at once

                        var failing = new FakeOrganizationService
                        {
                            ExecuteHandler = request => throw FakeOrganizationService.Fault(FakeOrganizationService.GenericFailure, "Simulated {0} failure")
                        };
                        control.UpdateConnection(failing, null, string.Empty, null);
                        UiTestHost.PumpUntil(() => !control.IsBusy && dialogs.Messages.Count == 1, "the error");
                    }

                    using (var reopened = new DataCopierControl())
                    {
                        reopened.ShowMessage = dialogs.Show;
                        Assert.True(UiTestHost.Find<CheckBox>(reopened, "dryRunCheckBox").Checked);   // read back by SettingsManager
                    }
                });

                string settings = File.ReadAllText(Path.Combine(root, "Settings", "MyscotekDataCopier.xml"));
                Assert.Contains("<DryRun>true</DryRun>", settings);
                Assert.Contains("<NeverCreateEntities>systemuser,team,businessunit,organization,transactioncurrency</NeverCreateEntities>", settings);
                string log = File.ReadAllText(Path.Combine(root, "Logs", "MyscotekDataCopier.log"));
                Assert.Contains("Loading entities failed: Simulated {0} failure", log);   // braces kept literally
                Assert.Equal("Simulated {0} failure", Assert.Single(dialogs.Messages));
            }
            finally
            {
                rootPath?.SetValue(null, null);   // back to the default root for anything else in this process
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public class UiLoggerTests
    {
        [Fact]
        public void Lines_are_prefixed_with_the_time_coloured_by_level_and_errors_reach_the_sink()
        {
            UiTestHost.Run(() =>
            {
                using (var box = new RichTextBox { WordWrap = false, ReadOnly = true, Width = 800 })   // as in the tool
                {
                    var sunk = new List<(LogLevel, string)>();
                    var logger = new UiLogger(box, (level, message) => sunk.Add((level, message)));
                    box.CreateControl();

                    logger.Log(LogLevel.Info, "Copying account \"A\" (1)...");
                    logger.Log(LogLevel.Success, "  Created contact \"B\" (2)");
                    logger.Log(LogLevel.Warning, "  Blanked parentcustomerid on contact \"B\"");
                    logger.Log(LogLevel.Error, "FAILED to create account \"A\" (1): boom");
                    logger.Write(LogLevel.Info, "shown only");
                    UiTestHost.PumpUntil(() => logger.LineCount == 5, "five lines");

                    string[] lines = box.Lines.Take(5).ToArray();
                    Assert.All(lines, l => Assert.Matches(@"^\[\d\d:\d\d:\d\d\] ", l));
                    Assert.EndsWith("]   Created contact \"B\" (2)", lines[1]);   // the engine's indentation is kept
                    Assert.Equal(SystemColors.WindowText.ToArgb(), ColourOfLine(box, 0).ToArgb());
                    Assert.Equal(Color.ForestGreen.ToArgb(), ColourOfLine(box, 1).ToArgb());
                    Assert.Equal(Color.DarkGoldenrod.ToArgb(), ColourOfLine(box, 2).ToArgb());
                    Assert.Equal(Color.Firebrick.ToArgb(), ColourOfLine(box, 3).ToArgb());
                    Assert.Equal(4, sunk.Count);   // Log lines go to the sink, Write lines do not
                }
            });
        }

        [Fact]
        public void The_oldest_lines_are_trimmed_beyond_the_cap_and_lines_logged_before_the_handle_exists_are_kept()
        {
            UiTestHost.Run(() =>
            {
                using (var box = new RichTextBox { WordWrap = false, ReadOnly = true })
                {
                    var logger = new UiLogger(box, maxLines: 20);
                    for (int i = 1; i <= 25; i++) logger.Write(LogLevel.Info, "line " + i);   // no handle yet: queued
                    box.CreateControl();
                    UiTestHost.PumpUntil(() => box.Text.Contains("line 25"), "the queued lines");

                    Assert.True(logger.LineCount <= 20);
                    Assert.Equal(logger.LineCount, box.Lines.Count(l => l.Length > 0));
                    Assert.DoesNotContain("] line 1\n", box.Text);
                    Assert.EndsWith("] line 25", box.Lines[logger.LineCount - 1]);

                    logger.Clear();
                    Assert.Equal(0, logger.LineCount);
                    Assert.Equal(string.Empty, box.Text);
                }
            });
        }

        /// <summary>The colour of the first message character of a line (after "[HH:mm:ss] ").</summary>
        private static Color ColourOfLine(RichTextBox box, int line)
        {
            int start = 0;
            for (int i = 0; i < line; i++) start = box.Text.IndexOf('\n', start) + 1;
            box.Select(start + 11, 1);
            return box.SelectionColor;
        }
    }

    public class UiHelperTests
    {
        [Fact]
        public void Row_filter_matches_any_column_and_escapes_like_wildcards_and_quotes()
        {
            Assert.Null(DataCopierControl.BuildRowFilter("  ", new[] { "c0" }));
            Assert.Null(DataCopierControl.BuildRowFilter("x", new string[0]));
            Assert.Equal("[c0] LIKE '%contoso%' OR [c1] LIKE '%contoso%'", DataCopierControl.BuildRowFilter(" contoso ", new[] { "c0", "c1" }));
            Assert.Equal("[c0] LIKE '%O''Neil[*] [[]UK[]] 5[%]%'", DataCopierControl.BuildRowFilter("O'Neil* [UK] 5%", new[] { "c0" }));
        }

        [Fact]
        public void Progress_label_has_the_spec_format_and_shows_updates_and_skips_only_when_there_are_any()
        {
            Assert.Equal("3 / 25 - created 41, failed 1",
                DataCopierControl.FormatProgress(new CopyProgress { SelectedIndex = 3, SelectedTotal = 25, Created = 41, Failed = 1 }));
            Assert.Equal("25 / 25 - created 0, updated 25, skipped 4, failed 0",
                DataCopierControl.FormatProgress(new CopyProgress { SelectedIndex = 25, SelectedTotal = 25, Updated = 25, SkippedExisting = 4 }));
            Assert.Equal("1 / 2 - created 9, children found 8, failed 0",
                DataCopierControl.FormatProgress(new CopyProgress { SelectedIndex = 1, SelectedTotal = 2, Created = 9, ChildRecordsFound = 8 }));
        }

        [Fact]
        public void Relationship_lines_describe_the_saved_selections_and_the_effective_relationships()
        {
            Assert.Equal("1:N relationships: no entity configured; every entity follows the subgrids on its active main forms.",
                DataCopierControl.DescribeConfiguredRelationships(new Dictionary<string, ISet<string>>()));
            Assert.Equal("1:N relationships saved - account: Account_Tasks, contact_customer_accounts; contact: none. " +
                         "Every other entity follows the subgrids on its active main forms.",
                DataCopierControl.DescribeConfiguredRelationships(new Dictionary<string, ISet<string>>
                {
                    ["contact"] = new HashSet<string>(),
                    ["account"] = new HashSet<string> { "contact_customer_accounts", "Account_Tasks" }
                }));

            FakeSchemaProvider schema = ChildRelationshipSelectorTests.EligibilitySchema();
            var configured = new DefaultChildRelationshipSelector(schema, entity => Array.Empty<string>(),
                new Dictionary<string, ISet<string>> { ["account"] = new HashSet<string> { "Account_Annotation", "contact_customer_accounts" } },
                new CopyOptions().NeverCreateEntities);
            Assert.Equal(new[]
            {
                "1:N relationships followed from account (ticked in Relationships...): contact_customer_accounts (contact.parentcustomerid), Account_Annotation (annotation.objectid)",
                "Other entities reached as child records follow the relationships ticked for them in Relationships..., otherwise the subgrids on their active main forms."
            }, DataCopierControl.DescribeChildRelationships(configured, "account"));

            var failing = new DefaultChildRelationshipSelector(schema, entity => throw new InvalidOperationException("forms unavailable"), null, null);
            Assert.Equal("1:N relationships followed from account (the subgrids on its active main forms): could not be determined: forms unavailable",
                DataCopierControl.DescribeChildRelationships(failing, "account")[0]);
            var none = new DefaultChildRelationshipSelector(schema, entity => Array.Empty<string>(), null, null);
            Assert.Equal("1:N relationships followed from account (the subgrids on its active main forms): none",
                DataCopierControl.DescribeChildRelationships(none, "account")[0]);
        }

        [Fact]
        public void Summary_block_lists_the_counts_then_the_errors()
        {
            var summary = new CopySummary
            {
                SelectedTotal = 25, Created = 41, Updated = 2, SkippedExisting = 7, Failed = 1, LookupsBlanked = 3,
                LookupsBackfilled = 1, ChildRecordsFound = 12, StateChanges = 4, DryRun = true, Cancelled = true,
                Elapsed = new TimeSpan(0, 1, 1, 23), Errors = new[] { "FAILED to create contact \"Jane\" (x): boom" }
            };

            IList<(LogLevel Level, string Text)> lines = DataCopierControl.BuildSummaryLines(summary);

            Assert.Equal(new[]
            {
                "---- Summary (dry run - nothing was written) (cancelled): 25 selected records ----",
                "  Created             41",
                "  Updated             2",
                "  Skipped (existed)   7",
                "  Failed              1",
                "  Lookups blanked     3",
                "  Lookups backfilled  1",
                "  Child records found 12",
                "  State changes       4",
                "  Elapsed             01:01:23",
                "  Errors (1):",
                "    FAILED to create contact \"Jane\" (x): boom"
            }, lines.Select(l => l.Text));
            Assert.Equal(LogLevel.Warning, lines[0].Level);
            Assert.Equal(LogLevel.Error, lines[4].Level);
            Assert.Equal(LogLevel.Error, lines[11].Level);
            Assert.Equal(LogLevel.Success, DataCopierControl.BuildSummaryLines(new CopySummary { SelectedTotal = 1, Created = 1 })[0].Level);
        }

        [Fact]
        public void Same_organisation_is_detected_by_name_or_url()
        {
            ConnectionDetail Detail(string organization, string webUrl) => new ConnectionDetail { Organization = organization, WebApplicationUrl = webUrl };

            Assert.True(DataCopierControl.SameOrganization(Detail("contoso", "https://a.crm4.dynamics.com/"), Detail("CONTOSO", "https://b.crm4.dynamics.com")));
            Assert.True(DataCopierControl.SameOrganization(Detail(null, "https://a.crm4.dynamics.com/"), Detail("", "https://A.crm4.dynamics.com")));
            Assert.False(DataCopierControl.SameOrganization(Detail("dev", "https://dev.crm4.dynamics.com"), Detail("test", "https://test.crm4.dynamics.com")));
            Assert.False(DataCopierControl.SameOrganization(Detail("dev", null), null));
        }

        [Fact]
        public void Record_id_comes_from_the_entity_id_or_the_primary_id_attribute()
        {
            Guid id = Guid.NewGuid();
            Assert.Equal(id, DataCopierControl.RecordId(new Entity("account", id), "accountid"));
            Assert.Equal(id, DataCopierControl.RecordId(new Entity("account") { ["accountid"] = id }, "accountid"));
            Assert.Equal(id, DataCopierControl.RecordId(new Entity("account") { ["accountid"] = new AliasedValue("account", "accountid", id) }, "accountid"));
            Assert.Equal(Guid.Empty, DataCopierControl.RecordId(new Entity("account"), "accountid"));
        }
    }

    /// <summary>Shared set-up for the UI tests.</summary>
    internal static class UiTest
    {
        /// <summary>A control with in-memory settings, no XrmToolBox log mirroring and recorded dialogs; handle created.</summary>
        public static DataCopierControl NewControl(DataCopierSettings settings, Action<DataCopierSettings> save = null, DialogRecorder dialogs = null)
        {
            var control = new DataCopierControl(() => settings, save ?? (_ => { }), mirrorToXrmToolBoxLog: false)
            {
                Size = new Size(1200, 800)
            };
            control.ShowMessage = (dialogs ?? new DialogRecorder()).Show;
            control.CreateControl();
            UiTestHost.Pump();
            return control;
        }

        public static string LogText(DataCopierControl control) => UiTestHost.Find<RichTextBox>(control, "logBox").Text;
    }

    /// <summary>Stands in for the control's message boxes: records the text and answers Yes/No questions with <see cref="Answer"/>.</summary>
    internal sealed class DialogRecorder
    {
        public List<string> Messages { get; } = new List<string>();

        public DialogResult Answer { get; set; } = DialogResult.No;

        public DialogResult Show(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            Messages.Add(text);
            return buttons == MessageBoxButtons.OK ? DialogResult.OK : Answer;
        }
    }
}
