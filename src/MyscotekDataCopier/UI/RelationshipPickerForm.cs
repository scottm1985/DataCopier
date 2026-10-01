using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Core.Schema;
using Label = System.Windows.Forms.Label;

namespace MyscotekDataCopier.UI
{
    /// <summary>
    /// The relationship picker (SPEC 5.10): which 1:N relationships are followed to child records and
    /// which N:N relationships to associated records (peers), entity by entity. A TreeView with check
    /// boxes whose root is the selected entity and whose children are its eligible 1:N and N:N
    /// relationships; each relationship expands, lazily, into the relationships of the entity it leads
    /// to (the child entity of a 1:N relationship, the entity at the other end of an N:N one), so the
    /// tree is shaped level by level. Ticks belong to an entity - once configured it shows the same
    /// ticks wherever it appears. An entity whose ticks were changed is configured; the others follow
    /// the subgrids on their active main forms, which are pre-ticked - except where the entity is
    /// listed as a peer (under an N:N relationship), where nothing is followed unless ticked.
    /// Metadata and forms are read on worker threads (wait cursor, progress in the status line).
    /// Built in code; shown with ShowDialog by <see cref="DataCopierControl"/>.
    /// </summary>
    internal sealed class RelationshipPickerForm : Form
    {
        internal const string LoadingText = "Loading...";
        internal const string NoRelationshipsText = "(no 1:N or N:N relationships that can be followed)";

        /// <summary>The first line under an expanded N:N relationship: the peer rule.</summary>
        internal const string PeerHintText = "(peer: nothing is followed unless ticked)";

        private static readonly object DummyTag = new object();   // the "Loading..." line of a node not expanded yet
        private static readonly object InfoTag = new object();    // an explanatory line without a tick

        private readonly string _rootEntity;
        private readonly string _rootDisplayName;
        private readonly ISchemaProvider _schema;
        private readonly Func<string, IReadOnlyCollection<string>> _subgrids;
        private readonly ISet<string> _neverCreate;
        private readonly Dictionary<string, HashSet<string>> _configured = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EntityRelationships> _loaded = new Dictionary<string, EntityRelationships>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<EntityRelationships>> _loading = new Dictionary<string, Task<EntityRelationships>>(StringComparer.OrdinalIgnoreCase);
        private int _pendingLoads;
        private bool _syncing;
        private bool _started;

        private Font _uiFont;
        private CheckedTreeView _tree;
        private Label _statusLabel;
        private Button _tickSubgridsButton;
        private Button _tickCustomButton;
        private Button _untickAllButton;
        private Button _okButton;
        private Button _cancelButton;
        private TreeNode _root;

        /// <param name="rootEntity">The selected entity (the root of the tree).</param>
        /// <param name="rootDisplayName">Its display name.</param>
        /// <param name="sourceSchema">Source metadata (thread-safe: read on worker threads).</param>
        /// <param name="subgridRelationshipNames">The relationship names of the main-form subgrids of an entity (worker threads).</param>
        /// <param name="neverCreate">Never-create entities: never offered as child or peer entities.</param>
        /// <param name="configuredSelections">The entities configured so far and their ticked relationships (copied).</param>
        internal RelationshipPickerForm(string rootEntity, string rootDisplayName, ISchemaProvider sourceSchema,
                                        Func<string, IReadOnlyCollection<string>> subgridRelationshipNames, ISet<string> neverCreate,
                                        IReadOnlyDictionary<string, ISet<string>> configuredSelections)
        {
            if (string.IsNullOrWhiteSpace(rootEntity)) throw new ArgumentException("An entity logical name is required.", nameof(rootEntity));
            _rootEntity = rootEntity.Trim().ToLowerInvariant();
            _rootDisplayName = string.IsNullOrWhiteSpace(rootDisplayName) ? _rootEntity : rootDisplayName;
            _schema = sourceSchema ?? throw new ArgumentNullException(nameof(sourceSchema));
            _subgrids = subgridRelationshipNames;
            _neverCreate = neverCreate ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (configuredSelections != null)
            {
                foreach (KeyValuePair<string, ISet<string>> pair in configuredSelections)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    _configured[pair.Key.Trim().ToLowerInvariant()] = new HashSet<string>(pair.Value ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                }
            }

            ShowMessage = (text, icon) => MessageBox.Show(this, text, DataCopierControl.DialogTitle, MessageBoxButtons.OK, icon);
            BuildUi();
        }

