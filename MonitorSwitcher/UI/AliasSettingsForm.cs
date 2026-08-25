#nullable enable
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using WorkMonitorSwitcher.Model;
using WorkMonitorSwitcher.Services;
using WorkMonitorSwitcher.UI;

namespace WorkMonitorSwitcher
{
    /// <summary>
    /// Settings dialog: shows StableKey (short), RegistryKey and editable Alias.
    /// Also includes a Dark Mode checkbox that feeds back to Form1.
    /// </summary>
    public sealed class AliasSettingsForm : Form
    {
        private readonly DataGridView _grid = new()
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        private readonly Button _ok = new ThemedButton { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Tone = ThemedButtonTone.Primary };
        private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        private readonly Button _remove = new ThemedButton { Text = "Remove Selected", AutoSize = true, Tone = ThemedButtonTone.Danger };
        private readonly Button _openRegistry = new() { Text = "Open Registry", AutoSize = true };
        private readonly Button _updateApp = new() { Text = "Check for Updates", AutoSize = true };
        private readonly Label _updateStatus = new()
        {
            AutoSize = true,
            Visible = false,
            UseMnemonic = false,
            AccessibleName = "Update status"
        };
        private readonly Button _showDiagnostics = new() { Text = "Diagnostics", AutoSize = true };
        private readonly Button _clearDiagnostics = new ThemedButton { Text = "Clear Diagnostics", AutoSize = true, Tone = ThemedButtonTone.Danger };
        private readonly CheckBox _chkDark = new() { Text = "Dark mode", AutoSize = true };
        private readonly CheckBox _chkTopMost = new() { Text = "Always on top", AutoSize = true };
        private readonly CheckBox _chkTray = new() { Text = "Minimize to tray", AutoSize = true };
        private readonly CheckBox _chkStartup = new() { Text = "Start with Windows", AutoSize = true };
        private readonly CheckBox _chkConfirmDisable = new() { Text = "Confirm before disabling", AutoSize = true };
        private readonly CheckBox _chkRestoreLayoutOnStartup = new() { Text = "Apply monitor profile on app start", AutoSize = true };
        private readonly Button _layoutProfileButton = new() { Text = "Default", AutoSize = true, MinimumSize = new Size(160, 30), TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button _deleteProfile = new ThemedButton { Text = "Delete Profile", AutoSize = true, MinimumSize = new Size(106, 30), Tone = ThemedButtonTone.Danger };
        private readonly TextBox _details = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.None,
            WordWrap = true,
            TabStop = false
        };
        private readonly BindingList<AliasViewRow> _rows;
        private string _diagnosticsText;
        private readonly Func<string>? _readDiagnostics;
        private readonly Func<string?>? _clearDiagnosticsLog;
        private readonly List<string> _layoutProfileNames;
        private readonly string _initialSelectedLayoutProfile;
        private string _selectedLayoutProfile;
        private ContextMenuStrip? _layoutProfileMenu;
        private readonly ToolTip _toolTip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 8000 };
        private readonly AppUpdateService _updateService;
        private readonly bool _ownsUpdateService;
        private readonly CheckState _initialStartupCheckState;
        private CancellationTokenSource? _updateCancellation;
        private bool _updateInProgress;
        private static readonly TimeSpan UpdateOperationTimeout = TimeSpan.FromMinutes(5);

        public Dictionary<string, string> UpdatedMappings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RemovedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool? DarkModeResult { get; private set; }
        public string? PreferredPrimaryKey { get; private set; }
        public string? FallbackPrimaryKey { get; private set; }
        public bool? AlwaysOnTopResult { get; private set; }
        public bool? MinimizeToTrayResult { get; private set; }
        public bool? StartWithWindowsResult { get; private set; }
        public bool? ConfirmBeforeDisableResult { get; private set; }
        public bool? RestoreLayoutOnStartupResult { get; private set; }
        public string? SelectedLayoutProfileResult { get; private set; }
        public HashSet<string> RemovedLayoutProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal AliasSettingsForm(
            List<AliasViewRow> current,
            bool darkMode,
            bool alwaysOnTop,
            bool minimizeToTray,
            StartupRegistrationState startupRegistrationState,
            bool confirmBeforeDisable,
            bool restoreLayoutOnStartup,
            List<string> layoutProfiles,
            string selectedLayoutProfile,
            string diagnosticsText,
            Func<string>? readDiagnostics = null,
            Func<string?>? clearDiagnosticsLog = null,
            AppUpdateService? updateService = null,
            Form? sizingOwner = null)

        {
            _rows = new BindingList<AliasViewRow>(current);
            _diagnosticsText = string.IsNullOrWhiteSpace(diagnosticsText)
                ? "No diagnostic events have been recorded yet."
                : diagnosticsText;
            _readDiagnostics = readDiagnostics;
            _clearDiagnosticsLog = clearDiagnosticsLog;
            _updateService = updateService ?? AppUpdateService.CreateDefault();
            _ownsUpdateService = updateService == null;
            _layoutProfileNames = layoutProfiles
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_layoutProfileNames.Count == 0)
                _layoutProfileNames.Add("Default");
            _selectedLayoutProfile = string.IsNullOrWhiteSpace(selectedLayoutProfile) ? "Default" : selectedLayoutProfile.Trim();
            _initialSelectedLayoutProfile = _selectedLayoutProfile;
            if (!_layoutProfileNames.Any(p => p.Equals(_selectedLayoutProfile, StringComparison.OrdinalIgnoreCase)))
                _layoutProfileNames.Add(_selectedLayoutProfile);

            Text = "Monitor Switcher Settings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowIcon = true;
            Icon = AppIcon.Current;
            MinimumSize = new Size(760, 480);
            Size = new Size(900, 540);
            _chkDark.Checked = darkMode;
            _chkTopMost.Checked = alwaysOnTop;
            _chkTray.Checked = minimizeToTray;
            _chkStartup.ThreeState = true;
            _initialStartupCheckState = startupRegistrationState switch
            {
                StartupRegistrationState.CurrentExecutable => CheckState.Checked,
                StartupRegistrationState.OtherExecutable => CheckState.Indeterminate,
                _ => CheckState.Unchecked
            };
            _chkStartup.CheckState = _initialStartupCheckState;
            UpdateStartupControlDescription();
            _chkStartup.CheckStateChanged += (_, __) => UpdateStartupControlDescription();
            _chkConfirmDisable.Checked = confirmBeforeDisable;
            _chkRestoreLayoutOnStartup.Checked = restoreLayoutOnStartup;
            SetSelectedLayoutProfile(_selectedLayoutProfile);
            _grid.RowHeadersVisible = false;
            _grid.AllowUserToOrderColumns = true;
            _grid.MultiSelect = true;
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            _grid.BorderStyle = BorderStyle.None;
            _grid.RowTemplate.Height = 28;
            _grid.MinimumSize = Size.Empty;
            _details.MinimumSize = new Size(0, 120);

