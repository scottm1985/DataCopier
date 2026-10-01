using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCopier.UI
{
    /// <summary>
    /// Control construction for <see cref="DataCopierControl"/> (SPEC section 6), built in code: there
    /// is no designer or .resx file. Layout, top to bottom: toolbar; vertical split with the entity
    /// list on the left and, on the right, a horizontal split with the view/records/options area on
    /// top and the log below. Every control the logic or the UI tests look up has a Name.
    /// The layout follows the tool's size (<see cref="FitLayout"/>): rows of buttons wrap, the entity
    /// list gives way on a narrow tool, and the records area keeps the height its rows need.
    /// </summary>
    public partial class DataCopierControl
    {
        // ---- layout sizes in pixels (the rows themselves size to their content) ----

        /// <summary>The entity list's width while there is room for it (dragging the splitter changes it).</summary>
        private const int EntityPanelWidth = 320;
        private const int EntityPanelMinWidth = 200;

        /// <summary>The records side keeps at least this width: below it, the entity list gives way down to its minimum.</summary>
        private const int RecordsComfortWidth = 560;
        private const int RecordsMinWidth = 360;

        /// <summary>The share of the height the records area gets at first; the log has the rest.</summary>
        private const double RecordsHeightShare = 0.62;

        /// <summary>The grid is never squeezed below this; a tool too small for it and the log scrolls the records area.</summary>
        internal const int MinimumGridHeight = 90;
        internal const int LogPanelMinHeight = 110;

        private const int ViewComboWidth = 320;
        private const int ViewComboMinWidth = 120;

        // ---- toolbar ----
        private ToolStrip _toolbar;
        private ToolStripButton _selectDestinationButton;
        private ToolStripButton _refreshEntitiesButton;
        private ToolStripButton _closeButton;
        private ToolStripLabel _sourceLabel;
        private ToolStripLabel _destinationLabel;

        // ---- splitters (placed once they have a real size, then kept fitting: see FitLayout) ----
        private SplitContainer _mainSplit;
        private SplitContainer _rightSplit;
        private bool _mainSplitPlaced;
        private bool _rightSplitPlaced;
        private int _entityPanelWidth = EntityPanelWidth;
        private bool _settingSplitter;   // our own SplitterDistance changes are not the user's
        private bool _fitQueued;

        /// <summary>A layout fit is waiting for the message loop (the UI tests pump until it has run).</summary>
        internal bool IsLayoutFitQueued => _fitQueued;

        // ---- entities (left) ----
        private TextBox _entityFilter;
        private ListView _entityList;

        // ---- views, records and options (right, top: rows of a table in a scrollable area) ----
        private Panel _recordsArea;
        private TableLayoutPanel _recordsPanel;
        private FlowLayoutPanel _viewRow;
        private Label _viewLabel;
        private ComboBox _viewCombo;
        private Button _loadRecordsButton;
        private Button _loadMoreButton;
        private Button _loadAllButton;
        private Label _recordCountLabel;
        private TableLayoutPanel _filterRow;
        private TextBox _recordFilter;
        private DataGridView _grid;
        private BindingSource _bindingSource;
        private FlowLayoutPanel _selectionRow;
        private Button _selectAllButton;
        private Button _selectNoneButton;
        private Label _selectedLabel;
        private FlowLayoutPanel _optionsRow;
        private CheckBox _dryRun;
        private CheckBox _preserveCreatedOn;
        private CheckBox _bypassPlugins;
        private CheckBox _copyLookups;
        private CheckBox _copyChildren;
        private Button _relationshipsButton;
        private TableLayoutPanel _actionRow;
        private Button _copyButton;
        private Button _cancelButton;
        private Label _progressLabel;

        // ---- log (right, bottom) ----
        private RichTextBox _log;
        private Button _copyLogButton;
        private Button _saveLogButton;
        private Button _clearLogButton;

        private ToolTip _toolTip;
        private System.Windows.Forms.Timer _recordFilterTimer;
        private Font _boldFont;
        private Font _logFont;

        private void BuildUi()
        {
            SuspendLayout();

            _toolTip = new ToolTip(_components);
            _boldFont = new Font(Font, FontStyle.Bold);
            _logFont = new Font("Consolas", 9f);
            // The record filter is applied a moment after the last keystroke, not on every one.
            _recordFilterTimer = new System.Windows.Forms.Timer(_components) { Interval = 300 };
            _recordFilterTimer.Tick += OnRecordFilterTimerTick;

            _toolbar = BuildToolbar();

            // The records rows sit in a scrollable area: it scrolls only when the tool is too small to
            // give them their height and a minimum grid next to the log's minimum (see FitRecordsArea).
            _recordsArea = new Panel { Name = "recordsArea", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            _recordsArea.Controls.Add(BuildRecordsPanel());

            _rightSplit = new SplitContainer
            {
                Name = "rightSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            _rightSplit.Panel1.Controls.Add(_recordsArea);
            _rightSplit.Panel2.Controls.Add(BuildLogPanel());

            _mainSplit = new SplitContainer
            {
                Name = "mainSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel1   // the entity list keeps its width when the tool is resized (FitLayout narrows it if need be)
            };
            _mainSplit.Panel1.Controls.Add(BuildEntityPanel());
            _mainSplit.Panel2.Controls.Add(_rightSplit);

            // Dock order: the control added last docks first, so the toolbar takes the top and the
            // split fills the rest.
            Controls.Add(_mainSplit);
            Controls.Add(_toolbar);

            // SplitterDistance can only be set once a SplitContainer has a real size, and the records
            // rows wrap differently at every width: fit the layout whenever a size changes.
            _mainSplit.SizeChanged += (sender, e) => QueueFitLayout();
            _rightSplit.SizeChanged += (sender, e) => QueueFitLayout();
            _recordsArea.ClientSizeChanged += (sender, e) => QueueFitLayout();
            foreach (Control row in new Control[] { _viewRow, _selectionRow, _optionsRow })
            {
                row.SizeChanged += (sender, e) => QueueFitLayout();   // a row wrapped onto more (or fewer) lines
            }
            _mainSplit.SplitterMoved += OnMainSplitterMoved;

            ResumeLayout(false);
            PerformLayout();
        }

        private ToolStrip BuildToolbar()
        {
            _selectDestinationButton = new ToolStripButton("Select destination environment...")
            {
                Name = "selectDestinationButton",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Choose the environment the records are copied TO. The source is the tool's normal XrmToolBox connection."
            };
            _selectDestinationButton.Click += OnSelectDestinationClick;

            _refreshEntitiesButton = new ToolStripButton("Refresh entities")
            {
                Name = "refreshEntitiesButton",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Reload the source entity list and forget the cached metadata of both environments (asks for the source connection when there is none)."
            };
            _refreshEntitiesButton.Click += OnRefreshEntitiesClick;

            _closeButton = new ToolStripButton("Close")
            {
                Name = "closeButton",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Alignment = ToolStripItemAlignment.Right
            };
            _closeButton.Click += OnCloseClick;

            _sourceLabel = new ToolStripLabel("Source: (none)") { Name = "sourceLabel", Alignment = ToolStripItemAlignment.Right };
            _destinationLabel = new ToolStripLabel("Destination: (none)") { Name = "destinationLabel", Alignment = ToolStripItemAlignment.Right };

            var toolbar = new ToolStrip { Name = "toolbar", GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            // Right-aligned items are laid out from the right edge in the order they are added, so
            // they read "Source: x | Destination: y | Close". Items that do not fit go to the overflow menu.
            toolbar.Items.AddRange(new ToolStripItem[]
            {
                _selectDestinationButton,
                new ToolStripSeparator(),
                _refreshEntitiesButton,
                _closeButton,
                new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right },
                _destinationLabel,
                new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right },
                _sourceLabel
            });
            return toolbar;
        }

        private Control BuildEntityPanel()
        {
            TableLayoutPanel panel = NewTable(new Padding(6, 6, 2, 6), SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent);
            panel.Name = "entityPanel";

            var title = new Label { Name = "entitiesLabel", Text = "Entities", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };

            _entityFilter = new TextBox { Name = "entityFilter", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            SetCueBanner(_entityFilter, "Filter by display or logical name");
            _entityFilter.TextChanged += OnEntityFilterChanged;
            _toolTip.SetToolTip(_entityFilter, "Filters the entities on display name or logical name as you type.");

            _entityList = new ListView
            {
                Name = "entityList",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Margin = Padding.Empty
            };
            _entityList.Columns.Add("Display name", 170);
            _entityList.Columns.Add("Logical name", 115);
            _entityList.SelectedIndexChanged += OnEntitySelectionChanged;

            panel.Controls.Add(title, 0, 0);
            panel.Controls.Add(_entityFilter, 0, 1);
            panel.Controls.Add(_entityList, 0, 2);
            return panel;
        }

        private Control BuildRecordsPanel()
        {
            _recordsPanel = NewTable(new Padding(2, 4, 6, 2),
                SizeType.AutoSize,   // view row
                SizeType.AutoSize,   // record filter
                SizeType.Percent,    // grid
                SizeType.AutoSize,   // select all / none
                SizeType.AutoSize,   // options (two lines on a wide tool, more on a narrow one)
                SizeType.AutoSize);  // copy, cancel, progress
            _recordsPanel.Name = "recordsPanel";

            // -- view row --
            _viewLabel = NewLabel("viewLabel", "View:");
            _viewCombo = new ComboBox
            {
                Name = "viewCombo",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = ViewComboWidth,   // narrower when the records side is narrow (FitViewCombo)
                Margin = new Padding(3, 4, 3, 2)
            };
            _viewCombo.SelectedIndexChanged += OnViewChanged;
            _toolTip.SetToolTip(_viewCombo, "System views first, then your personal views (suffixed \"(personal)\"), each sorted by name.");

            _loadRecordsButton = NewButton("loadRecordsButton", "Load records", OnLoadRecordsClick);
            _toolTip.SetToolTip(_loadRecordsButton, "Clear the grid and load the first page of the selected view.");
            _loadMoreButton = NewButton("loadMoreButton", "Load more", OnLoadMoreClick);
            _toolTip.SetToolTip(_loadMoreButton, "Append the next page of the view.");
            _loadAllButton = NewButton("loadAllButton", "Load all", OnLoadAllClick);
            _toolTip.SetToolTip(_loadAllButton, "Keep loading pages until every record of the view is loaded (Cancel stops it).");

            _viewRow = NewRow("viewRow");
            _viewRow.Controls.AddRange(new Control[] { _viewLabel, _viewCombo, _loadRecordsButton, _loadMoreButton, _loadAllButton });

            // -- client-side filter and, at its right, how many records are loaded (here rather than after
            //    the load buttons: this line never wraps, and the view row keeps one line on a narrow tool) --
            _recordFilter = new TextBox { Name = "recordFilter", Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 3) };
            SetCueBanner(_recordFilter, "Filter loaded records...");
            _recordFilter.TextChanged += OnRecordFilterTextChanged;
            _toolTip.SetToolTip(_recordFilter, "Shows only the loaded records whose visible columns contain this text. Ticks are kept.");
            _recordCountLabel = NewLabel("recordCountLabel", "0 records loaded");
            _recordCountLabel.Margin = new Padding(6, 6, 3, 2);   // level with the filter's text

            _filterRow = new TableLayoutPanel
            {
                Name = "filterRow",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _filterRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _filterRow.Controls.Add(_recordFilter, 0, 0);
            _filterRow.Controls.Add(_recordCountLabel, 1, 0);

            // -- grid: bound to a DataTable through a BindingSource whose Filter is the record filter --
            _bindingSource = new BindingSource(_components);
            _grid = new DataGridView
            {
                Name = "recordGrid",
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                ReadOnly = false,                     // the Copy column is editable; every other column is ReadOnly
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                BackgroundColor = SystemColors.Window,
                Margin = new Padding(3),
                DataSource = _bindingSource
            };
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.CurrentCellDirtyStateChanged += OnGridCurrentCellDirtyStateChanged;
            _grid.CellValueChanged += OnGridCellValueChanged;
            _grid.CellContentClick += OnGridCellContentClick;
            _grid.CellContentDoubleClick += OnGridCellContentClick;   // a double click is two toggles, like a CheckBox
            _grid.KeyDown += OnGridKeyDown;
            _grid.DataError += OnGridDataError;

            // -- selection row --
            _selectAllButton = NewButton("selectAllButton", "Select all", OnSelectAllClick);
            _toolTip.SetToolTip(_selectAllButton, "Tick every record the filter shows.");
            _selectNoneButton = NewButton("selectNoneButton", "Select none", OnSelectNoneClick);
            _toolTip.SetToolTip(_selectNoneButton, "Untick every record the filter shows.");
            _selectedLabel = NewLabel("selectedLabel", "Selected: 0");

            _selectionRow = NewRow("selectionRow");
            _selectionRow.Controls.AddRange(new Control[] { _selectAllButton, _selectNoneButton, _selectedLabel });

            // -- options (wrap onto more lines when the tool is narrow) --
            _dryRun = NewCheckBox("dryRunCheckBox", "Dry run (write nothing)");
            _toolTip.SetToolTip(_dryRun, "Resolve everything and log what would happen, but write nothing to the destination.");
            _preserveCreatedOn = NewCheckBox("preserveCreatedOnCheckBox", "Preserve created on (overriddencreatedon)");
            _toolTip.SetToolTip(_preserveCreatedOn,
                "On create, write the source 'created on' date to overriddencreatedon. If the destination refuses it, the record is created without it (warning).");
            _bypassPlugins = NewCheckBox("bypassPluginsCheckBox", "Bypass custom plugins (online only)");
            _toolTip.SetToolTip(_bypassPlugins,
                "Send BypassCustomPluginExecution with every create and update so custom plugins do not run. Dataverse online only; needs the prvBypassCustomPlugins privilege.");
            _dryRun.CheckedChanged += OnOptionChanged;
            _preserveCreatedOn.CheckedChanged += OnOptionChanged;
            _bypassPlugins.CheckedChanged += OnOptionChanged;

            // -- relationship options (SPEC 5.10) --
            _copyLookups = NewCheckBox("copyLookupsCheckBox", "Create related records for N:1 relationships (lookups)");
            _toolTip.SetToolTip(_copyLookups,
                "Ticked: the records the copied records point at through lookups are created first, as deep as needed. " +
                "Unticked: no lookup target is created or updated - a lookup is kept if its record exists in the destination by the end of the selected record's copy, otherwise left blank.");
            _copyChildren = NewCheckBox("copyChildrenCheckBox", "Create related records for 1:N and N:N relationships (subgrids)");
            _toolTip.SetToolTip(_copyChildren,
                "Also copy the child records of the selected records (the records pointing at them through the 1:N relationships chosen with Relationships...), " +
                "and their children in turn; and the records associated with them through the N:N relationships chosen there (peers), which are created when " +
                "missing and then associated. Records and associations that already exist are skipped. Default: the subgrids on each entity's main forms; " +
                "a peer follows only the relationships ticked for its entity.");
            _relationshipsButton = NewButton("relationshipsButton", "Relationships...", OnRelationshipsClick);
            _toolTip.SetToolTip(_relationshipsButton,
                "Choose which 1:N and N:N relationships are followed, entity by entity (pre-ticked: the subgrids on the active main forms).");
            _copyLookups.CheckedChanged += OnOptionChanged;
            _copyChildren.CheckedChanged += OnOptionChanged;

            _optionsRow = NewRow("optionsRow");
            _optionsRow.Controls.AddRange(new Control[]
            {
                _dryRun, _preserveCreatedOn, _bypassPlugins,
                _copyLookups, _copyChildren, _relationshipsButton
            });
            _optionsRow.SetFlowBreak(_bypassPlugins, true);   // the relationship options start the second line

            // -- copy, cancel and the progress text, which takes the rest of the line ("..." and a
            //    tooltip with the whole text when it does not fit) --
            _copyButton = NewButton("copyButton", "Copy selected records", OnCopyClick);
            _copyButton.Font = _boldFont;
            _toolTip.SetToolTip(_copyButton,
                "Copy the ticked records to the destination with the same GUIDs, creating the records their lookups point at first.");
            _cancelButton = NewButton("cancelButton", "Cancel", OnCancelClick);
            _progressLabel = new Label
            {
                Name = "progressLabel",
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 21,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(6, 2, 3, 2)
            };

            _actionRow = new TableLayoutPanel
            {
                Name = "actionRow",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _actionRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _actionRow.Controls.Add(_copyButton, 0, 0);
            _actionRow.Controls.Add(_cancelButton, 1, 0);
            _actionRow.Controls.Add(_progressLabel, 2, 0);

            _recordsPanel.Controls.Add(_viewRow, 0, 0);
            _recordsPanel.Controls.Add(_filterRow, 0, 1);
            _recordsPanel.Controls.Add(_grid, 0, 2);
            _recordsPanel.Controls.Add(_selectionRow, 0, 3);
            _recordsPanel.Controls.Add(_optionsRow, 0, 4);
            _recordsPanel.Controls.Add(_actionRow, 0, 5);
            return _recordsPanel;
        }

        private Control BuildLogPanel()
        {
            TableLayoutPanel panel = NewTable(new Padding(2, 0, 6, 6), SizeType.AutoSize, SizeType.Percent);
            panel.Name = "logPanel";

            _copyLogButton = NewButton("copyLogButton", "Copy log", OnCopyLogClick);
            _saveLogButton = NewButton("saveLogButton", "Save log...", OnSaveLogClick);
            _clearLogButton = NewButton("clearLogButton", "Clear", OnClearLogClick);

            FlowLayoutPanel header = NewRow("logHeader");
            header.Controls.AddRange(new Control[] { NewLabel("logLabel", "Log"), _copyLogButton, _saveLogButton, _clearLogButton });

            _log = new RichTextBox
            {
                Name = "logBox",
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                WordWrap = false,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Font = _logFont,
                Margin = new Padding(3)
            };

            panel.Controls.Add(header, 0, 0);
            panel.Controls.Add(_log, 0, 1);
            return panel;
        }

        // =====================================================================================
        // Fitting the layout to the tool's size
        // =====================================================================================

        /// <summary>
        /// Fits the layout once the current layout pass is over. A size event comes in the middle of a
        /// SplitContainer's own resize, while its panels' layout is suspended: a SplitterDistance set
        /// there resizes the panel but not the controls docked in it. Several events make one fit.
        /// </summary>
        private void QueueFitLayout()
        {
            if (_fitQueued || IsDisposed || !IsHandleCreated) return;   // without a handle: OnLoad fits the layout
            _fitQueued = true;
            BeginInvoke(new Action(() =>
            {
                _fitQueued = false;
                if (!IsDisposed) FitLayout();
            }));
        }

        /// <summary>
        /// Fits the layout to the tool's size (in OnLoad, then whenever a split, the records area or one
        /// of its rows changes size). The entity list keeps its width (320 px, or what the user dragged
        /// it to) while the records side keeps <see cref="RecordsComfortWidth"/>, and gives way down to
        /// <see cref="EntityPanelMinWidth"/> on a narrow tool. The records area gets the height its rows
        /// need at their current width plus <see cref="MinimumGridHeight"/> (the splitter cannot be
        /// dragged above that) while the log keeps <see cref="LogPanelMinHeight"/>; only a tool too small
        /// for both scrolls the records area. A SplitContainer refuses a SplitterDistance or panel
        /// minimum it cannot honour (also while it still has its default size), so each step is
        /// guarded and simply retried on the next size change.
        /// </summary>
        private void FitLayout()
        {
            FitEntityPanel();
            FitRecordsArea();
        }

        private void FitEntityPanel()
        {
            int room = _mainSplit.Width - _mainSplit.SplitterWidth;
            if (room < EntityPanelMinWidth + RecordsMinWidth) return;   // not laid out at a real size yet, or far too narrow
            int width = Math.Max(EntityPanelMinWidth, Math.Min(_entityPanelWidth, room - RecordsComfortWidth));
            if (_mainSplitPlaced && _mainSplit.SplitterDistance == width) return;
            if (TrySetSplitter(_mainSplit, width, EntityPanelMinWidth, RecordsMinWidth)) _mainSplitPlaced = true;
        }

        /// <summary>A splitter the user dragged sets the entity list's width from then on (when there is room for it).</summary>
        private void OnMainSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (!_settingSplitter && _mainSplitPlaced) _entityPanelWidth = Math.Max(EntityPanelMinWidth, _mainSplit.SplitterDistance);
        }

        private void FitRecordsArea()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                int width = _recordsArea.ClientSize.Width;
                if (width <= 0) return;
                FitViewCombo(width);
                int needed = RecordsHeightNeeded(width);
                if (_recordsArea.AutoScrollMinSize.Height != needed) _recordsArea.AutoScrollMinSize = new Size(0, needed);
                FitRecordsSplitter(needed);
                if (_recordsArea.ClientSize.Width == width) return;   // else a scroll bar came or went: fit the new width
            }
        }

        /// <summary>
        /// The view list narrows on a narrow records side: down to <see cref="ViewComboMinWidth"/> to keep
        /// the load buttons on its line (one line less for the records area); where even that does not
        /// fit, only as far as it takes to stay beside its label.
        /// </summary>
        private void FitViewCombo(int areaWidth)
        {
            int room = areaWidth - _recordsPanel.Padding.Horizontal - _viewRow.Margin.Horizontal - _viewRow.Padding.Horizontal
                       - _viewLabel.PreferredSize.Width - _viewLabel.Margin.Horizontal - _viewCombo.Margin.Horizontal;
            int besideButtons = room;
            foreach (Control button in new Control[] { _loadRecordsButton, _loadMoreButton, _loadAllButton })
                besideButtons -= button.PreferredSize.Width + button.Margin.Horizontal;
            int width = besideButtons >= ViewComboMinWidth
                ? Math.Min(ViewComboWidth, besideButtons)
                : Math.Max(ViewComboMinWidth, Math.Min(ViewComboWidth, room));
            if (_viewCombo.Width != width) _viewCombo.Width = width;
        }

        /// <summary>
        /// The height the records area needs at <paramref name="areaWidth"/>: each row at its preferred
        /// height for that width (the rows of buttons wrap) plus <see cref="MinimumGridHeight"/>.
        /// </summary>
        private int RecordsHeightNeeded(int areaWidth)
        {
            int inner = areaWidth - _recordsPanel.Padding.Horizontal;
            int height = _recordsPanel.Padding.Vertical + _grid.Margin.Vertical + MinimumGridHeight;
            foreach (Control row in new Control[] { _viewRow, _filterRow, _selectionRow, _optionsRow, _actionRow })
            {
                height += row.GetPreferredSize(new Size(Math.Max(1, inner - row.Margin.Horizontal), 0)).Height + row.Margin.Vertical;
            }
            return height;
        }

        /// <summary>
        /// Keeps the records area at least <paramref name="needed"/> high (its panel minimum, so the
        /// splitter cannot be dragged above it either) while the log keeps <see cref="LogPanelMinHeight"/>;
        /// the first time the records area gets <see cref="RecordsHeightShare"/> of the height, or more.
        /// </summary>
        private void FitRecordsSplitter(int needed)
        {
            int room = _rightSplit.Height - _rightSplit.SplitterWidth;
            int maxDistance = room - LogPanelMinHeight;
            if (maxDistance < 50) return;   // not laid out at a real size yet, or far too short
            int recordsMin = Math.Min(needed, maxDistance);
            int distance = _rightSplitPlaced ? _rightSplit.SplitterDistance : (int)(room * RecordsHeightShare);
            int target = Math.Max(recordsMin, Math.Min(distance, maxDistance));
            if (_rightSplitPlaced && target == _rightSplit.SplitterDistance
                && _rightSplit.Panel1MinSize == recordsMin && _rightSplit.Panel2MinSize == LogPanelMinHeight)
            {
                return;
            }
            if (TrySetSplitter(_rightSplit, target, recordsMin, LogPanelMinHeight)) _rightSplitPlaced = true;
        }

        private bool TrySetSplitter(SplitContainer split, int distance, int panel1Min, int panel2Min)
        {
            _settingSplitter = true;
            try
            {
                // Relax the minimums first so the new distance is never out of range, then apply them.
                split.Panel1MinSize = 25;
                split.Panel2MinSize = 25;
                split.SplitterDistance = distance;
                split.Panel1MinSize = panel1Min;
                split.Panel2MinSize = panel2Min;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            finally
            {
                _settingSplitter = false;
            }
        }

        // ---- small factories so every row lines up the same way ----

        private static TableLayoutPanel NewTable(Padding padding, params SizeType[] rows)
        {
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = rows.Length,
                Padding = padding,
                Margin = Padding.Empty
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            foreach (SizeType row in rows)
            {
                table.RowStyles.Add(row == SizeType.Percent ? new RowStyle(SizeType.Percent, 100f) : new RowStyle(row));
            }
            return table;
        }

        private static FlowLayoutPanel NewRow(string name) => new FlowLayoutPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        private static Button NewButton(string name, string text, EventHandler onClick)
        {
            var button = new Button
            {
                Name = name,
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(80, 27),
                Margin = new Padding(3, 2, 3, 2),
                UseVisualStyleBackColor = true
            };
            button.Click += onClick;
            return button;
        }

        private static CheckBox NewCheckBox(string name, string text) => new CheckBox
        {
            Name = name,
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 7, 9, 2)
        };

        private static Label NewLabel(string name, string text) => new Label
        {
            Name = name,
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 8, 3, 2)
        };

        // ---- cue banner (grey placeholder text) for the filter boxes ----

        private const int EmSetCueBanner = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string cue)
        {
            // Needs visual styles (XrmToolBox has them); set again whenever the handle is recreated.
            box.HandleCreated += (sender, e) => SendMessage(box.Handle, EmSetCueBanner, (IntPtr)1, cue);
        }
    }
}
