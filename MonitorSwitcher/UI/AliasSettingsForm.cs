#nullable enable
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using WorkMonitorSwitcher.Model;
using WorkMonitorSwitcher.Services;

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
        private readonly Button _updateApp = new() { Text = "Update App", AutoSize = true };
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
        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        private static readonly Uri LatestReleaseApiUri = new("https://api.github.com/repos/Ci303/monitor-switcher-native/releases/latest");
        private const long MaxReleaseApiBytes = 2L * 1024 * 1024;
        private const long MaxChecksumBytes = 16L * 1024;
        private const long MaxAppArchiveBytes = 300L * 1024 * 1024;
        private const long MaxAppExpandedBytes = 750L * 1024 * 1024;
        private const int MaxAppArchiveEntries = 5000;
        private const ushort PeMachineAmd64 = 0x8664;

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

        public AliasSettingsForm(
            List<AliasViewRow> current,
            bool darkMode,
            bool alwaysOnTop,
            bool minimizeToTray,
            bool startWithWindows,
            bool confirmBeforeDisable,
            bool restoreLayoutOnStartup,
            List<string> layoutProfiles,
            string selectedLayoutProfile,
            string diagnosticsText,
            Func<string>? readDiagnostics = null,
            Func<string?>? clearDiagnosticsLog = null,
            Form? sizingOwner = null)

        {
            _rows = new BindingList<AliasViewRow>(current);
            _diagnosticsText = string.IsNullOrWhiteSpace(diagnosticsText)
                ? "No diagnostic events have been recorded yet."
                : diagnosticsText;
            _readDiagnostics = readDiagnostics;
            _clearDiagnosticsLog = clearDiagnosticsLog;
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
            MinimizeBox = true;
            ShowIcon = true;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // Non-fatal: the dialog can still open without a title bar icon.
            }
            MinimumSize = new Size(760, 480);
            Size = new Size(900, 540);
            _chkDark.Checked = darkMode;
            _chkTopMost.Checked = alwaysOnTop;
            _chkTray.Checked = minimizeToTray;
            _chkStartup.Checked = startWithWindows;
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

            foreach (var row in _rows)
                row.IsPreferredPrimary = false;
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
            _updateApp.Click += async (_, __) => await UpdateAppAsync();
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
            _toolTip.SetToolTip(_updateApp, "Download the latest GitHub release asset if one is published.");
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
                Height = 250,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(4)
            };
            generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
            generalLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));

            var appearanceGroup = new GroupBox
            {
                Text = "Appearance and window",
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 20, 14, 12),
                Margin = new Padding(0, 0, 7, 10)
            };
            var appearanceOptions = CreateVerticalOptionsPanel(_chkDark, _chkTopMost, _chkTray);
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
            behaviourGroup.Controls.Add(behaviourOptions);

            var maintenanceGroup = new GroupBox
            {
                Text = "Application",
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 20, 14, 10),
                Margin = new Padding(0)
            };
            var maintenanceActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            maintenanceActions.Controls.Add(_updateApp);
            maintenanceActions.Controls.Add(_showDiagnostics);
            maintenanceActions.Controls.Add(_clearDiagnostics);
            maintenanceGroup.Controls.Add(maintenanceActions);

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
                    int detailsHeight = textHeight + detailsGroup.Padding.Vertical + detailsGroup.Margin.Vertical + 8;

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
                        ClientSize = new Size(ClientSize.Width, Math.Min(desiredClientHeight, maxClientHeight));
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

            var generalButton = new ThemedButton { Text = "General", Size = new Size(92, 32), Tone = ThemedButtonTone.Primary };
            var monitorsButton = new ThemedButton { Text = "Monitors", Size = new Size(92, 32) };
            var profilesButton = new ThemedButton { Text = "Profiles", Size = new Size(92, 32) };
            var navigation = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(14, 7, 14, 7)
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
                    button.Tone = ReferenceEquals(button, selectedButton)
                        ? ThemedButtonTone.Primary
                        : ThemedButtonTone.Neutral;
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
                    ClientSize = new Size(ClientSize.Width, Math.Min(desiredClientHeight, maxClientHeight));
                }
            }

            generalButton.Click += (_, __) => ShowPage(generalPage, generalButton);
            monitorsButton.Click += (_, __) => ShowPage(monitorsPage, monitorsButton);
            profilesButton.Click += (_, __) => ShowPage(profilesPage, profilesButton);

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
                StartWithWindowsResult = _chkStartup.Checked;
                ConfirmBeforeDisableResult = _chkConfirmDisable.Checked;
                RestoreLayoutOnStartupResult = _chkRestoreLayoutOnStartup.Checked;
                SelectedLayoutProfileResult = _selectedLayoutProfile;
                DialogResult = DialogResult.OK;
                Close();
            };

            UpdateDetailsPanel();
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

        private async Task UpdateAppAsync()
        {
            var originalText = _updateApp.Text;
            Enabled = false;
            UseWaitCursor = true;
            _updateApp.Text = "Checking...";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MonitorSwitcher", "1.0"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                byte[] releaseJson;
                try
                {
                    releaseJson = await DownloadBytesAsync(request, MaxReleaseApiBytes, IsExpectedReleaseApiUri);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    MessageBox.Show(
                        this,
                        "No GitHub release is published for this repository yet.",
                        "Update App",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                var release = JsonSerializer.Deserialize<GitHubRelease>(releaseJson);
                if (release == null)
                    throw new InvalidOperationException("Unable to read the latest release details.");

                if (release.Draft || release.Prerelease)
                    throw new InvalidDataException("GitHub returned a draft or pre-release instead of the latest stable release.");

                if (!SemanticVersion.TryParseReleaseTag(release.TagName, out SemanticVersion releaseVersion))
                    throw new InvalidDataException($"The release tag '{release.TagName}' is not a stable semantic version such as v1.2.3.");

                string currentVersionText = GetCurrentInformationalVersion();
                if (!SemanticVersion.TryParse(currentVersionText, out SemanticVersion currentVersion))
                    throw new InvalidDataException($"The installed app version '{currentVersionText}' is not a valid semantic version.");

                if (releaseVersion.CompareTo(currentVersion) <= 0)
                {
                    MessageBox.Show(
                        this,
                        $"You already have MonitorSwitcher {currentVersion}.\n\n" +
                        $"The latest published release is {releaseVersion}, so no update is required.",
                        "Update App",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                string expectedAssetName = $"MonitorSwitcher-{release.TagName}-win-x64.zip";
                string expectedChecksumName = expectedAssetName + ".sha256";
                GitHubReleaseAsset asset = FindUniqueReleaseAsset(release.Assets, expectedAssetName, required: true)!;
                GitHubReleaseAsset checksumAsset = FindUniqueReleaseAsset(release.Assets, expectedChecksumName, required: true)!;
                ValidateReleaseAsset(asset, release.TagName, expectedAssetName, MaxAppArchiveBytes);
                ValidateReleaseAsset(checksumAsset, release.TagName, expectedChecksumName, MaxChecksumBytes);

                _updateApp.Text = "Downloading...";

                string appDir = Path.GetFullPath(AppContext.BaseDirectory);
                string updatesDir = Path.Combine(appDir, "updates");
                Directory.CreateDirectory(updatesDir);
                string? stagingRoot = Path.Combine(updatesDir, $".app-update-staging-{Guid.NewGuid():N}");

                try
                {
                    Directory.CreateDirectory(stagingRoot);
                    string assetPath = Path.Combine(stagingRoot, expectedAssetName);
                    await DownloadFileAsync(
                        new Uri(asset.DownloadUrl, UriKind.Absolute),
                        assetPath,
                        MaxAppArchiveBytes,
                        IsSafeGitHubDownloadRedirectUri);
                    VerifyExpectedDownloadSize(assetPath, asset.Size, expectedAssetName);

                    string checksumPath = Path.Combine(stagingRoot, expectedChecksumName);
                    await DownloadFileAsync(
                        new Uri(checksumAsset.DownloadUrl, UriKind.Absolute),
                        checksumPath,
                        MaxChecksumBytes,
                        IsSafeGitHubDownloadRedirectUri);
                    VerifyExpectedDownloadSize(checksumPath, checksumAsset.Size, expectedChecksumName);
                    VerifyPublishedSha256(assetPath, checksumPath, expectedAssetName);

                    VerifyGitHubDigestWhenPresent(assetPath, asset.Digest);

                    string packageDir = Path.Combine(stagingRoot, "package");
                    ExtractZipSafely(
                        assetPath,
                        packageDir,
                        MaxAppArchiveEntries,
                        MaxAppExpandedBytes,
                        MaxAppArchiveBytes);
                    ValidateMonitorSwitcherPackage(packageDir, releaseVersion);

                    string finalRoot = GetUniqueDirectoryPath(
                        Path.Combine(updatesDir, $"MonitorSwitcher-{release.TagName}-win-x64"));
                    Directory.Move(stagingRoot, finalRoot);
                    stagingRoot = null;
                    string finalPath = Path.Combine(finalRoot, "package");

                    var openChoice = MessageBox.Show(
                        this,
                        $"MonitorSwitcher {releaseVersion} was downloaded and verified.\n\n" +
                        $"The current installation was not changed, so it remains available for rollback.\n\n" +
                        $"Extracted update:\n{finalPath}\n\nOpen the folder now?",
                        "Update App",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);

                    if (openChoice == DialogResult.Yes)
                        OpenExplorerAt(finalPath);
                }
                finally
                {
                    if (stagingRoot != null)
                        TryDeleteDirectory(stagingRoot);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Unable to update from GitHub releases.\n{ex.Message}",
                    "Update App",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                _updateApp.Text = originalText;
                UseWaitCursor = false;
                Enabled = true;
            }
        }

        private static async Task<byte[]> DownloadBytesAsync(
            HttpRequestMessage request,
            long maximumBytes,
            Func<Uri, bool> isAllowedFinalUri)
        {
            using var response = await SendWithValidatedRedirectsAsync(request, isAllowedFinalUri);
            ValidateDownloadResponse(response, maximumBytes, isAllowedFinalUri);

            await using var source = await response.Content.ReadAsStreamAsync();
            using var destination = new MemoryStream();
            await CopyStreamWithLimitAsync(source, destination, maximumBytes);
            return destination.ToArray();
        }

        private static async Task DownloadFileAsync(
            Uri sourceUri,
            string destinationPath,
            long maximumBytes,
            Func<Uri, bool> isAllowedFinalUri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MonitorSwitcher", "1.0"));
            using var response = await SendWithValidatedRedirectsAsync(request, isAllowedFinalUri);
            ValidateDownloadResponse(response, maximumBytes, isAllowedFinalUri);

            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await CopyStreamWithLimitAsync(source, destination, maximumBytes);
        }

        private static async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
            HttpRequestMessage initialRequest,
            Func<Uri, bool> isAllowedUri)
        {
            const int maximumRedirects = 5;
            HttpRequestMessage currentRequest = initialRequest;
            bool ownsCurrentRequest = false;

            try
            {
                for (int redirectCount = 0; ; redirectCount++)
                {
                    Uri? currentUri = currentRequest.RequestUri;
                    if (currentUri == null || !isAllowedUri(currentUri))
                        throw new InvalidDataException("The download address is not permitted.");

                    HttpResponseMessage response = await Http.SendAsync(
                        currentRequest,
                        HttpCompletionOption.ResponseHeadersRead);
                    if (!IsRedirectStatusCode(response.StatusCode))
                        return response;

                    try
                    {
                        if (redirectCount >= maximumRedirects)
                            throw new InvalidDataException("The download exceeded the permitted redirect count.");

                        Uri? location = response.Headers.Location;
                        if (location == null)
                            throw new InvalidDataException("The download returned a redirect without a destination.");

                        Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        if (!isAllowedUri(nextUri))
                            throw new InvalidDataException("The download was redirected to an unexpected address.");

                        var nextRequest = new HttpRequestMessage(HttpMethod.Get, nextUri);
                        foreach (var header in initialRequest.Headers)
                            nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

                        if (ownsCurrentRequest)
                            currentRequest.Dispose();
                        currentRequest = nextRequest;
                        ownsCurrentRequest = true;
                    }
                    finally
                    {
                        response.Dispose();
                    }
                }
            }
            finally
            {
                if (ownsCurrentRequest)
                    currentRequest.Dispose();
            }
        }

        private static bool IsRedirectStatusCode(System.Net.HttpStatusCode statusCode)
            => statusCode is System.Net.HttpStatusCode.MovedPermanently or
                System.Net.HttpStatusCode.Redirect or
                System.Net.HttpStatusCode.RedirectMethod or
                System.Net.HttpStatusCode.TemporaryRedirect or
                System.Net.HttpStatusCode.PermanentRedirect;

        private static void ValidateDownloadResponse(
            HttpResponseMessage response,
            long maximumBytes,
            Func<Uri, bool> isAllowedFinalUri)
        {
            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri == null || !isAllowedFinalUri(finalUri))
                throw new InvalidDataException("The download was redirected to an unexpected address.");

            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is < 1)
                throw new InvalidDataException("The server returned an empty download.");
            if (contentLength > maximumBytes)
                throw new InvalidDataException($"The server reported a download larger than the {FormatByteLimit(maximumBytes)} limit.");
        }

        private static async Task CopyStreamWithLimitAsync(Stream source, Stream destination, long maximumBytes)
        {
            var buffer = new byte[81920];
            long totalBytes = 0;
            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (bytesRead == 0)
                    break;

                totalBytes = checked(totalBytes + bytesRead);
                if (totalBytes > maximumBytes)
                    throw new InvalidDataException($"The download exceeded the {FormatByteLimit(maximumBytes)} limit.");

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead));
            }

            if (totalBytes == 0)
                throw new InvalidDataException("The server returned an empty download.");
        }

        private static string FormatByteLimit(long bytes)
            => $"{bytes / (1024 * 1024)} MB";

        private static bool IsExpectedReleaseApiUri(Uri uri)
            => IsExactHttpsUri(uri, LatestReleaseApiUri);

        private static bool IsSafeGitHubDownloadRedirectUri(Uri uri)
        {
            if (!IsSafeHttpsUri(uri) || !string.IsNullOrEmpty(uri.Fragment))
                return false;

            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExactHttpsUri(Uri actual, Uri expected)
            => IsSafeHttpsUri(actual) &&
               actual.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) &&
               actual.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(actual.Query) &&
               string.IsNullOrEmpty(actual.Fragment);

        private static bool IsSafeHttpsUri(Uri uri)
            => uri.IsAbsoluteUri &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               (uri.IsDefaultPort || uri.Port == 443);

        private static GitHubReleaseAsset? FindUniqueReleaseAsset(
            IEnumerable<GitHubReleaseAsset> assets,
            string expectedName,
            bool required)
        {
            GitHubReleaseAsset[] matches = assets
                .Where(asset => asset.Name.Equals(expectedName, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length > 1)
                throw new InvalidDataException($"Release {expectedName} is listed more than once.");
            if (matches.Length == 0 && required)
                throw new InvalidDataException($"The release is missing the expected asset {expectedName}.");

            return matches.SingleOrDefault();
        }

        private static void ValidateReleaseAsset(
            GitHubReleaseAsset asset,
            string releaseTag,
            string expectedName,
            long maximumBytes)
        {
            if (!IsSafeFileName(asset.Name) || !asset.Name.Equals(expectedName, StringComparison.Ordinal))
                throw new InvalidDataException("The release contains an unsafe or unexpected asset name.");
            if (asset.Size < 1 || asset.Size > maximumBytes)
                throw new InvalidDataException($"The reported size of {expectedName} is outside the allowed range.");
            if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? downloadUri))
                throw new InvalidDataException($"The download address for {expectedName} is invalid.");

            if (!IsExpectedGitHubReleaseAssetUri(downloadUri, releaseTag, expectedName))
                throw new InvalidDataException($"The download address for {expectedName} is not the expected GitHub release path.");
        }

        internal static bool IsExpectedGitHubReleaseAssetUri(Uri uri, string releaseTag, string assetName)
        {
            if (!SemanticVersion.TryParseReleaseTag(releaseTag, out _) || !IsSafeFileName(assetName))
                return false;

            var expectedUri = new Uri($"https://github.com/Ci303/monitor-switcher-native/releases/download/{releaseTag}/{assetName}");
            return IsExactHttpsUri(uri, expectedUri);
        }

        private static bool IsSafeFileName(string value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Equals(Path.GetFileName(value), StringComparison.Ordinal) &&
               !value.Contains('/') &&
               !value.Contains('\\') &&
               IsSafePathSegment(value);

        private static void VerifyExpectedDownloadSize(string path, long expectedBytes, string displayName)
        {
            long actualBytes = new FileInfo(path).Length;
            if (actualBytes != expectedBytes)
            {
                throw new InvalidDataException(
                    $"Downloaded size mismatch for {displayName}: GitHub reported {expectedBytes} bytes but received {actualBytes} bytes.");
            }
        }

        private static void VerifyPublishedSha256(string assetPath, string checksumPath, string expectedAssetName)
        {
            string checksumText = File.ReadAllText(checksumPath, Encoding.UTF8);
            if (!TryParsePublishedSha256(checksumText, expectedAssetName, out string expectedHash))
            {
                throw new InvalidDataException("The published SHA-256 sidecar has an unexpected format or filename.");
            }

            string actualHash = ComputeSha256(assetPath);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update does not match its published SHA-256 checksum.");
        }

        internal static bool TryParsePublishedSha256(
            string? checksumText,
            string expectedAssetName,
            out string expectedHash)
        {
            expectedHash = string.Empty;
            if (checksumText == null || !IsSafeFileName(expectedAssetName))
                return false;

            string[] fields = checksumText.Trim().TrimStart('\uFEFF')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 ||
                !IsSha256(fields[0]) ||
                !fields[1].TrimStart('*').Equals(expectedAssetName, StringComparison.Ordinal))
            {
                return false;
            }

            expectedHash = fields[0];
            return true;
        }

        private static void VerifyGitHubDigestWhenPresent(string assetPath, string? publishedDigest)
        {
            if (string.IsNullOrWhiteSpace(publishedDigest))
                return;

            const string prefix = "sha256:";
            if (!publishedDigest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !IsSha256(publishedDigest[prefix.Length..]))
            {
                throw new InvalidDataException("GitHub returned an unsupported or malformed release-asset digest.");
            }

            string actualHash = ComputeSha256(assetPath);
            if (!actualHash.Equals(publishedDigest[prefix.Length..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update does not match GitHub's release-asset digest.");
        }

        private static bool IsSha256(string value)
            => value.Length == 64 && value.All(Uri.IsHexDigit);

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static string GetCurrentInformationalVersion()
        {
            Assembly? assembly = Assembly.GetEntryAssembly();
            return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Application.ProductVersion;
        }

        private static void ExtractZipSafely(
            string archivePath,
            string destinationDirectory,
            int maximumEntries,
            long maximumExpandedBytes,
            long maximumEntryBytes)
        {
            Directory.CreateDirectory(destinationDirectory);
            string destinationRoot = Path.GetFullPath(destinationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
                throw new InvalidDataException($"The archive entry count is outside the allowed range (maximum {maximumEntries}).");

            long totalExpandedBytes = 0;
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string rawPath = entry.FullName.Replace('\\', '/');
                bool isDirectory = rawPath.EndsWith("/", StringComparison.Ordinal);
                string trimmedPath = rawPath.TrimEnd('/');

                if (!IsSafeArchivePath(rawPath))
                    throw new InvalidDataException($"The archive contains an unsafe path: {entry.FullName}");
                if (IsZipLinkOrReparsePoint(entry))
                    throw new InvalidDataException($"The archive contains a link or reparse point: {entry.FullName}");

                string[] segments = trimmedPath.Split('/');
                string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, Path.Combine(segments)));
                if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The archive path escapes the extraction directory: {entry.FullName}");
                if (!destinations.Add(destinationPath))
                    throw new InvalidDataException($"The archive contains duplicate paths: {entry.FullName}");

                if (isDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                if (entry.Length < 0 || entry.Length > maximumEntryBytes)
                    throw new InvalidDataException($"Archive entry {entry.FullName} is larger than allowed.");

                totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
                if (totalExpandedBytes > maximumExpandedBytes)
                    throw new InvalidDataException("The archive expands beyond the allowed size.");

                string? parentDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                    Directory.CreateDirectory(parentDirectory);

                using Stream source = entry.Open();
                using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyArchiveEntryWithLimit(source, destination, entry.Length, maximumEntryBytes);
            }
        }

        internal static bool IsSafeArchivePath(string? entryFullName)
        {
            if (string.IsNullOrWhiteSpace(entryFullName))
                return false;

            string normalised = entryFullName.Replace('\\', '/');
            string trimmed = normalised.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(trimmed) || normalised.StartsWith("/", StringComparison.Ordinal))
                return false;

            return trimmed.Split('/').All(IsSafePathSegment);
        }

        private static void CopyArchiveEntryWithLimit(
            Stream source,
            Stream destination,
            long declaredLength,
            long maximumBytes)
        {
            var buffer = new byte[81920];
            long written = 0;
            while (true)
            {
                int count = source.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;

                written = checked(written + count);
                if (written > maximumBytes || written > declaredLength)
                    throw new InvalidDataException("An archive entry exceeded its declared or allowed size.");

                destination.Write(buffer, 0, count);
            }

            if (written != declaredLength)
                throw new InvalidDataException("An archive entry did not match its declared size.");
        }

        private static bool IsZipLinkOrReparsePoint(ZipArchiveEntry entry)
        {
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            uint unixFileType = (attributes >> 16) & 0xF000;
            return unixFileType == 0xA000 ||
                   (attributes & (uint)FileAttributes.ReparsePoint) != 0;
        }

        private static bool IsSafePathSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Length > 255 ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            string deviceName = segment.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (deviceName.Length == 4 &&
                (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                deviceName[3] is >= '1' and <= '9')
            {
                return false;
            }

            return true;
        }

        private static void ValidateMonitorSwitcherPackage(string packageDirectory, SemanticVersion expectedVersion)
        {
            string[] requiredFiles =
            {
                "MonitorSwitcher.exe",
                "MonitorSwitcher.dll",
                "MonitorSwitcher.deps.json",
                "MonitorSwitcher.runtimeconfig.json",
                "LICENSE",
                "README.md",
                "RELEASE_NOTES.md",
                "THIRD-PARTY-NOTICES.md",
                "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt"
            };

            foreach (string requiredFile in requiredFiles)
            {
                string requiredPath = Path.Combine(packageDirectory, requiredFile);
                if (!File.Exists(requiredPath) || new FileInfo(requiredPath).Length == 0)
                    throw new InvalidDataException($"The update package is missing {requiredFile}.");
            }

            string packageRoot = Path.GetFullPath(packageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string[] executableExtensions = { ".exe", ".com", ".cpl", ".msi", ".msp", ".msix", ".scr" };
            foreach (string file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
                string extension = Path.GetExtension(file);
                if (!executableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    continue;

                bool allowed = relativePath.Equals("MonitorSwitcher.exe", StringComparison.OrdinalIgnoreCase) ||
                               relativePath.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase);
                if (!allowed)
                    throw new InvalidDataException($"The update package contains an unexpected executable: {relativePath}");
            }

            string executablePath = Path.Combine(packageDirectory, "MonitorSwitcher.exe");
            ValidatePeMachine(executablePath, PeMachineAmd64);
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            if (!string.Equals(versionInfo.ProductName, "MonitorSwitcher", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update executable does not identify itself as MonitorSwitcher.");
            if (!SemanticVersion.TryParse(versionInfo.ProductVersion, out SemanticVersion packagedVersion) ||
                packagedVersion.CompareTo(expectedVersion) != 0)
            {
                throw new InvalidDataException(
                    $"The update executable version '{versionInfo.ProductVersion}' does not match release {expectedVersion}.");
            }
        }

        private static void ValidatePeMachine(string executablePath, ushort expectedMachine)
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
                throw new InvalidDataException($"{Path.GetFileName(executablePath)} is not a valid Windows PE file.");

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 64 || peOffset > stream.Length - 6)
                throw new InvalidDataException($"{Path.GetFileName(executablePath)} has an invalid PE header.");

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != expectedMachine)
                throw new InvalidDataException($"{Path.GetFileName(executablePath)} is not the expected x64 Windows executable.");
        }

        private static string GetUniqueDirectoryPath(string preferredPath)
        {
            if (!Directory.Exists(preferredPath) && !File.Exists(preferredPath))
                return preferredPath;

            for (int suffix = 2; suffix <= 999; suffix++)
            {
                string candidate = $"{preferredPath}-{suffix}";
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }

            return $"{preferredPath}-{Guid.NewGuid():N}";
        }

        private static void OpenExplorerAt(string path)
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup. Staging directories never replace the live app.
            }
        }

        internal static int? CompareSemanticVersions(string? left, string? right)
        {
            if (!SemanticVersion.TryParse(left, out SemanticVersion leftVersion) ||
                !SemanticVersion.TryParse(right, out SemanticVersion rightVersion))
            {
                return null;
            }

            return Math.Sign(leftVersion.CompareTo(rightVersion));
        }

        internal readonly struct SemanticVersion : IComparable<SemanticVersion>
        {
            private readonly int _major;
            private readonly int _minor;
            private readonly int _patch;
            private readonly string[] _preRelease;
            private readonly bool _hasBuildMetadata;

            private SemanticVersion(
                int major,
                int minor,
                int patch,
                string[] preRelease,
                bool hasBuildMetadata)
            {
                _major = major;
                _minor = minor;
                _patch = patch;
                _preRelease = preRelease;
                _hasBuildMetadata = hasBuildMetadata;
            }

            public static bool TryParseReleaseTag(string? value, out SemanticVersion version)
            {
                version = default;
                if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('v'))
                    return false;

                if (!TryParse(value, out SemanticVersion parsed) ||
                    parsed._preRelease.Length != 0 ||
                    parsed._hasBuildMetadata ||
                    !value.Equals($"v{parsed}", StringComparison.Ordinal))
                {
                    return false;
                }

                version = parsed;
                return true;
            }

            public static bool TryParse(string? value, out SemanticVersion version)
            {
                version = default;
                if (string.IsNullOrWhiteSpace(value) || !value.Equals(value.Trim(), StringComparison.Ordinal))
                    return false;

                string text = value;
                if (text.StartsWith('v'))
                    text = text[1..];

                int plusIndex = text.IndexOf('+');
                string? build = null;
                if (plusIndex >= 0)
                {
                    if (text.IndexOf('+', plusIndex + 1) >= 0)
                        return false;
                    build = text[(plusIndex + 1)..];
                    text = text[..plusIndex];
                }

                int dashIndex = text.IndexOf('-');
                string? preRelease = null;
                if (dashIndex >= 0)
                {
                    preRelease = text[(dashIndex + 1)..];
                    text = text[..dashIndex];
                }

                string[] core = text.Split('.');
                if (core.Length != 3 ||
                    !TryParseCoreNumber(core[0], out int major) ||
                    !TryParseCoreNumber(core[1], out int minor) ||
                    !TryParseCoreNumber(core[2], out int patch))
                {
                    return false;
                }

                string[] preReleaseParts = string.IsNullOrEmpty(preRelease)
                    ? Array.Empty<string>()
                    : preRelease.Split('.');
                if (preRelease != null &&
                    (preReleaseParts.Any(part => !IsValidIdentifier(part)) ||
                     preReleaseParts.Any(part => IsNumericIdentifier(part) && part.Length > 1 && part[0] == '0')))
                {
                    return false;
                }

                if (build != null && build.Split('.').Any(part => !IsValidIdentifier(part)))
                    return false;

                version = new SemanticVersion(major, minor, patch, preReleaseParts, build != null);
                return true;
            }

            public int CompareTo(SemanticVersion other)
            {
                int comparison = _major.CompareTo(other._major);
                if (comparison != 0)
                    return comparison;
                comparison = _minor.CompareTo(other._minor);
                if (comparison != 0)
                    return comparison;
                comparison = _patch.CompareTo(other._patch);
                if (comparison != 0)
                    return comparison;

                string[] leftPreRelease = _preRelease ?? Array.Empty<string>();
                string[] rightPreRelease = other._preRelease ?? Array.Empty<string>();
                if (leftPreRelease.Length == 0 || rightPreRelease.Length == 0)
                    return leftPreRelease.Length == rightPreRelease.Length ? 0 : leftPreRelease.Length == 0 ? 1 : -1;

                int commonLength = Math.Min(leftPreRelease.Length, rightPreRelease.Length);
                for (int index = 0; index < commonLength; index++)
                {
                    comparison = CompareIdentifier(leftPreRelease[index], rightPreRelease[index]);
                    if (comparison != 0)
                        return comparison;
                }

                return leftPreRelease.Length.CompareTo(rightPreRelease.Length);
            }

            public override string ToString()
            {
                string core = $"{_major}.{_minor}.{_patch}";
                return (_preRelease?.Length ?? 0) == 0
                    ? core
                    : $"{core}-{string.Join('.', _preRelease ?? Array.Empty<string>())}";
            }

            private static bool TryParseCoreNumber(string value, out int number)
            {
                number = 0;
                return IsNumericIdentifier(value) &&
                       (value.Length == 1 || value[0] != '0') &&
                       int.TryParse(value, out number);
            }

            private static bool IsValidIdentifier(string value)
                => value.Length > 0 && value.All(character =>
                    (character >= '0' && character <= '9') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    character == '-');

            private static bool IsNumericIdentifier(string value)
                => value.Length > 0 && value.All(character => character >= '0' && character <= '9');

            private static int CompareIdentifier(string left, string right)
            {
                bool leftNumeric = IsNumericIdentifier(left);
                bool rightNumeric = IsNumericIdentifier(right);
                if (leftNumeric != rightNumeric)
                    return leftNumeric ? -1 : 1;
                if (!leftNumeric)
                    return string.Compare(left, right, StringComparison.Ordinal);

                int lengthComparison = left.Length.CompareTo(right.Length);
                return lengthComparison != 0
                    ? lengthComparison
                    : string.Compare(left, right, StringComparison.Ordinal);
            }
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = string.Empty;

            [JsonPropertyName("draft")]
            public bool Draft { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("assets")]
            public List<GitHubReleaseAsset> Assets { get; set; } = new();
        }

        private sealed class GitHubReleaseAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("browser_download_url")]
            public string DownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("digest")]
            public string? Digest { get; set; }
        }

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
            var palette = _chkDark.Checked ? ThemePalette.Dark() : ThemePalette.Light();
            ApplyTitleBarTheme(palette, _chkDark.Checked);
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
            const int boxSize = 14;
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

            using var checkPen = new Pen(checkColor, 2f);
            checkPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            checkPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            graphics.DrawLines(checkPen, new[]
            {
                new Point(boxBounds.Left + 3, boxBounds.Top + 7),
                new Point(boxBounds.Left + 6, boxBounds.Top + 10),
                new Point(boxBounds.Left + 11, boxBounds.Top + 4)
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