        /// <summary>Shows a message box owned by the dialog: (text, icon). A seam for the UI tests.</summary>
        internal Func<string, MessageBoxIcon, DialogResult> ShowMessage { get; set; }

        internal TreeView Tree => _tree;
        internal TreeNode RootNode => _root;
        internal bool IsLoading => _pendingLoads > 0;
        internal string StatusText => _statusLabel.Text;

        /// <summary>The configured entities and their ticked relationship schema names (1:N and N:N; a copy): what OK saves.</summary>
        internal IReadOnlyDictionary<string, ISet<string>> ConfiguredSelections =>
            _configured.ToDictionary(p => p.Key, p => (ISet<string>)new HashSet<string>(p.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        /// <summary>The text of a 1:N relationship node, e.g. "Contacts (contact) via parentcustomerid - contact_customer_accounts [subgrid]" (with an em dash).</summary>
        internal static string RelationshipText(ChildRelationship relationship, string childDisplayName, bool isSubgrid)
        {
            string child = (relationship.ChildEntity ?? string.Empty).Trim().ToLowerInvariant();
            string name = string.IsNullOrWhiteSpace(childDisplayName) ? child : childDisplayName.Trim();
            string text = $"{name} ({child}) via {relationship.ChildLookupAttribute} — {relationship.SchemaName}";
            if (isSubgrid) text += " [subgrid]";
            if (relationship.IsCustomRelationship) text += " [custom]";
            return text;
        }

        /// <summary>
        /// The text of an N:N relationship node of <paramref name="entity"/>, naming the entity at the other
        /// end, e.g. "Contacts (contact) - ptl_matter_contact [N:N] [subgrid]" (with an em dash).
        /// </summary>
        internal static string ManyToManyText(ManyToManyRelationship relationship, string entity, string peerDisplayName, bool isSubgrid)
        {
            string peer = relationship.OtherEntity(entity) ?? string.Empty;
            string name = string.IsNullOrWhiteSpace(peerDisplayName) ? peer : peerDisplayName.Trim();
            string text = $"{name} ({peer}) — {relationship.SchemaName} [N:N]";
            if (isSubgrid) text += " [subgrid]";
            if (relationship.IsCustomRelationship) text += " [custom]";
            return text;
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void BuildUi()
        {
            SuspendLayout();
            _uiFont = new Font("Segoe UI", 9f);
            Font = _uiFont;
            Text = $"1:N and N:N relationships to copy - {_rootDisplayName} ({_rootEntity})";
            Name = "relationshipPicker";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            Size = new Size(700, 500);
            MinimumSize = new Size(520, 360);

            var intro = new Label
            {
                Name = "introLabel",
                AutoSize = true,
                Margin = new Padding(3, 3, 3, 6),
                Text = "Tick the 1:N relationships whose child records are copied with each record, and the N:N relationships ([N:N]) " +
                       "whose associated records (peers) are copied - when missing - and associated with it; expand a relationship to choose " +
                       "the relationships of its entity in turn. Pre-ticked: the subgrids on the active main forms of the entity ([subgrid]). " +
                       "An entity whose ticks you change keeps them for this source organisation; every other entity follows its main-form " +
                       "subgrids, but a peer follows nothing unless ticked."
            };
            // Wrap the introduction to the width of the dialog.
            intro.MaximumSize = new Size(ClientSize.Width - 24, 0);
            Resize += (sender, e) => intro.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0);

            _tree = new CheckedTreeView
            {
                Name = "relationshipTree",
                Dock = DockStyle.Fill,
                CheckBoxes = true,
                HideSelection = false,
                ShowNodeToolTips = true,
                Margin = new Padding(3)
            };
            _tree.BeforeExpand += OnBeforeExpand;
            _tree.BeforeCheck += OnBeforeCheck;
            _tree.AfterCheck += OnAfterCheck;

            _statusLabel = new Label
            {
                Name = "statusLabel",
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 2, 3, 2)
            };

            _tickSubgridsButton = NewButton("tickSubgridsButton", "Tick subgrids", OnTickSubgridsClick);
            _tickCustomButton = NewButton("tickCustomButton", "Tick custom", OnTickCustomClick);
            _untickAllButton = NewButton("untickAllButton", "Untick all", OnUntickAllClick);
            _okButton = NewButton("okButton", "OK", null);
            _okButton.DialogResult = DialogResult.OK;
            _cancelButton = NewButton("cancelButton", "Cancel", null);
            _cancelButton.DialogResult = DialogResult.Cancel;
            var tips = new ToolTip();
            Disposed += (sender, e) => tips.Dispose();
            const string target = " (of the entity the selected relationship leads to, or of the root entity)";
            tips.SetToolTip(_tickSubgridsButton, "Tick the 1:N and N:N relationships shown as subgrids on the main forms" + target + ".");
            tips.SetToolTip(_tickCustomButton, "Tick the custom 1:N and N:N relationships" + target + ".");
            tips.SetToolTip(_untickAllButton, "Untick every relationship" + target + ".");

            var actions = new FlowLayoutPanel { Name = "actionButtons", AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            actions.Controls.AddRange(new Control[] { _tickSubgridsButton, _tickCustomButton, _untickAllButton });
            var closing = new FlowLayoutPanel
            {
                Name = "closeButtons",
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                Margin = Padding.Empty
            };
            closing.Controls.AddRange(new Control[] { _cancelButton, _okButton });

            var buttonRow = new TableLayoutPanel { Name = "buttonRow", Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonRow.Controls.Add(actions, 0, 0);
            buttonRow.Controls.Add(closing, 1, 0);

            var layout = new TableLayoutPanel { Name = "pickerLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(_tree, 0, 1);
            layout.Controls.Add(_statusLabel, 0, 2);
            layout.Controls.Add(buttonRow, 0, 3);
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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            StartLoading();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _uiFont?.Dispose();
        }

        /// <summary>Adds the root and loads its relationships (when the dialog is shown; once).</summary>
        internal void StartLoading()
        {
            if (_started) return;
            _started = true;
            _root = new TreeNode($"{_rootDisplayName} ({_rootEntity})") { Name = _rootEntity, Tag = new EntityTag(_rootEntity) };
            _root.Nodes.Add(NewPlaceholder(LoadingText, DummyTag));
            _tree.Nodes.Add(_root);
            HideCheckBox(_root);
            HideCheckBox(_root.Nodes[0]);
            _tree.SelectedNode = _root;
            _root.Expand();   // BeforeExpand loads it
        }

        // =====================================================================================
        // Lazy loading
        // =====================================================================================

        private void OnBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode node = e.Node;
            if (node == null || node.Nodes.Count != 1 || !ReferenceEquals(node.Nodes[0].Tag, DummyTag)) return;
            string entity = EntityOf(node);
            if (entity == null) return;
            if (_loaded.TryGetValue(entity, out EntityRelationships known)) Populate(node, known, ListsPeer(node));   // an entity met before: at once
            else LoadInto(node, entity);
        }

        /// <summary>Loads the relationships of <paramref name="entity"/> (on a worker thread) and shows them under <paramref name="node"/>.</summary>
        private async void LoadInto(TreeNode node, string entity)
        {
            try
            {
                EntityRelationships data = await LoadAsync(entity);
                if (IsDisposed || node.TreeView == null) return;
                if (node.Nodes.Count == 1 && ReferenceEquals(node.Nodes[0].Tag, DummyTag))
                {
                    Populate(node, data, ListsPeer(node));
                    node.Expand();   // replacing the only child may reset the native expanded state
                }
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                if (node.TreeView != null)
                {
                    // Back to "not loaded": expanding the node again retries.
                    node.Collapse();
                    node.Nodes.Clear();
                    node.Nodes.Add(NewPlaceholder(LoadingText, DummyTag));
                    HideCheckBox(node.Nodes[0]);
                }
                string message = $"The relationships of {entity} could not be loaded: {CopyEngine.ErrorText(ex)}";
                SetStatus(message);
                ShowMessage(message, MessageBoxIcon.Error);
            }
        }

        /// <summary>The relationships of an entity: loaded once per dialog, on a worker thread; concurrent requests share the load.</summary>
        private async Task<EntityRelationships> LoadAsync(string entity)
        {
            if (_loaded.TryGetValue(entity, out EntityRelationships known)) return known;
            if (!_loading.TryGetValue(entity, out Task<EntityRelationships> task))
            {
                var progress = new Progress<string>(SetStatus);   // reports come back to the UI thread
                task = Task.Run(() => ReadRelationships(entity, progress));
                _loading[entity] = task;
            }

            BeginLoad();
            try
            {
                EntityRelationships data = await task;
                _loaded[entity] = data;
                return data;
            }
            finally
            {
                _loading.Remove(entity);
                EndLoad(entity);
            }
        }

        /// <summary>
        /// Worker thread: the metadata of the entity and of the entities its relationships lead to (child
        /// entities and peer entities), its eligible 1:N and N:N relationships and the subgrids on its
        /// active main forms. A failure to read the forms is reported (nothing is pre-ticked then); a
        /// failure to read the entity propagates.
        /// </summary>
        private EntityRelationships ReadRelationships(string entity, IProgress<string> progress)
        {
            progress.Report($"Reading the metadata of {entity}...");
            EntitySchema schema = _schema.GetEntity(entity) ?? throw new InvalidOperationException($"Entity {entity} does not exist in the source.");
            IEnumerable<string> children = (schema.OneToManyRelationships ?? Array.Empty<ChildRelationship>())
                .Select(r => r?.ChildEntity?.Trim().ToLowerInvariant())
                .Where(c => !string.IsNullOrEmpty(c) && !ChildRelationshipEligibility.IsSystemExcluded(c));
            IEnumerable<string> peers = (schema.ManyToManyRelationships ?? Array.Empty<ManyToManyRelationship>())
                .Where(r => r != null && !ManyToManyEligibility.IsSystemIntersect(r.IntersectEntity))
                .Select(r => r.OtherEntity(entity))
                .Where(p => !string.IsNullOrEmpty(p) && !ManyToManyEligibility.IsExcludedPeerEntity(p));
            List<string> related = children.Concat(peers)
                .Where(e => !_neverCreate.Contains(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < related.Count; i++)
            {
                progress.Report(string.Format(CultureInfo.CurrentCulture, "Reading the metadata of the related entities of {0}: {1} / {2} ({3})...",
                    entity, i + 1, related.Count, related[i]));
                try
                {
                    _schema.GetEntity(related[i]);
                }
                catch (Exception)
                {
                    // Not eligible then (the eligibility rules treat it so).
                }
            }
            IReadOnlyList<ChildRelationship> eligible = ChildRelationshipEligibility.GetEligible(_schema, entity, _neverCreate);
            IReadOnlyList<ManyToManyRelationship> eligibleManyToMany = ManyToManyEligibility.GetEligible(_schema, entity, _neverCreate);

            progress.Report($"Reading the main forms of {entity}...");
            var subgrids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string subgridProblem = null;
            try
            {
                foreach (string name in _subgrids?.Invoke(entity) ?? Array.Empty<string>()) subgrids.Add(name);
            }
            catch (Exception ex)
            {
                subgridProblem = CopyEngine.ErrorText(ex);
            }

            var entityNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> shown = eligible.Select(r => r.ChildEntity.Trim().ToLowerInvariant())
                .Concat(eligibleManyToMany.Select(r => r.OtherEntity(entity)));
            foreach (string name in shown)
            {
                if (!entityNames.ContainsKey(name)) entityNames[name] = PluralDisplayName(name);
            }
            return new EntityRelationships(entity, eligible, eligibleManyToMany, subgrids, entityNames, subgridProblem);
        }

        private string PluralDisplayName(string entity)
        {
            try
            {
                EntitySchema schema = _schema.GetEntity(entity);
                if (!string.IsNullOrWhiteSpace(schema?.DisplayCollectionName)) return schema.DisplayCollectionName;
                if (!string.IsNullOrWhiteSpace(schema?.DisplayName)) return schema.DisplayName;
            }
            catch (Exception)
            {
                // the logical name will do
            }
            return entity;
        }

        private void BeginLoad()
        {
            _pendingLoads++;
            UseWaitCursor = true;
        }

        private void EndLoad(string entity)
        {
            _pendingLoads--;
            if (IsDisposed) return;
            if (_pendingLoads == 0) UseWaitCursor = false;
            if (_loaded.TryGetValue(entity, out EntityRelationships data) && data.SubgridProblem != null)
            {
                string warning = $"The main forms of {entity} could not be read, so none of its relationships is pre-ticked: {data.SubgridProblem}";
                data.SubgridProblem = null;   // said once
                SetStatus(warning);
                ShowMessage(warning, MessageBoxIcon.Warning);
            }
            else if (_pendingLoads == 0)
            {
                SetStatus(SelectionSummary());
            }
        }

        /// <summary>
        /// Shows the eligible 1:N and N:N relationships of an entity under <paramref name="owner"/>:
        /// subgrids first, then by text. Under an N:N relationship (<paramref name="asPeer"/>) the entity
        /// is a peer: a first line says so, and an unconfigured peer has nothing ticked.
        /// </summary>
        private void Populate(TreeNode owner, EntityRelationships data, bool asPeer)
        {
            var placeholders = new List<TreeNode>();
            var rows = data.Eligible
                .Select(r =>
                {
                    bool isSubgrid = data.Subgrids.Contains(r.SchemaName);
                    string child = r.ChildEntity.Trim().ToLowerInvariant();
                    return new
                    {
                        Tag = new RelationshipTag(data.Entity, r, isSubgrid, asPeer),
                        Text = RelationshipText(r, data.EntityNames.TryGetValue(child, out string childName) ? childName : null, isSubgrid),
                        ToolTip = $"{r.SchemaName}: the {child} records whose {r.ChildLookupAttribute} points at the {data.Entity}"
                    };
                })
                .Concat(data.EligibleManyToMany.Select(r =>
                {
                    bool isSubgrid = data.Subgrids.Contains(r.SchemaName);
                    string peer = r.OtherEntity(data.Entity);
                    return new
                    {
                        Tag = new RelationshipTag(data.Entity, r, isSubgrid, asPeer),
                        Text = ManyToManyText(r, data.Entity, data.EntityNames.TryGetValue(peer, out string peerName) ? peerName : null, isSubgrid),
                        ToolTip = $"{r.SchemaName}: the {peer} records associated with the {data.Entity} (through {r.IntersectEntity}), " +
                                  "created when missing and then associated"
                    };
                }))
                .OrderByDescending(r => r.Tag.IsSubgrid)
                .ThenBy(r => r.Text, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _tree.BeginUpdate();
            _syncing = true;
            try
            {
                owner.Nodes.Clear();
                if (asPeer)
                {
                    TreeNode hint = NewPlaceholder(PeerHintText, InfoTag);
                    owner.Nodes.Add(hint);
                    placeholders.Add(hint);
                }
                foreach (var row in rows)
                {
                    var node = new TreeNode(row.Text) { Name = row.Tag.SchemaName, Tag = row.Tag, ToolTipText = row.ToolTip };
                    TreeNode dummy = NewPlaceholder(LoadingText, DummyTag);
                    node.Nodes.Add(dummy);
                    placeholders.Add(dummy);
                    owner.Nodes.Add(node);
                    node.Checked = IsTicked(data.Entity, row.Tag.SchemaName, asPeer);   // once in the tree (AfterCheck is muted meanwhile)
                }
                if (rows.Count == 0)
                {
                    TreeNode none = NewPlaceholder(NoRelationshipsText, InfoTag);
                    owner.Nodes.Add(none);
                    placeholders.Add(none);
                }
            }
            finally
            {
                _syncing = false;
                _tree.EndUpdate();
            }
            foreach (TreeNode placeholder in placeholders) HideCheckBox(placeholder);
        }

        // =====================================================================================
        // Ticks: one set per entity
        // =====================================================================================

        private void OnBeforeCheck(object sender, TreeViewCancelEventArgs e)
        {
            if (!(e.Node?.Tag is RelationshipTag)) e.Cancel = true;   // the root and the placeholder lines have no tick
        }

        private void OnAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_syncing || !(e.Node?.Tag is RelationshipTag tag)) return;
            HashSet<string> ticked = Configure(tag.ParentEntity, tag.ParentIsPeer);
            if (e.Node.Checked) ticked.Add(tag.SchemaName);
            else ticked.Remove(tag.SchemaName);
            SyncEntity(tag.ParentEntity);
        }

        /// <summary>
        /// Is the relationship of the entity ticked: its configured ticks, else (unconfigured) its
        /// main-form subgrids - and nothing where the entity is listed as a peer.
        /// </summary>
        private bool IsTicked(string entity, string relationship, bool asPeer) =>
            _configured.TryGetValue(entity, out HashSet<string> ticked)
                ? ticked.Contains(relationship)
                : !asPeer && _loaded.TryGetValue(entity, out EntityRelationships data) && data.Subgrids.Contains(relationship);

        /// <summary>
        /// The ticked set of an entity, which becomes configured - starting from the ticks it showed where
        /// it was changed: its eligible subgrids, or nothing where it is listed as a peer.
        /// </summary>
        private HashSet<string> Configure(string entity, bool asPeer)
        {
            if (_configured.TryGetValue(entity, out HashSet<string> ticked)) return ticked;
            ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!asPeer && _loaded.TryGetValue(entity, out EntityRelationships data))
            {
                foreach (string relationship in data.SchemaNames)
                {
                    if (data.Subgrids.Contains(relationship)) ticked.Add(relationship);
                }
            }
            _configured[entity] = ticked;
            return ticked;
        }

        /// <summary>Shows the ticks of <paramref name="entity"/> on every node of its relationships, wherever the entity appears.</summary>
        private void SyncEntity(string entity)
        {
            _syncing = true;
            try
            {
                foreach (TreeNode node in AllNodes(_tree.Nodes))
                {
                    if (!(node.Tag is RelationshipTag tag) || !string.Equals(tag.ParentEntity, entity, StringComparison.OrdinalIgnoreCase)) continue;
                    bool ticked = IsTicked(entity, tag.SchemaName, tag.ParentIsPeer);
                    if (node.Checked != ticked) node.Checked = ticked;
                }
            }
            finally
            {
                _syncing = false;
            }
            if (!IsLoading) SetStatus(SelectionSummary());
        }

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (TreeNode child in AllNodes(node.Nodes)) yield return child;
            }
        }