            // Columns
            var shortKeyCol = new DataGridViewTextBoxColumn
            {
                HeaderText = "Stable Key",
                DataPropertyName = nameof(AliasViewRow.ShortKey),
                ReadOnly = true,
                FillWeight = 28,
                MinimumWidth = 150
            };
            var regCol = new DataGridViewTextBoxColumn
            {
                HeaderText = "Registry Key",
                DataPropertyName = nameof(AliasViewRow.RegistryKeyShort),
                ReadOnly = true,
                FillWeight = 44,
                MinimumWidth = 220
            };
            var aliasCol = new DataGridViewTextBoxColumn
            {
                HeaderText = "Alias",
                DataPropertyName = nameof(AliasViewRow.Alias),
                ReadOnly = false,
                FillWeight = 28,
                MinimumWidth = 160
            };
            var fallbackPrimaryCol = new DataGridViewCheckBoxColumn
            {
                HeaderText = "Primary fallback",
                DataPropertyName = nameof(AliasViewRow.IsFallbackPrimary),
                ReadOnly = false,
                FillWeight = 14,
                MinimumWidth = 110
            };
            fallbackPrimaryCol.ThreeState = false;

            NormalisePrimaryFallbackRows();

            _grid.Columns.Add(shortKeyCol);
            _grid.Columns.Add(regCol);
            _grid.Columns.Add(aliasCol);
            _grid.Columns.Add(fallbackPrimaryCol);
            _grid.DataSource = _rows;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellBeginEdit += (_, e) =>
            {
                if (IsFallbackPrimaryCell(e.RowIndex, e.ColumnIndex) &&
                    _rows[e.RowIndex].IsPreferredPrimary)
                {
                    e.Cancel = true;
                }
            };
            _grid.CellPainting += (_, e) =>
            {
                if (IsDisabledFallbackPrimaryCell(e.RowIndex, e.ColumnIndex))
                    PaintDisabledFallbackCheckbox(e);
            };

            // Tooltip with full StableKey when hovering the first column
            _grid.CellFormatting += (_, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                {
                    var row = _rows[e.RowIndex];
                    _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText = row.StableKey;
                }
                else if (e.RowIndex >= 0 &&
                         e.ColumnIndex >= 0 &&
                         _grid.Columns[e.ColumnIndex].DataPropertyName == nameof(AliasViewRow.RegistryKeyShort))
                {
                    var row = _rows[e.RowIndex];
                    _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText = row.RegistryKey;
                }
            };

