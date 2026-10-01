using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using MyscotekDataCopier.Core;
using MyscotekDataCopier.Core.Schema;
using MyscotekDataCopier.Core.Services;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace MyscotekDataCopier.UI
{
    /// <summary>
    /// The Data Copier tool (SPEC section 6). The SOURCE is the tool's normal XrmToolBox
    /// connection (<see cref="PluginControlBase.Service"/>); the DESTINATION is a second connection
    /// requested with <see cref="PluginControlBase.RaiseRequestConnectionEvent"/> as an additional
    /// organisation (<see cref="DestinationActionName"/>). Pick an entity and
    /// a view, load and tick records, choose the relationship options (SPEC 5.10; the 1:N
    /// relationships in <see cref="RelationshipPickerForm"/>), then copy them with <see cref="CopyEngine"/>.
    /// Long operations run with Task.Run + async/await (not WorkAsync) so the log stays visible and live
    /// while they run; one operation at a time, and Cancel stops it. Every action that needs the
    /// source asks XrmToolBox for it when there is none (<see cref="RequestSource"/>). Control
    /// construction is in DataCopierControl.Layout.cs.
    /// </summary>
    public partial class DataCopierControl : PluginControlBase, IGitHubPlugin, IHelpPlugin
    {
        /// <summary>
        /// The action name of the destination connection request: XrmToolBox's "additional organisation".
        /// For it the host leaves the tab's own connection alone - its status bar connection, the tab
        /// title and highlight, and the connection it records for the tab stay the source's - and hands
        /// the new connection to <see cref="UpdateConnection"/> with this action name (XrmToolBox's own
        /// MultipleConnectionsPluginControlBase uses the same name). Any other action name would make the
        /// destination the tab's connection in XrmToolBox.
        /// </summary>
        internal const string DestinationActionName = "AdditionalOrganization";

        /// <summary>The Parameter of the destination request; XrmToolBox passes it back unchanged.</summary>
        internal const string DestinationParameter = "destination";

        internal const string DialogTitle = "Data Copier";

        // The public repository: XrmToolBox links the tool to it (IGitHubPlugin) and opens the help page (IHelpPlugin).
        internal const string GitHubUserName = "scottm1985";
        internal const string GitHubRepositoryName = "DataCopier";
        internal const string HelpPageUrl = "https://github.com/scottm1985/DataCopier#readme";

        // Columns of the DataTable behind the grid: the Copy tick box, the record id, then c0..cN - one
        // per view column. The synthetic names are unique and keep the row filter (and the grid's
        // DataPropertyName) free of escaping problems with alias.attribute names; the grid headers show
        // the attribute display names (the raw view column name is the header's tooltip).
        internal const string CopyColumn = "Copy";
        internal const string IdColumn = "__id";
        private const string ValueColumnPrefix = "c";

        internal const string NoSourceMessage =
            "Not connected: choose the SOURCE environment with the XrmToolBox connection bar (or press Refresh entities) and its entities are listed here.";

        /// <summary>Suffix of a virtual table's logical name in the entity list.</summary>
        internal const string VirtualMarker = " (virtual)";

        private readonly IContainer _components = new Container();
        private readonly Action<DataCopierSettings> _saveSettings;
        private readonly bool _mirrorToXrmToolBoxLog;
        private readonly UiLogger _logger;
        private DataCopierSettings _settings;
        private bool _applyingSettings;

        /// <summary>Source metadata, cached for the lifetime of the source connection: grid headers and the copy.</summary>
        private DataverseSchemaProvider _sourceSchema;

        // ---- destination connection (the source is Service / ConnectionDetail) ----
        private IOrganizationService _destinationService;
        private ConnectionDetail _destinationDetail;
        private DataverseSchemaProvider _destinationSchema;   // cached for the lifetime of the destination connection

        /// <summary>Bumped when the source changes or is refreshed: results of older operations are dropped.</summary>
        private int _generation;

        // ---- entities and views ----
        private IList<EntityInfo> _entities = new List<EntityInfo>();
        private EntityInfo _currentEntity;
        private bool _suppressEntitySelection;

        // ---- loaded records ----
        private DataTable _table;
        private ViewInfo _loadedView;
        private IList<ViewColumn> _loadedColumns = new List<ViewColumn>();
        private string _loadedFetchXml;
        private int _nextPage = 1;
        private string _pagingCookie;
        private bool _moreRecords;
        private int _rowsWithoutId;
        private int _selectedCount;

        // ---- the one long operation that may run at a time ----
        private CancellationTokenSource _operation;
        private bool _reloadEntitiesWhenIdle;
        private int _copyRun;
        private CopyProgress _lastProgress;

        private enum RecordLoad { FirstPage, NextPage, AllPages }

        /// <summary>Created by XrmToolBox (<see cref="MyscotekDataCopierPlugin.GetControl"/>).</summary>
        public DataCopierControl()
            : this(DataCopierSettings.LoadFromXrmToolBox, DataCopierSettings.SaveToXrmToolBox, mirrorToXrmToolBoxLog: true)
        {
        }

        /// <summary>
        /// Test seam: where the settings come from and go to, and whether log lines are mirrored to
        /// XrmToolBox's own log file. The UI tests pass in-memory settings and no mirroring, so they
        /// touch nothing outside the test run. A settings store that fails to load is not fatal: the
        /// defaults are used and a warning is logged.
        /// </summary>
        internal DataCopierControl(Func<DataCopierSettings> loadSettings, Action<DataCopierSettings> saveSettings, bool mirrorToXrmToolBoxLog)
        {
            _saveSettings = saveSettings;
            _mirrorToXrmToolBoxLog = mirrorToXrmToolBoxLog;
            ShowMessage = ShowMessageBox;
            ShowPicker = picker => picker.ShowDialog(this);

            // A design size so the docked layout is computed before XrmToolBox docks the control.
            Size = new Size(1100, 750);
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9f);
            BuildUi();

            _logger = new UiLogger(_log, MirrorEngineLine);
            _settings = LoadSettings(loadSettings);
            ApplySettingsToControls();
            ClearRecords();
            UpdateControlStates();
        }

        /// <summary>
        /// Shows a message box owned by the tool: (text, buttons, icon, default button) => result.
        /// A seam for the UI tests, which replace it so no modal dialog can block them.
        /// </summary>
        internal Func<string, MessageBoxButtons, MessageBoxIcon, MessageBoxDefaultButton, DialogResult> ShowMessage { get; set; }

        /// <summary>
        /// Shows the relationship picker modally and returns its result (ShowDialog owned by the tool).
        /// A seam for the UI tests, which drive the dialog without a modal loop.
        /// </summary>
        internal Func<RelationshipPickerForm, DialogResult> ShowPicker { get; set; }

        // ---- read-only views of the state, for the UI tests ----
        internal UiLogger Logger => _logger;
        internal DataCopierSettings Settings => _settings;
        internal DataTable Records => _table;
        internal bool IsBusy => _operation != null;

        // ---- IGitHubPlugin, IHelpPlugin ----
        public string UserName => GitHubUserName;
        public string RepositoryName => GitHubRepositoryName;
        public string HelpUrl => HelpPageUrl;

        // =====================================================================================
        // Host integration: connections, closing, disposal
        // =====================================================================================

        /// <summary>
        /// Called by XrmToolBox when a connection is applied to the tool. The destination comes back
        /// as an additional organisation (<see cref="DestinationActionName"/>, with
        /// <see cref="DestinationParameter"/>); anything else is the source: a plain connection
        /// (actionName "") or one an action asked for through <see cref="RequestSource"/> (actionName =
        /// the name of that action's method).
        /// </summary>
        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateConnection(newService, detail, actionName, parameter)));
                return;
            }

            if (string.Equals(actionName, DestinationActionName, StringComparison.Ordinal))
            {
                // Base is deliberately NOT called: it would replace Service, which must stay the source
                // (and then look for a method named AdditionalOrganization). The tool asks for no other
                // additional organisation, so the parameter is not checked.
                SetDestination(newService, detail);
                return;
            }

            // Base comes first: it sets Service and ConnectionDetail and then, for a requested
            // connection, invokes the named method by reflection - so the action runs once, against the
            // new source. ("" is a plain connection; null would make base look for a nameless method.)
            string action = actionName ?? string.Empty;
            // Refresh entities resets the tool and loads the new source's entities itself (it does
            // nothing while an operation runs - never the case without a source - so check that too).
            bool actionLoadsEntities = string.Equals(action, nameof(RefreshEntities), StringComparison.Ordinal) && !IsBusy;
            base.UpdateConnection(newService, detail, action, parameter);
            OnSourceChanged(resetAndLoad: !actionLoadsEntities);
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            SaveSettings();
            base.ClosingPlugin(info);
            if (!info.Cancel) _operation?.Cancel();   // a running copy stops at the next record
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitLayout();
            if (Service == null) _logger.Write(LogLevel.Info, NoSourceMessage);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _operation?.Cancel();   // a running operation stops; its continuation sees IsDisposed
            base.Dispose(disposing);
            if (disposing)
            {
                // After base: the controls using these (grid, buttons, log box) are disposed by then.
                _components.Dispose();
                _table?.Dispose();
                _boldFont?.Dispose();
                _logFont?.Dispose();
            }
        }

        private void SetDestination(IOrganizationService service, ConnectionDetail detail)
        {
            _destinationService = service;
            _destinationDetail = service != null ? detail : null;
            _destinationSchema = service != null ? new DataverseSchemaProvider(service) : null;
            RefreshConnectionLabels();

            if (service == null)
            {
                _logger.Write(LogLevel.Warning, "Destination environment cleared.");
            }
            else
            {
                _logger.Write(LogLevel.Info, "Destination environment: " + DescribeConnection(detail, service));
                WarnIfSameOrganization();
            }
            UpdateControlStates();
        }

        /// <summary>
        /// A new source: labels, log line and same-organisation check; with <paramref name="resetAndLoad"/>
        /// also forgets everything of the previous source and loads the new source's entities (without
        /// it the requested action - Refresh entities - has just done so).
        /// </summary>
        private void OnSourceChanged(bool resetAndLoad)
        {
            if (resetAndLoad)
            {
                _generation++;
                _operation?.Cancel();   // whatever is running belongs to the previous source
                _sourceSchema = Service != null ? new DataverseSchemaProvider(Service) : null;
                _entities = new List<EntityInfo>();
                _currentEntity = null;
                PopulateEntityList();
                ClearViews();
                ClearRecords();
            }
            RefreshConnectionLabels();

            if (Service == null)
            {
                _logger.Write(LogLevel.Info, NoSourceMessage);
                UpdateControlStates();
                return;
            }

            _logger.Write(LogLevel.Info, "Source environment: " + DescribeConnection(ConnectionDetail, Service));
            WarnIfSameOrganization();
            if (resetAndLoad) StartEntityLoad();
        }

        private void RefreshConnectionLabels()
        {
            _sourceLabel.Text = "Source: " + ConnectionName(ConnectionDetail, Service);
            _destinationLabel.Text = "Destination: " + ConnectionName(_destinationDetail, _destinationService);
        }

        private void WarnIfSameOrganization()
        {
            if (Service != null && _destinationService != null && IsSameOrganization())
                _logger.Write(LogLevel.Warning, "The destination looks like the same organisation as the source: you will be asked to confirm before anything is copied.");
        }

        private bool IsSameOrganization()
        {
            if (Service != null && ReferenceEquals(Service, _destinationService)) return true;
            return ConnectionDetail != null && _destinationDetail != null && SameOrganization(ConnectionDetail, _destinationDetail);
        }

        /// <summary>The same organisation: the same organisation name, or the same (web or service) URL.</summary>
        internal static bool SameOrganization(ConnectionDetail a, ConnectionDetail b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.Organization) && !string.IsNullOrEmpty(b.Organization)
                && string.Equals(a.Organization, b.Organization, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string ua = NormalizeUrl(a.WebApplicationUrl) ?? NormalizeUrl(a.OrganizationServiceUrl);
            string ub = NormalizeUrl(b.WebApplicationUrl) ?? NormalizeUrl(b.OrganizationServiceUrl);
            return !string.IsNullOrEmpty(ua) && string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            string trimmed = url.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string ConnectionName(ConnectionDetail detail, IOrganizationService service)
        {
            if (service == null) return "(none)";
            string name = detail?.ConnectionName;
            return string.IsNullOrWhiteSpace(name) ? "(unnamed connection)" : name;
        }

        /// <summary>Connection name plus its URL when known, for log lines.</summary>
        private static string DescribeConnection(ConnectionDetail detail, IOrganizationService service)
        {
            string name = ConnectionName(detail, service);
            string url = NormalizeUrl(detail?.WebApplicationUrl) ?? NormalizeUrl(detail?.OrganizationServiceUrl);
            return url == null ? name : name + " (" + url + ")";
        }

        // =====================================================================================
        // Settings
        // =====================================================================================

        private DataCopierSettings LoadSettings(Func<DataCopierSettings> load)
        {
            try
            {
                return load?.Invoke() ?? new DataCopierSettings();
            }
            catch (Exception ex)
            {
                _logger.Write(LogLevel.Warning, "Settings could not be loaded, the defaults are used: " + CopyEngine.ErrorText(ex));
                return new DataCopierSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                _saveSettings?.Invoke(_settings);
            }
            catch (Exception ex)
            {
                _logger.Write(LogLevel.Warning, "Settings could not be saved: " + CopyEngine.ErrorText(ex));
            }
        }

        private void ApplySettingsToControls()
        {
            _applyingSettings = true;
            try
            {
                _dryRun.Checked = _settings.DryRun;
                _preserveCreatedOn.Checked = _settings.PreserveCreatedOn;
                _bypassPlugins.Checked = _settings.BypassCustomPlugins;
                _copyLookups.Checked = _settings.CopyLookups;
                _copyChildren.Checked = _settings.CopyChildren;
                _includePersonalViews.Checked = _settings.IncludePersonalViews;
            }
            finally
            {
                _applyingSettings = false;
            }
        }

        private void OnOptionChanged(object sender, EventArgs e)
        {
            if (_applyingSettings) return;
            _settings.DryRun = _dryRun.Checked;
            _settings.PreserveCreatedOn = _preserveCreatedOn.Checked;
            _settings.BypassCustomPlugins = _bypassPlugins.Checked;
            _settings.CopyLookups = _copyLookups.Checked;
            _settings.CopyChildren = _copyChildren.Checked;
            SaveSettings();
            UpdateControlStates();   // Relationships... follows the 1:N option
        }

        private void OnIncludePersonalViewsChanged(object sender, EventArgs e)
        {
            if (_applyingSettings) return;
            _settings.IncludePersonalViews = _includePersonalViews.Checked;
            SaveSettings();
            // Only a setting while no entity is selected (no connection needed); otherwise its views are read again.
            if (_currentEntity != null && !IsBusy) RequestSource("Loading views", ReloadViews);
        }

        /// <summary>Reads the current entity's views again, keeping the loaded records. Runs through <see cref="RequestSource"/>.</summary>
        private void ReloadViews()
        {
            EntityInfo entity = _currentEntity;
            if (entity != null && !IsBusy) RunGuarded("Loading views", () => LoadViewsAsync(entity, resetRecords: false));
        }

        private CopyOptions BuildOptions()
        {
            var options = new CopyOptions
            {
                DryRun = _dryRun.Checked,
                PreserveCreatedOn = _preserveCreatedOn.Checked,
                BypassCustomPluginExecution = _bypassPlugins.Checked,
                CopyLookups = _copyLookups.Checked,
                CopyChildren = _copyChildren.Checked
            };
            options.SetNeverCreateEntities(_settings.NeverCreateEntities);
            return options;
        }

        private static string NeverCreateList(CopyOptions options) =>
            string.Join(", ", options.NeverCreateEntities.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        /// <summary>How the source organisation keys the relationship selections in the settings: its URL (or name).</summary>
        private string SourceOrganizationKey() =>
            DataCopierSettings.OrganizationKey(NormalizeUrl(ConnectionDetail?.WebApplicationUrl)
                                               ?? NormalizeUrl(ConnectionDetail?.OrganizationServiceUrl)
                                               ?? ConnectionDetail?.Organization);

        // =====================================================================================
        // 1:N relationships (SPEC 5.10)
        // =====================================================================================

        private void OnRelationshipsClick(object sender, EventArgs e) => RequestSource("Choosing the 1:N relationships", ChooseRelationships);

        /// <summary>Relationships...: runs through <see cref="RequestSource"/>.</summary>
        private void ChooseRelationships() => Guard("Choosing the 1:N relationships", ShowRelationshipPicker);

        /// <summary>
        /// Opens the relationship picker for the current entity with the selections saved for this source
        /// organisation; OK saves the entities configured in it.
        /// </summary>
        private void ShowRelationshipPicker()
        {
            IOrganizationService service = Service;
            EntityInfo entity = _currentEntity;
            if (IsBusy || service == null || entity == null) return;

            string organization = SourceOrganizationKey();
            var subgrids = new FormSubgridService();   // the main forms are read afresh for each picker session
            using (var picker = new RelationshipPickerForm(entity.LogicalName, entity.DisplayName, SourceSchema(service),
                       name => subgrids.GetMainFormSubgridRelationships(service, name), NeverCreateSet(),
                       _settings.GetRelationshipSelections(organization)))
            {
                if (ShowPicker(picker) != DialogResult.OK) return;
                IReadOnlyDictionary<string, ISet<string>> configured = picker.ConfiguredSelections;
                _settings.SetRelationshipSelections(organization, configured);
                SaveSettings();
                _logger.Write(LogLevel.Info, DescribeConfiguredRelationships(configured));
            }
        }

        /// <summary>The log line after the picker: the configured entities and their ticked relationships.</summary>
        internal static string DescribeConfiguredRelationships(IReadOnlyDictionary<string, ISet<string>> configured)
        {
            if (configured == null || configured.Count == 0)
                return "1:N relationships: no entity configured; every entity follows the subgrids on its active main forms.";
            string entities = string.Join("; ", configured
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + ": " + (p.Value == null || p.Value.Count == 0
                    ? "none"
                    : string.Join(", ", p.Value.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)))));
            return "1:N relationships saved - " + entities + ". Every other entity follows the subgrids on its active main forms.";
        }

        /// <summary>
        /// The run header lines about the 1:N relationships: those followed from the selected entity
        /// (and whether they were ticked in the picker or come from its main-form subgrids), then how
        /// the other entities are treated. Reads metadata and forms: call it off the UI thread.
        /// </summary>
        internal static IList<string> DescribeChildRelationships(DefaultChildRelationshipSelector selector, string entity)
        {
            string origin = selector.IsConfigured(entity) ? "ticked in Relationships..." : "the subgrids on its active main forms";
            string list;
            try
            {
                IReadOnlyList<ChildRelationship> relationships = selector.GetChildRelationships(entity);
                list = relationships.Count == 0 ? "none" : string.Join(", ", relationships.Select(r => r.ToString()));
            }
            catch (Exception ex)
            {
                list = "could not be determined: " + CopyEngine.ErrorText(ex);
            }
            return new List<string>
            {
                $"1:N relationships followed from {entity} ({origin}): {list}",
                "Other entities reached as child records follow the relationships ticked for them in Relationships..., otherwise the subgrids on their active main forms."
            };
        }

        // =====================================================================================
        // Actions that need the source connection
        // =====================================================================================

        /// <summary>
        /// Runs <paramref name="action"/> through <see cref="PluginControlBase.ExecuteMethod(Action)"/>: at
        /// once while the source is connected; without one XrmToolBox shows its connection dialog (the
        /// tool library's rule for a control that needs a connection) and, once the user has connected,
        /// calls <see cref="UpdateConnection"/> with the action's method NAME, which the base class
        /// invokes by reflection (instance, public or not, no parameters). So every action passed here is
        /// a parameterless instance method with a unique name - never a lambda, which ExecuteMethod
        /// refuses - and guards itself: an exception in it would reach XrmToolBox from UpdateConnection.
        /// </summary>
        private void RequestSource(string what, Action action)
        {
            try
            {
                ExecuteMethod(action);
            }
            catch (Exception ex)
            {
                ReportError(what + " failed", ex);
            }
        }

        // =====================================================================================
        // Long operations
        // =====================================================================================

        /// <summary>
        /// Runs a long operation for an event handler: this async void never lets an exception escape.
        /// Failures are logged and shown in a message box; a cancellation is only logged.
        /// </summary>
        private async void RunGuarded(string action, Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) _logger.Write(LogLevel.Warning, action + " cancelled.");
            }
            catch (Exception ex)
            {
                ReportError(action + " failed", ex);
            }
        }

        /// <summary>Runs a synchronous handler's work; a failure is logged and shown instead of reaching XrmToolBox.</summary>
        private void Guard(string action, Action work)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                ReportError(action + " failed", ex);
            }
        }

        private void ReportError(string what, Exception ex)
        {
            if (IsDisposed) return;
            _logger.Log(LogLevel.Error, what + ": " + CopyEngine.ErrorText(ex));   // Log: mirrored to XrmToolBox's log
            try
            {
                ShowMessage(ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
            }
            catch (Exception)
            {
                // The tab may be closing; the error is in the log.
            }
        }

        /// <summary>Marks the tool busy (inputs disabled, Cancel enabled) and returns the operation's token.</summary>
        private CancellationToken BeginOperation(string status)
        {
            if (_operation != null) throw new InvalidOperationException("Another operation is still running.");
            _operation = new CancellationTokenSource();
            SetProgress(status);
            UpdateControlStates();
            return _operation.Token;
        }

        private void EndOperation()
        {
            CancellationTokenSource operation = _operation;
            _operation = null;
            operation?.Dispose();
            if (IsDisposed) return;
            // Whatever the outcome (a failure too), the running-status text no longer applies; the copy
            // then shows its final counts.
            SetProgress(string.Empty);
            UpdateControlStates();
            if (_reloadEntitiesWhenIdle)
            {
                _reloadEntitiesWhenIdle = false;
                StartEntityLoad();
            }
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            if (_operation == null) return;
            _operation.Cancel();
            SetProgress("Cancelling...");
        }

        private void SetProgress(string text) => _progressLabel.Text = text ?? string.Empty;

        /// <summary>Enables the inputs that make sense now (SPEC 6: busy disables them and enables Cancel).</summary>
        private void UpdateControlStates()
        {
            bool busy = IsBusy;
            bool connected = Service != null;
            bool hasRows = _table != null && _table.Rows.Count > 0;
            var selectedView = _viewCombo.SelectedItem as ViewInfo;
            bool selectedIsLoaded = selectedView != null && SameView(selectedView, _loadedView);

            _selectDestinationButton.Enabled = !busy;
            _refreshEntitiesButton.Enabled = !busy;
            _entityList.Enabled = !busy;
            _viewCombo.Enabled = !busy && _viewCombo.Items.Count > 0;
            _includePersonalViews.Enabled = !busy;
            _loadRecordsButton.Enabled = !busy && connected && _currentEntity != null && selectedView != null;
            _loadMoreButton.Enabled = !busy && connected && selectedIsLoaded && _moreRecords;
            _loadAllButton.Enabled = !busy && connected && _currentEntity != null && selectedView != null && (!selectedIsLoaded || _moreRecords);
            _selectAllButton.Enabled = !busy && hasRows;
            _selectNoneButton.Enabled = !busy && hasRows;
            _dryRun.Enabled = !busy;
            _preserveCreatedOn.Enabled = !busy;
            _bypassPlugins.Enabled = !busy;
            _copyLookups.Enabled = !busy;
            _copyChildren.Enabled = !busy;
            _relationshipsButton.Enabled = !busy && connected && _copyChildren.Checked && _currentEntity != null && IsCopyable(_currentEntity);
            _copyButton.Enabled = !busy && connected && _destinationService != null && _currentEntity != null && _selectedCount > 0;
            _cancelButton.Enabled = busy;

            // Ticks are frozen while something runs; the grid stays scrollable.
            DataGridViewColumn copyColumn = _grid.Columns[CopyColumn];
            if (copyColumn != null && copyColumn.ReadOnly != busy)
            {
                if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
                copyColumn.ReadOnly = busy;
            }
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        // =====================================================================================
        // Entities
        // =====================================================================================

        private void OnRefreshEntitiesClick(object sender, EventArgs e) => RequestSource("Refreshing entities", RefreshEntities);

        /// <summary>Refresh entities: runs through <see cref="RequestSource"/> (and <see cref="UpdateConnection"/> knows it loads the entities).</summary>
        private void RefreshEntities() => Guard("Refreshing entities", ReloadEntities);

        private void ReloadEntities()
        {
            if (IsBusy) return;
            if (Service == null)
            {
                _logger.Write(LogLevel.Warning, NoSourceMessage);
                return;
            }

            _generation++;
            // "Refresh" also forgets the cached metadata of both sides, so schema changes are picked up.
            _sourceSchema = new DataverseSchemaProvider(Service);
            if (_destinationService != null) _destinationSchema = new DataverseSchemaProvider(_destinationService);
            _entities = new List<EntityInfo>();
            _currentEntity = null;
            PopulateEntityList();
            ClearViews();
            ClearRecords();
            UpdateControlStates();
            StartEntityLoad();
        }

        private void StartEntityLoad()
        {
            if (IsBusy)
            {
                _reloadEntitiesWhenIdle = true;   // after the (cancelled) running operation has ended
                return;
            }
            RunGuarded("Loading entities", LoadEntitiesAsync);
        }

        private async Task LoadEntitiesAsync()
        {
            IOrganizationService service = Service;
            if (IsBusy) return;
            if (service == null)
            {
                _logger.Write(LogLevel.Warning, NoSourceMessage);
                return;
            }

            int generation = _generation;
            CancellationToken token = BeginOperation("Loading entities...");
            IList<EntityInfo> entities;
            bool cancelled;
            try
            {
                entities = await Task.Run(() => EntityCatalog.GetEntities(service));
                cancelled = token.IsCancellationRequested;
            }
            finally
            {
                EndOperation();
            }

            if (generation != _generation || IsDisposed) return;   // the source changed meanwhile
            SetProgress(string.Empty);
            if (cancelled) throw new OperationCanceledException("Loading entities was cancelled.");

            _entities = entities;
            PopulateEntityList();
            _logger.Write(LogLevel.Info, Plural(entities.Count, "entity", "entities") + " loaded from the source.");
            UpdateControlStates();
            ReselectEntity(_settings.LastEntity);
        }

        private void ReselectEntity(string logicalName)
        {
            if (string.IsNullOrWhiteSpace(logicalName)) return;
            EntityInfo entity = _entities.FirstOrDefault(x => string.Equals(x.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
            if (entity == null) return;

            _suppressEntitySelection = true;
            try
            {
                foreach (ListViewItem item in _entityList.Items)
                {
                    item.Selected = ReferenceEquals(item.Tag, entity);
                    if (item.Selected) item.EnsureVisible();
                }
            }
            finally
            {
                _suppressEntitySelection = false;
            }
            RunGuarded("Loading views", () => LoadViewsAsync(entity, resetRecords: true));
        }

        private void OnEntityFilterChanged(object sender, EventArgs e) => Guard("Filtering entities", PopulateEntityList);

        /// <summary>Fills the list with the entities matching the filter (display or logical name), keeping the selection.</summary>
        private void PopulateEntityList()
        {
            string filter = _entityFilter.Text.Trim();
            _suppressEntitySelection = true;
            _entityList.BeginUpdate();
            try
            {
                _entityList.Items.Clear();
                var items = new List<ListViewItem>();
                foreach (EntityInfo entity in _entities)
                {
                    if (filter.Length > 0 && !ContainsText(entity.DisplayName, filter) && !ContainsText(entity.LogicalName, filter)) continue;
                    string logicalName = entity.IsVirtual ? entity.LogicalName + VirtualMarker : entity.LogicalName;
                    items.Add(new ListViewItem(new[] { entity.DisplayName ?? entity.LogicalName, logicalName })
                    {
                        Name = entity.LogicalName,
                        Tag = entity
                    });
                }
                _entityList.Items.AddRange(items.ToArray());

                ListViewItem current = _currentEntity == null
                    ? null
                    : items.FirstOrDefault(i => string.Equals(((EntityInfo)i.Tag).LogicalName, _currentEntity.LogicalName, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    current.Selected = true;
                    current.EnsureVisible();
                }
            }
            finally
            {
                _entityList.EndUpdate();
                _suppressEntitySelection = false;
            }
        }

        private void OnEntitySelectionChanged(object sender, EventArgs e)
        {
            if (_suppressEntitySelection || IsBusy || _entityList.SelectedItems.Count != 1) return;
            RequestSource("Loading views", LoadSelectedEntityViews);
        }

        /// <summary>Loads the views of the entity selected in the list. Runs through <see cref="RequestSource"/>.</summary>
        private void LoadSelectedEntityViews()
        {
            if (_suppressEntitySelection || IsBusy) return;
            if (_entityList.SelectedItems.Count != 1 || !(_entityList.SelectedItems[0].Tag is EntityInfo entity)) return;
            if (_currentEntity != null && string.Equals(_currentEntity.LogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase)) return;
            RunGuarded("Loading views", () => LoadViewsAsync(entity, resetRecords: true));
        }

        /// <summary>Clears the entity selection so that clicking the entity again retries.</summary>
        private void ForgetEntity()
        {
            _currentEntity = null;
            _suppressEntitySelection = true;
            try
            {
                foreach (ListViewItem item in _entityList.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false;
            }
            finally
            {
                _suppressEntitySelection = false;
            }
            UpdateControlStates();
        }

        private HashSet<string> NeverCreateSet()
        {
            var options = new CopyOptions();
            options.SetNeverCreateEntities(_settings.NeverCreateEntities);
            return options.NeverCreateEntities;
        }

        /// <summary>False for a never-create entity or a virtual table: its records are refused by Copy.</summary>
        private bool IsCopyable(EntityInfo entity) => !entity.IsVirtual && !NeverCreateSet().Contains(entity.LogicalName);

        // =====================================================================================
        // Views
        // =====================================================================================

        /// <summary>
        /// Loads the entity's system views (then personal ones when asked). With
        /// <paramref name="resetRecords"/> the entity becomes current and the grid is cleared;
        /// without it (personal views toggled) the loaded records and the selected view are kept.
        /// </summary>
        private async Task LoadViewsAsync(EntityInfo entity, bool resetRecords)
        {
            IOrganizationService service = Service;
            if (IsBusy || service == null || entity == null) return;

            int generation = _generation;
            ViewInfo keep = resetRecords ? null : _viewCombo.SelectedItem as ViewInfo;
            if (resetRecords)
            {
                _currentEntity = entity;
                _settings.LastEntity = entity.LogicalName;
                SaveSettings();
                ClearViews();
                ClearRecords();
                if (NeverCreateSet().Contains(entity.LogicalName))
                    _logger.Write(LogLevel.Warning, $"{entity.LogicalName} is in the never-create list: its records can be listed but not copied.");
                else if (entity.IsVirtual)
                    _logger.Write(LogLevel.Warning, $"{entity.LogicalName} is a virtual table: its records can be listed but not copied.");
            }

            bool includePersonal = _includePersonalViews.Checked;
            CancellationToken token = BeginOperation($"Loading the views of {entity.LogicalName}...");
            IList<ViewInfo> views;
            bool loaded = false;
            try
            {
                views = await Task.Run(() => ViewService.GetViews(service, entity.LogicalName, includePersonal, _logger));
                loaded = !token.IsCancellationRequested;
            }
            finally
            {
                EndOperation();
                if (!loaded && resetRecords && generation == _generation && !IsDisposed && ReferenceEquals(_currentEntity, entity))
                    ForgetEntity();
            }

            if (generation != _generation || IsDisposed || !ReferenceEquals(_currentEntity, entity)) return;
            SetProgress(string.Empty);
            if (!loaded) throw new OperationCanceledException("Loading views was cancelled.");

            if (views.Count == 0)
            {
                views = new List<ViewInfo> { ViewService.CreateAllRecordsView(entity.LogicalName, entity.PrimaryIdAttribute, entity.PrimaryNameAttribute) };
                _logger.Write(LogLevel.Info, $"{entity.LogicalName} has no usable views: {ViewService.AllRecordsViewName} is used.");
            }
            PopulateViews(views, keep);
            UpdateControlStates();
        }

        private void PopulateViews(IList<ViewInfo> views, ViewInfo keep)
        {
            _viewCombo.BeginUpdate();
            try
            {
                _viewCombo.Items.Clear();
                int selected = 0;
                int widest = _viewCombo.Width;
                for (int i = 0; i < views.Count; i++)
                {
                    _viewCombo.Items.Add(views[i]);
                    if (keep != null && SameView(views[i], keep)) selected = i;
                    widest = Math.Max(widest, TextRenderer.MeasureText(views[i].DisplayName ?? string.Empty, _viewCombo.Font).Width + SystemInformation.VerticalScrollBarWidth + 8);
                }
                _viewCombo.DropDownWidth = Math.Min(widest, 700);
                _viewCombo.SelectedIndex = views.Count > 0 ? selected : -1;
            }
            finally
            {
                _viewCombo.EndUpdate();
            }
        }

        private void ClearViews() => _viewCombo.Items.Clear();

        private void OnViewChanged(object sender, EventArgs e) => UpdateControlStates();

        /// <summary>Same saved view (views are re-read when personal views are toggled, so compare ids, not instances).</summary>
        private static bool SameView(ViewInfo a, ViewInfo b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            return a.Id == b.Id && a.IsPersonal == b.IsPersonal
                   && (a.Id != Guid.Empty || string.Equals(a.Name, b.Name, StringComparison.Ordinal));
        }

        // =====================================================================================
        // Records
        // =====================================================================================

        private void OnLoadRecordsClick(object sender, EventArgs e) => RequestSource("Loading records", LoadRecords);

        private void OnLoadMoreClick(object sender, EventArgs e) => RequestSource("Loading records", LoadMoreRecords);

        private void OnLoadAllClick(object sender, EventArgs e) => RequestSource("Loading records", LoadAllRecords);

        // Load records, Load more, Load all: they run through RequestSource.
        private void LoadRecords() => RunGuarded("Loading records", () => LoadRecordsAsync(RecordLoad.FirstPage));

        private void LoadMoreRecords() => RunGuarded("Loading records", () => LoadRecordsAsync(RecordLoad.NextPage));

        private void LoadAllRecords() => RunGuarded("Loading records", () => LoadRecordsAsync(RecordLoad.AllPages));

        /// <summary>
        /// Load records: clear and load page 1 of the selected view. Load more: append the next page.
        /// Load all: keep loading (from where the selected view got to, or from page 1) until there are
        /// no more records or Cancel is pressed; what was loaded stays.
        /// </summary>
        private async Task LoadRecordsAsync(RecordLoad mode)
        {
            IOrganizationService service = Service;
            EntityInfo entity = _currentEntity;
            var view = _viewCombo.SelectedItem as ViewInfo;
            if (IsBusy || service == null || entity == null || view == null) return;

            bool continuing = mode != RecordLoad.FirstPage && SameView(view, _loadedView);
            if (continuing && !_moreRecords) return;                 // everything is loaded already
            if (mode == RecordLoad.NextPage && !continuing) return;  // "more" of a view that is not loaded
            if (!continuing) StartRecordSet(entity, view);

            int generation = _generation;
            int pageSize = _settings.EffectivePageSize;
            string fetchXml = _loadedFetchXml;
            string primaryId = entity.PrimaryIdAttribute;
            // A new record set also gets its column headers (display names), resolved off the UI thread
            // together with its first page.
            IList<ViewColumn> headerColumns = continuing ? null : _loadedColumns;
            ISchemaProvider sourceSchema = SourceSchema(service);
            CancellationToken token = BeginOperation("Loading records...");
            try
            {
                do
                {
                    int page = _nextPage;
                    string cookie = _pagingCookie;
                    IList<ViewColumn> columns = headerColumns;
                    SetProgress($"Loading records (page {page})...");
                    (RecordPage result, IList<string> headers) = await Task.Run(() =>
                        (RecordPager.Fetch(service, fetchXml, page, pageSize, cookie), ResolveHeaders(fetchXml, entity.LogicalName, columns, sourceSchema)));
                    if (generation != _generation || IsDisposed) return;

                    headerColumns = null;
                    if (headers != null) ApplyColumnHeaders(headers);
                    AppendRecords(result.Entities, primaryId);
                    _nextPage = page + 1;
                    _pagingCookie = result.PagingCookie;
                    _moreRecords = result.MoreRecords;
                    UpdateRecordCountLabel();
                }
                while (mode == RecordLoad.AllPages && _moreRecords && !token.IsCancellationRequested);

                if (token.IsCancellationRequested && _moreRecords)
                    _logger.Write(LogLevel.Warning, "Loading stopped: " + _recordCountLabel.Text + ".");
            }
            finally
            {
                EndOperation();
            }
            if (generation == _generation && !IsDisposed) SetProgress(string.Empty);
        }

        /// <summary>Starts a new record set for the view: its columns, a fetch that returns the id, an empty grid.</summary>
        private void StartRecordSet(EntityInfo entity, ViewInfo view)
        {
            IList<ViewColumn> columns = LayoutParser.Parse(view.LayoutXml);
            string fetchXml = FetchXmlHelper.EnsureAttribute(view.FetchXml, entity.PrimaryIdAttribute);
            if (columns.Count == 0 && !string.IsNullOrEmpty(entity.PrimaryNameAttribute))
            {
                // A view without a usable layout still shows the primary name.
                columns = new List<ViewColumn> { new ViewColumn { Name = entity.PrimaryNameAttribute, Width = 300 } };
                fetchXml = FetchXmlHelper.EnsureAttribute(fetchXml, entity.PrimaryNameAttribute);
            }

            _loadedView = view;
            _loadedColumns = columns;
            _loadedFetchXml = fetchXml;
            _nextPage = 1;
            _pagingCookie = null;
            _moreRecords = false;
            _rowsWithoutId = 0;
            ResetGrid(columns);
        }

        private void ClearRecords()
        {
            _loadedView = null;
            _loadedColumns = new List<ViewColumn>();
            _loadedFetchXml = null;
            _nextPage = 1;
            _pagingCookie = null;
            _moreRecords = false;
            _rowsWithoutId = 0;
            ResetGrid(_loadedColumns);
        }

        /// <summary>The cached source metadata (created for <paramref name="service"/> when there is none yet).</summary>
        private DataverseSchemaProvider SourceSchema(IOrganizationService service) =>
            _sourceSchema ?? (_sourceSchema = new DataverseSchemaProvider(service));

        /// <summary>
        /// The display-name headers of a new record set, or null when there is nothing to resolve. Runs on
        /// a worker thread (metadata requests); never throws - headers are cosmetic, the raw names stay.
        /// </summary>
        private static IList<string> ResolveHeaders(string fetchXml, string entity, IList<ViewColumn> columns, ISchemaProvider schema)
        {
            if (columns == null || columns.Count == 0 || schema == null) return null;
            try
            {
                return ColumnHeaderResolver.Resolve(fetchXml, entity, columns, schema);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Shows the resolved header (display name) of each view column; the raw column name stays its tooltip.</summary>
        private void ApplyColumnHeaders(IList<string> headers)
        {
            for (int i = 0; i < headers.Count && i < _loadedColumns.Count; i++)
            {
                DataGridViewColumn column = _grid.Columns[ValueColumnName(i)];
                if (column != null && !string.IsNullOrWhiteSpace(headers[i])) column.HeaderText = headers[i];
            }
        }

        /// <summary>
        /// Rebinds the grid to a new DataTable: bool Copy, hidden Guid __id, then one string column per
        /// view column (header = the raw column name until <see cref="ApplyColumnHeaders"/> shows the
        /// display names; the raw name is also the header tooltip; width from the layout).
        /// </summary>
        private void ResetGrid(IList<ViewColumn> columns)
        {
            var table = new DataTable("Records") { Locale = CultureInfo.CurrentCulture };
            table.Columns.Add(CopyColumn, typeof(bool)).DefaultValue = false;
            table.Columns.Add(IdColumn, typeof(Guid));
            for (int i = 0; i < columns.Count; i++) table.Columns.Add(ValueColumnName(i), typeof(string));

            if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
            _bindingSource.DataSource = null;
            // The BindingSource re-applies its Filter/Sort to a new data source, and they name columns
            // the new table may not have: drop them (ApplyRecordFilter sets the filter again below).
            _bindingSource.Filter = null;
            _bindingSource.Sort = null;
            _grid.Columns.Clear();
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = CopyColumn,
                DataPropertyName = CopyColumn,
                HeaderText = "Copy",
                Width = 48,
                ThreeState = false,
                Frozen = true,
                SortMode = DataGridViewColumnSortMode.Automatic,
                ReadOnly = IsBusy
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = IdColumn, DataPropertyName = IdColumn, HeaderText = "Id", Visible = false, ReadOnly = true });
            for (int i = 0; i < columns.Count; i++)
            {
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = ValueColumnName(i),
                    DataPropertyName = ValueColumnName(i),
                    HeaderText = columns[i].Name,
                    ToolTipText = columns[i].Name,
                    Width = Math.Max(40, columns[i].Width),
                    ReadOnly = true,
                    SortMode = DataGridViewColumnSortMode.Automatic
                });
            }

            DataTable previous = _table;
            _table = table;
            _bindingSource.DataSource = table;
            previous?.Dispose();

            ApplyRecordFilter();
            UpdateRecordCountLabel();
        }

        private static string ValueColumnName(int index) => ValueColumnPrefix + index.ToString(CultureInfo.InvariantCulture);

        private void AppendRecords(EntityCollection records, string primaryIdAttribute)
        {
            if (_table == null || records == null || records.Entities.Count == 0)
            {
                UpdateSelectedCount();
                return;
            }

            int firstVisibleRow = _grid.FirstDisplayedScrollingRowIndex;
            IList<ViewColumn> columns = _loadedColumns;
            _bindingSource.RaiseListChangedEvents = false;
            _table.BeginLoadData();
            try
            {
                foreach (Entity record in records.Entities)
                {
                    Guid id = RecordId(record, primaryIdAttribute);
                    if (id == Guid.Empty)
                    {
                        _rowsWithoutId++;
                        continue;
                    }
                    var values = new object[2 + columns.Count];
                    values[0] = false;
                    values[1] = id;
                    for (int i = 0; i < columns.Count; i++) values[2 + i] = CellFormatter.Format(record, columns[i].Name);
                    _table.Rows.Add(values);
                }
            }
            finally
            {
                _table.EndLoadData();
                _bindingSource.RaiseListChangedEvents = true;
                _bindingSource.ResetBindings(false);
                RestoreScroll(firstVisibleRow);
            }
            UpdateSelectedCount();
        }

        /// <summary>The record's id: Entity.Id, else the primary id attribute (possibly aliased); Guid.Empty if neither.</summary>
        internal static Guid RecordId(Entity record, string primaryIdAttribute)
        {
            if (record == null) return Guid.Empty;
            if (record.Id != Guid.Empty) return record.Id;
            if (!string.IsNullOrEmpty(primaryIdAttribute) && record.Attributes.TryGetValue(primaryIdAttribute, out object value))
            {
                if (value is Guid id) return id;
                if (value is AliasedValue aliased && aliased.Value is Guid aliasedId) return aliasedId;
            }
            return Guid.Empty;
        }

        private void RestoreScroll(int firstVisibleRow)
        {
            if (firstVisibleRow <= 0 || firstVisibleRow >= _grid.RowCount) return;
            try
            {
                _grid.FirstDisplayedScrollingRowIndex = firstVisibleRow;
            }
            catch (InvalidOperationException)
            {
                // Not laid out yet: the grid simply starts at the top.
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        private void UpdateRecordCountLabel()
        {
            int count = _table?.Rows.Count ?? 0;
            string text = Plural(count, "record") + " loaded";
            if (_loadedView != null && _nextPage > 1) text += _moreRecords ? " (more available)" : " (all)";
            if (_rowsWithoutId > 0) text += $"; {_rowsWithoutId} without an id skipped";
            _recordCountLabel.Text = text;
        }

        // ---- client-side filter ----

        private void OnRecordFilterTextChanged(object sender, EventArgs e)
        {
            _recordFilterTimer.Stop();
            _recordFilterTimer.Start();
        }

        private void OnRecordFilterTimerTick(object sender, EventArgs e) => Guard("Filtering records", ApplyRecordFilter);

        /// <summary>Applies the record filter text to the grid now (normally a moment after the last keystroke).</summary>
        internal void ApplyRecordFilter()
        {
            _recordFilterTimer.Stop();
            if (_table == null) return;
            CommitGridEdit();
            string filter = BuildRowFilter(_recordFilter.Text,
                _table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Where(n => n != CopyColumn && n != IdColumn));
            try
            {
                _bindingSource.Filter = filter;
            }
            catch (InvalidExpressionException ex)
            {
                _bindingSource.RemoveFilter();
                _logger.Write(LogLevel.Warning, "The record filter could not be applied: " + ex.Message);
            }
            UpdateSelectedCount();
        }

        /// <summary>
        /// A DataView row filter matching rows where any of <paramref name="columns"/> contains
        /// <paramref name="text"/> (case-insensitive); null for no filter. The text is escaped for LIKE:
        /// ' is doubled and *, %, [ and ] are bracketed, so they match literally.
        /// </summary>
        internal static string BuildRowFilter(string text, IEnumerable<string> columns)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return null;
            string pattern = "'%" + EscapeLikeValue(value) + "%'";
            List<string> conditions = (columns ?? Enumerable.Empty<string>())
                .Select(c => "[" + EscapeColumnName(c) + "] LIKE " + pattern)
                .ToList();
            return conditions.Count == 0 ? null : string.Join(" OR ", conditions);
        }

        internal static string EscapeLikeValue(string value)
        {
            var escaped = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\'':
                        escaped.Append("''");
                        break;
                    case '*':
                    case '%':
                    case '[':
                    case ']':
                        escaped.Append('[').Append(c).Append(']');
                        break;
                    default:
                        escaped.Append(c);
                        break;
                }
            }
            return escaped.ToString();
        }

        /// <summary>Inside [...] a column name escapes ] and \ with a backslash.</summary>
        private static string EscapeColumnName(string name) => (name ?? string.Empty).Replace("\\", "\\\\").Replace("]", "\\]");

        // ---- ticking records ----

        private void OnGridCurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            // Commit a tick immediately (a checkbox cell otherwise commits only when the cell is left),
            // so the Selected count follows every click.
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == CopyColumn) UpdateSelectedCount();
        }

        private void OnGridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            // A check box cell toggles itself only when it was already in edit mode, i.e. current before
            // the click; the first click on a row only makes it current. Toggle it here in that case, so
            // one click on a tick box is always one toggle.
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != CopyColumn) return;
            if (IsBusy || _grid.IsCurrentCellInEditMode) return;
            Guard("Ticking the record", () => ToggleRow(e.RowIndex));
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            // Space ticks/unticks the current row, whichever of its cells is current. On the Copy cell
            // itself the check box handles Space (on key-up it toggles in edit mode and raises
            // CellContentClick); toggling here as well would undo the tick.
            if (e.KeyCode != Keys.Space || e.Modifiers != Keys.None || IsBusy || _grid.CurrentCell == null) return;
            if (_grid.CurrentCell.OwningColumn?.Name == CopyColumn) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            Guard("Ticking the record", () =>
            {
                CommitGridEdit();
                ToggleRow(_grid.CurrentCell.RowIndex);
            });
        }

        private void ToggleRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count || !(_grid.Rows[rowIndex].DataBoundItem is DataRowView rowView)) return;
            rowView.Row[CopyColumn] = !IsChecked(rowView.Row);
            UpdateSelectedCount();
        }

        private void OnGridDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
            _logger.Write(LogLevel.Warning, "Grid: " + (e.Exception?.Message ?? "data error"));
        }

        private void OnSelectAllClick(object sender, EventArgs e) => Guard("Select all", () => SetVisibleRowsChecked(true));

        private void OnSelectNoneClick(object sender, EventArgs e) => Guard("Select none", () => SetVisibleRowsChecked(false));

        /// <summary>Ticks or unticks the rows the filter shows; hidden rows keep their state.</summary>
        private void SetVisibleRowsChecked(bool value)
        {
            if (_table == null || IsBusy) return;
            CommitGridEdit();
            // Collect first: when the grid is sorted by Copy, changing it reorders the view.
            List<DataRow> rows = _bindingSource.List.Cast<object>().OfType<DataRowView>().Select(v => v.Row).ToList();
            int firstVisibleRow = _grid.FirstDisplayedScrollingRowIndex;
            _bindingSource.RaiseListChangedEvents = false;
            try
            {
                foreach (DataRow row in rows) row[CopyColumn] = value;
            }
            finally
            {
                _bindingSource.RaiseListChangedEvents = true;
                _bindingSource.ResetBindings(false);
                RestoreScroll(firstVisibleRow);
            }
            UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            int selected = 0;
            int visibleSelected = 0;
            if (_table != null)
            {
                foreach (DataRow row in _table.Rows)
                {
                    if (IsChecked(row)) selected++;
                }
                if (_bindingSource.List is DataView view)
                {
                    foreach (DataRowView rowView in view)
                    {
                        if (IsChecked(rowView.Row)) visibleSelected++;
                    }
                }
            }

            _selectedCount = selected;
            int hidden = selected - visibleSelected;
            _selectedLabel.Text = hidden > 0 ? $"Selected: {selected} ({hidden} hidden by the filter)" : $"Selected: {selected}";
            UpdateControlStates();
        }

        /// <summary>The ticked record ids, from the DataTable (so rows hidden by the filter count), in load order, without duplicates.</summary>
        internal IReadOnlyList<Guid> GetSelectedIds()
        {
            var ids = new List<Guid>();
            if (_table == null) return ids;
            var seen = new HashSet<Guid>();
            foreach (DataRow row in _table.Rows)
            {
                if (IsChecked(row) && row[IdColumn] is Guid id && seen.Add(id)) ids.Add(id);
            }
            return ids;
        }

        private static bool IsChecked(DataRow row) =>
            row.RowState != DataRowState.Deleted && row.RowState != DataRowState.Detached && row[CopyColumn] is bool ticked && ticked;

        private void CommitGridEdit()
        {
            if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
            _bindingSource.EndEdit();
        }

        // =====================================================================================
        // Copy
        // =====================================================================================

        private void OnSelectDestinationClick(object sender, EventArgs e)
        {
            try
            {
                // XrmToolBox shows its connection selector and hands the choice to UpdateConnection as an
                // additional organisation: the tab keeps the source as its connection (and its title).
                RaiseRequestConnectionEvent(new RequestConnectionEventArgs
                {
                    ActionName = DestinationActionName,
                    Parameter = DestinationParameter,
                    Control = this
                });
            }
            catch (Exception ex)
            {
                ReportError("Selecting the destination failed", ex);
            }
        }

        private void OnCopyClick(object sender, EventArgs e) => RequestSource("Copy", CopySelectedRecords);

        /// <summary>Copy selected records: runs through <see cref="RequestSource"/>.</summary>
        private void CopySelectedRecords() => RunGuarded("Copy", CopySelectedAsync);

        private async Task CopySelectedAsync()
        {
            if (IsBusy) return;
            CommitGridEdit();
            IOrganizationService source = Service;
            IOrganizationService destination = _destinationService;
            EntityInfo entity = _currentEntity;
            IReadOnlyList<Guid> ids = GetSelectedIds();

            string problem = source == null ? "Connect to the source environment first (XrmToolBox connection bar)."
                : destination == null ? "Select the destination environment first (toolbar: Select destination environment...)."
                : entity == null ? "Select an entity first."
                : ids.Count == 0 ? "Tick at least one record in the Copy column first."
                : null;
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                return;
            }

            CopyOptions options = BuildOptions();
            if (options.NeverCreateEntities.Contains(entity.LogicalName))
            {
                _logger.Write(LogLevel.Warning, $"Copy refused: {entity.LogicalName} is in the never-create list.");
                ShowMessage(
                    $"{entity.LogicalName} is in the never-create list ({NeverCreateList(options)}).\n\n" +
                    "Records of these entities are never created by this tool, so they cannot be copied. " +
                    "The list is the NeverCreateEntities setting.",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                return;
            }
            if (entity.IsVirtual)
            {
                _logger.Write(LogLevel.Warning, $"Copy refused: {entity.LogicalName} is a virtual table.");
                ShowMessage(
                    CopyEngine.VirtualTableRefusal(entity.LogicalName) + "\n\n" +
                    "Records that point at its rows can still be copied: those lookups are kept when the same row exists in the destination.",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                return;
            }

            string sourceName = DescribeConnection(ConnectionDetail, source);
            string destinationName = DescribeConnection(_destinationDetail, destination);
            if (IsSameOrganization())
            {
                DialogResult answer = ShowMessage(
                    "The destination appears to be the SAME organisation as the source:\n\n" +
                    "Source: " + sourceName + "\nDestination: " + destinationName + "\n\n" +
                    "Copying records onto themselves overwrites them with their own values (and runs their plugins). Continue anyway?",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    _logger.Write(LogLevel.Info, "Copy not started: same organisation not confirmed.");
                    return;
                }
            }

            if (_destinationSchema == null) _destinationSchema = new DataverseSchemaProvider(destination);
            DefaultChildRelationshipSelector selector = null;
            if (options.CopyChildren)
            {
                // The relationships ticked in the picker for this source, else each entity's main-form
                // subgrids - read once per run (a fresh FormSubgridService).
                var subgrids = new FormSubgridService();
                selector = new DefaultChildRelationshipSelector(SourceSchema(source), name => subgrids.GetMainFormSubgridRelationships(source, name),
                    _settings.GetRelationshipSelections(SourceOrganizationKey()), options.NeverCreateEntities);
                options.ChildRelationshipSelector = selector;
            }
            var engine = new CopyEngine(source, destination, SourceSchema(source), _destinationSchema, options, _logger);

            WriteRunLine(LogLevel.Info, new string('=', 60));
            WriteRunLine(LogLevel.Info, $"Copying {ids.Count} {entity.LogicalName} {(ids.Count == 1 ? "record" : "records")} from {sourceName} to {destinationName}");
            WriteRunLine(LogLevel.Info,
                $"Options: dry run {OnOff(options.DryRun)}, preserve created on {OnOff(options.PreserveCreatedOn)}, " +
                $"bypass custom plugins {OnOff(options.BypassCustomPluginExecution)}, " +
                $"copy N:1 relationships (lookups) {OnOff(options.CopyLookups)}, copy 1:N relationships (subgrids) {OnOff(options.CopyChildren)}; " +
                $"never created: {NeverCreateList(options)}");

            int run = ++_copyRun;
            _lastProgress = null;
            var progress = new Progress<CopyProgress>(p => OnCopyProgress(run, p));
            CancellationToken token = BeginOperation($"0 / {ids.Count}");
            CopySummary summary;
            Exception failure = null;
            try
            {
                if (selector != null)
                {
                    // The effective relationships of the selected entity (metadata and forms: off the UI thread).
                    IList<string> relationshipLines = await Task.Run(() => DescribeChildRelationships(selector, entity.LogicalName));
                    if (IsDisposed) return;
                    foreach (string line in relationshipLines) WriteRunLine(LogLevel.Info, line);
                }
                if (options.DryRun) WriteRunLine(LogLevel.Warning, "DRY RUN: nothing will be written to the destination.");
                summary = await Task.Run(() => engine.Copy(entity.LogicalName, ids, progress, token));
            }
            catch (OperationCanceledException)
            {
                summary = engine.LastSummary;
                if (!IsDisposed) WriteRunLine(LogLevel.Warning, "Cancelled by user");
            }
            catch (Exception ex)
            {
                // A setup failure (e.g. the entity is missing in the destination); the engine has logged it.
                summary = engine.LastSummary;
                failure = ex;
            }
            finally
            {
                _copyRun++;   // late progress reports of this run are ignored from now on
                EndOperation();
            }

            if (IsDisposed) return;
            if (failure != null) _logger.Log(LogLevel.Error, "Copy stopped: " + CopyEngine.ErrorText(failure));
            if (summary != null)
            {
                foreach ((LogLevel level, string text) in BuildSummaryLines(summary)) WriteRunLine(level, text);
            }
            SetProgress(FinalProgressText(summary, failure));
            if (failure != null) ShowMessage(failure.Message, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
        }

        private void OnCopyProgress(int run, CopyProgress progress)
        {
            if (run != _copyRun || IsDisposed) return;
            _lastProgress = progress;
            SetProgress(FormatProgress(progress));
        }

        private string FinalProgressText(CopySummary summary, Exception failure)
        {
            if (summary == null) return failure != null ? "Copy failed" : string.Empty;
            string outcome = failure != null ? "Stopped"
                : summary.Cancelled ? "Cancelled"
                : summary.DryRun ? "Dry run done"
                : "Done";
            int reached = failure == null && !summary.Cancelled ? summary.SelectedTotal : _lastProgress?.SelectedIndex ?? 0;
            return outcome + ": " + FormatProgress(new CopyProgress
            {
                SelectedIndex = reached,
                SelectedTotal = summary.SelectedTotal,
                Created = summary.Created,
                Updated = summary.Updated,
                SkippedExisting = summary.SkippedExisting,
                Failed = summary.Failed,
                ChildRecordsFound = summary.ChildRecordsFound
            });
        }

        /// <summary>
        /// The progress label text, e.g. "3 / 25 - created 41, failed 1"; updated, skipped (existing
        /// related records) and child records found are included when not zero.
        /// </summary>
        internal static string FormatProgress(CopyProgress progress)
        {
            var text = new StringBuilder();
            text.Append(progress.SelectedIndex.ToString(CultureInfo.CurrentCulture))
                .Append(" / ").Append(progress.SelectedTotal.ToString(CultureInfo.CurrentCulture))
                .Append(" - created ").Append(progress.Created.ToString(CultureInfo.CurrentCulture));
            if (progress.Updated > 0) text.Append(", updated ").Append(progress.Updated.ToString(CultureInfo.CurrentCulture));
            if (progress.SkippedExisting > 0) text.Append(", skipped ").Append(progress.SkippedExisting.ToString(CultureInfo.CurrentCulture));
            if (progress.ChildRecordsFound > 0) text.Append(", children found ").Append(progress.ChildRecordsFound.ToString(CultureInfo.CurrentCulture));
            text.Append(", failed ").Append(progress.Failed.ToString(CultureInfo.CurrentCulture));
            return text.ToString();
        }

        /// <summary>The summary block logged after a run: counts, elapsed time, then one line per error.</summary>
        internal static IList<(LogLevel Level, string Text)> BuildSummaryLines(CopySummary summary)
        {
            string qualifier = summary.DryRun ? " (dry run - nothing was written)" : string.Empty;
            if (summary.Cancelled) qualifier += " (cancelled)";
            LogLevel headline = summary.Cancelled || summary.Failed > 0 || summary.Errors.Count > 0 ? LogLevel.Warning : LogLevel.Success;

            var lines = new List<(LogLevel Level, string Text)>
            {
                (headline, $"---- Summary{qualifier}: {Plural(summary.SelectedTotal, "selected record")} ----"),
                (LogLevel.Info, SummaryRow("Created", summary.Created)),
                (LogLevel.Info, SummaryRow("Updated", summary.Updated)),
                (LogLevel.Info, SummaryRow("Skipped (existed)", summary.SkippedExisting)),
                (summary.Failed > 0 ? LogLevel.Error : LogLevel.Info, SummaryRow("Failed", summary.Failed)),
                (summary.LookupsBlanked > 0 ? LogLevel.Warning : LogLevel.Info, SummaryRow("Lookups blanked", summary.LookupsBlanked)),
                (LogLevel.Info, SummaryRow("Lookups backfilled", summary.LookupsBackfilled)),
                (LogLevel.Info, SummaryRow("Child records found", summary.ChildRecordsFound)),
                (LogLevel.Info, SummaryRow("State changes", summary.StateChanges)),
                (LogLevel.Info, "  " + "Elapsed".PadRight(20) + FormatElapsed(summary.Elapsed))
            };
            if (summary.Errors.Count > 0)
            {
                lines.Add((LogLevel.Error, $"  Errors ({summary.Errors.Count}):"));
                foreach (string error in summary.Errors) lines.Add((LogLevel.Error, "    " + error));
            }
            return lines;
        }

        private static string SummaryRow(string label, int value) => "  " + label.PadRight(20) + value.ToString(CultureInfo.CurrentCulture);

        internal static string FormatElapsed(TimeSpan elapsed) =>
            string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);

        private static string OnOff(bool value) => value ? "on" : "off";

        // =====================================================================================
        // Log panel and XrmToolBox's own log
        // =====================================================================================

        /// <summary>A copy header/summary line: on screen, and in XrmToolBox's log as Info.</summary>
        private void WriteRunLine(LogLevel level, string text)
        {
            _logger.Write(level, text);
            MirrorToXrmToolBoxLog(LogLevel.Info, text);
        }

        /// <summary>The sink for every <see cref="UiLogger.Log"/> line (engine and UI errors): errors go to XrmToolBox's log.</summary>
        private void MirrorEngineLine(LogLevel level, string message)
        {
            if (level == LogLevel.Error) MirrorToXrmToolBoxLog(LogLevel.Error, message);
        }

        private void MirrorToXrmToolBoxLog(LogLevel level, string message)
        {
            if (!_mirrorToXrmToolBoxLog) return;
            try
            {
                // "{0}" + argument: the host formats the message, so braces in record names stay literal.
                string text = (message ?? string.Empty).Trim();
                if (level == LogLevel.Error) LogError("{0}", text);
                else LogInfo("{0}", text);
            }
            catch (Exception)
            {
                // XrmToolBox's log file is best-effort; it must never break a copy.
            }
        }

        private void OnCopyLogClick(object sender, EventArgs e)
        {
            try
            {
                string text = LogText();
                if (text.Length > 0) Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                ReportError("Copying the log failed", ex);
            }
        }

        private void OnSaveLogClick(object sender, EventArgs e)
        {
            try
            {
                using (var dialog = new SaveFileDialog
                {
                    Title = "Save log",
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    DefaultExt = "txt",
                    AddExtension = true,
                    FileName = "DataCopier-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt"
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(dialog.FileName, LogText(), Encoding.UTF8);
                    _logger.Write(LogLevel.Info, "Log saved to " + dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                ReportError("Saving the log failed", ex);
            }
        }

        private void OnClearLogClick(object sender, EventArgs e) => Guard("Clearing the log", _logger.Clear);

        /// <summary>The log text with Windows line breaks (the RichTextBox uses \n).</summary>
        private string LogText() => _log.Text.TrimEnd('\n').Replace("\n", Environment.NewLine);

        private void OnCloseClick(object sender, EventArgs e) => Guard("Closing the tool", CloseTool);

        // =====================================================================================
        // Small helpers
        // =====================================================================================

        private static bool ContainsText(string text, string value) =>
            text != null && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        internal static string Plural(int count, string singular, string plural = null) =>
            count.ToString(CultureInfo.CurrentCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");

        private DialogResult ShowMessageBox(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) =>
            MessageBox.Show(this, text, DialogTitle, buttons, icon, defaultButton);
    }
}