        /// <summary>E.g. "Configured: account (2 ticked), contact (none). Every other entity follows its main-form subgrids (as a peer: nothing)."</summary>
        private string SelectionSummary()
        {
            if (_configured.Count == 0) return "No entity configured: every entity follows the subgrids on its main forms (as a peer: nothing).";
            string configured = string.Join(", ", _configured
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + " (" + (p.Value.Count == 0 ? "none" : p.Value.Count.ToString(CultureInfo.CurrentCulture) + " ticked") + ")"));
            return "Configured: " + configured + ". Every other entity follows its main-form subgrids (as a peer: nothing).";
        }

        // =====================================================================================
        // Buttons
        // =====================================================================================

        private void OnTickSubgridsClick(object sender, EventArgs e) =>
            ApplyToTarget((ticked, data) =>
            {
                foreach (string relationship in data.SchemaNames)
                {
                    if (data.Subgrids.Contains(relationship)) ticked.Add(relationship);
                }
            });

        private void OnTickCustomClick(object sender, EventArgs e) =>
            ApplyToTarget((ticked, data) =>
            {
                foreach (ChildRelationship relationship in data.Eligible)
                {
                    if (relationship.IsCustomRelationship) ticked.Add(relationship.SchemaName);
                }
                foreach (ManyToManyRelationship relationship in data.EligibleManyToMany)
                {
                    if (relationship.IsCustomRelationship) ticked.Add(relationship.SchemaName);
                }
            });

