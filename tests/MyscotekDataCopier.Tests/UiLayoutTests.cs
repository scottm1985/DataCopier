using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MyscotekDataCopier.Tests.Fakes;
using MyscotekDataCopier.UI;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// The tool library checklist: the tool adapts its controls to the size XrmToolBox gives it. The
    /// real control, connected and with a page of records loaded, is sized like a small and a large
    /// XrmToolBox tab: every container fills its space, the rows stack without gaps or overlaps, the
    /// grid and the log take what is left, and no button is clipped (rows wrap; only a tool smaller
    /// than any real tab scrolls the records area).
    /// </summary>
    public class UiLayoutTests
    {
        [Fact]
        public void The_tool_fills_800x500_and_1600x900_without_clipping_or_overlapping_controls()
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
                    SplitContainer main = UiTestHost.Find<SplitContainer>(control, "mainSplit");

                    SizeTo(control, new Size(800, 500));
                    AssertFills(control, scroll: false);
                    int narrowEntityWidth = main.SplitterDistance;
                    Assert.InRange(narrowEntityWidth, 200, 319);   // the entity list gives way on a small tool
                    Assert.True(OptionLines(control) >= 2);

                    SizeTo(control, new Size(1600, 900));
                    AssertFills(control, scroll: false);
                    Assert.Equal(320, main.SplitterDistance);   // and gets its width back on a large one
                    Assert.Equal(2, OptionLines(control));       // the options, then the relationship options

                    SizeTo(control, new Size(800, 500));
                    AssertFills(control, scroll: false);
                    Assert.Equal(narrowEntityWidth, main.SplitterDistance);

                    // Smaller than any XrmToolBox tab: the rows still do not overlap and the grid keeps its
                    // minimum; the records area scrolls instead.
                    SizeTo(control, new Size(640, 400));
                    AssertFills(control, scroll: true);
                    Assert.Equal(200, main.SplitterDistance);

                    // A width the user dragged the entity list to is kept whenever there is room for it.
                    SizeTo(control, new Size(1600, 900));
                    main.SplitterDistance = 280;
                    SizeTo(control, new Size(800, 500));
                    Assert.Equal(narrowEntityWidth, main.SplitterDistance);
                    SizeTo(control, new Size(1600, 900));
                    Assert.Equal(280, main.SplitterDistance);
                    AssertFills(control, scroll: false);

                    // Ticks hidden by the filter give the longest "Selected" text: the small size still fits.
                    UiTestHost.Find<Button>(control, "selectAllButton").PerformClick();
                    UiTestHost.Find<TextBox>(control, "recordFilter").Text = "fabrikam";
                    control.ApplyRecordFilter();
                    SizeTo(control, new Size(800, 500));
                    Assert.Equal("Selected: 3 (2 hidden by the filter)", UiTestHost.Find<Label>(control, "selectedLabel").Text);
                    AssertFills(control, scroll: false);
                    Assert.Empty(scenario.Dialogs.Messages);
                }
            });
        }

        private static void SizeTo(DataCopierControl control, Size size)
        {
            control.Size = size;
            for (int i = 0; i < 3; i++)   // a fit can resize a split, which queues one more
            {
                UiTestHost.PumpUntil(() => !control.IsLayoutFitQueued, "the layout to fit");
                UiTestHost.Pump(20);
            }
            Assert.Equal(size, control.ClientSize);
        }

        private static void AssertFills(DataCopierControl control, bool scroll)
        {
            Size size = control.ClientSize;
            string at = $" at {size.Width}x{size.Height}";

            // The toolbar across the top, every item on it (on a tool narrower than any tab, items may go
            // to its overflow menu); the splits below.
            ToolStrip toolbar = UiTestHost.Find<ToolStrip>(control, "toolbar");
            Assert.Equal(new Rectangle(0, 0, size.Width, toolbar.Height), toolbar.Bounds);
            if (size.Width >= 800) Assert.All(toolbar.Items.Cast<ToolStripItem>(), item => Assert.Equal(ToolStripItemPlacement.Main, item.Placement));
            SplitContainer main = UiTestHost.Find<SplitContainer>(control, "mainSplit");
            Assert.Equal(new Rectangle(0, toolbar.Height, size.Width, size.Height - toolbar.Height), main.Bounds);
            SplitContainer right = UiTestHost.Find<SplitContainer>(control, "rightSplit");
            Assert.Equal(main.Panel2.ClientSize, right.Size);

            // Left: the entity list fills its panel below the filter.
            var entityPanel = UiTestHost.Find<TableLayoutPanel>(control, "entityPanel");
            Assert.Equal(main.Panel1.ClientSize, entityPanel.Size);
            AssertStacked(entityPanel, new Control[]
            {
                UiTestHost.Find<Label>(control, "entitiesLabel"), UiTestHost.Find<TextBox>(control, "entityFilter"), UiTestHost.Find<ListView>(control, "entityList")
            }, fullWidthFrom: 1, at);

            // Right, top: the rows of the records panel, the grid taking the rest.
            var area = UiTestHost.Find<Panel>(control, "recordsArea");
            Assert.Equal(right.Panel1.ClientSize, area.Size);
            Assert.False(area.HorizontalScroll.Visible, "the records area scrolls sideways" + at);
            Assert.Equal(scroll, area.VerticalScroll.Visible);
            var records = UiTestHost.Find<TableLayoutPanel>(control, "recordsPanel");
            Assert.Equal(area.ClientSize.Width, records.Width);
            Assert.Equal(scroll ? area.AutoScrollMinSize.Height : area.ClientSize.Height, records.Height);
            DataGridView grid = UiTestHost.Find<DataGridView>(control, "recordGrid");
            var rows = new Control[]
            {
                UiTestHost.Find<FlowLayoutPanel>(control, "viewRow"), UiTestHost.Find<TableLayoutPanel>(control, "filterRow"), grid,
                UiTestHost.Find<FlowLayoutPanel>(control, "selectionRow"), UiTestHost.Find<FlowLayoutPanel>(control, "optionsRow"),
                UiTestHost.Find<TableLayoutPanel>(control, "actionRow")
            };
            AssertStacked(records, rows, fullWidthFrom: 0, at);
            Assert.True(grid.Height >= DataCopierControl.MinimumGridHeight, $"grid {grid.Height} px high" + at);
            foreach (Control row in rows.Where(r => r is FlowLayoutPanel || r is TableLayoutPanel)) AssertChildrenInside(row, at);
            var progress = UiTestHost.Find<Label>(control, "progressLabel");
            Assert.Equal(progress.Parent.ClientSize.Width - progress.Margin.Right, progress.Right);   // the progress text takes the rest of its line

            // Right, bottom: the log fills its panel below its buttons.
            var logPanel = UiTestHost.Find<TableLayoutPanel>(control, "logPanel");
            Assert.Equal(right.Panel2.ClientSize, logPanel.Size);
            Assert.True(right.Panel2.Height >= DataCopierControl.LogPanelMinHeight, $"log panel {right.Panel2.Height} px high" + at);
            var logHeader = UiTestHost.Find<FlowLayoutPanel>(control, "logHeader");
            RichTextBox log = UiTestHost.Find<RichTextBox>(control, "logBox");
            AssertStacked(logPanel, new Control[] { logHeader, log }, fullWidthFrom: 0, at);
            AssertChildrenInside(logHeader, at);
            Assert.True(log.Height >= 60, $"log {log.Height} px high" + at);

            // Nothing named lies outside the tool.
            var bounds = new Rectangle(Point.Empty, size);
            foreach (Control named in UiTestHost.Descendants(control).Where(c => c.Visible && !string.IsNullOrEmpty(c.Name) && !IsInScrolledArea(c, area, scroll)))
            {
                Rectangle inTool = control.RectangleToClient(named.Parent.RectangleToScreen(named.Bounds));
                Assert.True(bounds.Contains(inTool), $"{named.Name} {inTool} lies outside the tool" + at);
            }
        }

        /// <summary>
        /// The controls of a one-column table, in order, each starting where the previous one ends
        /// (no gap, no overlap) and the last one ending at the bottom; from <paramref name="fullWidthFrom"/>
        /// on they span the column.
        /// </summary>
        private static void AssertStacked(TableLayoutPanel table, IList<Control> controls, int fullWidthFrom, string at)
        {
            int top = table.Padding.Top;
            for (int i = 0; i < controls.Count; i++)
            {
                Control c = controls[i];
                Assert.True(c.Top == top + c.Margin.Top, $"{c.Name} starts at {c.Top}, expected {top + c.Margin.Top}" + at);
                Assert.Equal(table.Padding.Left + c.Margin.Left, c.Left);
                if (i >= fullWidthFrom)
                    Assert.True(c.Right == table.ClientSize.Width - table.Padding.Right - c.Margin.Right, $"{c.Name} ends at {c.Right}" + at);
                top = c.Bottom + c.Margin.Bottom;
            }
            Assert.True(top == table.ClientSize.Height - table.Padding.Bottom, $"{table.Name}: the last row ends at {top} of {table.ClientSize.Height}" + at);
        }

        /// <summary>Every control of a row is wholly inside it (not clipped) and none overlaps another.</summary>
        private static void AssertChildrenInside(Control row, string at)
        {
            List<Control> children = row.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (Control child in children)
            {
                Assert.True(row.ClientRectangle.Contains(child.Bounds), $"{child.Name} {child.Bounds} is clipped by {row.Name} {row.ClientRectangle}" + at);
                Assert.DoesNotContain(children, other => other != child && other.Bounds.IntersectsWith(child.Bounds));
            }
        }

        /// <summary>The number of lines the option check boxes and Relationships... take.</summary>
        private static int OptionLines(DataCopierControl control) =>
            UiTestHost.Find<FlowLayoutPanel>(control, "optionsRow").Controls.Cast<Control>().Select(c => c.Top - c.Margin.Top).Distinct().Count();

        private static bool IsInScrolledArea(Control c, Control area, bool scroll)
        {
            if (!scroll) return false;
            for (Control parent = c.Parent; parent != null; parent = parent.Parent)
            {
                if (parent == area) return true;
            }
            return false;
        }
    }
}
