using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MyscotekDataCopier.Core;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCopier.UI
{
    /// <summary>
    /// Settings... (SPEC section 6): the two settings without a check box - the tables that are never
    /// created (<see cref="DataCopierSettings.NeverCreateEntities"/>, one logical name per line) and the
    /// page size (<see cref="DataCopierSettings.PageSize"/>). OK reads the list the way the run does
    /// (<see cref="CopyOptions.ParseNeverCreateEntities"/>: trimmed, lower case, no blanks or duplicates,
    /// in order; an empty list is the defaults) and asks before keeping a name added here that is not a
    /// table in the source catalog, when that is loaded (names the list already had and the defaults are
    /// not questioned). Built in code; shown with ShowDialog by <see cref="DataCopierControl"/>, which
    /// saves and applies the result.
    /// </summary>
    internal sealed class CopierSettingsForm : Form
    {
        internal const string NeverCreateText =
            "Tables that are never created (one logical name per line). Lookups to them are kept when the record already exists in the destination, otherwise blanked.";

        /// <summary>The page size range and step offered here (the largest page Dataverse returns is <see cref="DataCopierSettings.MaxPageSize"/>).</summary>
        internal const int MinimumPageSize = 50;
        internal const int PageSizeStep = 50;

        private readonly ISet<string> _initialNames;
        private readonly ISet<string> _sourceTables;   // null: the source catalog is not loaded

        private Font _uiFont;
        private TextBox _neverCreateBox;
        private NumericUpDown _pageSizeBox;
        private Button _resetButton;
        private Button _okButton;
        private Button _cancelButton;

        /// <param name="neverCreateEntities">The never-create tables now in use, in order (null: the defaults).</param>
        /// <param name="pageSize">The page size now in use (shown within 50 - 5000).</param>
        /// <param name="sourceTables">The logical names of the loaded source catalog; null when it is not loaded.</param>
        internal CopierSettingsForm(IEnumerable<string> neverCreateEntities, int pageSize, IEnumerable<string> sourceTables)
        {
            List<string> names = (neverCreateEntities ?? CopyOptions.DefaultNeverCreateEntities).ToList();
            _initialNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            _sourceTables = sourceTables == null ? null : new HashSet<string>(sourceTables.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);

            ShowMessage = (text, buttons, icon, defaultButton) => MessageBox.Show(this, text, DataCopierControl.DialogTitle, buttons, icon, defaultButton);
            BuildUi(names, pageSize);
            // The caret at the end, nothing selected: a TextBox focused without a selection of its own
            // selects all its text, and the first key pressed would replace the whole list.
            _neverCreateBox.Select(_neverCreateBox.TextLength, 0);
            NeverCreateEntities = names.AsReadOnly();
            PageSize = (int)_pageSizeBox.Value;
        }

        /// <summary>
        /// Shows a message box owned by the dialog: (text, buttons, icon, default button) => result.
        /// A seam for the UI tests.
        /// </summary>
        internal Func<string, MessageBoxButtons, MessageBoxIcon, MessageBoxDefaultButton, DialogResult> ShowMessage { get; set; }

        /// <summary>After OK: the never-create tables, in order (never empty - the defaults when the list was).</summary>
        internal IReadOnlyList<string> NeverCreateEntities { get; private set; }

        /// <summary>After OK: the page size.</summary>
        internal int PageSize { get; private set; }

        /// <summary>The question asked on OK about a name added here that is not a table in the source.</summary>
        internal static string UnknownTableQuestion(string name) => $"'{name}' is not a table in the source environment. Keep it anyway?";

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void BuildUi(IEnumerable<string> names, int pageSize)
        {
            SuspendLayout();
            _uiFont = new Font("Segoe UI", 9f);
            Font = _uiFont;
            Text = "Data Copier settings";
            Name = "copierSettings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            Size = new Size(520, 420);
            MinimumSize = new Size(400, 320);

            var intro = new Label { Name = "neverCreateLabel", AutoSize = true, Margin = new Padding(3, 3, 3, 6), Text = NeverCreateText };
            // Wrap the text to the width of the dialog.
            intro.MaximumSize = new Size(ClientSize.Width - 24, 0);
            Resize += (sender, e) => intro.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0);

            _neverCreateBox = new TextBox
            {
                Name = "neverCreateTextBox",
                Multiline = true,
                AcceptsReturn = true,   // Enter starts a new line rather than pressing OK
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(3),
                Text = string.Join(Environment.NewLine, names)
            };

            _resetButton = NewButton("resetDefaultsButton", "Reset to defaults", OnResetClick);
            var listButtons = new FlowLayoutPanel { Name = "listButtons", AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            listButtons.Controls.Add(_resetButton);

            var pageSizeLabel = new Label { Name = "pageSizeLabel", Text = "Page size:", AutoSize = true, Margin = new Padding(3, 7, 3, 2) };
            _pageSizeBox = new NumericUpDown
            {
                Name = "pageSizeUpDown",
                Minimum = MinimumPageSize,
                Maximum = DataCopierSettings.MaxPageSize,
                Increment = PageSizeStep,
                Value = Math.Max(MinimumPageSize, Math.Min(DataCopierSettings.MaxPageSize, pageSize)),
                Width = 80,
                TextAlign = HorizontalAlignment.Right,
                Margin = new Padding(3, 4, 3, 2)
            };
            var pageSizeHint = new Label { Name = "pageSizeHint", Text = "records per page when loading records", AutoSize = true, Margin = new Padding(3, 7, 3, 2) };
            var pageSizeRow = new FlowLayoutPanel { Name = "pageSizeRow", AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            pageSizeRow.Controls.AddRange(new Control[] { pageSizeLabel, _pageSizeBox, pageSizeHint });

            _okButton = NewButton("okButton", "OK", OnOkClick);   // no DialogResult of its own: OK checks the list first
            _cancelButton = NewButton("cancelButton", "Cancel", null);
            _cancelButton.DialogResult = DialogResult.Cancel;
            var closing = new FlowLayoutPanel
            {
                Name = "closeButtons",
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                Margin = new Padding(0, 6, 0, 0)
            };
            closing.Controls.AddRange(new Control[] { _cancelButton, _okButton });

            var tips = new ToolTip();
            Disposed += (sender, e) => tips.Dispose();
            tips.SetToolTip(_neverCreateBox, "Records of these tables are never read from the source nor created. An empty list restores the defaults.");
            tips.SetToolTip(_resetButton, "Put back the defaults: " + string.Join(", ", CopyOptions.DefaultNeverCreateEntities) + ".");
            tips.SetToolTip(_pageSizeBox, "Records per page for Load records, Load more and Load all (" + MinimumPageSize + " - " + DataCopierSettings.MaxPageSize + ").");

            var layout = new TableLayoutPanel { Name = "settingsLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // the explanation
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // the list
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Reset to defaults
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // page size
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // OK / Cancel
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(_neverCreateBox, 0, 1);
            layout.Controls.Add(listButtons, 0, 2);
            layout.Controls.Add(pageSizeRow, 0, 3);
            layout.Controls.Add(closing, 0, 4);
            Controls.Add(layout);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
            ResumeLayout(false);
            PerformLayout();
        }

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
            if (onClick != null) button.Click += onClick;
            return button;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _uiFont?.Dispose();
        }

        // =====================================================================================
        // Buttons
        // =====================================================================================

        private void OnResetClick(object sender, EventArgs e) =>
            _neverCreateBox.Text = string.Join(Environment.NewLine, CopyOptions.DefaultNeverCreateEntities);

        /// <summary>
        /// OK: the list as the run reads it, without the questioned names the user did not keep (an
        /// empty result is the defaults), and the page size; then the dialog closes.
        /// </summary>
        private void OnOkClick(object sender, EventArgs e)
        {
            var kept = new List<string>();
            foreach (string name in CopyOptions.ParseNeverCreateEntities(_neverCreateBox.Text))
            {
                if (IsQuestionable(name)
                    && ShowMessage(UnknownTableQuestion(name), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    continue;   // No: dropped
                }
                kept.Add(name);
            }
            NeverCreateEntities = CopyOptions.ParseNeverCreateEntities(string.Join(",", kept));
            PageSize = (int)_pageSizeBox.Value;   // Value also takes a number still being typed
            DialogResult = DialogResult.OK;
        }

        /// <summary>
        /// A name to ask about: added in this dialog and not a table in the loaded source catalog. The
        /// names the list already had and the defaults are kept without a question, and so is every
        /// name while the catalog is not loaded.
        /// </summary>
        private bool IsQuestionable(string name) =>
            _sourceTables != null && !_sourceTables.Contains(name) && !_initialNames.Contains(name)
            && !CopyOptions.DefaultNeverCreateEntities.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