        private void OnUntickAllClick(object sender, EventArgs e) => ApplyToTarget((ticked, data) => ticked.Clear());

        /// <summary>
        /// Applies a button to the relationships under the selected node - those of the entity a
        /// relationship leads to, or of the root entity (also when nothing is selected): loads them if
        /// needed, changes the ticked set of that entity (which becomes configured) and shows the result.
        /// </summary>
        private async void ApplyToTarget(Action<HashSet<string>, EntityRelationships> change)
        {
            TreeNode target = _tree.SelectedNode;
            while (target != null && !(target.Tag is RelationshipTag) && !(target.Tag is EntityTag)) target = target.Parent;
            target = target ?? _root;
            if (target == null) return;
            string entity = EntityOf(target);
            bool asPeer = ListsPeer(target);
            try
            {
                EntityRelationships data = await LoadAsync(entity);
                if (IsDisposed) return;
                change(Configure(entity, asPeer), data);
                SyncEntity(entity);
                if (target.TreeView != null) target.Expand();   // shows the relationships just changed
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                string message = $"The relationships of {entity} could not be loaded: {CopyEngine.ErrorText(ex)}";
                SetStatus(message);
                ShowMessage(message, MessageBoxIcon.Error);
            }
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        /// <summary>
        /// The entity whose relationships a node lists: the root entity, the child entity of a 1:N
        /// relationship, or the entity at the other end of an N:N relationship.
        /// </summary>
        private string EntityOf(TreeNode node)
        {
            for (TreeNode current = node; current != null; current = current.Parent)
            {
                if (current.Tag is RelationshipTag relationship) return relationship.TargetEntity;
                if (current.Tag is EntityTag root) return root.Entity;
            }
            return null;
        }

        /// <summary>True when the relationships a node lists are those of a peer: the node is an N:N relationship.</summary>
        private static bool ListsPeer(TreeNode node)
        {
            for (TreeNode current = node; current != null; current = current.Parent)
            {
                if (current.Tag is RelationshipTag relationship) return relationship.IsManyToMany;
                if (current.Tag is EntityTag) return false;
            }
            return false;
        }

        private void SetStatus(string text)
        {
            if (!IsDisposed) _statusLabel.Text = text ?? string.Empty;
        }

        private static TreeNode NewPlaceholder(string text, object tag) => new TreeNode(text) { Tag = tag, ForeColor = SystemColors.GrayText };

        // ---- no check box on the root and the placeholder lines ----

        private const int TvFirst = 0x1100;
        private const int TvmSetItemW = TvFirst + 63;
        private const int TvifState = 0x0008;
        private const int TvisStateImageMask = 0xF000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TvItem
        {
            public int Mask;
            public IntPtr Item;
            public int State;
            public int StateMask;
            public IntPtr Text;
            public int TextMax;
            public int Image;
            public int SelectedImage;
            public int Children;
            public IntPtr Param;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TvItem lParam);

        /// <summary>Removes the check box of one node (state image 0); BeforeCheck refuses its tick anyway.</summary>
        private void HideCheckBox(TreeNode node)
        {
            if (node?.TreeView == null || !_tree.IsHandleCreated) return;
            var item = new TvItem { Mask = TvifState, Item = node.Handle, StateMask = TvisStateImageMask, State = 0 };
            SendMessage(_tree.Handle, TvmSetItemW, IntPtr.Zero, ref item);
        }

        // =====================================================================================
        // Types
        // =====================================================================================

        /// <summary>The tag of the root node.</summary>
        private sealed class EntityTag
        {
            public EntityTag(string entity) { Entity = entity; }
            public string Entity { get; }
        }

        /// <summary>
        /// The tag of a relationship node: whose relationship it is (and whether that entity is listed as a
        /// peer there), the 1:N or the N:N relationship, and whether a main form shows it.
        /// </summary>
        internal sealed class RelationshipTag
        {
            public RelationshipTag(string parentEntity, ChildRelationship relationship, bool isSubgrid, bool parentIsPeer)
            {
                ParentEntity = parentEntity;
                Relationship = relationship;
                IsSubgrid = isSubgrid;
                ParentIsPeer = parentIsPeer;
            }

            public RelationshipTag(string parentEntity, ManyToManyRelationship relationship, bool isSubgrid, bool parentIsPeer)
            {
                ParentEntity = parentEntity;
                ManyToMany = relationship;
                IsSubgrid = isSubgrid;
                ParentIsPeer = parentIsPeer;
            }

            /// <summary>The entity the relationship belongs to: the entity of the node above.</summary>
            public string ParentEntity { get; }

            /// <summary>The 1:N relationship (null for an N:N one).</summary>
            public ChildRelationship Relationship { get; }

            /// <summary>The N:N relationship (null for a 1:N one).</summary>
            public ManyToManyRelationship ManyToMany { get; }

            public bool IsSubgrid { get; }

            /// <summary>The parent entity is listed as a peer there (unconfigured, it has nothing ticked).</summary>
            public bool ParentIsPeer { get; }

            public bool IsManyToMany => ManyToMany != null;

            public string SchemaName => IsManyToMany ? ManyToMany.SchemaName : Relationship.SchemaName;

            /// <summary>The entity the node expands into: the child entity (1:N) or the entity at the other end (N:N).</summary>
            public string TargetEntity => IsManyToMany ? ManyToMany.OtherEntity(ParentEntity) : Relationship.ChildEntity.Trim().ToLowerInvariant();
        }

        /// <summary>What the dialog knows of one entity: its eligible relationships, main-form subgrids and the names of the entities they lead to.</summary>
        private sealed class EntityRelationships
        {
            public EntityRelationships(string entity, IReadOnlyList<ChildRelationship> eligible, IReadOnlyList<ManyToManyRelationship> eligibleManyToMany,
                                       HashSet<string> subgrids, Dictionary<string, string> entityNames, string subgridProblem)
            {
                Entity = entity;
                Eligible = eligible;
                EligibleManyToMany = eligibleManyToMany;
                Subgrids = subgrids;
                EntityNames = entityNames;
                SubgridProblem = subgridProblem;
            }

            public string Entity { get; }
            public IReadOnlyList<ChildRelationship> Eligible { get; }
            public IReadOnlyList<ManyToManyRelationship> EligibleManyToMany { get; }
            public HashSet<string> Subgrids { get; }
            public Dictionary<string, string> EntityNames { get; }   // child and peer entities: their plural display names
            public string SubgridProblem { get; set; }               // the forms could not be read (reported once)

            /// <summary>The schema names of the eligible relationships: 1:N, then N:N.</summary>
            public IEnumerable<string> SchemaNames => Eligible.Select(r => r.SchemaName).Concat(EligibleManyToMany.Select(r => r.SchemaName));
        }

        /// <summary>
        /// A TreeView whose check boxes survive a double click: natively a double click on a check box
        /// toggles it twice while WinForms raises one AfterCheck, so tick and model drift apart. The
        /// second click of a double click on a check box is ignored (one click, one toggle).
        /// </summary>
        private sealed class CheckedTreeView : TreeView
        {
            private const int WmLButtonDblClk = 0x0203;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmLButtonDblClk && CheckBoxes)
                {
                    long lParam = m.LParam.ToInt64();
                    var point = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                    if (HitTest(point).Location == TreeViewHitTestLocations.StateImage)
                    {
                        m.Result = IntPtr.Zero;
                        return;
                    }
                }
                base.WndProc(ref m);
            }
        }
    }
}