            // Ctrl+C copies current cell text
            _grid.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && _grid.CurrentCell != null)
                {
                    var text = _grid.Columns[_grid.CurrentCell.ColumnIndex].DataPropertyName == nameof(AliasViewRow.RegistryKeyShort)
                        ? _rows[_grid.CurrentCell.RowIndex].RegistryKey
                        : Convert.ToString(_grid.CurrentCell.Value) ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                        Clipboard.SetText(text);
                    e.Handled = true;
                }
            };

            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0) return;

                if (_grid.Columns[e.ColumnIndex] is not DataGridViewCheckBoxColumn)
                    return;

                var propertyName = _grid.Columns[e.ColumnIndex].DataPropertyName;
                if (propertyName == nameof(AliasViewRow.IsPreferredPrimary))
                {
                    KeepSingleCheckedRow(
                        e.RowIndex,
                        row => row.IsPreferredPrimary,
                        (row, value) => row.IsPreferredPrimary = value);

                    if (_rows[e.RowIndex].IsPreferredPrimary)
                        _rows[e.RowIndex].IsFallbackPrimary = false;
                }
                else if (propertyName == nameof(AliasViewRow.IsFallbackPrimary))
                {
                    if (_rows[e.RowIndex].IsPreferredPrimary)
                    {
                        _rows[e.RowIndex].IsFallbackPrimary = false;
                    }

                    KeepSingleCheckedRow(
                        e.RowIndex,
                        row => row.IsFallbackPrimary,
                        (row, value) => row.IsFallbackPrimary = value);
                }

                NormalisePrimaryFallbackRows();
                UpdatePrimaryFallbackCellStates();
                UpdateDetailsPanel();
            };
            _grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].DataPropertyName == nameof(AliasViewRow.RegistryKeyShort))
                    OpenRegistryForRow(e.RowIndex);
            };
            _grid.SelectionChanged += (_, __) => UpdateDetailsPanel();
            _remove.Click += (_, __) => RemoveSelectedRows();
            _openRegistry.Click += (_, __) => OpenRegistryForSelectedRow();
            _layoutProfileButton.Click += (_, __) => ShowLayoutProfileMenu();
            _deleteProfile.Click += (_, __) => DeleteSelectedProfile();
            _updateApp.Click += async (_, __) =>
            {
                if (_updateInProgress)
                {
                    _updateStatus.Text = "Cancelling update check…";
                    _updateCancellation?.Cancel();
                    return;
                }

                await CheckForUpdatesAsync();
            };
            _showDiagnostics.Click += (_, __) => ShowDiagnosticsDialog();
            _clearDiagnostics.Click += (_, __) => ClearDiagnostics();
            _toolTip.SetToolTip(_chkDark, "Use the dark color theme.");
            _toolTip.SetToolTip(_chkTopMost, "Keep the main switcher window above other windows.");
            _toolTip.SetToolTip(_chkTray, "Close to the notification area instead of exiting.");
            _toolTip.SetToolTip(_chkStartup, "Start Monitor Switcher when you sign in to Windows.");
            _toolTip.SetToolTip(_chkConfirmDisable, "Ask before disabling a monitor directly or by applying a layout profile.");
            _toolTip.SetToolTip(_chkRestoreLayoutOnStartup, "Apply only the selected profile's enabled monitor set when Monitor Switcher starts. Windows keeps position and orientation.");
            _toolTip.SetToolTip(_layoutProfileButton, "Current layout profile used by the main window.");
            _toolTip.SetToolTip(_deleteProfile, "Delete the selected saved layout profile. Default cannot be deleted.");
            _toolTip.SetToolTip(_remove, "Remove selected saved monitor entries.");
            _toolTip.SetToolTip(_openRegistry, "Open Registry Editor at the selected monitor key.");
            _toolTip.SetToolTip(_updateApp, "Check for, verify and extract the latest stable GitHub release.");
            _toolTip.SetToolTip(_showDiagnostics, "Show recent monitor action and layout profile events.");
            _toolTip.SetToolTip(_clearDiagnostics, "Permanently clear the saved diagnostics log and its temporary exported copy.");

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12, 8, 12, 10),
                Height = 54
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var commitActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true
            };

            foreach (var button in new[] { _openRegistry, _updateApp, _showDiagnostics, _clearDiagnostics, _remove, _cancel, _ok })
                button.Margin = new Padding(0, 0, 8, 0);

            commitActions.Controls.Add(_ok);
            commitActions.Controls.Add(_cancel);
            bottom.Controls.Add(commitActions, 1, 0);

            var generalPage = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), AutoScroll = true };
            var monitorsPage = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), AutoScroll = true };
            var profilesPage = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), AutoScroll = true };

            var generalLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 258,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(4)
            };
            generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
            generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

            var appearanceGroup = new GroupBox
            {
                Text = "Appearance and window",
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 20, 14, 12),
                Margin = new Padding(0, 0, 7, 10)
            };
            var appearanceOptions = CreateVerticalOptionsPanel(_chkDark, _chkTopMost, _chkTray);
            _chkDark.TabIndex = 0;
            _chkTopMost.TabIndex = 1;
            _chkTray.TabIndex = 2;
            appearanceGroup.Controls.Add(appearanceOptions);

            var behaviourGroup = new GroupBox
            {
                Text = "Behaviour",
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 20, 14, 12),
                Margin = new Padding(7, 0, 0, 10)
            };
            var behaviourOptions = CreateVerticalOptionsPanel(
                _chkStartup,
                _chkConfirmDisable,
                _chkRestoreLayoutOnStartup);
            _chkStartup.TabIndex = 0;
            _chkConfirmDisable.TabIndex = 1;
            _chkRestoreLayoutOnStartup.TabIndex = 2;
            behaviourGroup.Controls.Add(behaviourOptions);

            var maintenanceGroup = new GroupBox
            {
                Text = "Application",
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 20, 14, 10),
                Margin = new Padding(0)
            };
            var maintenanceContent = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            maintenanceContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            maintenanceContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            maintenanceContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var maintenanceActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _updateApp.TabIndex = 0;
            _showDiagnostics.TabIndex = 1;
            _clearDiagnostics.TabIndex = 2;
            maintenanceActions.Controls.Add(_updateApp);
            maintenanceActions.Controls.Add(_showDiagnostics);
            maintenanceActions.Controls.Add(_clearDiagnostics);
            _updateStatus.Dock = DockStyle.Fill;
            _updateStatus.AutoEllipsis = true;
            _updateStatus.Margin = new Padding(0, 6, 0, 0);
            maintenanceContent.Controls.Add(maintenanceActions, 0, 0);
            maintenanceContent.Controls.Add(_updateStatus, 0, 1);
            maintenanceGroup.Controls.Add(maintenanceContent);

            generalLayout.Controls.Add(appearanceGroup, 0, 0);
            generalLayout.Controls.Add(behaviourGroup, 1, 0);
            generalLayout.Controls.Add(maintenanceGroup, 0, 1);
            generalLayout.SetColumnSpan(maintenanceGroup, 2);
            generalPage.Controls.Add(generalLayout);

            var monitorLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                RowCount = 2
            };
            int visibleMonitorRows = CalculateVisibleMonitorRows(_rows.Count);
            int monitorGridHeight = _grid.ColumnHeadersHeight + (visibleMonitorRows * _grid.RowTemplate.Height) + 4;
            int monitorDetailsHeight = 170;
            monitorLayout.Height = monitorGridHeight + monitorDetailsHeight;
            monitorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var monitorGridRow = new RowStyle(SizeType.Absolute, monitorGridHeight);
            var monitorDetailsRow = new RowStyle(SizeType.Absolute, monitorDetailsHeight);
            monitorLayout.RowStyles.Add(monitorGridRow);
            monitorLayout.RowStyles.Add(monitorDetailsRow);
            monitorLayout.Controls.Add(_grid, 0, 0);

            var detailsGroup = new GroupBox
            {
                Text = "Selected monitor information",
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 20, 10, 10),
                Margin = new Padding(0, 12, 0, 4)
            };
            detailsGroup.Controls.Add(_details);
            monitorLayout.Controls.Add(detailsGroup, 0, 1);

            var monitorActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true
            };
            monitorActions.Controls.Add(_openRegistry);
            monitorActions.Controls.Add(_remove);
            _grid.TabIndex = 0;
            _openRegistry.TabIndex = 0;
            _remove.TabIndex = 1;
            monitorsPage.Controls.Add(monitorLayout);
            bottom.Controls.Add(monitorActions, 0, 0);

            var profileGroup = new GroupBox
            {
                Text = "Saved monitor profiles",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 22, 14, 14)
            };
            var profileDescription = new Label
            {
                Text = "Choose the profile used by the main window. Changes are applied when you save these settings.",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12)
            };
            var profileActions = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            var profileLabel = new Label
            {
                Text = "Selected profile",
                AutoSize = true,
                Margin = new Padding(0, 7, 8, 0)
            };
            _layoutProfileButton.Margin = new Padding(0, 0, 8, 0);
            _deleteProfile.Margin = new Padding(0, 0, 8, 0);
            _layoutProfileButton.TabIndex = 0;
            _deleteProfile.TabIndex = 1;
            profileActions.Controls.Add(profileLabel);
            profileActions.Controls.Add(_layoutProfileButton);
            profileActions.Controls.Add(_deleteProfile);
            var profileContent = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            profileContent.Controls.Add(profileDescription);
            profileContent.Controls.Add(profileActions);
            profileGroup.Controls.Add(profileContent);
            profilesPage.Controls.Add(profileGroup);

            bool updatingMonitorLayout = false;

            void UpdateMonitorContentSize(bool resizeWindow)
            {
                if (updatingMonitorLayout)
                    return;

                updatingMonitorLayout = true;
                try
                {
                    int rowCount = CalculateVisibleMonitorRows(_rows.Count);
                    int gridHeight = _grid.ColumnHeadersHeight + (rowCount * _grid.RowTemplate.Height) + 4;
                    int textWidth = Math.Max(240, detailsGroup.ClientSize.Width - detailsGroup.Padding.Horizontal - 8);
                    int textHeight = CalculateDetailsTextHeight(_details.Text, _details.Font, textWidth);
                    int detailsHeight = Math.Max(
                        128,
                        textHeight + detailsGroup.Padding.Vertical + detailsGroup.Margin.Vertical + 8);

                    monitorGridRow.Height = gridHeight;
                    monitorDetailsRow.Height = detailsHeight;
                    monitorLayout.Height = gridHeight + detailsHeight;
                    _grid.ScrollBars = _rows.Count > 8
                        ? ScrollBars.Vertical
                        : ScrollBars.None;

                    if (resizeWindow && monitorsPage.Visible)
                    {
                        int desiredClientHeight = 46 + bottom.Height + monitorsPage.Padding.Vertical + monitorLayout.Height;
                        int maxClientHeight = Math.Max(300, Screen.FromControl(this).WorkingArea.Height - 80);
                        SetResponsiveClientHeight(Math.Min(desiredClientHeight, maxClientHeight));
                    }
                }
                finally
                {
                    updatingMonitorLayout = false;
                }
            }

            _details.TextChanged += (_, __) => UpdateMonitorContentSize(resizeWindow: true);
            detailsGroup.ClientSizeChanged += (_, __) => UpdateMonitorContentSize(resizeWindow: false);
            _rows.ListChanged += (_, __) => UpdateMonitorContentSize(resizeWindow: true);

            var pageHost = new Panel { Dock = DockStyle.Fill };
            pageHost.Controls.Add(profilesPage);
            pageHost.Controls.Add(monitorsPage);
            pageHost.Controls.Add(generalPage);

            var generalButton = new ThemedButton { Text = "General", Size = new Size(92, 32), Tone = ThemedButtonTone.Primary, TabIndex = 0 };
            var monitorsButton = new ThemedButton { Text = "Monitors", Size = new Size(92, 32), TabIndex = 1 };
            var profilesButton = new ThemedButton { Text = "Profiles", Size = new Size(92, 32), TabIndex = 2 };
            var navigation = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(14, 7, 14, 7),
                AccessibleName = "Settings sections",
                AccessibleRole = AccessibleRole.PageTabList
            };
            foreach (var button in new[] { generalButton, monitorsButton, profilesButton })
            {
                button.Margin = new Padding(0, 0, 8, 0);
                navigation.Controls.Add(button);
            }

            void ShowPage(Panel page, ThemedButton selectedButton, bool resizeWindow = true)
            {
                generalPage.Visible = ReferenceEquals(page, generalPage);
                monitorsPage.Visible = ReferenceEquals(page, monitorsPage);
                profilesPage.Visible = ReferenceEquals(page, profilesPage);
                monitorActions.Visible = ReferenceEquals(page, monitorsPage);
                page.BringToFront();

                var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();
                foreach (var button in new[] { generalButton, monitorsButton, profilesButton })
                {
                    bool isSelected = ReferenceEquals(button, selectedButton);
                    button.AccessibilitySelected = isSelected;
                    button.Tone = ReferenceEquals(button, selectedButton)
                        ? ThemedButtonTone.Primary
                        : ThemedButtonTone.Neutral;
                    button.AccessibleRole = AccessibleRole.PageTab;
                    button.AccessibleName = isSelected
                        ? $"{button.Text} settings tab, selected"
                        : $"{button.Text} settings tab";
                    button.AccessibleDescription = isSelected
                        ? "Selected settings section."
                        : "Select this settings section.";
                    Themer.ApplyButtonStyle(button, palette);
                }

                if (resizeWindow)
                {
                    if (ReferenceEquals(page, monitorsPage))
                        UpdateMonitorContentSize(resizeWindow: false);

                    int contentHeight = ReferenceEquals(page, generalPage)
                        ? generalLayout.Height
                        : ReferenceEquals(page, monitorsPage)
                            ? monitorLayout.Height
                            : Math.Max(profileGroup.Height, profileGroup.PreferredSize.Height);
                    int desiredClientHeight = 46 + bottom.Height + page.Padding.Vertical + contentHeight;
                    int maxClientHeight = Math.Max(300, Screen.FromControl(this).WorkingArea.Height - 80);
                    SetResponsiveClientHeight(Math.Min(desiredClientHeight, maxClientHeight));
                }
            }

            generalButton.Click += (_, __) => ShowPage(generalPage, generalButton);
            monitorsButton.Click += (_, __) => ShowPage(monitorsPage, monitorsButton);
            profilesButton.Click += (_, __) => ShowPage(profilesPage, profilesButton);
            var navigationButtons = new[] { generalButton, monitorsButton, profilesButton };
            for (int index = 0; index < navigationButtons.Length; index++)
            {
                int buttonIndex = index;
                navigationButtons[index].KeyDown += (_, e) =>
                {
                    int targetIndex = e.KeyCode switch
                    {
                        Keys.Left => (buttonIndex + navigationButtons.Length - 1) % navigationButtons.Length,
                        Keys.Right => (buttonIndex + 1) % navigationButtons.Length,
                        Keys.Home => 0,
                        Keys.End => navigationButtons.Length - 1,
                        _ => -1
                    };
                    if (targetIndex < 0)
                        return;

                    var targetButton = navigationButtons[targetIndex];
                    var targetPage = targetIndex == 0 ? generalPage : targetIndex == 1 ? monitorsPage : profilesPage;
                    ShowPage(targetPage, targetButton);
                    targetButton.Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                };
            }

            var navigationSeparator = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 1,
                BackColor = (darkMode ? ThemePalette.Dark() : ThemePalette.Light()).Border
            };
            var settingsShell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            settingsShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            settingsShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            settingsShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            settingsShell.Controls.Add(navigation, 0, 0);
            settingsShell.Controls.Add(navigationSeparator, 0, 1);
            settingsShell.Controls.Add(pageHost, 0, 2);

            Controls.Add(settingsShell);
            Controls.Add(bottom);
            FitInitialSizeToContent(monitorActions, commitActions, sizingOwner);
            ShowPage(generalPage, generalButton);
            UpdatePrimaryFallbackCellStates();

            AcceptButton = _ok;
            CancelButton = _cancel;
            _cancel.TabIndex = 0;
            _ok.TabIndex = 1;

            // Initial theme
            ApplyDialogTheme(darkMode);

            _chkDark.CheckedChanged += (_, __) =>
            {
                ApplyDialogTheme(_chkDark.Checked);
                navigationSeparator.BackColor = (_chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light()).Border;
            };

            _ok.Click += (_, __) =>
            {
                _grid.EndEdit();
                NormalisePrimaryFallbackRows();
                UpdatedMappings.Clear();
                foreach (var row in _rows)
                    UpdatedMappings[row.StableKey] = row.Alias ?? string.Empty;

                DarkModeResult = _chkDark.Checked;
                PreferredPrimaryKey = _rows.FirstOrDefault(r => r.IsPreferredPrimary)?.StableKey;
                FallbackPrimaryKey = _rows.FirstOrDefault(r => r.IsFallbackPrimary)?.StableKey;
                AlwaysOnTopResult = _chkTopMost.Checked;
                MinimizeToTrayResult = _chkTray.Checked;
                StartWithWindowsResult = ResolveStartupIntent(
                    _initialStartupCheckState,
                    _chkStartup.CheckState);
                ConfirmBeforeDisableResult = _chkConfirmDisable.Checked;
                RestoreLayoutOnStartupResult = _chkRestoreLayoutOnStartup.Checked;
                SelectedLayoutProfileResult = _selectedLayoutProfile;
                DialogResult = DialogResult.OK;
                Close();
            };

            UpdateDetailsPanel();
        }

        internal static bool? ResolveStartupIntent(
            CheckState initialState,
            CheckState currentState)
        {
            if (currentState == initialState)
                return null;

            return currentState switch
            {
                CheckState.Checked => true,
                CheckState.Unchecked => false,
                _ => null
            };
        }

        private void UpdateStartupControlDescription()
        {
            if (_chkStartup.CheckState == CheckState.Indeterminate)
            {
                _chkStartup.Text = "Start with Windows (different entry)";
                _chkStartup.AccessibleDescription =
                    "A different or unrecognised Monitor Switcher command is registered to start with Windows. " +
                    "Leave this unchanged to preserve it, tick to replace it, or untick to remove it.";
            }
            else
            {
                _chkStartup.Text = "Start with Windows";
                _chkStartup.AccessibleDescription = _chkStartup.Checked
                    ? "This Monitor Switcher executable will start with Windows."
                    : "Monitor Switcher will not change the Windows startup entry unless this option is changed.";
            }

            _toolTip.SetToolTip(_chkStartup, _chkStartup.AccessibleDescription);
        }

        private static FlowLayoutPanel CreateVerticalOptionsPanel(params CheckBox[] options)
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };

            foreach (var option in options)
            {
                option.Margin = new Padding(0, 5, 0, 8);
                panel.Controls.Add(option);
            }

            return panel;
        }

        internal static int CalculateVisibleMonitorRows(int monitorCount)
            => Math.Clamp(monitorCount, 1, 8);

        internal static int CalculateDetailsTextHeight(string? text, Font font, int availableWidth)
        {
            int safeWidth = Math.Max(120, availableWidth);
            var measured = TextRenderer.MeasureText(
                string.IsNullOrWhiteSpace(text) ? " " : text,
                font,
                new Size(safeWidth, int.MaxValue),
                TextFormatFlags.TextBoxControl |
                TextFormatFlags.WordBreak |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.NoPadding);
            // Keep one full line of reserve so moving the caret in this read-only
            // text box cannot scroll the first line out of view.
            return Math.Max((font.Height * 2) + 8, measured.Height + font.Height + 10);
        }

        private void FitInitialSizeToContent(
            FlowLayoutPanel monitorActions,
            FlowLayoutPanel commitActions,
            Form? sizingOwner)
        {
            int gridMinWidth = _grid.Columns
                .Cast<DataGridViewColumn>()
                .Sum(c => c.MinimumWidth) +
                SystemInformation.VerticalScrollBarWidth +
                16;

            int monitorAreaWidth = gridMinWidth + 48;
            int bottomWidth =
                PreferredControlsWidth(monitorActions.Controls) +
                PreferredControlsWidth(commitActions.Controls) +
                72;

            int desiredClientWidth = Math.Max(
                880,
                Math.Max(monitorAreaWidth, bottomWidth));

            int visibleRows = Math.Min(Math.Max(_rows.Count, 3), 8);
            int desiredGridHeight = _grid.ColumnHeadersHeight + (visibleRows * _grid.RowTemplate.Height) + 4;
            int desiredClientHeight = Math.Max(
                500,
                46 + 54 + desiredGridHeight + 170 + 72);

            var screen = sizingOwner != null && !sizingOwner.IsDisposed
                ? Screen.FromControl(sizingOwner)
                : Screen.FromPoint(Cursor.Position);
            var workingArea = screen.WorkingArea;
            int maxClientWidth = Math.Max(640, workingArea.Width - 80);
            int maxClientHeight = Math.Max(480, workingArea.Height - 80);

            var desiredClientSize = new Size(
                Math.Min(desiredClientWidth, maxClientWidth),
                Math.Min(desiredClientHeight, maxClientHeight));

            var minimumClientSize = new Size(
                Math.Min(760, maxClientWidth),
                Math.Min(240, maxClientHeight));

            MinimumSize = SizeFromClientSize(minimumClientSize);
            ClientSize = desiredClientSize;
        }

        private void SetResponsiveClientHeight(int desiredClientHeight)
        {
            if (WindowState != FormWindowState.Normal)
                return;

            var workingArea = Screen.FromControl(this).WorkingArea;
            int nonClientHeight = Height - ClientSize.Height;
            int maximumClientHeight = Math.Max(240, workingArea.Height - nonClientHeight);
            int minimumClientHeight = Math.Min(
                maximumClientHeight,
                Math.Max(240, MinimumSize.Height - nonClientHeight));
            int clampedHeight = Math.Clamp(desiredClientHeight, minimumClientHeight, maximumClientHeight);
            ClientSize = new Size(Math.Min(ClientSize.Width, workingArea.Width), clampedHeight);

            if (Visible && IsHandleCreated)
                ClampToWorkingArea(workingArea);
        }

        private void ClampToWorkingArea(Rectangle workingArea)
        {
            if (WindowState != FormWindowState.Normal)
                return;

            int width = Math.Min(Width, workingArea.Width);
            int height = Math.Min(Height, workingArea.Height);
            int left = Math.Clamp(Left, workingArea.Left, workingArea.Right - width);
            int top = Math.Clamp(Top, workingArea.Top, workingArea.Bottom - height);
            Bounds = new Rectangle(left, top, width, height);
        }

        private void KeepSingleCheckedRow(
            int selectedRowIndex,
            Func<AliasViewRow, bool> isChecked,
            Action<AliasViewRow, bool> setChecked)
        {
            if (!isChecked(_rows[selectedRowIndex]))
                return;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (i != selectedRowIndex && isChecked(_rows[i]))
                    setChecked(_rows[i], false);
            }

            _grid.Invalidate();
        }

        private static int PreferredControlsWidth(Control.ControlCollection controls)
        {
            int width = 0;
            foreach (Control control in controls)
            {
                var preferred = control.GetPreferredSize(Size.Empty);
                width += Math.Max(control.Width, preferred.Width) + control.Margin.Horizontal;
            }

            return width;
        }

        private void RemoveSelectedRows()
        {
            var selectedRows = _grid.SelectedCells
                .Cast<DataGridViewCell>()
                .Select(c => c.RowIndex)
                .Where(i => i >= 0)
                .Distinct()
                .OrderByDescending(i => i)
                .ToList();

            if (selectedRows.Count == 0)
                return;

            if (MessageBox.Show(
                    this,
                    "Remove the selected monitor entries from saved settings? Connected monitors will reappear after the next refresh.",
                    "Remove Monitor Entries",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            foreach (var rowIndex in selectedRows)
            {
                RemovedKeys.Add(_rows[rowIndex].StableKey);
                _rows.RemoveAt(rowIndex);
            }

            UpdatePrimaryFallbackCellStates();
            UpdateDetailsPanel();
        }

        private void DeleteSelectedProfile()
        {
            var profile = (_selectedLayoutProfile ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(profile) || profile.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "The Default layout profile cannot be deleted.", "Delete Profile",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this, $"Delete layout profile '{profile}'?", "Delete Profile",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            RemovedLayoutProfiles.Add(profile);
            _layoutProfileNames.RemoveAll(p => p.Equals(profile, StringComparison.OrdinalIgnoreCase));
            SetSelectedLayoutProfile(_layoutProfileNames.FirstOrDefault() ?? "Default");
        }

        private void SetSelectedLayoutProfile(string profile)
        {
            _selectedLayoutProfile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
            _layoutProfileButton.Text = _selectedLayoutProfile;
            _ok.Text = _selectedLayoutProfile.Equals(
                _initialSelectedLayoutProfile,
                StringComparison.OrdinalIgnoreCase)
                ? "Save"
                : "Save && Apply";
            BuildLayoutProfileMenu();
        }

        private void BuildLayoutProfileMenu()
        {
            _layoutProfileMenu?.Dispose();

            var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();
            _layoutProfileMenu = new ContextMenuStrip
            {
                BackColor = palette.Back,
                ForeColor = palette.Text,
                Renderer = new LayoutProfileMenuRenderer(palette),
                ShowCheckMargin = true,
                ShowImageMargin = false
            };

            _layoutProfileMenu.BackColor = palette.Surface;
            _layoutProfileMenu.ForeColor = palette.Text;

            foreach (var profile in _layoutProfileNames
                         .OrderBy(p => p.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                         .ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ToolStripMenuItem(profile)
                {
                    Checked = profile.Equals(_selectedLayoutProfile, StringComparison.OrdinalIgnoreCase),
                    ForeColor = palette.Text
                };
                item.Click += (_, __) => SetSelectedLayoutProfile(profile);
                _layoutProfileMenu.Items.Add(item);
            }
        }

        private void ShowLayoutProfileMenu()
        {
            BuildLayoutProfileMenu();
            _layoutProfileMenu?.Show(_layoutProfileButton, new Point(0, _layoutProfileButton.Height));
        }

        private void ShowDiagnosticsDialog()
        {
            try
            {
                var latestDiagnostics = _readDiagnostics?.Invoke();
                if (!string.IsNullOrWhiteSpace(latestDiagnostics))
                    _diagnosticsText = latestDiagnostics;

                var path = DiagnosticsExportPath();
                File.WriteAllText(path, _diagnosticsText);
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to open diagnostics.\n{ex.Message}", "Diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ClearDiagnostics()
        {
            if (_clearDiagnosticsLog == null)
            {
                MessageBox.Show(this, "Diagnostics cannot be cleared from this build.", "Clear Diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    this,
                    "Permanently clear the saved diagnostics log?",
                    "Clear Diagnostics",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            string? clearError;
            try
            {
                clearError = _clearDiagnosticsLog();
            }
            catch (Exception ex)
            {
                clearError = ex.Message;
            }

            if (!string.IsNullOrWhiteSpace(clearError))
            {
                MessageBox.Show(this, $"Unable to clear diagnostics.\n{clearError}", "Clear Diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var latestDiagnostics = _readDiagnostics?.Invoke();
                _diagnosticsText = string.IsNullOrWhiteSpace(latestDiagnostics)
                    ? "No diagnostic events have been recorded yet."
                    : latestDiagnostics;
            }
            catch
            {
                _diagnosticsText = "No diagnostic events have been recorded yet.";
            }

            try
            {
                var exportPath = DiagnosticsExportPath();
                if (File.Exists(exportPath))
                    File.Delete(exportPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"The saved diagnostics log was cleared, but its temporary exported copy could not be removed.\n{ex.Message}",
                    "Clear Diagnostics",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            MessageBox.Show(this, "Diagnostics cleared.", "Clear Diagnostics",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string DiagnosticsExportPath()
            => Path.Combine(Path.GetTempPath(), "MonitorSwitcher-diagnostics.txt");

        private void UpdateDetailsPanel()
        {
            var rowIndex = _grid.CurrentCell?.RowIndex ?? -1;
            if (rowIndex < 0 || rowIndex >= _rows.Count)
            {
                _details.Text = "Select a monitor to view saved identity details.";
                _details.Select(0, 0);
                _details.ScrollToCaret();
                return;
            }

            var row = _rows[rowIndex];
            _details.Text =
                $"Alias: {row.Alias}    Fallback primary: {(row.IsFallbackPrimary ? "Yes" : "No")}{Environment.NewLine}" +
                Environment.NewLine +
                $"Stable key: {row.StableKeyFull}{Environment.NewLine}" +
                $"Registry key: {row.RegistryKey}{Environment.NewLine}" +
                Environment.NewLine +
                $"Device name: {row.DeviceName}{Environment.NewLine}" +
                $"Monitor name: {row.MonitorName}{Environment.NewLine}" +
                $"Monitor ID: {row.MonitorId}{Environment.NewLine}" +
                $"Instance ID: {row.InstanceId}{Environment.NewLine}" +
                $"Serial number: {row.SerialNumber}{Environment.NewLine}" +
                $"Known command targets: {row.KnownTargets}";
            _details.Select(0, 0);
            _details.ScrollToCaret();
        }

        private void OpenRegistryForSelectedRow()
        {
            var rowIndex = _grid.CurrentCell?.RowIndex ?? -1;
            if (rowIndex < 0 && _grid.SelectedCells.Count > 0)
                rowIndex = _grid.SelectedCells.Cast<DataGridViewCell>().Min(c => c.RowIndex);

            OpenRegistryForRow(rowIndex);
        }

        private void OpenRegistryForRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count)
                return;

            var rawPath = _rows[rowIndex].RegistryKey;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                MessageBox.Show(this, "This monitor does not have a saved registry key yet.", "Open Registry",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var regeditPath = ToRegeditLastKeyPath(rawPath);
            if (string.IsNullOrWhiteSpace(regeditPath))
            {
                MessageBox.Show(this, $"Unable to open this registry path:\n{rawPath}", "Open Registry",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    key?.SetValue("LastKey", regeditPath, RegistryValueKind.String);

                Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to open Registry Editor.\n{ex.Message}", "Open Registry",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string ToRegeditLastKeyPath(string rawPath)
        {
            var path = rawPath.Trim();
            const string machinePrefix = @"\Registry\Machine\";
            const string userPrefix = @"\Registry\User\";

            if (path.StartsWith(machinePrefix, StringComparison.OrdinalIgnoreCase))
                return @"Computer\HKEY_LOCAL_MACHINE\" + path[machinePrefix.Length..];

            if (path.StartsWith(userPrefix, StringComparison.OrdinalIgnoreCase))
                return @"Computer\HKEY_USERS\" + path[userPrefix.Length..];

            if (path.StartsWith(@"HKEY_", StringComparison.OrdinalIgnoreCase))
                return @"Computer\" + path;

            if (path.StartsWith(@"Computer\HKEY_", StringComparison.OrdinalIgnoreCase))
                return path;

            return string.Empty;
        }

        private async Task CheckForUpdatesAsync()
        {
            _updateInProgress = true;
            _updateApp.Text = "Cancel Update Check";
            _updateApp.AccessibleName = "Cancel update check";
            _updateStatus.Text = "Checking GitHub releases…";
            _updateStatus.Visible = true;

            using var timeoutCancellation = new CancellationTokenSource(UpdateOperationTimeout);
            using var userCancellation = new CancellationTokenSource();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation.Token,
                userCancellation.Token);
            _updateCancellation = userCancellation;

            var progress = new Progress<AppUpdateProgress>(value =>
            {
                if (!IsDisposed && !Disposing)
                    _updateStatus.Text = value.DisplayText;
            });

            try
            {
                AppUpdateResult result = await _updateService.CheckAndDownloadAsync(
                    GetCurrentInformationalVersion(),
                    progress,
                    linkedCancellation.Token);
                if (IsDisposed || Disposing)
                    return;

                switch (result.Status)
                {
                    case AppUpdateStatus.NoPublishedRelease:
                        _updateStatus.Text = "No stable release is currently published.";
                        ThemedMessageBox.Info(
                            this,
                            "No stable GitHub release is published for this repository yet.",
                            "Check for Updates",
                            _chkDark.Checked);
                        break;

                    case AppUpdateStatus.Current:
                        _updateStatus.Text = "MonitorSwitcher is up to date.";
                        ThemedMessageBox.Info(
                            this,
                            $"You already have MonitorSwitcher {result.CurrentVersion}.\n\n" +
                            $"The latest stable release is {result.ReleaseVersion}.",
                            "Check for Updates",
                            _chkDark.Checked);
                        break;

                    case AppUpdateStatus.Ready:
                    case AppUpdateStatus.Reused:
                        _updateStatus.Text = result.Status == AppUpdateStatus.Reused
                            ? $"Verified release {result.ReleaseVersion} is already downloaded."
                            : $"Release {result.ReleaseVersion} was downloaded and verified.";
                        ShowDownloadedUpdate(result);
                        break;
                }

                if (!string.IsNullOrWhiteSpace(result.CleanupWarning))
                {
                    ThemedMessageBox.Warn(
                        this,
                        "The verified release is ready, but its temporary download directory could not be removed.\n\n" +
                        result.CleanupWarning,
                        "Check for Updates",
                        _chkDark.Checked);
                }
            }
            catch (OperationCanceledException)
            {
                if (IsDisposed || Disposing)
                    return;

                _updateStatus.Text = timeoutCancellation.IsCancellationRequested
                    ? "The update check timed out."
                    : "Update check cancelled.";
                if (timeoutCancellation.IsCancellationRequested)
                {
                    ThemedMessageBox.Warn(
                        this,
                        $"The update check did not finish within {UpdateOperationTimeout.TotalMinutes:0} minutes and was cancelled.",
                        "Check for Updates",
                        _chkDark.Checked);
                }
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing)
                    return;

                _updateStatus.Text = "The update check failed.";
                ThemedMessageBox.Warn(
                    this,
                    $"Unable to check or download GitHub releases.\n{ex.Message}",
                    "Check for Updates",
                    _chkDark.Checked);
            }
            finally
            {
                if (ReferenceEquals(_updateCancellation, userCancellation))
                    _updateCancellation = null;
                _updateInProgress = false;
                if (!IsDisposed && !Disposing)
                {
                    _updateApp.Text = "Check for Updates";
                    _updateApp.AccessibleName = "Check for updates";
                }
            }
        }

        private void ShowDownloadedUpdate(AppUpdateResult result)
        {
            if (string.IsNullOrWhiteSpace(result.PackageDirectory))
                throw new InvalidOperationException("The verified update folder was not returned.");

            var openChoice = MessageBox.Show(
                this,
                BuildDownloadedUpdateMessage(result),
                "Check for Updates",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (openChoice == DialogResult.Yes)
                OpenExplorerAt(result.PackageDirectory);
        }

        internal static string BuildDownloadedUpdateMessage(AppUpdateResult result)
        {
            string availability = result.Status == AppUpdateStatus.Reused
                ? $"A previously downloaded copy of MonitorSwitcher {result.ReleaseVersion} was verified again."
                : $"MonitorSwitcher {result.ReleaseVersion} was downloaded, verified and extracted.";
            return $"{availability}\n\n" +
                   "The checksum confirms that these files match the published GitHub release, but the executable " +
                   "is not Authenticode-signed and has no verified publisher identity. Windows Defender SmartScreen " +
                   "may show an unrecognised-app warning.\n\n" +
                   "The running installation and its Start or startup shortcuts were not changed. " +
                   "Run MonitorSwitcher.exe from the folder below to test the release.\n\n" +
                   $"Extracted release:\n{result.PackageDirectory}\n\nOpen the folder now?";
        }

        private static string GetCurrentInformationalVersion()
        {
            Assembly? assembly = Assembly.GetEntryAssembly();
            return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Application.ProductVersion;
        }

        private static void OpenExplorerAt(string path)
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }

        internal static int? CompareSemanticVersions(string? left, string? right)
            => AppUpdateService.CompareSemanticVersions(left, right);

        internal static bool IsExpectedGitHubReleaseAssetUri(Uri uri, string releaseTag, string assetName)
            => AppUpdateService.IsExpectedGitHubReleaseAssetUri(uri, releaseTag, assetName);

        internal static bool TryParsePublishedSha256(
            string? checksumText,
            string expectedAssetName,
            out string expectedHash)
            => AppUpdateService.TryParsePublishedSha256(checksumText, expectedAssetName, out expectedHash);

        internal static bool IsSafeArchivePath(string? entryFullName)
            => AppUpdateService.IsSafeArchivePath(entryFullName);

        private void ApplyDialogTheme(bool dark)
        {
            var palette = dark ? ThemePalette.Dark() : ThemePalette.Light();

            // Apply palette to this form and its controls
            Themer.Apply(this, palette);

            ApplyTitleBarTheme(palette, dark);
            BuildLayoutProfileMenu();
            UpdatePrimaryFallbackCellStates();

            Invalidate(true);
            Update();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ClampToWorkingArea(Screen.FromControl(this).WorkingArea);
            var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();
            ApplyTitleBarTheme(palette, _chkDark.Checked);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _updateCancellation?.Cancel();
            if (_ownsUpdateService)
                _updateService.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();
            ApplyTitleBarTheme(palette, _chkDark.Checked);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();
            ApplyTitleBarTheme(palette, _chkDark.Checked);
        }

        private void ApplyTitleBarTheme(ThemePalette palette, bool dark)
        {
            if (!IsHandleCreated)
                return;

            DwmInterop.SetDarkTitleBar(Handle, dark);
            DwmInterop.SetCaptionColor(
                Handle,
                WindowsTheme.AccentColor() ?? (dark ? palette.Surface : palette.Back));
        }

        private bool IsFallbackPrimaryCell(int rowIndex, int columnIndex)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count || columnIndex < 0 || columnIndex >= _grid.Columns.Count)
                return false;

            return _grid.Columns[columnIndex].DataPropertyName == nameof(AliasViewRow.IsFallbackPrimary);
        }

        private bool IsDisabledFallbackPrimaryCell(int rowIndex, int columnIndex)
            => IsFallbackPrimaryCell(rowIndex, columnIndex) && _rows[rowIndex].IsPreferredPrimary;

        private void PaintDisabledFallbackCheckbox(DataGridViewCellPaintingEventArgs e)
        {
            e.Handled = true;

            var graphics = e.Graphics;
            if (graphics == null)
                return;

            var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();
            var selected = (e.State & DataGridViewElementStates.Selected) == DataGridViewElementStates.Selected;
            var background = selected
                ? ControlPaint.Light(palette.Surface, 0.08f)
                : palette.Surface;

            using (var back = new SolidBrush(background))
                graphics.FillRectangle(back, e.CellBounds);

            using (var border = new Pen(_grid.GridColor))
            {
                graphics.DrawLine(border, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
                graphics.DrawLine(border, e.CellBounds.Right - 1, e.CellBounds.Top, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
            }

            bool dark = _chkDark.Checked;
            var boxFill = dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(222, 222, 222);
            var boxBorder = dark ? Color.FromArgb(96, 96, 96) : Color.FromArgb(160, 160, 160);
            var checkColor = dark ? Color.FromArgb(132, 132, 132) : Color.FromArgb(130, 130, 130);
            int boxSize = _grid.LogicalToDeviceUnits(14);
            var boxBounds = new Rectangle(
                e.CellBounds.Left + ((e.CellBounds.Width - boxSize) / 2),
                e.CellBounds.Top + ((e.CellBounds.Height - boxSize) / 2),
                boxSize,
                boxSize);

            using (var boxBack = new SolidBrush(boxFill))
                graphics.FillRectangle(boxBack, boxBounds);
            using (var boxPen = new Pen(boxBorder))
                graphics.DrawRectangle(boxPen, boxBounds);

            var isChecked = e.Value is bool value && value;
            if (!isChecked)
                return;

            using var checkPen = new Pen(checkColor, _grid.LogicalToDeviceUnits(2));
            checkPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            checkPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            graphics.DrawLines(checkPen, new[]
            {
                new Point(boxBounds.Left + _grid.LogicalToDeviceUnits(3), boxBounds.Top + _grid.LogicalToDeviceUnits(7)),
                new Point(boxBounds.Left + _grid.LogicalToDeviceUnits(6), boxBounds.Top + _grid.LogicalToDeviceUnits(10)),
                new Point(boxBounds.Left + _grid.LogicalToDeviceUnits(11), boxBounds.Top + _grid.LogicalToDeviceUnits(4))
            });
        }

        private void NormalisePrimaryFallbackRows()
        {
            string? preferredPrimaryKey = null;
            string? fallbackPrimaryKey = null;

            foreach (var row in _rows)
            {
                if (row.IsPreferredPrimary)
                {
                    if (preferredPrimaryKey == null)
                    {
                        preferredPrimaryKey = row.StableKey;
                    }
                    else
                    {
                        row.IsPreferredPrimary = false;
                    }
                }

                if (row.IsPreferredPrimary && row.IsFallbackPrimary)
                    row.IsFallbackPrimary = false;

                if (row.IsFallbackPrimary)
                {
                    if (fallbackPrimaryKey == null)
                    {
                        fallbackPrimaryKey = row.StableKey;
                    }
                    else
                    {
                        row.IsFallbackPrimary = false;
                    }
                }
            }
        }

        private void UpdatePrimaryFallbackCellStates()
        {
            var fallbackColumnIndex = FindColumnIndex(nameof(AliasViewRow.IsFallbackPrimary));
            if (fallbackColumnIndex < 0 || _grid.Rows.Count == 0)
                return;

            var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();

            for (int rowIndex = 0; rowIndex < _rows.Count && rowIndex < _grid.Rows.Count; rowIndex++)
            {
                var row = _rows[rowIndex];
                var fallbackCell = _grid.Rows[rowIndex].Cells[fallbackColumnIndex];
                var fallbackDisabled = row.IsPreferredPrimary;

                fallbackCell.ReadOnly = fallbackDisabled;
                fallbackCell.ToolTipText = fallbackDisabled
                    ? "A primary monitor cannot also be the fallback primary."
                    : string.Empty;
                fallbackCell.Style.BackColor = fallbackDisabled ? palette.Surface : _grid.DefaultCellStyle.BackColor;
                fallbackCell.Style.ForeColor = fallbackDisabled ? palette.TextSubtle : _grid.DefaultCellStyle.ForeColor;
                fallbackCell.Style.SelectionBackColor = fallbackDisabled ? palette.Surface : _grid.DefaultCellStyle.SelectionBackColor;
                fallbackCell.Style.SelectionForeColor = fallbackDisabled ? palette.TextSubtle : _grid.DefaultCellStyle.SelectionForeColor;
            }

            _grid.Refresh();
            _grid.Invalidate();
        }

        private int FindColumnIndex(string propertyName)
        {
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                if (column.DataPropertyName == propertyName)
                    return column.Index;
            }

            return -1;
        }

        private sealed class LayoutProfileMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly ThemePalette _palette;

            public LayoutProfileMenuRenderer(ThemePalette palette)
                : base(new LayoutProfileColorTable(palette))
            {
                _palette = palette;
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var item = e.Item as ToolStripMenuItem;
                bool selected = e.Item.Selected;
                bool isChecked = item?.Checked == true;

                var bounds = new Rectangle(Point.Empty, e.Item.Size);
                var backColor = selected
                    ? ControlPaint.Light(_palette.Surface, 0.18f)
                    : isChecked
                        ? ControlPaint.Light(_palette.Surface, 0.08f)
                        : _palette.Back;

                using var back = new SolidBrush(backColor);
                e.Graphics.FillRectangle(back, bounds);

                if (selected || isChecked)
                {
                    using var border = new Pen(_palette.Border);
                    e.Graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = _palette.Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using var back = new SolidBrush(_palette.Surface);
                e.Graphics.FillRectangle(back, e.AffectedBounds);
            }
        }

        private sealed class LayoutProfileColorTable : ProfessionalColorTable
        {
            private readonly ThemePalette _palette;
            private readonly Color _selected;
            private readonly Color _checked;

            public LayoutProfileColorTable(ThemePalette palette)
            {
                _palette = palette;
                _selected = ControlPaint.Light(palette.Surface, 0.18f);
                _checked = ControlPaint.Light(palette.Surface, 0.08f);
            }

            public override Color ToolStripDropDownBackground => _palette.Back;
            public override Color ImageMarginGradientBegin => _palette.Surface;
            public override Color ImageMarginGradientMiddle => _palette.Surface;
            public override Color ImageMarginGradientEnd => _palette.Surface;
            public override Color MenuBorder => _palette.Border;
            public override Color MenuItemBorder => _palette.Border;
            public override Color MenuItemSelected => _selected;
            public override Color MenuItemSelectedGradientBegin => _selected;
            public override Color MenuItemSelectedGradientEnd => _selected;
            public override Color CheckBackground => _checked;
            public override Color CheckPressedBackground => _selected;
            public override Color CheckSelectedBackground => _selected;
        }
    }
}
