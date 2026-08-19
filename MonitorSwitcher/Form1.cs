#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

using WorkMonitorSwitcher.Model;
using WorkMonitorSwitcher.Services;
using WorkMonitorSwitcher.UI;
using System.Threading.Tasks;
using static WorkMonitorSwitcher.Services.MonitorTargetResolver;


namespace WorkMonitorSwitcher
{
    public partial class Form1 : Form
    {
        // ---- Layout paths ----
        private readonly string _bundledLayoutPath = Path.Combine(AppContext.BaseDirectory, "monitor-layout.cfg");
        private readonly string _layoutPath;

        // Per-user app data dir
        private readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WorkMonitorSwitcher");

        // Stores (JSON) and services
        private readonly UiSettingsStore _uiStore;
        private readonly AliasStore _aliasStore;
        private readonly LayoutProfileStore _profileStore;
        private readonly DiagnosticsLog _log;
        private readonly DetectionService _detectSvc;
        private readonly LayoutService _layoutSvc;
        private readonly DisplayTopologyService _topologySvc;

        // In-memory state
        private UiSettings _uiSettings;
        private bool _startupEnabledForCurrentExecutable;
        private bool _profileTransactionsHealthy = true;
        private readonly Dictionary<string, MonitorInfo> _aliasMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MonitorControls> _controlsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Control> _dynamicControls = new();
        private readonly List<string> _monitorCardOrder = new();
        private readonly List<string> _monitorCardOrderBeforeDrag = new();
        private List<DetectedMonitor> _detected = new();
        private string _lastDetectionLogSignature = string.Empty;
        private bool _displayedDetectionUsedScreenFallback;
        private string _displayedDetectionWarning = string.Empty;
        private readonly ReconnectLayoutRestoreTracker _reconnectRestoreTracker = new();
        private ReconnectLayoutRestoreRequest? _pendingReconnectLayoutRestore;
        private long _reconnectDisplayEventGeneration;
        private long _consumedReconnectDisplayEventGeneration;
        private long _reconnectRestoreCancellationGeneration;
        private long _reconnectDetectionRetryGeneration;
        private long _monitorRemovalGeneration;
        private long _monitorArrivalAfterRemovalGeneration;
        private long _consumedMonitorArrivalAfterRemovalGeneration;
        private bool _monitorRemovalAwaitingArrival;
        private int _reconnectDetectionRetryCount;
        private IntPtr _monitorDeviceNotificationHandle;

        // Debounced refresh on display changes
        private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 800 };
        private readonly System.Windows.Forms.Timer _monitorCardAnimationTimer = new() { Interval = 15 };
        private string? _draggedMonitorKey;
        private Control? _monitorCardDragCapture;
        private Point _monitorCardDragStartScreen;
        private Point _monitorCardLastPointerScreen;
        private int _monitorCardDragPointerOffsetY;
        private bool _monitorCardDragActive;
        private bool _endingMonitorCardDrag;

        // ---- Layout constants / handles ----
        private const int SideMargin = 14;
        private const int ControlGapX = 10;
        private const int ButtonWidth = 108;
        private const int ButtonHeight = 30;
        private const int RowPanelWidth = 390;
        private const int RowPanelHeight = 82;
        private const int RowVerticalGap = 92;
        private const int TopButtonsY = 12;
        private const int FirstRowY = 68; // fallback start if no HR yet

        private Button? _btnSettings;
        private Button? _btnRefresh;
        private Button? _btnSaveLayout;
        private Button? _btnRestoreLayout;
        private Label? _sectionTitleLabel;
        private Label? _summaryLabel;
        private Panel? _hrTop;
        private Panel? _missingToolPanel;
        private Label? _missingToolLabel;
        private Button? _missingToolSettingsButton;
        private NotifyIcon? _trayIcon;
        private ContextMenuStrip? _trayMenu;
        private readonly ToolTip _toolTip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 8000 };

        private int _layoutRightMost;

        // Tracks rows currently executing a tool command
        private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _displayActionGate = new(1, 1);
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private bool _exitRequested;
        private bool _restoringFromTray;
        private bool _refreshAgainAfterCurrent;
        private bool _refreshAfterDisplayAction;
        private bool _reloadAliasesAfterDisplayAction;
        private Task _refreshTask = Task.CompletedTask;
        private System.Windows.Forms.Timer? _restoreRefreshTimer;
        private System.Windows.Forms.Timer? _startupRestoreTimer;
        private System.Windows.Forms.Timer? _reconnectRestoreTimer;
        private System.Windows.Forms.Timer? _reconnectDetectionRetryTimer;
        private EventWaitHandle? _showExistingWindowEvent;
        private RegisteredWaitHandle? _showExistingWindowWait;

        // Restore robustness
        private bool _deferredLayout; // schedule a full rebuild after restore/show
        private bool IsNormalVisible => Visible && WindowState == FormWindowState.Normal;

        private sealed class PrimaryDisableAttempt
        {
            public bool Success { get; init; }
            public string ErrorMessage { get; init; } = string.Empty;
        }

        private sealed record ReconnectLayoutRestoreRequest(
            string ProfileName,
            string LayoutPath,
            string ProfileIdentity,
            long CancellationGeneration,
            long PhysicalReconnectGeneration = 0,
            int TopologyAttempt = 1);

        private enum MonitorActivityState
        {
            Active,
            Inactive,
            Inconclusive
        }

        private sealed record MonitorActivityVerification(
            MonitorActivityState State,
            List<DetectedMonitor> Monitors,
            string Detail);

        private const int WmDisplayChange = 0x007E;
        private const int WmDeviceChange = 0x0219;
        private const int WmSettingChange = 0x001A;
        private const int DbtDeviceArrival = 0x8000;
        private const int DbtDeviceRemoveComplete = 0x8004;
        private const int DbtDevTypeDeviceInterface = 0x00000005;
        private const uint DeviceNotifyWindowHandle = 0x00000000;
        private static readonly Guid MonitorDeviceInterfaceClassGuid =
            new("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceBroadcastHeader
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DeviceBroadcastInterface
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
            public Guid ClassGuid;
            public char Name;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterDeviceNotification(
            IntPtr recipient,
            ref DeviceBroadcastInterface notificationFilter,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        public Form1()
        {
            // Reduce flicker
            SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,  // repaint background on resize
            true);

            UpdateStyles();

            InitializeComponent();

            Text = "Monitor Switcher";
            AutoSize = false;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            AutoScroll = true;
            MinimumSize = new Size(420, 260); // avoids restore-to-titlebar

            Directory.CreateDirectory(_appDataDir);
            _layoutPath = Path.Combine(_appDataDir, "monitor-layout.cfg");

            _uiStore = new UiSettingsStore(_appDataDir);
            _aliasStore = new AliasStore(_appDataDir);
            _profileStore = new LayoutProfileStore(_appDataDir, _layoutPath);
            _log = new DiagnosticsLog(_appDataDir);
            var transactionRecovery = _profileStore.RecoverProfileTransactionsWithResult();
            _profileTransactionsHealthy = transactionRecovery.Success;
            if (!transactionRecovery.Success)
            {
                _log.Write(
                    $"Layout profile transaction recovery failed; automatic layout restores are disabled: {transactionRecovery.ErrorMessage}");
            }
            else if (!string.IsNullOrWhiteSpace(transactionRecovery.WarningMessage))
            {
                _log.Write($"Layout profile transaction recovery warning: {transactionRecovery.WarningMessage}");
            }
            if (_profileTransactionsHealthy)
                EnsureUserLayoutPath();
            _detectSvc = new DetectionService(_log.Write);
            _layoutSvc = new LayoutService(_log.Write);
            _topologySvc = new DisplayTopologyService();

            _uiSettings = _uiStore.LoadOrDefault();
            _startupEnabledForCurrentExecutable = StartupManager.IsEnabled();
            if (_uiSettings.StartWithWindows != _startupEnabledForCurrentExecutable)
            {
                _log.Write(
                    "Observed Windows startup state for this executable without changing the shared settings or Run entry.");
            }
            var aliases = _aliasStore.Load();
            foreach (var kv in aliases) _aliasMap[kv.Key] = kv.Value;

            // Top buttons
            _btnSettings = new ThemedButton { Text = "Settings", Size = new Size(92, 32) };
            _btnSettings.Click += SettingsButton_Click;
            Controls.Add(_btnSettings);
            _toolTip.SetToolTip(_btnSettings, "Edit aliases, primary monitor, theme, and app tools.");

            _btnRefresh = new ThemedButton { Text = "Refresh", Size = new Size(88, 32) };
            _btnRefresh.Click += async (_, __) => await ManualRefreshMonitorsAndUiAsync();
            Controls.Add(_btnRefresh);
            _toolTip.SetToolTip(_btnRefresh, "Reload aliases and detect monitors again.");

            _btnSaveLayout = new ThemedButton { Text = "Save", Size = new Size(74, 32) };
            _btnSaveLayout.Click += async (_, __) => await SaveSelectedLayoutProfileAsync();
            Controls.Add(_btnSaveLayout);
            _toolTip.SetToolTip(_btnSaveLayout, "Save the current monitor layout to a named profile.");

            _btnRestoreLayout = new ThemedButton
            {
                Text = "Restore",
                Size = new Size(82, 32),
                Tone = ThemedButtonTone.Primary
            };
            _btnRestoreLayout.Click += async (_, __) => await RestoreSelectedLayoutProfileAsync(showMessage: true);
            Controls.Add(_btnRestoreLayout);
            _toolTip.SetToolTip(_btnRestoreLayout, "Restore the selected saved monitor layout.");

            _sectionTitleLabel = new Label
            {
                AutoSize = true,
                Text = "Monitors",
                Font = new Font("Segoe UI Semibold", 11.25f, FontStyle.Regular)
            };
            Controls.Add(_sectionTitleLabel);

            _summaryLabel = new Label
            {
                AutoSize = false,
                Text = "Detecting monitors...",
                Size = new Size(230, 22),
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Regular)
            };
            Controls.Add(_summaryLabel);

            _missingToolPanel = new ThemedCardPanel
            {
                Height = 52,
                Visible = false
            };
            _missingToolLabel = new Label
            {
                AutoSize = false,
                Text = "Windows monitor details could not be read. Detection is limited to active displays.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            _missingToolSettingsButton = new ThemedButton { Text = "Details", Size = new Size(82, 30) };
            _missingToolSettingsButton.Click += SettingsButton_Click;
            _missingToolPanel.Controls.Add(_missingToolLabel);
            _missingToolPanel.Controls.Add(_missingToolSettingsButton);
            Controls.Add(_missingToolPanel);

            SetupTrayIcon();
            SetupSingleInstanceRestoreSignal();

            _hrTop = new Panel { Height = 1, BackColor = SystemColors.ControlDark, Visible = true };
            Controls.Add(_hrTop);

            SizeChanged += (_, __) => LeftTopButtons();
            Layout += (_, __) => LeftTopButtons();

            // Apply theme and caption styling
            ApplyTheme(_uiSettings.DarkMode);

            // Honor persisted "always on top" (reinforced again in OnShown)
            TopMost = _uiSettings.AlwaysOnTop;

            // Restore window position/size (safe-guarded)
            RestoreWindowBounds();

            _refreshTimer.Tick += async (_, __) =>
            {
                _refreshTimer.Stop();
                await RefreshMonitorsAndUiAsync();
            };
            _monitorCardAnimationTimer.Tick += (_, __) => AnimateMonitorCards();

            LeftTopButtons();
            UpdateTopSeparator();

            RefreshMonitorsAndUi();

            FormClosing += (_, e) =>
            {
                if (!_exitRequested && _uiSettings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }

                SaveWindowBounds();
                LogPersistenceResult("save UI settings while closing", _uiStore.SaveWithResult(_uiSettings));
            };
        }

        // Re-assert TopMost and complete any deferred layout when first shown
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            BeginInvoke(new Action(() =>
            {
                try { TopMost = _uiSettings.AlwaysOnTop; } catch { /* ignore */ }

                if (_deferredLayout)
                {
                    _deferredLayout = false;
                    RefreshMonitorsAndUi();
                    LeftTopButtons();
                }

                QueueStartupLayoutRestore();
            }));
        }

        // Finish deferred layout on restore from minimized
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);

            if (!_restoringFromTray && _uiSettings != null && _uiSettings.MinimizeToTray && WindowState == FormWindowState.Minimized && Visible)
            {
                BeginInvoke(new Action(HideToTray));
                return;
            }

            if (IsNormalVisible && _deferredLayout)
            {
                _deferredLayout = false;
                RefreshMonitorsAndUi();
                LeftTopButtons();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _lifetimeCancellation.Cancel();
            _showExistingWindowWait?.Unregister(null);
            _showExistingWindowEvent?.Dispose();
            _restoreRefreshTimer?.Stop();
            _restoreRefreshTimer?.Dispose();
            _startupRestoreTimer?.Stop();
            _startupRestoreTimer?.Dispose();
            _reconnectRestoreTimer?.Stop();
            _reconnectRestoreTimer?.Dispose();
            _reconnectDetectionRetryTimer?.Stop();
            _reconnectDetectionRetryTimer?.Dispose();
            _monitorCardAnimationTimer.Stop();
            _monitorCardAnimationTimer.Dispose();
            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterForMonitorInterfaceNotifications();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterMonitorInterfaceNotifications();
            base.OnHandleDestroyed(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Explicitly clear the entire client area to our BackColor to avoid dark-mode “black box” artifacts
            e.Graphics.Clear(this.BackColor);
        }

        // React to display topology changes
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SingleInstanceMessenger.ShowExistingWindowMessage)
            {
                ShowMainWindowForInteraction();
                return;
            }

            if (m.Msg == WmDisplayChange || m.Msg == WmDeviceChange || m.Msg == WmSettingChange)
            {
                if (m.Msg == WmDisplayChange)
                    AdvanceReconnectEventGeneration("display topology change");

                if (m.Msg == WmDeviceChange &&
                    TryReadMonitorInterfaceNotification(
                        m.WParam,
                        m.LParam,
                        out var isArrival,
                        out var isRemoval))
                {
                    RecordMonitorInterfaceNotification(isArrival, isRemoval);
                }

                _refreshTimer.Stop();
                _refreshTimer.Start();
            }
            base.WndProc(ref m);
        }

        private void RegisterForMonitorInterfaceNotifications()
        {
            if (_monitorDeviceNotificationHandle != IntPtr.Zero ||
                !IsHandleCreated ||
                IsDisposed)
            {
                return;
            }

            var filter = new DeviceBroadcastInterface
            {
                Size = Marshal.SizeOf<DeviceBroadcastInterface>(),
                DeviceType = DbtDevTypeDeviceInterface,
                ClassGuid = MonitorDeviceInterfaceClassGuid
            };
            _monitorDeviceNotificationHandle = RegisterDeviceNotification(
                Handle,
                ref filter,
                DeviceNotifyWindowHandle);

            if (_monitorDeviceNotificationHandle == IntPtr.Zero && _log != null)
            {
                _log.Write(
                    $"Unable to register for physical monitor interface notifications: Windows error {Marshal.GetLastWin32Error()}.");
            }
        }

        private void UnregisterMonitorInterfaceNotifications()
        {
            var notificationHandle = _monitorDeviceNotificationHandle;
            _monitorDeviceNotificationHandle = IntPtr.Zero;
            if (notificationHandle == IntPtr.Zero)
                return;

            if (!UnregisterDeviceNotification(notificationHandle) && _log != null)
            {
                _log.Write(
                    $"Unable to unregister physical monitor interface notifications: Windows error {Marshal.GetLastWin32Error()}.");
            }
        }

        private static bool TryReadMonitorInterfaceNotification(
            IntPtr eventCode,
            IntPtr eventData,
            out bool isArrival,
            out bool isRemoval)
        {
            isArrival = false;
            isRemoval = false;
            if (eventData == IntPtr.Zero)
                return false;

            int code;
            try
            {
                code = checked((int)eventCode.ToInt64());
            }
            catch (OverflowException)
            {
                return false;
            }

            if (code != DbtDeviceArrival && code != DbtDeviceRemoveComplete)
                return false;

            try
            {
                var header = Marshal.PtrToStructure<DeviceBroadcastHeader>(eventData);
                if (header.DeviceType != DbtDevTypeDeviceInterface ||
                    header.Size < Marshal.SizeOf<DeviceBroadcastInterface>())
                {
                    return false;
                }

                var deviceInterface = Marshal.PtrToStructure<DeviceBroadcastInterface>(eventData);
                if (deviceInterface.ClassGuid != MonitorDeviceInterfaceClassGuid)
                    return false;

                isArrival = code == DbtDeviceArrival;
                isRemoval = code == DbtDeviceRemoveComplete;
                return true;
            }
            catch
            {
                // Malformed native notification data is not reconnect evidence.
                return false;
            }
        }

        private void RecordMonitorInterfaceNotification(bool isArrival, bool isRemoval)
        {
            if (!isArrival && !isRemoval)
                return;

            if (isRemoval)
            {
                _monitorRemovalGeneration++;
                _monitorRemovalAwaitingArrival = true;
                AdvanceReconnectEventGeneration("physical monitor removal");
            }

            if (isArrival)
            {
                if (_monitorRemovalAwaitingArrival)
                {
                    _monitorArrivalAfterRemovalGeneration = _monitorRemovalGeneration;
                    _monitorRemovalAwaitingArrival = false;
                    AdvanceReconnectEventGeneration("physical monitor arrival after removal");
                }
            }
        }

        private void AdvanceReconnectEventGeneration(string reason)
        {
            _reconnectDisplayEventGeneration++;
            _reconnectDetectionRetryGeneration = _reconnectDisplayEventGeneration;
            _reconnectDetectionRetryCount = 0;
            _reconnectDetectionRetryTimer?.Stop();
            _log?.Write($"Recorded {reason} as reconnect evidence generation {_reconnectDisplayEventGeneration}.");
        }

        // ---------- Guardrails ----------
        // Which Windows.Forms.Screen is currently hosting this window?
        private Screen GetHostScreen()
        {
            try { return Screen.FromRectangle(this.Bounds); }
            catch { return Screen.PrimaryScreen ?? Screen.AllScreens.First(); }
        }

        // Try to get a live device name (\\.\DISPLAYn) for a given stable key.
        // Returns null if not currently present.
        private string? TryGetDeviceNameForStableKey(string stableKey)
        {
            var live = _detected.FirstOrDefault(d =>
                d.IsPresent &&
                d.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(live?.DeviceName) ? null : live!.DeviceName;
        }

        // From a device name (\\.\DISPLAYn), get the corresponding Screen (if any)
        private Screen? TryGetScreenForDevice(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return null;
            return Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        }

        // Choose a safe fallback active screen to host the window,
        // excluding the screen identified by excludeDevice (the one we’re about to disable).
        // Prefers the configured fallback primary, then the left-most active screen.
        private Screen GetFallbackActiveScreen(string? excludeDevice)
        {
            var configuredFallback = PrimaryMonitorPreference.ResolveConfiguredFallbackDeviceName(
                excludeDevice,
                _detected,
                _aliasMap);
            if (!string.IsNullOrWhiteSpace(configuredFallback))
            {
                var configuredScreen = TryGetScreenForDevice(configuredFallback);
                if (configuredScreen != null)
                    return configuredScreen;
            }

            foreach (var dev in PrimaryMonitorPreference.ResolveAutomaticFallbackDeviceNames(excludeDevice, _detected))
            {
                var scr = TryGetScreenForDevice(dev);
                if (scr != null) return scr;
            }

            var primary = Screen.PrimaryScreen;
            if (primary != null &&
                !string.Equals(primary.DeviceName, excludeDevice, StringComparison.OrdinalIgnoreCase))
                return primary;

            var any = Screen.AllScreens.FirstOrDefault(s =>
                !string.Equals(s.DeviceName, excludeDevice, StringComparison.OrdinalIgnoreCase));
            return any ?? (primary ?? Screen.AllScreens.First());
        }
        // Center this window on the given screen's working area.
        // Temporarily drops TopMost to avoid z-order oddities while moving.
        private void RehomeWindowToScreen(Screen target)
        {
            var wa = target.WorkingArea;

            // Keep a sensible minimum size; clamp to the target work area.
            int w = Math.Min(wa.Width, Math.Max(420, this.Width));
            int h = Math.Min(wa.Height, Math.Max(260, this.Height));

            int x = wa.Left + Math.Max(0, (wa.Width - w) / 2);
            int y = wa.Top + Math.Max(0, (wa.Height - h) / 2);

            var prevTopMost = this.TopMost;
            try
            {
                this.TopMost = false;                 // prevent flicker/z-fight while moving
                this.StartPosition = FormStartPosition.Manual;
                this.Bounds = new Rectangle(x, y, w, h);
                this.Activate();                      // bring to front on its new screen
            }
            finally
            {
                this.TopMost = prevTopMost;          // restore user's preference
            }
        }

        // If this window currently lives on the screen identified by 'disablingDevice',
        // move it to a safe, still-active screen before we disable that display.
        private void MoveWindowIfHostedOn(string? disablingDevice)
        {
            if (string.IsNullOrWhiteSpace(disablingDevice))
                return;

            var host = GetHostScreen();
            if (!string.Equals(host.DeviceName, disablingDevice, StringComparison.OrdinalIgnoreCase))
                return; // we're not on the soon-to-be-disabled display

            var fallback = GetFallbackActiveScreen(disablingDevice);
            RehomeWindowToScreen(fallback);
        }

        // ---------- Top layout helpers ----------

        private void LeftTopButtons()
        {
            if (_btnSettings == null || _btnRefresh == null || _btnSaveLayout == null || _btnRestoreLayout == null) return;

            // Don't lay out while minimized; mark that we owe a layout later.
            if (!IsNormalVisible) { _deferredLayout = true; return; }
            if (AutoScrollPosition.X != 0 || AutoScrollPosition.Y != 0) return;

            const int spacing = 18;
            int x = SideMargin;

            _btnSettings.Location = new Point(x, TopButtonsY);
            x = _btnSettings.Right + spacing;

            _btnRefresh.Location = new Point(x, TopButtonsY);
            x = _btnRefresh.Right + spacing;

            _btnSaveLayout.Location = new Point(x, TopButtonsY);
            x = _btnSaveLayout.Right + spacing;

            _btnRestoreLayout.Location = new Point(x, TopButtonsY);
            x = _btnRestoreLayout.Right + spacing;

            _btnSettings.BringToFront();
            _btnRefresh.BringToFront();
            _btnSaveLayout.BringToFront();
            _btnRestoreLayout.BringToFront();

            UpdateTopSeparator();
        }

        private int GetCompactClientWidth()
        {
            int contentRight = SideMargin + RowPanelWidth;
            int toolbarRight = (_btnRestoreLayout?.Right ?? _btnSaveLayout?.Right ?? 0);
            return Math.Max(contentRight + SideMargin, toolbarRight + SideMargin);
        }

        private int GetSeparatorY()
        {
            int buttonHeight = _btnSettings?.Height ?? 30;
            return TopButtonsY + buttonHeight + 10;
        }

        private void UpdateTopSeparator()
        {
            if (_hrTop == null) return;
            int y = GetSeparatorY();
            int contentWidth = Math.Min(ClientSize.Width, GetCompactClientWidth());
            _hrTop.Location = new Point(SideMargin, y);
            _hrTop.Width = Math.Max(0, contentWidth - (SideMargin * 2));
            _hrTop.Height = 1;
            // color is updated by ApplyTheme

            UpdateMissingToolPanel();
            UpdateSectionHeaderLayout();
        }

        private void UpdateMissingToolPanel()
        {
            if (_missingToolPanel == null || _missingToolLabel == null || _missingToolSettingsButton == null)
                return;

            bool degraded = _displayedDetectionUsedScreenFallback;
            _missingToolPanel.Visible = degraded;
            if (!degraded) return;

            _missingToolLabel.Text = "Monitor detection is limited.\nActive displays only; see Diagnostics.";
            _toolTip.SetToolTip(_missingToolLabel, _displayedDetectionWarning);

            int y = GetSeparatorY() + 8;
            int contentWidth = Math.Min(ClientSize.Width, GetCompactClientWidth());
            _missingToolPanel.Location = new Point(SideMargin, y);
            _missingToolPanel.Width = Math.Max(0, contentWidth - (SideMargin * 2));

            _missingToolSettingsButton.Location = new Point(
                _missingToolPanel.Width - _missingToolSettingsButton.Width - 10,
                11);
            _missingToolLabel.Location = new Point(12, 6);
            _missingToolLabel.Size = new Size(Math.Max(0, _missingToolSettingsButton.Left - 20), 40);
            _missingToolPanel.BringToFront();
        }

        private void UpdateSectionHeaderLayout()
        {
            if (_sectionTitleLabel == null || _summaryLabel == null)
                return;

            int y = GetSeparatorY() + (_hrTop?.Height ?? 1) + 12;
            if (_missingToolPanel?.Visible == true)
                y = _missingToolPanel.Bottom + 12;

            _sectionTitleLabel.Location = new Point(SideMargin, y);
            _summaryLabel.Location = new Point(
                SideMargin + RowPanelWidth - _summaryLabel.Width,
                y + 1);
            _sectionTitleLabel.BringToFront();
            _summaryLabel.BringToFront();
        }

        private int GetRowsStartY()
        {
            UpdateSectionHeaderLayout();
            int headerBottom = Math.Max(
                _sectionTitleLabel?.Bottom ?? FirstRowY,
                _summaryLabel?.Bottom ?? FirstRowY);
            return Math.Max(FirstRowY, headerBottom + 10);
        }

        private void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Open Monitor Switcher", null, (_, __) => RestoreFromTray());
            _trayMenu.Items.Add("Refresh", null, async (_, __) => await ManualRefreshMonitorsAndUiAsync());
            _trayMenu.Items.Add("Save Layout", null, async (_, __) =>
            {
                ShowMainWindowForInteraction();
                await SaveSelectedLayoutProfileAsync();
            });
            _trayMenu.Items.Add("Restore Layout", null, async (_, __) =>
            {
                ShowMainWindowForInteraction();
                await RestoreSelectedLayoutProfileAsync(showMessage: true);
            });
            _trayMenu.Items.Add("Settings", null, (sender, e) =>
            {
                ShowMainWindowForInteraction();
                SettingsButton_Click(sender, e);
            });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Exit", null, (_, __) =>
            {
                _exitRequested = true;
                Close();
            });

            Icon trayIcon;
            try
            {
                trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch
            {
                trayIcon = SystemIcons.Application;
            }

            _trayIcon = new NotifyIcon
            {
                Text = "Monitor Switcher",
                ContextMenuStrip = _trayMenu,
                Icon = trayIcon,
                Visible = true
            };

            _trayIcon.DoubleClick += (_, __) => RestoreFromTray();
        }

        private void SetupSingleInstanceRestoreSignal()
        {
            try
            {
                _showExistingWindowEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: SingleInstanceMessenger.ShowExistingWindowEventName);

                _showExistingWindowWait = ThreadPool.RegisterWaitForSingleObject(
                    _showExistingWindowEvent,
                    (_, __) =>
                    {
                        if (IsDisposed) return;
                        try { BeginInvoke(new Action(ShowMainWindowForInteraction)); }
                        catch { /* ignore shutdown races */ }
                    },
                    state: null,
                    millisecondsTimeOutInterval: -1,
                    executeOnlyOnce: false);
            }
            catch
            {
                // The window-message fallback still handles normal visible-window activation.
            }
        }

        private void HideToTray()
        {
            SaveWindowBounds();
            LogPersistenceResult("save UI settings while hiding", _uiStore.SaveWithResult(_uiSettings));
            ShowInTaskbar = false;
            Hide();
            if (_trayIcon != null)
                _trayIcon.ShowBalloonTip(2000, "Monitor Switcher", "Still running in the notification area.", ToolTipIcon.Info);
        }

        private void ShowMainWindowForInteraction()
        {
            if (!Visible || WindowState == FormWindowState.Minimized || !ShowInTaskbar)
                RestoreFromTray();
            else
                Activate();
        }

        private void RestoreFromTray()
        {
            _restoringFromTray = true;
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Show();
            Activate();

            QueueRestoreRefresh();
        }

        private void QueueRestoreRefresh()
        {
            _restoreRefreshTimer?.Stop();
            _restoreRefreshTimer?.Dispose();

            _restoreRefreshTimer = new System.Windows.Forms.Timer { Interval = 350 };
            _restoreRefreshTimer.Tick += (_, __) =>
            {
                _restoreRefreshTimer?.Stop();
                _restoreRefreshTimer?.Dispose();
                _restoreRefreshTimer = null;

                CompleteRestoreFromTray();
            };
            _restoreRefreshTimer.Start();
        }

        private void CompleteRestoreFromTray()
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        WindowState = FormWindowState.Normal;
                        _deferredLayout = false;
                        RefreshMonitorsAndUi();
                        LeftTopButtons();
                        EnsureDynamicRowsVisible();
                        Invalidate(true);
                        Update();
                    }
                    finally
                    {
                        _restoringFromTray = false;
                    }
                }));
            }
            catch
            {
                _restoringFromTray = false;
            }
        }

        private string SelectedLayoutProfileName()
            => LayoutProfileStore.NormalizeProfileName(_uiSettings.SelectedLayoutProfile);

        private string SelectedLayoutPath()
            => _profileStore.GetLayoutPath(SelectedLayoutProfileName());

        private void QueueStartupLayoutRestore()
        {
            if (!_uiSettings.RestoreLayoutOnStartup || !_profileTransactionsHealthy)
                return;

            if (_startupRestoreTimer != null)
                return;

            _startupRestoreTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _startupRestoreTimer.Tick += async (_, __) =>
            {
                _startupRestoreTimer?.Stop();
                _startupRestoreTimer?.Dispose();
                _startupRestoreTimer = null;

                await RestoreLayoutOnStartupAsync();
            };
            _startupRestoreTimer.Start();
        }

        private async Task RestoreLayoutOnStartupAsync()
        {
            if (!_uiSettings.RestoreLayoutOnStartup || !_profileTransactionsHealthy)
                return;

            var profile = SelectedLayoutProfileName();
            var path = SelectedLayoutPath();
            if (!File.Exists(path))
            {
                _log.Write($"Skipped startup layout restore for profile '{profile}' because the layout file was not found: {path}.");
                return;
            }

            if (_displayActionGate.CurrentCount == 0)
            {
                _log.Write($"Deferred startup layout restore for profile '{profile}' because another display action is already running.");
                QueueStartupLayoutRestore();
                return;
            }

            _log.Write($"Startup layout restore requested for profile '{profile}' at {path}.");
            await RestoreSelectedLayoutProfileAsync(showMessage: false, isStartupRestore: true);
        }

        private void CancelQueuedStartupLayoutRestore(string actionName)
        {
            var timer = _startupRestoreTimer;
            if (timer == null)
                return;

            _startupRestoreTimer = null;
            timer.Stop();
            timer.Dispose();
            _log.Write($"Cancelled queued startup layout restore because another layout-affecting action started: {actionName}.");
        }

        private void CancelQueuedReconnectLayoutRestore(string manualAction)
        {
            _reconnectRestoreCancellationGeneration++;
            _consumedReconnectDisplayEventGeneration = _reconnectDisplayEventGeneration;
            _consumedMonitorArrivalAfterRemovalGeneration = _monitorArrivalAfterRemovalGeneration;
            _reconnectDetectionRetryGeneration = _reconnectDisplayEventGeneration;
            _reconnectDetectionRetryCount = 0;
            _reconnectDetectionRetryTimer?.Stop();
            ClearQueuedReconnectLayoutRestore(
                $"Cancelled queued reconnect layout restore because another layout-affecting action started: {manualAction}.");
        }

        private void QueueReconnectDetectionRetry()
        {
            const int maxRetries = 3;
            if (_reconnectDetectionRetryGeneration != _reconnectDisplayEventGeneration)
            {
                _reconnectDetectionRetryGeneration = _reconnectDisplayEventGeneration;
                _reconnectDetectionRetryCount = 0;
                _reconnectDetectionRetryTimer?.Stop();
            }

            if (_reconnectDetectionRetryCount >= maxRetries ||
                IsDisposed ||
                _lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            _reconnectDetectionRetryCount++;
            if (_reconnectDetectionRetryTimer == null)
            {
                _reconnectDetectionRetryTimer = new System.Windows.Forms.Timer();
                _reconnectDetectionRetryTimer.Tick += async (_, __) =>
                {
                    _reconnectDetectionRetryTimer?.Stop();
                    await RefreshMonitorsAndUiAsync();
                };
            }

            _reconnectDetectionRetryTimer.Interval = 1000 * _reconnectDetectionRetryCount;
            _reconnectDetectionRetryTimer.Stop();
            _reconnectDetectionRetryTimer.Start();
            _log.Write(
                $"Queued reconnect detection retry {_reconnectDetectionRetryCount} of {maxRetries} after an inconclusive monitor snapshot.");
        }

        private void ClearQueuedReconnectLayoutRestore(string logMessage)
        {
            if (_pendingReconnectLayoutRestore == null)
                return;

            _pendingReconnectLayoutRestore = null;
            _reconnectRestoreTimer?.Stop();
            _log.Write(logMessage);
        }

        private void ObserveReconnectLayoutState(bool allowTrigger, bool detectionIsReliable)
        {
            if (!_profileTransactionsHealthy)
                return;

            if (!detectionIsReliable)
            {
                ObserveDefiniteIncompleteFallbackLayoutState();
                _log.Write("Deferred reconnect layout-state evaluation because monitor detection used the Windows Screen fallback.");
                return;
            }

            var profile = SelectedLayoutProfileName();
            var path = SelectedLayoutPath();
            var profileIdentity = BuildLayoutProfileIdentity(profile, path);
            var savedIdentities = LayoutIdentityStore.Load(path);
            var exactSavedSetActive = DisplayTopologyService.IsExactSavedMonitorSetActive(
                path,
                _detected,
                savedIdentities);
            var arrivalGeneration = _monitorArrivalAfterRemovalGeneration;
            var hasUnconsumedPhysicalReconnect = ShouldEvaluatePhysicalReconnect(
                arrivalGeneration,
                _consumedMonitorArrivalAfterRemovalGeneration);

            if (_pendingReconnectLayoutRestore != null &&
                (!exactSavedSetActive ||
                 !string.Equals(
                     _pendingReconnectLayoutRestore.ProfileIdentity,
                     profileIdentity,
                     StringComparison.OrdinalIgnoreCase)))
            {
                ClearQueuedReconnectLayoutRestore(
                    "Cancelled queued reconnect layout restore because the active monitor set or selected profile changed.");
            }

            var shouldRestore = _reconnectRestoreTracker.Observe(
                profileIdentity,
                exactSavedSetActive,
                allowTrigger);
            if (shouldRestore)
            {
                QueueReconnectLayoutRestore(
                    profile,
                    path,
                    profileIdentity,
                    hasUnconsumedPhysicalReconnect ? arrivalGeneration : 0);
                if (hasUnconsumedPhysicalReconnect)
                    _consumedMonitorArrivalAfterRemovalGeneration = arrivalGeneration;
            }
            else if (exactSavedSetActive &&
                     hasUnconsumedPhysicalReconnect)
            {
                var applied = _topologySvc.CheckSavedLayoutApplied(
                    path,
                    _detected,
                    savedIdentities);
                if (applied.State == SavedLayoutAppliedState.NotApplied)
                {
                    QueueReconnectLayoutRestore(
                        profile,
                        path,
                        profileIdentity,
                        arrivalGeneration);
                    _log.Write(
                        $"Queued safe reconnect restore for profile '{profile}' after a physical monitor removal/arrival cycle because its saved geometry was not applied.");
                }
                else
                {
                    _log.Write(
                        applied.State == SavedLayoutAppliedState.Applied
                            ? $"Consumed physical reconnect evidence for profile '{profile}' because its saved geometry was already applied."
                            : $"Skipped physical reconnect layout restore for profile '{profile}' because saved geometry verification was inconclusive: {applied.Message}");
                }

                // Do not let stale physical-event evidence turn a later manual
                // rearrangement into an automatic restore. An inconclusive check
                // is consumed safely without changing the topology.
                _consumedMonitorArrivalAfterRemovalGeneration = arrivalGeneration;
            }
            else if (hasUnconsumedPhysicalReconnect && !exactSavedSetActive)
            {
                // This arrival does not describe the selected profile's complete,
                // exact monitor set. Consume it so unrelated later layout changes
                // cannot reuse stale hardware evidence.
                _consumedMonitorArrivalAfterRemovalGeneration = arrivalGeneration;
                _log.Write(
                    $"Consumed physical reconnect evidence because the active monitor set did not exactly match profile '{profile}'.");
            }
            else if (allowTrigger &&
                     exactSavedSetActive &&
                     _pendingReconnectLayoutRestore != null)
            {
                RestartReconnectLayoutRestoreTimer();
            }
        }

        internal static bool ShouldEvaluatePhysicalReconnect(
            long arrivalAfterRemovalGeneration,
            long consumedArrivalGeneration)
            => arrivalAfterRemovalGeneration > consumedArrivalGeneration;

        private void QueueReconnectLayoutRestore(
            string profile,
            string path,
            string profileIdentity,
            long physicalReconnectGeneration = 0)
        {
            _pendingReconnectLayoutRestore = new ReconnectLayoutRestoreRequest(
                profile,
                path,
                profileIdentity,
                _reconnectRestoreCancellationGeneration,
                PhysicalReconnectGeneration: physicalReconnectGeneration,
                TopologyAttempt: 1);

            if (_reconnectRestoreTimer == null)
            {
                _reconnectRestoreTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                _reconnectRestoreTimer.Tick += async (_, __) =>
                {
                    _reconnectRestoreTimer?.Stop();
                    var request = _pendingReconnectLayoutRestore;
                    if (request != null)
                        await RestoreLayoutAfterReconnectAsync(request);
                };
            }

            RestartReconnectLayoutRestoreTimer();
            _log.Write(
                $"Queued identity-matched layout restore for profile '{profile}' after the saved monitor set became active.");
        }

        private void RestartReconnectLayoutRestoreTimer()
        {
            _reconnectRestoreTimer?.Stop();
            _reconnectRestoreTimer?.Start();
        }

        private async Task RestoreLayoutAfterReconnectAsync(ReconnectLayoutRestoreRequest request)
        {
            try
            {
                if (!IsReconnectRestoreRequestCurrent(request))
                    return;

                if (_displayActionGate.CurrentCount == 0)
                {
                    RestartReconnectLayoutRestoreTimer();
                    return;
                }

                if (!await WaitForPendingMonitorRefreshAsync())
                    return;
                if (!IsReconnectRestoreRequestCurrent(request))
                    return;

                var currentProfile = SelectedLayoutProfileName();
                var currentPath = SelectedLayoutPath();
                var currentIdentity = BuildLayoutProfileIdentity(currentProfile, currentPath);
                if (!string.Equals(request.ProfileIdentity, currentIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    ClearReconnectLayoutRestoreRequest(request);
                    _log.Write(
                        $"Skipped reconnect layout restore for profile '{request.ProfileName}' because the selected profile changed.");
                    return;
                }

                if (!TryBeginDisplayAction("automatic reconnect layout restore", showBusyMessage: false))
                {
                    RestartReconnectLayoutRestoreTimer();
                    return;
                }

                try
                {
                    CancelQueuedStartupLayoutRestore("automatic reconnect layout restore");
                    var detection = await _detectSvc.DetectWithStatusAsync(_lifetimeCancellation.Token);
                    _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                    if (!IsReconnectRestoreRequestCurrent(request))
                        return;
                    currentProfile = SelectedLayoutProfileName();
                    currentPath = SelectedLayoutPath();
                    currentIdentity = BuildLayoutProfileIdentity(currentProfile, currentPath);
                    if (!string.Equals(request.ProfileIdentity, currentIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        ClearReconnectLayoutRestoreRequest(request);
                        _log.Write(
                            $"Skipped reconnect layout restore for profile '{request.ProfileName}' because the selected profile changed while detection was running.");
                        return;
                    }

                    if (detection.UsedScreenFallback)
                    {
                        _log.Write(
                            $"Reconnect layout restore for profile '{currentProfile}' stopped because monitor detection remained in Windows Screen fallback mode.");
                        ClearReconnectLayoutRestoreRequest(request);
                        return;
                    }

                    var savedIdentities = LayoutIdentityStore.Load(currentPath);
                    if (!DisplayTopologyService.IsExactSavedMonitorSetActive(
                            currentPath,
                            detection.Monitors,
                            savedIdentities))
                    {
                        _reconnectRestoreTracker.Observe(
                            currentIdentity,
                            exactSavedMonitorSetActive: false,
                            allowTrigger: false);
                        ClearReconnectLayoutRestoreRequest(request);
                        _log.Write(
                            $"Skipped reconnect layout restore for profile '{currentProfile}' because the active monitor set no longer matched it exactly.");
                        return;
                    }

                    if (request.PhysicalReconnectGeneration > 0)
                    {
                        var applied = _topologySvc.CheckSavedLayoutApplied(
                            currentPath,
                            detection.Monitors,
                            savedIdentities);
                        if (applied.State != SavedLayoutAppliedState.NotApplied)
                        {
                            ClearReconnectLayoutRestoreRequest(request);
                            _log.Write(
                                applied.State == SavedLayoutAppliedState.Applied
                                    ? $"Skipped reconnect layout restore for profile '{currentProfile}' because its geometry was already applied at execution time."
                                    : $"Skipped reconnect layout restore for profile '{currentProfile}' because execution-time geometry verification was inconclusive: {applied.Message}");
                            return;
                        }
                    }
                    else if (_monitorArrivalAfterRemovalGeneration >
                             request.PhysicalReconnectGeneration)
                    {
                        var applied = _topologySvc.CheckSavedLayoutApplied(
                            currentPath,
                            detection.Monitors,
                            savedIdentities);
                        if (applied.State == SavedLayoutAppliedState.Inconclusive)
                        {
                            ClearReconnectLayoutRestoreRequest(request);
                            _log.Write(
                                $"Skipped reconnect layout restore for profile '{currentProfile}' because newer physical monitor activity made the request inconclusive: {applied.Message}");
                            return;
                        }
                    }

                    var result = await ApplySavedLayoutTopologyWithRetryAsync(
                        currentProfile,
                        currentPath,
                        requireReliableDetection: true);
                    if (!result.Success)
                    {
                        if (result.RollbackAttempted && !result.RollbackVerified)
                        {
                            ClearReconnectLayoutRestoreRequest(request);
                            _log.Write(
                                $"Reconnect layout restore for profile '{currentProfile}' stopped because rollback could not be verified. " +
                                "No automatic retry will run until a new display event is observed.");
                            return;
                        }

                        const int maxTopologyAttempts = 3;
                        if (request.TopologyAttempt < maxTopologyAttempts &&
                            IsReconnectRestoreRequestCurrent(request))
                        {
                            var retryRequest = request with
                            {
                                TopologyAttempt = request.TopologyAttempt + 1
                            };
                            _pendingReconnectLayoutRestore = retryRequest;
                            RestartReconnectLayoutRestoreTimer();
                            _log.Write(
                                $"Reconnect layout restore for profile '{currentProfile}' failed transiently; " +
                                $"queued safe CCD retry {retryRequest.TopologyAttempt} of {maxTopologyAttempts} without running the active-set-changing fallback.");
                        }
                        else
                        {
                            ClearReconnectLayoutRestoreRequest(request);
                            _log.Write(
                                $"Reconnect layout restore for profile '{currentProfile}' failed after {request.TopologyAttempt} safe CCD attempt(s) without running the active-set-changing fallback.");
                        }
                        return;
                    }

                    _log.Write(
                        $"Auto-restored identity-matched layout profile '{currentProfile}' after the saved monitors physically reconnected.");
                    _pendingReconnectLayoutRestore = null;
                    await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
                    _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                    var primaryResult = await EnforcePreferredPrimaryIfActiveAsync(
                        "preferred primary after reconnect restore");
                    if (HasUnverifiedRollback(primaryResult))
                    {
                        SurfaceUnverifiedTopology(
                            primaryResult!,
                            "Automatic reconnect restore");
                        return;
                    }
                    if (primaryResult?.Success == true)
                    {
                        await Task.Delay(400, _lifetimeCancellation.Token);
                        await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
                    }
                }
                finally
                {
                    EndDisplayAction("automatic reconnect layout restore");
                }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                ClearReconnectLayoutRestoreRequest(request);
                _log.Write($"Reconnect layout restore failed: {ex.Message}");
            }
        }

        private void ObserveDefiniteIncompleteFallbackLayoutState()
        {
            if (!_displayedDetectionUsedScreenFallback)
                return;

            var profile = SelectedLayoutProfileName();
            var path = SelectedLayoutPath();
            var savedActiveMonitorCount = DisplayTopologyService.GetSavedActiveMonitorCount(path);
            if (savedActiveMonitorCount <= 0)
                return;

            var detectedActiveDeviceCount = _detected
                .Where(monitor => monitor.IsPresent &&
                                  monitor.IsActive &&
                                  !string.IsNullOrWhiteSpace(monitor.DeviceName))
                .Select(monitor => MonitorTargetResolver.NormalizeDeviceNameForComparison(monitor.DeviceName))
                .Where(deviceName => !string.IsNullOrWhiteSpace(deviceName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (detectedActiveDeviceCount >= savedActiveMonitorCount)
                return;

            var profileIdentity = BuildLayoutProfileIdentity(profile, path);
            _reconnectRestoreTracker.Observe(
                profileIdentity,
                exactSavedMonitorSetActive: false,
                allowTrigger: false);
            _log.Write(
                $"Recorded an incomplete saved monitor set from fallback detection ({detectedActiveDeviceCount} active; {savedActiveMonitorCount} saved).");
        }

        private void ClearReconnectLayoutRestoreRequest(ReconnectLayoutRestoreRequest request)
        {
            if (ReferenceEquals(_pendingReconnectLayoutRestore, request))
                _pendingReconnectLayoutRestore = null;
        }

        private bool IsReconnectRestoreRequestCurrent(ReconnectLayoutRestoreRequest request)
            => !IsDisposed &&
               !_lifetimeCancellation.IsCancellationRequested &&
               request.CancellationGeneration == _reconnectRestoreCancellationGeneration &&
               ReferenceEquals(_pendingReconnectLayoutRestore, request);

        private static string BuildLayoutProfileIdentity(string profile, string path)
        {
            try
            {
                var file = new FileInfo(path);
                var identity = new FileInfo(LayoutIdentityStore.GetIdentityPath(path));
                return string.Join(
                    "|",
                    profile.Trim(),
                    file.FullName,
                    file.Exists ? file.Length : -1,
                    file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
                    identity.Exists ? identity.Length : -1,
                    identity.Exists ? identity.LastWriteTimeUtc.Ticks : 0);
            }
            catch
            {
                return $"{profile.Trim()}|{path}";
            }
        }

        private async Task SaveSelectedLayoutProfileAsync()
        {
            if (!await WaitForPendingMonitorRefreshAsync())
                return;
            if (!TryBeginDisplayAction("save layout"))
                return;

            try
            {
                if (!_profileTransactionsHealthy)
                {
                    var recovery = _profileStore.RecoverProfileTransactionsWithResult();
                    _profileTransactionsHealthy = recovery.Success;
                    LogPersistenceResult("recover layout profile transactions before save", recovery);
                    if (!recovery.Success)
                    {
                        ThemedMessageBox.Warn(this,
                            "Layout profiles are in an unresolved recovery state and cannot be saved safely. " +
                            "See diagnostics.log for details; no profile was changed.",
                            "Save Layout", _uiSettings.DarkMode);
                        return;
                    }
                }

                var profile = PromptForLayoutProfileName(SelectedLayoutProfileName());
                if (string.IsNullOrWhiteSpace(profile))
                    return;

                CancelQueuedStartupLayoutRestore("save layout");
                CancelQueuedReconnectLayoutRestore("save layout");

                var path = _profileStore.GetLayoutPath(profile);
                var saveResult = await SaveLayoutProfileTransactionAsync(profile, path, "Saved");
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                _log.Write(saveResult.Success
                    ? $"Saved layout profile '{profile}' to {path}."
                    : $"Failed to save layout profile '{profile}': {saveResult.ErrorMessage}");

                if (!saveResult.Success)
                {
                    ThemedMessageBox.Info(this,
                        $"Unable to save layout. {saveResult.ErrorMessage}\n\nThe existing profile was left unchanged.",
                        "Save Layout", _uiSettings.DarkMode);
                    return;
                }

                var warnings = new List<string>();
                if (!string.IsNullOrWhiteSpace(saveResult.WarningMessage))
                    warnings.Add(saveResult.WarningMessage);

                var profileResult = _profileStore.AddProfileNameWithResult(profile);
                LogPersistenceResult($"save layout profile index '{profile}'", profileResult);
                if (!profileResult.Success)
                    warnings.Add(profileResult.ErrorMessage);
                else if (!string.IsNullOrWhiteSpace(profileResult.WarningMessage))
                    warnings.Add(profileResult.WarningMessage);

                _uiSettings.SelectedLayoutProfile = profile;
                var settingsResult = _uiStore.SaveWithResult(_uiSettings);
                LogPersistenceResult("save selected layout profile", settingsResult);
                if (!settingsResult.Success)
                    warnings.Add(settingsResult.ErrorMessage);
                else if (!string.IsNullOrWhiteSpace(settingsResult.WarningMessage))
                    warnings.Add(settingsResult.WarningMessage);

                var message = $"Layout profile '{profile}' saved.";
                if (warnings.Count > 0)
                    message += $"\n\nSome profile metadata could not be saved:\n{string.Join("\n", warnings)}";

                ThemedMessageBox.Info(this, message, "Save Layout", _uiSettings.DarkMode);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // The application is closing.
            }
            finally
            {
                EndDisplayAction("save layout");
            }
        }

        private async Task<PersistenceResult> AutoSaveSelectedLayoutBeforeDisableAsync()
        {
            var profile = SelectedLayoutProfileName();
            var path = SelectedLayoutPath();

            if (!_profileTransactionsHealthy)
            {
                _log.Write(
                    $"Skipped automatic layout profile save for '{profile}' because an earlier profile transaction could not be recovered safely.");
                return PersistenceResult.Failed(
                    "Layout profiles are in an unresolved recovery state. See diagnostics.log; no monitor was disabled.");
            }

            if (!_uiSettings.AutoSaveLayoutBeforeDisable)
            {
                _log.Write($"Skipped automatic layout profile save before disable because auto-save is disabled. Profile '{profile}' at {path} remains unchanged.");
                return PersistenceResult.Unchanged();
            }

            _log.Write($"Automatic layout profile save before disable requested for '{profile}' at {path}.");

            if (!File.Exists(path))
            {
                return PersistenceResult.Failed(
                    $"Layout profile '{profile}' has not been saved yet. Save it before disabling a monitor, or turn off automatic save-before-disable.");
            }

            var detection = await _detectSvc.DetectWithStatusAsync(_lifetimeCancellation.Token);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            if (detection.UsedScreenFallback)
            {
                return PersistenceResult.Failed(
                    "Monitor identities could not be verified reliably. No profile was overwritten and no monitor was disabled.");
            }

            var savedIdentities = LayoutIdentityStore.Load(path);
            if (!DisplayTopologyService.IsExactSavedMonitorSetActive(
                    path,
                    detection.Monitors,
                    savedIdentities))
            {
                return PersistenceResult.Failed(
                    $"The currently active physical monitor set does not exactly match profile '{profile}'. " +
                    "Automatic save was stopped so a disconnected monitor is not removed from the profile.");
            }

            if (File.Exists(path) && !TryCreateAutoSaveBackup(profile, path, out var backupPath, out var backupError))
            {
                _log.Write($"Skipped automatic layout profile save for '{profile}' because backup failed: {backupError}");
                return PersistenceResult.Failed(
                    $"A safe backup of profile '{profile}' could not be created: {backupError}");
            }

            var saveResult = await SaveLayoutProfileTransactionAsync(profile, path, "Automatically saved");
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            _log.Write(saveResult.Success
                ? $"Automatically saved layout profile '{profile}' before disable to {path}."
                : $"Failed to automatically save layout profile '{profile}' before disable: {saveResult.ErrorMessage}");
            if (!string.IsNullOrWhiteSpace(saveResult.WarningMessage))
                _log.Write($"Automatic layout profile save warning for '{profile}': {saveResult.WarningMessage}");
            return saveResult;
        }

        private async Task<PersistenceResult> SaveLayoutProfileTransactionAsync(
            string profile,
            string layoutPath,
            string action)
        {
            string? stagingPath = null;
            try
            {
                stagingPath = LayoutProfileTransaction.CreateStagingLayoutPath(layoutPath);
                var saved = await _layoutSvc.SaveLayoutAsync(stagingPath, _lifetimeCancellation.Token);
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                if (!saved)
                    return PersistenceResult.Failed(
                        "Windows could not capture and validate the current display layout.");

                var detection = await _detectSvc.DetectWithStatusAsync(_lifetimeCancellation.Token);
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                if (detection.UsedScreenFallback)
                {
                    return PersistenceResult.Failed(
                        "Monitor identities could not be read reliably; fallback detection was used.");
                }

                var identityResult = LayoutIdentityStore.SaveWithResult(stagingPath, detection.Monitors);
                if (!identityResult.Success)
                    return PersistenceResult.Failed(
                        $"The monitor identity map could not be saved: {identityResult.ErrorMessage}");

                var stagedIdentities = LayoutIdentityStore.Load(stagingPath);
                if (!DisplayTopologyService.IsExactSavedMonitorSetActive(
                        stagingPath,
                        detection.Monitors,
                        stagedIdentities))
                {
                    return PersistenceResult.Failed(
                        "The captured layout and physical monitor identities did not exactly match the currently active monitor set.");
                }

                var commitResult = LayoutProfileTransaction.Commit(stagingPath, layoutPath);
                if (!commitResult.Success)
                {
                    _profileTransactionsHealthy = false;
                    return commitResult;
                }

                var recovery = _profileStore.RecoverProfileTransactionsWithResult();
                _profileTransactionsHealthy = recovery.Success;

                var warning = string.Join(
                    " ",
                    new[]
                    {
                        identityResult.WarningMessage,
                        commitResult.WarningMessage,
                        recovery.Success
                            ? recovery.WarningMessage
                            : "Another layout profile transaction remains unresolved; automatic saves and restores stay disabled. " +
                              recovery.ErrorMessage
                    }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                _log.Write(
                    $"{action} layout and identity map for profile '{profile}' as one transaction.");
                return PersistenceResult.Saved(warningMessage: warning);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Write($"Failed to save layout profile transaction for '{profile}': {ex.Message}");
                return PersistenceResult.Failed(ex.Message);
            }
            finally
            {
                LayoutProfileTransaction.DeleteStagingArtifacts(stagingPath);
            }
        }

        private bool TryCreateAutoSaveBackup(
            string profile,
            string layoutPath,
            out string backupPath,
            out string errorMessage)
        {
            backupPath = string.Empty;
            errorMessage = string.Empty;

            try
            {
                var directory = Path.GetDirectoryName(layoutPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                backupPath = NextAutoSaveBackupPath(layoutPath, DateTime.Now);
                File.Copy(layoutPath, backupPath, overwrite: false);
                var identityPath = LayoutIdentityStore.GetIdentityPath(layoutPath);
                if (File.Exists(identityPath))
                {
                    var backupIdentityPath = LayoutIdentityStore.GetIdentityPath(backupPath);
                    var identityBackupCreated = false;
                    try
                    {
                        File.Copy(identityPath, backupIdentityPath, overwrite: false);
                        identityBackupCreated = true;
                    }
                    catch
                    {
                        try { File.Delete(backupPath); } catch { /* best-effort rollback */ }
                        if (identityBackupCreated)
                        {
                            try { File.Delete(backupIdentityPath); } catch { /* best-effort rollback */ }
                        }
                        throw;
                    }
                }
                _log.Write($"Backed up layout profile '{profile}' before automatic save to {backupPath}.");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        internal static string NextAutoSaveBackupPath(string layoutPath, DateTime timestamp)
        {
            var stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var basePath = $"{layoutPath}.autosave-{stamp}.bak";
            if (!File.Exists(basePath) &&
                !File.Exists(LayoutIdentityStore.GetIdentityPath(basePath)))
                return basePath;

            for (int i = 2; i < 1000; i++)
            {
                var candidate = $"{layoutPath}.autosave-{stamp}-{i}.bak";
                if (!File.Exists(candidate) &&
                    !File.Exists(LayoutIdentityStore.GetIdentityPath(candidate)))
                    return candidate;
            }

            return $"{layoutPath}.autosave-{stamp}-{Guid.NewGuid():N}.bak";
        }

        internal static Size CalculateScrollableClientSize(
            int desiredWidth,
            int desiredHeight,
            int maxClientHeight,
            int verticalScrollbarWidth)
        {
            int safeWidth = Math.Max(1, desiredWidth);
            int safeHeight = Math.Max(1, desiredHeight);
            int safeMaximumHeight = Math.Max(1, maxClientHeight);
            bool needsVerticalScroll = safeHeight > safeMaximumHeight;

            return new Size(
                checked(safeWidth + (needsVerticalScroll ? Math.Max(0, verticalScrollbarWidth) : 0)),
                Math.Min(safeHeight, safeMaximumHeight));
        }

        private string? PromptForLayoutProfileName(string currentName)
        {
            using var dlg = new Form
            {
                Text = "Save Layout Profile",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 126)
            };

            var label = new Label
            {
                Text = "Profile name",
                Location = new Point(14, 16),
                AutoSize = true
            };
            var input = new TextBox
            {
                Text = currentName,
                Location = new Point(14, 42),
                Size = new Size(330, 24)
            };
            var ok = new ThemedButton
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                Size = new Size(82, 30),
                Location = new Point(172, 84),
                Tone = ThemedButtonTone.Primary
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(82, 30), Location = new Point(262, 84) };

            dlg.Controls.Add(label);
            dlg.Controls.Add(input);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            Themer.Apply(dlg, _uiSettings.DarkMode ? ThemePalette.Dark() : ThemePalette.Light());
            DwmInterop.SetDarkTitleBar(dlg.Handle, _uiSettings.DarkMode);

            input.SelectAll();
            return dlg.ShowDialog(this) == DialogResult.OK
                ? LayoutProfileStore.NormalizeProfileName(input.Text)
                : null;
        }

        private async Task RestoreSelectedLayoutProfileAsync(
            bool showMessage,
            bool isStartupRestore = false,
            bool displayActionAlreadyHeld = false)
        {
            if (!displayActionAlreadyHeld)
            {
                if (!await WaitForPendingMonitorRefreshAsync())
                    return;
                if (!TryBeginDisplayAction("restore layout", showMessage))
                {
                    if (isStartupRestore && _uiSettings.RestoreLayoutOnStartup)
                    {
                        _log.Write("Deferred startup layout restore after losing the display-action gate race.");
                        QueueStartupLayoutRestore();
                    }
                    return;
                }
            }

            try
            {
                if (!_profileTransactionsHealthy)
                {
                    var recovery = _profileStore.RecoverProfileTransactionsWithResult();
                    _profileTransactionsHealthy = recovery.Success;
                    LogPersistenceResult("recover layout profile transactions before restore", recovery);
                    if (!recovery.Success)
                    {
                        if (showMessage)
                        {
                            ThemedMessageBox.Warn(this,
                                "Layout profiles are in an unresolved recovery state and cannot be restored safely. " +
                                "See diagnostics.log for details; no display changes were made.",
                                "Restore Layout", _uiSettings.DarkMode);
                        }
                        return;
                    }
                }

                var profile = SelectedLayoutProfileName();
                var path = _profileStore.GetLayoutPath(profile);

                if (!File.Exists(path))
                {
                    if (showMessage)
                        ThemedMessageBox.Warn(this,
                            $"Unable to restore layout profile '{profile}'. Save it first.",
                            "Restore Layout", _uiSettings.DarkMode);
                    return;
                }

                CancelQueuedStartupLayoutRestore("restore layout");
                CancelQueuedReconnectLayoutRestore("restore layout");

                var result = await ApplySavedLayoutTopologyWithRetryAsync(
                    profile,
                    path,
                    requireReliableDetection: isStartupRestore);
                if (!result.Success &&
                    !isStartupRestore &&
                    !(result.RollbackAttempted && !result.RollbackVerified))
                {
                    var savedIdentities = LayoutIdentityStore.Load(path);
                    var detection = await _detectSvc.DetectWithStatusAsync(_lifetimeCancellation.Token);
                    _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                    if (detection.UsedScreenFallback)
                    {
                        result = new DisplayTopologyResult
                        {
                            Success = false,
                            Message = "Reliable physical monitor detection was unavailable; the active display set was not changed."
                        };
                    }
                    else
                    {
                        int savedActiveCount = DisplayTopologyService.GetSavedActiveMonitorCount(path);
                        int currentActiveCount = detection.Monitors.Count(monitor => monitor.IsPresent && monitor.IsActive);
                        if (ShouldConfirmExactSetRestore(_uiSettings.ConfirmBeforeDisable))
                        {
                            var exactSetMessage =
                                $"Layout profile '{profile}' saves {savedActiveCount} active display(s); " +
                                $"Windows currently has {currentActiveCount} active. " +
                                "Applying it may enable displays saved in the profile and disable currently active displays that are not in it. Continue?";
                            var choice = MessageBox.Show(
                                this,
                                exactSetMessage,
                                displayActionAlreadyHeld ? "Apply Layout Profile" : "Restore Monitor Set",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (choice != DialogResult.Yes)
                                return;
                        }
                        else
                        {
                            _log.Write(
                                $"Applying exact display set for profile '{profile}' without confirmation because " +
                                $"Confirm before disabling is off (saved active={savedActiveCount}, current active={currentActiveCount}).");
                        }

                        _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                        var exactResult = _layoutSvc.RestoreLayoutWithResult(
                            path,
                            detection.Monitors,
                            savedIdentities);
                        LogTopologyResult($"native exact-set restore for profile '{profile}'", exactResult);
                        _log.Write(exactResult.Success
                            ? $"Native exact-set restore applied for layout profile '{profile}' from {path}."
                            : $"Native exact-set restore failed for layout profile '{profile}' from {path}.");

                        if (exactResult.Success)
                        {
                            await Task.Delay(800, _lifetimeCancellation.Token);
                            result = await ApplySavedLayoutTopologyWithRetryAsync(profile, path);
                        }
                        else
                        {
                            result = exactResult;
                        }
                    }
                }
                else if (!result.Success)
                {
                    _log.Write(
                        $"Startup restore for layout profile '{profile}' did not change the active display set. " +
                        "Use Restore manually if monitors must be enabled or disabled.");
                }

                if (!result.Success)
                {
                    if (showMessage)
                        ThemedMessageBox.Warn(this,
                            $"Unable to restore layout profile '{profile}'. {result.Message}",
                            "Restore Layout", _uiSettings.DarkMode);
                    return;
                }

                await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                var primaryResult = await EnforcePreferredPrimaryIfActiveAsync(
                    "preferred primary after layout restore");
                if (HasUnverifiedRollback(primaryResult))
                {
                    SurfaceUnverifiedTopology(primaryResult!, "Restore Layout");
                    return;
                }
                if (primaryResult?.Success == true)
                {
                    await Task.Delay(400, _lifetimeCancellation.Token);
                    await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
                }

                if (showMessage)
                {
                    ThemedMessageBox.Info(
                        this,
                        displayActionAlreadyHeld
                            ? $"Layout profile '{profile}' applied."
                            : $"Layout profile '{profile}' restored.",
                        displayActionAlreadyHeld ? "Apply Layout Profile" : "Restore Layout",
                        _uiSettings.DarkMode);
                }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // The application is closing.
            }
            finally
            {
                if (!displayActionAlreadyHeld)
                    EndDisplayAction("restore layout");
            }
        }

        private async Task<DisplayTopologyResult> ApplySavedLayoutTopologyWithRetryAsync(
            string profile,
            string path,
            bool requireReliableDetection = false)
        {
            DisplayTopologyResult? result = null;
            var savedIdentities = LayoutIdentityStore.Load(path);
            if (savedIdentities.Count > 0)
                _log.Write($"Loaded {savedIdentities.Count} saved monitor identity record(s) for layout profile '{profile}'.");

            for (int attempt = 0; attempt < 5; attempt++)
            {
                var detection = await _detectSvc.DetectWithStatusAsync(_lifetimeCancellation.Token);
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                if (requireReliableDetection && detection.UsedScreenFallback)
                {
                    result = new DisplayTopologyResult
                    {
                        Success = false,
                        Message = "Reliable monitor identity detection was unavailable for the automatic layout restore."
                    };
                }
                else
                {
                    result = _topologySvc.ApplyLayoutPositionsFromConfig(
                        path,
                        detection.Monitors,
                        savedIdentities);
                }
                if (result.Success)
                    break;
                if (result.RollbackAttempted && !result.RollbackVerified)
                    break;

                await Task.Delay(500, _lifetimeCancellation.Token);
            }

            result ??= new DisplayTopologyResult
            {
                Success = false,
                Message = "Saved layout topology was not attempted."
            };

            LogTopologyResult($"CCD layout restore for profile '{profile}'", result);
            if (result.Success)
                await Task.Delay(400, _lifetimeCancellation.Token);

            return result;
        }

        private void LogTopologyResult(string action, DisplayTopologyResult result)
        {
            _log.Write(
                $"{action}: success={result.Success}, validate={FormatCode(result.ValidateCode)}, " +
                $"apply={FormatCode(result.ApplyCode)}, message='{result.Message}'.");

            foreach (var detail in result.Details)
                _log.Write($"{action}: {detail}");
        }

        private static string FormatCode(int? code)
            => code.HasValue ? code.Value.ToString() : "n/a";

        private void LogPersistenceResult(string action, PersistenceResult result)
        {
            if (!result.Success)
            {
                _log.Write($"Failed to {action}: {result.ErrorMessage}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.WarningMessage))
                _log.Write($"Warning while attempting to {action}: {result.WarningMessage}");
        }

        // ---------- Theme / caption ----------

        private void ApplyTheme(bool dark)
        {
            var palette = dark ? ThemePalette.Dark() : ThemePalette.Light();

            // Apply to this form + children
            Themer.Apply(this, palette);

            // Keep HR visible
            if (_hrTop != null) _hrTop.BackColor = palette.Border;
            if (_summaryLabel != null) _summaryLabel.ForeColor = palette.TextSubtle;
            ApplyWarningBannerTheme(palette);
            UpdateButtonStatus();

            // Native title bar
            DwmInterop.SetDarkTitleBar(this.Handle, dark);
            var accent = WindowsTheme.AccentColor();
            if (accent.HasValue) DwmInterop.SetCaptionColor(this.Handle, accent.Value);

            Invalidate(true);
            Update();
        }

        private void ApplyWarningBannerTheme(ThemePalette palette)
        {
            if (_missingToolPanel != null)
            {
                _missingToolPanel.BackColor = palette.StatusWarnBack;
                if (_missingToolPanel is ThemedCardPanel card)
                    card.BorderColor = palette.StatusWarn;
                _missingToolPanel.Invalidate();
            }

            if (_missingToolLabel != null)
            {
                _missingToolLabel.BackColor = Color.Transparent;
                _missingToolLabel.ForeColor = palette.StatusWarn;
            }
        }

        // ---------- Settings dialog ----------
        private async void SettingsButton_Click(object? sender, EventArgs e)
        {
            var restoreChangedProfile = false;

            if (!Visible || WindowState == FormWindowState.Minimized || !ShowInTaskbar)
                ShowMainWindowForInteraction();

            if (!await WaitForPendingMonitorRefreshAsync())
                return;
            if (!TryBeginDisplayAction("settings"))
                return;

            try
            {
                if (!_profileTransactionsHealthy)
                {
                    var recovery = _profileStore.RecoverProfileTransactionsWithResult();
                    _profileTransactionsHealthy = recovery.Success;
                    LogPersistenceResult("recover layout profile transactions before settings", recovery);
                    if (!recovery.Success)
                    {
                        ThemedMessageBox.Warn(this,
                            "Settings cannot be opened while layout profiles are in an unresolved recovery state. " +
                            "See diagnostics.log for details; no settings or profiles were changed.",
                            "Monitor Switcher", _uiSettings.DarkMode);
                        return;
                    }
                }
                var aliasRows = BuildAliasSettingsRows();
                var representedAliasKeys = aliasRows
                    .Select(row => row.StableKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                using var dlg = new AliasSettingsForm(
                    aliasRows,
                    _uiSettings.DarkMode,
                    _uiSettings.AlwaysOnTop,
                    _uiSettings.MinimizeToTray,
                    _startupEnabledForCurrentExecutable,
                    _uiSettings.ConfirmBeforeDisable,
                    _uiSettings.AutoSaveLayoutBeforeDisable,
                    _uiSettings.RestoreLayoutOnStartup,
                    _profileStore.LoadProfileNames(),
                    SelectedLayoutProfileName(),
                    _log.Read(),
                    this)
                {
                    StartPosition = FormStartPosition.CenterParent,
                    ShowInTaskbar = false,
                    TopMost = true
                };

                var prevTopMost = this.TopMost;
                DialogResult dr;
                try
                {
                    // Ensure the dialog can float above everything without fighting the host.
                    this.TopMost = false;
                    dlg.TopMost = true;
                    dr = dlg.ShowDialog(this);
                }
                finally
                {
                    // Restore whatever the user had configured for the main window.
                    this.TopMost = prevTopMost;
                }

                if (dr != DialogResult.OK)
                    return;

                CancelQueuedStartupLayoutRestore("apply settings");
                CancelQueuedReconnectLayoutRestore("apply settings");
                restoreChangedProfile = await ApplySettingsDialogResultsAsync(dlg, representedAliasKeys);
                if (restoreChangedProfile)
                {
                    _log.Write($"Applying newly selected layout profile '{SelectedLayoutProfileName()}'.");
                    var restoreButton = _btnRestoreLayout;
                    var restoreButtonText = restoreButton?.Text;
                    if (restoreButton != null)
                    {
                        restoreButton.Text = "Applying…";
                        restoreButton.Enabled = false;
                    }
                    try
                    {
                        await RestoreSelectedLayoutProfileAsync(
                            showMessage: true,
                            displayActionAlreadyHeld: true);
                    }
                    finally
                    {
                        if (restoreButton != null)
                        {
                            restoreButton.Text = restoreButtonText ?? "Restore";
                            restoreButton.Enabled = true;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // The application is closing.
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Error(this,
                    $"Unable to apply settings.\n{ex.Message}",
                    "Monitor Switcher",
                    _uiSettings.DarkMode);
            }
            finally
            {
                EndDisplayAction("settings");
            }

        }

        private List<AliasViewRow> BuildAliasSettingsRows()
            => AliasSettingsMapper.BuildRows(BuildPresentationList(), _aliasMap);

        private async Task<bool> ApplySettingsDialogResultsAsync(
            AliasSettingsForm dlg,
            IReadOnlySet<string> representedAliasKeys)
        {
            var warnings = new List<string>();
            var previousLayoutProfile = SelectedLayoutProfileName();

            var representedAliases = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in representedAliasKeys)
            {
                if (_aliasMap.TryGetValue(key, out var info))
                    representedAliases[key] = info;
            }

            var representedMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in dlg.UpdatedMappings)
            {
                if (representedAliasKeys.Contains(mapping.Key))
                    representedMappings[mapping.Key] = mapping.Value;
            }

            var representedPreferredKey = representedAliasKeys.Contains(dlg.PreferredPrimaryKey ?? string.Empty)
                ? dlg.PreferredPrimaryKey
                : null;
            var representedFallbackKey = representedAliasKeys.Contains(dlg.FallbackPrimaryKey ?? string.Empty)
                ? dlg.FallbackPrimaryKey
                : null;
            if (!string.IsNullOrWhiteSpace(representedPreferredKey) &&
                string.Equals(representedPreferredKey, representedFallbackKey, StringComparison.OrdinalIgnoreCase))
            {
                representedFallbackKey = null;
            }

            ClearExistingMonitorPreferenceFlags(
                clearPreferred: !string.IsNullOrWhiteSpace(representedPreferredKey),
                clearFallback: !string.IsNullOrWhiteSpace(representedFallbackKey));

            AliasSettingsMapper.ApplyMonitorSettings(
                representedAliases,
                dlg.RemovedKeys.Where(representedAliasKeys.Contains),
                representedMappings,
                representedPreferredKey,
                representedFallbackKey);

            foreach (var key in representedAliasKeys)
            {
                if (representedAliases.TryGetValue(key, out var info))
                    _aliasMap[key] = info;
                else
                    _aliasMap.Remove(key);
            }

            var aliasResult = _aliasStore.SaveWithResult(_aliasMap);
            LogPersistenceResult("save monitor aliases", aliasResult);
            if (!aliasResult.Success)
                warnings.Add(aliasResult.ErrorMessage);
            else if (!string.IsNullOrWhiteSpace(aliasResult.WarningMessage))
                warnings.Add(aliasResult.WarningMessage);

            if (dlg.DarkModeResult.HasValue && dlg.DarkModeResult.Value != _uiSettings.DarkMode)
            {
                _uiSettings.DarkMode = dlg.DarkModeResult.Value;
                ApplyTheme(_uiSettings.DarkMode);
            }

            if (dlg.AlwaysOnTopResult.HasValue && dlg.AlwaysOnTopResult.Value != _uiSettings.AlwaysOnTop)
            {
                _uiSettings.AlwaysOnTop = dlg.AlwaysOnTopResult.Value;
                TopMost = _uiSettings.AlwaysOnTop;
            }

            if (dlg.MinimizeToTrayResult.HasValue)
                _uiSettings.MinimizeToTray = dlg.MinimizeToTrayResult.Value;

            if (dlg.StartWithWindowsResult.HasValue &&
                dlg.StartWithWindowsResult.Value != _startupEnabledForCurrentExecutable)
            {
                var requestedStartupEnabled = dlg.StartWithWindowsResult.Value;
                var startupResult = StartupManager.SetEnabledWithResult(
                    requestedStartupEnabled,
                    Application.ExecutablePath);
                LogPersistenceResult("update Windows startup", startupResult);
                if (!startupResult.Success)
                {
                    warnings.Add(startupResult.ErrorMessage);
                    _startupEnabledForCurrentExecutable = StartupManager.IsEnabled();
                }
                else if (!string.IsNullOrWhiteSpace(startupResult.WarningMessage))
                {
                    warnings.Add(startupResult.WarningMessage);
                }

                if (startupResult.Success)
                    _startupEnabledForCurrentExecutable = requestedStartupEnabled;
            }
            // Settings OK is the explicit point at which the observed Run state
            // may be synchronised back to the per-user UI settings file.
            _uiSettings.StartWithWindows = _startupEnabledForCurrentExecutable;

            if (dlg.ConfirmBeforeDisableResult.HasValue)
                _uiSettings.ConfirmBeforeDisable = dlg.ConfirmBeforeDisableResult.Value;

            if (dlg.AutoSaveLayoutBeforeDisableResult.HasValue)
                _uiSettings.AutoSaveLayoutBeforeDisable = dlg.AutoSaveLayoutBeforeDisableResult.Value;

            if (dlg.RestoreLayoutOnStartupResult.HasValue)
                _uiSettings.RestoreLayoutOnStartup = dlg.RestoreLayoutOnStartupResult.Value;

            if (!string.IsNullOrWhiteSpace(dlg.SelectedLayoutProfileResult))
                _uiSettings.SelectedLayoutProfile = dlg.SelectedLayoutProfileResult;

            foreach (var profile in dlg.RemovedLayoutProfiles)
            {
                var deleteResult = _profileStore.DeleteProfileWithResult(profile);
                LogPersistenceResult($"delete layout profile '{profile}'", deleteResult);
                if (!deleteResult.Success)
                    warnings.Add(deleteResult.ErrorMessage);
                else if (!string.IsNullOrWhiteSpace(deleteResult.WarningMessage))
                    warnings.Add(deleteResult.WarningMessage);
            }

            var settingsResult = _uiStore.SaveWithResult(_uiSettings);
            LogPersistenceResult("save UI settings", settingsResult);
            if (!settingsResult.Success)
                warnings.Add(settingsResult.ErrorMessage);
            else if (!string.IsNullOrWhiteSpace(settingsResult.WarningMessage))
                warnings.Add(settingsResult.WarningMessage);

            await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            var selectedLayoutProfileChanged = !string.Equals(
                previousLayoutProfile,
                SelectedLayoutProfileName(),
                StringComparison.OrdinalIgnoreCase);
            DisplayTopologyResult? primaryResult = null;
            var preferredPrimary = PrimaryMonitorPreference.ResolvePreferredPrimaryTarget(_detected, _aliasMap);
            if (!selectedLayoutProfileChanged && !string.IsNullOrWhiteSpace(preferredPrimary))
                primaryResult = await EnforcePreferredPrimaryIfActiveAsync("preferred primary after settings save");
            if (!selectedLayoutProfileChanged &&
                !string.IsNullOrWhiteSpace(preferredPrimary) &&
                primaryResult?.Success != true)
            {
                warnings.Add(primaryResult?.Message ?? "The preferred primary monitor could not be applied.");
            }
            else if (primaryResult?.Success == true)
            {
                await Task.Delay(400, _lifetimeCancellation.Token);
                await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
            }

            if (warnings.Count > 0)
            {
                ThemedMessageBox.Warn(this,
                    $"Some settings could not be applied:\n{string.Join("\n", warnings.Distinct())}",
                    "Monitor Switcher",
                    _uiSettings.DarkMode);
            }

            return selectedLayoutProfileChanged && settingsResult.Success;
        }

        private bool TryBeginDisplayAction(string actionName, bool showBusyMessage = true)
        {
            if (_displayActionGate.Wait(0))
            {
                _log.Write($"Display action started: {actionName}.");
                return true;
            }

            if (showBusyMessage)
            {
                ThemedMessageBox.Info(this,
                    "Another monitor action is already running. Wait for it to finish, then try again.",
                    "Monitor Switcher", _uiSettings.DarkMode);
            }

            _log.Write($"Display action skipped: {actionName}; another monitor action is already running.");
            return false;
        }

        private void EndDisplayAction(string actionName)
        {
            _log.Write($"Display action finished: {actionName}.");
            _displayActionGate.Release();

            if (_reloadAliasesAfterDisplayAction && !_lifetimeCancellation.IsCancellationRequested)
            {
                _reloadAliasesAfterDisplayAction = false;
                ReloadAliasesFromStore("deferred manual refresh");
            }

            if (_refreshAfterDisplayAction && !_lifetimeCancellation.IsCancellationRequested)
            {
                _refreshAfterDisplayAction = false;
                RefreshMonitorsAndUi();
            }
        }

        // ---------- Working change ----------
        private void SetRowBusy(string stableKey, bool busy)
        {
            if (busy)
                _busy.Add(stableKey);
            else
                _busy.Remove(stableKey);

            if (!_controlsByKey.TryGetValue(stableKey, out var ctrls))
                return;

            ctrls.IsBusy = busy;

            // While busy: show a working state and disable both buttons.
            if (busy)
            {
                var palette = _uiSettings.DarkMode ? ThemePalette.Dark() : ThemePalette.Light();
                ApplyMonitorStatus(ctrls.StatusLabel, MonitorVisualStatus.Working, palette);

                ctrls.DisableButton.Enabled = false;
                ctrls.EnableButton.Enabled = false;
                Themer.ApplyButtonStyle(ctrls.DisableButton, palette);
                Themer.ApplyButtonStyle(ctrls.EnableButton, palette);
            }
            else
            {
                // When we clear busy, we’ll immediately re-run UpdateButtonStatus()
                // to put the correct state text and button states back.
            }

            ctrls.StatusLabel.Invalidate();
        }

        // ---------- Refresh + dynamic UI build ----------

        private async Task ManualRefreshMonitorsAndUiAsync()
        {
            if (_displayActionGate.CurrentCount == 0)
            {
                _reloadAliasesAfterDisplayAction = true;
                _refreshAfterDisplayAction = true;
                _log.Write("Manual refresh queued until the current display action finishes.");
                return;
            }

            ReloadAliasesFromStore("manual refresh");
            _log.Write("Manual refresh requested.");
            await RefreshMonitorsAndUiAsync();
        }

        private void ReloadAliasesFromStore(string reason)
        {
            var aliases = _aliasStore.Load();
            _aliasMap.Clear();
            foreach (var kv in aliases)
                _aliasMap[kv.Key] = kv.Value;

            _log.Write($"Reloaded {_aliasMap.Count} alias record(s) from {_aliasStore.AliasPath} for {reason}.");
        }

        private void RefreshMonitorsAndUi()
        {
            _ = RefreshMonitorsAndUiAsync();
        }

        private Task RefreshMonitorsAndUiAsync(bool allowDuringDisplayAction = false)
        {
            if (!allowDuringDisplayAction && _displayActionGate.CurrentCount == 0)
            {
                _refreshAfterDisplayAction = true;
                return Task.CompletedTask;
            }

            _refreshAgainAfterCurrent = true;
            if (!_refreshTask.IsCompleted)
                return _refreshTask;

            _refreshTask = RunRefreshLoopAsync();
            return _refreshTask;
        }

        private async Task<bool> WaitForPendingMonitorRefreshAsync()
        {
            while (true)
            {
                if (_refreshTimer.Enabled)
                {
                    _refreshTimer.Stop();
                    await RefreshMonitorsAndUiAsync();
                }

                if (_refreshTask.IsCompleted && !_refreshTimer.Enabled)
                    break;

                var pendingRefresh = _refreshTask;
                await pendingRefresh;
            }

            return !_lifetimeCancellation.IsCancellationRequested;
        }

        private async Task RunRefreshLoopAsync()
        {
            try
            {
                do
                {
                    _refreshAgainAfterCurrent = false;
                    await RefreshMonitorsAndUiCoreAsync();
                }
                while (_refreshAgainAfterCurrent && !IsDisposed && !_lifetimeCancellation.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _log.Write($"Monitor refresh failed: {ex.Message}");
                if (_summaryLabel != null && !IsDisposed)
                {
                    _summaryLabel.Text = "Detection failed · Refresh to retry";
                    _toolTip.SetToolTip(_summaryLabel, ex.Message);
                }
            }
        }

        private async Task RefreshMonitorsAndUiCoreAsync()
        {
            var reconnectEventGenerationAtDetectionStart = _reconnectDisplayEventGeneration;
            var detection = await _detectSvc.DetectWithStatusAsync(_lifetimeCancellation.Token);
            _detected = detection.Monitors;
            _displayedDetectionUsedScreenFallback = detection.UsedScreenFallback;
            _displayedDetectionWarning = detection.WarningMessage;
            if (IsDisposed || _lifetimeCancellation.IsCancellationRequested)
                return;

            var detectionIsReliableForReconnect = IsReconnectDetectionSnapshotReliable(
                reconnectEventGenerationAtDetectionStart,
                _reconnectDisplayEventGeneration,
                detection.UsedScreenFallback);
            var allowReconnectRestore = detectionIsReliableForReconnect &&
                                         ShouldEvaluateReconnectEvent(
                                             reconnectEventGenerationAtDetectionStart,
                                             _consumedReconnectDisplayEventGeneration) &&
                                         _displayActionGate.CurrentCount > 0;
            if (detectionIsReliableForReconnect)
            {
                _consumedReconnectDisplayEventGeneration = reconnectEventGenerationAtDetectionStart;
                _reconnectDetectionRetryCount = 0;
                _reconnectDetectionRetryTimer?.Stop();
            }
            else if (_reconnectDisplayEventGeneration > _consumedReconnectDisplayEventGeneration)
            {
                QueueReconnectDetectionRetry();
            }
            ObserveReconnectLayoutState(
                allowReconnectRestore,
                detectionIsReliable: detectionIsReliableForReconnect);
            LogDetectionSnapshotIfChanged();

            if (!IsNormalVisible)
            {
                _deferredLayout = true;
                return;
            }

            // Attempt to re-bind aliases if stable keys changed (e.g., port swaps)
            ReconcileAliasesForDetected();
            SuppressShadowedDeviceFallbackDetections();

            // Keep alias metadata up to date + remember targets
            foreach (var m in _detected)
            {
                if (!_aliasMap.TryGetValue(m.StableKey, out var info))
                {
                    info = new MonitorInfo();
                    _aliasMap[m.StableKey] = info;
                }
                if (m.IsPresent && m.IsActive && !string.IsNullOrWhiteSpace(m.DeviceName))
                    info.LastDeviceName = m.DeviceName;
                if (m.IsPresent && m.IsActive)
                    info.LastKnownX = m.PositionX;
                if (!string.IsNullOrWhiteSpace(m.MonitorKey))
                    info.LastRegistryKey = m.MonitorKey;
                if (!string.IsNullOrWhiteSpace(m.SerialNumber))
                    info.LastSerialNumber = m.SerialNumber;
                if (!string.IsNullOrWhiteSpace(m.InstanceId))
                    info.LastInstanceId = m.InstanceId;
                if (!string.IsNullOrWhiteSpace(m.MonitorId))
                    info.LastMonitorId = m.MonitorId;

                if (m.IsPresent && m.IsActive)
                    MonitorTargetResolver.EnsureKnownTargets(_aliasMap, m.StableKey, m.DeviceName, m.Name);
            }

            RemoveShadowedDeviceAliases();
            MonitorTargetResolver.PruneKnownTargets(_aliasMap, _detected);
            LogPersistenceResult("save refreshed monitor aliases", _aliasStore.SaveWithResult(_aliasMap));

            var toShow = BuildPresentationList();
            UpdateMonitorSummary(toShow);
            CancelMonitorCardDrag();
            _monitorCardOrder.Clear();

            int previousScrollY = Math.Max(0, -AutoScrollPosition.Y);
            if (previousScrollY > 0)
                AutoScrollPosition = Point.Empty;

            LeftTopButtons();
            UpdateTopSeparator();

            SuspendLayout();
            try
            {
                foreach (var c in _dynamicControls)
                {
                    ClearToolTips(c);
                    Controls.Remove(c);
                    c.Dispose();
                }
                _dynamicControls.Clear();
                _controlsByKey.Clear();

                _layoutRightMost = 0;

                UpdateMissingToolPanel();
                int y = GetRowsStartY();

                int lastRowBottom = y;
                foreach (var m in toShow)
                {
                    AddMonitorControls(m, GetPresentationTitle(m), y);
                    lastRowBottom = y + RowPanelHeight;
                    y += RowVerticalGap;
                }

                y = toShow.Count > 0
                    ? lastRowBottom
                    : GetRowsStartY() + 32;

                int desiredWidth = GetCompactClientWidth();
                int desiredHeight = Math.Max(120, y + SideMargin);

                if (IsNormalVisible)
                {
                    var hostScreen = Screen.FromControl(this);
                    int nonClientHeight = Math.Max(0, Height - ClientSize.Height);
                    int maxClientHeight = Math.Max(
                        120,
                        hostScreen.WorkingArea.Height - nonClientHeight - 32);
                    var boundedSize = CalculateScrollableClientSize(
                        desiredWidth,
                        desiredHeight,
                        maxClientHeight,
                        SystemInformation.VerticalScrollBarWidth);

                    AutoScrollMinSize = new Size(0, desiredHeight);
                    ClientSize = boundedSize;
                }
                else
                {
                    _deferredLayout = true; // resize later
                }
                var palette = _uiSettings.DarkMode ? ThemePalette.Dark() : ThemePalette.Light();
                Themer.Apply(this, palette);
                if (_hrTop != null) _hrTop.BackColor = palette.Border;
                if (_summaryLabel != null) _summaryLabel.ForeColor = palette.TextSubtle;
                ApplyWarningBannerTheme(palette);


                UpdateButtonStatus();
                EnsureDynamicRowsVisible();
            }
            finally
            {
                ResumeLayout(performLayout: true);
                if (previousScrollY > 0 && IsNormalVisible)
                {
                    int maximumScrollY = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
                    AutoScrollPosition = new Point(0, Math.Min(previousScrollY, maximumScrollY));
                }
            }
        }

        internal static bool ShouldEvaluateReconnectEvent(
            long eventGenerationAtDetectionStart,
            long consumedEventGeneration)
            => eventGenerationAtDetectionStart > consumedEventGeneration;

        internal static bool ShouldConfirmExactSetRestore(bool confirmBeforeDisable)
            => confirmBeforeDisable;

        internal static bool IsReconnectDetectionSnapshotReliable(
            long eventGenerationAtDetectionStart,
            long currentEventGeneration,
            bool usedScreenFallback)
            => !usedScreenFallback &&
               eventGenerationAtDetectionStart == currentEventGeneration;

        private void EnsureDynamicRowsVisible()
        {
            if (_dynamicControls.Count == 0)
                return;

            foreach (var control in _dynamicControls.Where(c => !c.IsDisposed))
            {
                control.Visible = true;
                EnsureControlTreeCreated(control);
                control.BringToFront();
                control.Invalidate(true);
                ForceRedraw(control);
            }

            _sectionTitleLabel?.BringToFront();
            _summaryLabel?.BringToFront();
            _btnSettings?.BringToFront();
            _btnRefresh?.BringToFront();
            _btnSaveLayout?.BringToFront();
            _btnRestoreLayout?.BringToFront();
            _hrTop?.BringToFront();

            ForceRedraw(this);
        }

        private static void EnsureControlTreeCreated(Control control)
        {
            control.CreateControl();
            foreach (Control child in control.Controls)
                EnsureControlTreeCreated(child);
        }

        private void ClearToolTips(Control control)
        {
            _toolTip.SetToolTip(control, null);
            foreach (Control child in control.Controls)
                ClearToolTips(child);
        }

        private static void ForceRedraw(Control control)
        {
            if (!control.IsHandleCreated) return;

            const int RDW_INVALIDATE = 0x0001;
            const int RDW_ERASE = 0x0004;
            const int RDW_ALLCHILDREN = 0x0080;
            const int RDW_UPDATENOW = 0x0100;

            RedrawWindow(
                control.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, int flags);

        private void UpdateMonitorSummary(IReadOnlyCollection<DetectedMonitor> monitors)
        {
            if (_summaryLabel == null) return;

            int present = monitors.Count;
            int active = monitors.Count(m => m.IsActive);

            _summaryLabel.Text = $"{active} of {present} active";
            _toolTip.SetToolTip(_summaryLabel, _summaryLabel.Text);
        }

        private static MonitorVisualStatus GetMonitorVisualStatus(bool isPresent, bool isActive)
        {
            if (!isPresent)
                return MonitorVisualStatus.Offline;
            return isActive ? MonitorVisualStatus.Online : MonitorVisualStatus.Disabled;
        }

        private static string GetMonitorStatusText(MonitorVisualStatus status)
            => status switch
            {
                MonitorVisualStatus.Online => "Active",
                MonitorVisualStatus.Disabled => "Disabled",
                MonitorVisualStatus.Working => "Working…",
                _ => "Unavailable"
            };

        private static void ApplyMonitorStatus(
            Label label,
            MonitorVisualStatus status,
            ThemePalette palette)
        {
            label.Text = GetMonitorStatusText(status);
            if (label is StatusBadge badge)
            {
                badge.VisualStatus = status;
                Themer.ApplyStatusBadge(badge, palette);
                return;
            }

            label.ForeColor = status switch
            {
                MonitorVisualStatus.Online => palette.StatusOk,
                MonitorVisualStatus.Disabled => palette.StatusWarn,
                MonitorVisualStatus.Working => palette.StatusBusy,
                _ => palette.TextSubtle
            };
        }

        private List<DetectedMonitor> BuildPresentationList()
            => MonitorPresentationBuilder.Build(_detected, _aliasMap);

        private void AddMonitorControls(DetectedMonitor monitor, string friendlyName, int positionY)
        {
            var palette = _uiSettings.DarkMode ? ThemePalette.Dark() : ThemePalette.Light();

            var card = new ThemedCardPanel
            {
                Location = new Point(SideMargin, positionY),
                Size = new Size(RowPanelWidth, RowPanelHeight),
                BackColor = palette.Surface,
                BorderColor = palette.Border
            };
            Controls.Add(card);
            _dynamicControls.Add(card);

            int labelX = 38;
            int disableX = RowPanelWidth - (ButtonWidth * 2) - ControlGapX - 14;
            int enableX = disableX + ButtonWidth + ControlGapX;
            int buttonY = 43;

            var dragHandle = new MonitorDragHandle
            {
                Location = new Point(7, 7),
                Size = new Size(24, RowPanelHeight - 14),
                ForeColor = palette.TextSubtle
            };
            card.Controls.Add(dragHandle);
            AttachMonitorCardDragSurface(dragHandle, monitor.StableKey);
            _toolTip.SetToolTip(dragHandle, "Drag to reorder monitor cards.");

            var label = new Label
            {
                Text = friendlyName,
                Location = new Point(labelX, 10),
                AutoSize = false,
                Size = new Size(236, 22),
                Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Regular),
                AutoEllipsis = true
            };
            card.Controls.Add(label);
            _toolTip.SetToolTip(label, friendlyName);

            var visualStatus = GetMonitorVisualStatus(monitor.IsPresent, monitor.IsActive);
            var statusLabel = new StatusBadge
            {
                AutoSize = false,
                Location = new Point(RowPanelWidth - 100, 10),
                Size = new Size(86, 24),
                Font = new Font("Segoe UI Semibold", 8.25f, FontStyle.Regular),
                Text = GetMonitorStatusText(visualStatus),
                VisualStatus = visualStatus
            };
            card.Controls.Add(statusLabel);

            var detail = new Label
            {
                Text = BuildMonitorDetailText(monitor),
                Location = new Point(labelX, 45),
                AutoSize = false,
                Size = new Size(Math.Max(0, disableX - labelX - 10), 24),
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
                ForeColor = palette.TextSubtle,
                TextAlign = ContentAlignment.MiddleLeft
            };
            card.Controls.Add(detail);
            _toolTip.SetToolTip(detail, BuildMonitorTooltipText(monitor));

            var buttonOff = new ThemedButton
            {
                Text = "Disable",
                Location = new Point(disableX, buttonY),
                Size = new Size(ButtonWidth, ButtonHeight),
                Tag = monitor.StableKey,
                Tone = ThemedButtonTone.Danger
            };
            buttonOff.Click += ButtonOff_Click;
            card.Controls.Add(buttonOff);
            _toolTip.SetToolTip(buttonOff, "Disable this monitor. At least one display must remain active.");

            var buttonOn = new ThemedButton
            {
                Text = "Enable",
                Location = new Point(enableX, buttonY),
                Size = new Size(ButtonWidth, ButtonHeight),
                Tag = monitor.StableKey,
                Tone = ThemedButtonTone.Primary
            };
            buttonOn.Click += ButtonOn_Click;
            card.Controls.Add(buttonOn);
            _toolTip.SetToolTip(buttonOn, "Enable this exact monitor using its current Windows physical target.");

            Themer.ApplyStatusBadge(statusLabel, palette);

            _layoutRightMost = Math.Max(_layoutRightMost, card.Right);

            _controlsByKey[monitor.StableKey] = new MonitorControls
            {
                Card = card,
                DisableButton = buttonOff,
                EnableButton = buttonOn,
                StatusLabel = statusLabel,
                TitleLabel = label,
                IsBusy = _busy.Contains(monitor.StableKey)
            };
            _monitorCardOrder.Add(monitor.StableKey);
        }

        private void AttachMonitorCardDragSurface(Control surface, string stableKey)
        {
            surface.Cursor = Cursors.Hand;
            surface.MouseDown += (_, e) => BeginMonitorCardDrag(stableKey, surface, e);
            surface.MouseMove += (_, e) => UpdateMonitorCardDrag(stableKey, surface, e);
            surface.MouseUp += (_, e) => EndMonitorCardDrag(stableKey, surface, e);
            surface.MouseCaptureChanged += (_, __) =>
            {
                if (!_endingMonitorCardDrag && ReferenceEquals(_monitorCardDragCapture, surface))
                    FinishMonitorCardDrag(saveOrder: _monitorCardDragActive);
            };
        }

        private void BeginMonitorCardDrag(string stableKey, Control surface, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left ||
                _monitorCardOrder.Count < 2 ||
                _displayActionGate.CurrentCount == 0 ||
                !_controlsByKey.TryGetValue(stableKey, out var controls) ||
                controls.Card.IsDisposed)
            {
                return;
            }

            CancelMonitorCardDrag();
            _draggedMonitorKey = stableKey;
            _monitorCardDragCapture = surface;
            _monitorCardDragStartScreen = surface.PointToScreen(e.Location);
            _monitorCardLastPointerScreen = _monitorCardDragStartScreen;
            _monitorCardDragPointerOffsetY = _monitorCardDragStartScreen.Y -
                                             controls.Card.PointToScreen(Point.Empty).Y;
            _monitorCardOrderBeforeDrag.Clear();
            _monitorCardOrderBeforeDrag.AddRange(_monitorCardOrder);
            surface.Capture = true;
        }

        private void UpdateMonitorCardDrag(string stableKey, Control surface, MouseEventArgs e)
        {
            if (!string.Equals(stableKey, _draggedMonitorKey, StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(surface, _monitorCardDragCapture) ||
                (e.Button & MouseButtons.Left) == 0 ||
                !_controlsByKey.TryGetValue(stableKey, out var controls) ||
                controls.Card.IsDisposed)
            {
                return;
            }

            var pointerScreen = surface.PointToScreen(e.Location);
            _monitorCardLastPointerScreen = pointerScreen;
            if (!_monitorCardDragActive)
            {
                var dragSize = SystemInformation.DragSize;
                if (Math.Abs(pointerScreen.X - _monitorCardDragStartScreen.X) < dragSize.Width / 2 &&
                    Math.Abs(pointerScreen.Y - _monitorCardDragStartScreen.Y) < dragSize.Height / 2)
                {
                    return;
                }

                _monitorCardDragActive = true;
                controls.Card.BorderColor = (_uiSettings.DarkMode ? ThemePalette.Dark() : ThemePalette.Light()).Accent;
                controls.Card.Invalidate();
                controls.Card.BringToFront();
            }

            PositionDraggedMonitorCard(stableKey, controls, pointerScreen);
            _monitorCardAnimationTimer.Start();
        }

        private void PositionDraggedMonitorCard(
            string stableKey,
            MonitorControls controls,
            Point pointerScreen)
        {
            AutoScrollMonitorCards(pointerScreen.Y);
            var pointerClient = PointToClient(pointerScreen);
            int firstSlotTop = GetRowsStartY() + AutoScrollPosition.Y;
            int lastSlotTop = firstSlotTop + ((_monitorCardOrder.Count - 1) * RowVerticalGap);
            int draggedTop = Math.Clamp(
                pointerClient.Y - _monitorCardDragPointerOffsetY,
                firstSlotTop,
                lastSlotTop);
            controls.Card.Top = draggedTop;

            int destinationIndex = Math.Clamp(
                (int)Math.Round(
                    (draggedTop - firstSlotTop) / (double)RowVerticalGap,
                    MidpointRounding.AwayFromZero),
                0,
                _monitorCardOrder.Count - 1);
            int currentIndex = _monitorCardOrder.FindIndex(
                key => key.Equals(stableKey, StringComparison.OrdinalIgnoreCase));
            if (currentIndex >= 0 && currentIndex != destinationIndex)
            {
                _monitorCardOrder.RemoveAt(currentIndex);
                _monitorCardOrder.Insert(destinationIndex, stableKey);
            }
        }

        private void EndMonitorCardDrag(string stableKey, Control surface, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left ||
                !string.Equals(stableKey, _draggedMonitorKey, StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(surface, _monitorCardDragCapture))
            {
                return;
            }

            FinishMonitorCardDrag(saveOrder: _monitorCardDragActive);
        }

        private void FinishMonitorCardDrag(bool saveOrder)
        {
            if (_endingMonitorCardDrag)
                return;

            _endingMonitorCardDrag = true;
            try
            {
                var draggedKey = _draggedMonitorKey;
                var capture = _monitorCardDragCapture;
                _draggedMonitorKey = null;
                _monitorCardDragCapture = null;
                _monitorCardDragActive = false;
                if (capture != null)
                    capture.Capture = false;

                if (!string.IsNullOrWhiteSpace(draggedKey) &&
                    _controlsByKey.TryGetValue(draggedKey, out var controls) &&
                    !controls.Card.IsDisposed)
                {
                    controls.Card.BorderColor = (_uiSettings.DarkMode ? ThemePalette.Dark() : ThemePalette.Light()).Border;
                    controls.Card.Invalidate();
                }

                if (saveOrder && !_monitorCardOrder.SequenceEqual(
                        _monitorCardOrderBeforeDrag,
                        StringComparer.OrdinalIgnoreCase))
                {
                    SaveMonitorCardOrder();
                }

                _monitorCardAnimationTimer.Start();
            }
            finally
            {
                _monitorCardOrderBeforeDrag.Clear();
                _endingMonitorCardDrag = false;
            }
        }

        private void CancelMonitorCardDrag()
        {
            _monitorCardAnimationTimer.Stop();
            if (_draggedMonitorKey == null && _monitorCardDragCapture == null)
                return;

            FinishMonitorCardDrag(saveOrder: false);
            _monitorCardAnimationTimer.Stop();
        }

        private void SaveMonitorCardOrder()
        {
            var previousOrders = _aliasMap.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.PreferredOrder,
                StringComparer.OrdinalIgnoreCase);
            if (!MonitorOrderService.TryApplyVisibleOrder(_aliasMap, _monitorCardOrder, out var validationError))
            {
                RestoreMonitorOrders(previousOrders);
                RestoreMonitorCardOrderAfterSaveFailure(validationError);
                return;
            }

            var saveResult = _aliasStore.SaveWithResult(_aliasMap);
            LogPersistenceResult("save dragged monitor order", saveResult);
            if (!saveResult.Success)
            {
                RestoreMonitorOrders(previousOrders);
                RestoreMonitorCardOrderAfterSaveFailure(saveResult.ErrorMessage);
                return;
            }

            _log.Write($"Saved monitor card order: {string.Join(", ", _monitorCardOrder)}.");
        }

        private void RestoreMonitorOrders(IReadOnlyDictionary<string, int?> previousOrders)
        {
            foreach (var pair in previousOrders)
            {
                if (_aliasMap.TryGetValue(pair.Key, out var info))
                    info.PreferredOrder = pair.Value;
            }
        }

        private void RestoreMonitorCardOrderAfterSaveFailure(string errorMessage)
        {
            _monitorCardOrder.Clear();
            _monitorCardOrder.AddRange(_monitorCardOrderBeforeDrag);
            ThemedMessageBox.Warn(
                this,
                $"The monitor order could not be saved. {errorMessage}",
                "Reorder Monitors",
                _uiSettings.DarkMode);
        }

        private void AnimateMonitorCards()
        {
            if (IsDisposed || _monitorCardOrder.Count == 0)
            {
                _monitorCardAnimationTimer.Stop();
                return;
            }

            if (_monitorCardDragActive &&
                !string.IsNullOrWhiteSpace(_draggedMonitorKey) &&
                _controlsByKey.TryGetValue(_draggedMonitorKey, out var draggedControls) &&
                !draggedControls.Card.IsDisposed)
            {
                PositionDraggedMonitorCard(
                    _draggedMonitorKey,
                    draggedControls,
                    _monitorCardLastPointerScreen);
            }

            int firstSlotTop = GetRowsStartY() + AutoScrollPosition.Y;
            bool moved = false;
            for (int index = 0; index < _monitorCardOrder.Count; index++)
            {
                var stableKey = _monitorCardOrder[index];
                if (_monitorCardDragActive &&
                    stableKey.Equals(_draggedMonitorKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!_controlsByKey.TryGetValue(stableKey, out var controls) || controls.Card.IsDisposed)
                    continue;

                int targetTop = firstSlotTop + (index * RowVerticalGap);
                int difference = targetTop - controls.Card.Top;
                if (difference == 0)
                    continue;

                int step = Math.Abs(difference) <= 2
                    ? difference
                    : Math.Sign(difference) * Math.Max(2, (int)Math.Ceiling(Math.Abs(difference) * 0.35));
                controls.Card.Top += step;
                moved = true;
            }

            if (!moved && !_monitorCardDragActive)
                _monitorCardAnimationTimer.Stop();
        }

        private void AutoScrollMonitorCards(int pointerScreenY)
        {
            const int edgeSize = 44;
            const int scrollStep = 20;
            var pointerClient = PointToClient(new Point(PointToScreen(Point.Empty).X, pointerScreenY));
            int currentScroll = Math.Max(0, -AutoScrollPosition.Y);
            int maximumScroll = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
            int requestedScroll = currentScroll;

            if (pointerClient.Y < GetRowsStartY() + edgeSize)
                requestedScroll = Math.Max(0, currentScroll - scrollStep);
            else if (pointerClient.Y > ClientSize.Height - edgeSize)
                requestedScroll = Math.Min(maximumScroll, currentScroll + scrollStep);

            if (requestedScroll != currentScroll)
                AutoScrollPosition = new Point(0, requestedScroll);
        }

        private string BuildMonitorDetailText(DetectedMonitor monitor)
        {
            var hasSavedAlias = _aliasMap.TryGetValue(monitor.StableKey, out var info) &&
                                !string.IsNullOrWhiteSpace(info.Name);

            if (hasSavedAlias && !string.IsNullOrWhiteSpace(monitor.Name))
                return monitor.Name.Trim();
            if (!string.IsNullOrWhiteSpace(monitor.DeviceName))
                return monitor.DeviceName.Replace(@"\\.\", string.Empty);
            if (!string.IsNullOrWhiteSpace(monitor.SerialNumber))
                return monitor.SerialNumber.Trim();
            return "Detected";
        }

        private void ClearExistingMonitorPreferenceFlags(bool clearPreferred, bool clearFallback)
        {
            if (!clearPreferred && !clearFallback)
                return;

            foreach (var info in _aliasMap.Values)
            {
                if (clearPreferred)
                    info.IsPreferredPrimary = false;
                if (clearFallback)
                    info.IsFallbackPrimary = false;
            }
        }

        private static string BuildMonitorTooltipText(DetectedMonitor monitor)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(monitor.DeviceName)) lines.Add($"Device: {monitor.DeviceName}");
            if (!string.IsNullOrWhiteSpace(monitor.NativeTargetPath)) lines.Add($"Native target: {monitor.NativeTargetPath}");
            if (!string.IsNullOrWhiteSpace(monitor.Name)) lines.Add($"Name: {monitor.Name}");
            if (!string.IsNullOrWhiteSpace(monitor.MonitorKey)) lines.Add($"Registry: {monitor.MonitorKey}");
            lines.Add($"Stable key: {monitor.StableKey}");
            return string.Join(Environment.NewLine, lines);
        }

        // ---------- Button handlers ----------

        private async void ButtonOff_Click(object? sender, EventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string stableKey) return;

            if (_uiSettings.ConfirmBeforeDisable)
            {
                var name = GetAliasFor(stableKey);
                var choice = MessageBox.Show(
                    this,
                    $"Disable '{name}'?",
                    "Disable Monitor",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (choice != DialogResult.Yes)
                    return;
            }

            if (!await WaitForPendingMonitorRefreshAsync())
                return;
            if (!TryBeginDisplayAction("disable monitor"))
                return;

            try
            {
                var present = _detected.Where(d => d.IsPresent).ToList();
                var selectedMonitor = present.FirstOrDefault(d =>
                    d.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase));
                if (selectedMonitor?.IsActive != true)
                {
                    ThemedMessageBox.Info(this,
                        "This monitor is no longer active. Refresh and try again if its state looks wrong.",
                        "Monitor Switcher", _uiSettings.DarkMode);
                    return;
                }

                var activeCount = present.Count(d => d.IsActive);
                if (activeCount <= 1)
                {
                    ThemedMessageBox.Warn(this,
                        "You cannot disable this monitor — at least one monitor must remain active.",
                        "Monitor Switcher", _uiSettings.DarkMode);
                    return;
                }

                var disablingDevice = selectedMonitor.DeviceName?.Trim();
                if (string.IsNullOrWhiteSpace(disablingDevice) ||
                    _displayedDetectionUsedScreenFallback ||
                    !NativeDisplayProfileCodec.IsStrongTargetPath(selectedMonitor.NativeTargetPath))
                {
                    LogMonitorAction($"DISABLE {stableKey}: no target resolved.");
                    ThemedMessageBox.Info(this,
                        "Cannot disable: Windows did not provide a reliable current physical identity for this monitor. Refresh and try again.",
                        "Monitor Switcher", _uiSettings.DarkMode);
                    return;
                }
                LogMonitorAction($"DISABLE {stableKey}: selected current device '{disablingDevice}'.");
                _log.Write($"Disable requested for '{GetAliasFor(stableKey)}' using current device '{disablingDevice}'.");

                // Show WORKING… and lock the row
                SetRowBusy(stableKey, true);
                UpdateButtonStatus();

                try
                {
                    // Capture baseline layout before any primary-monitor topology change.
                    if (_uiSettings.AutoSaveLayoutBeforeDisable)
                    {
                        var autoSave = await AutoSaveSelectedLayoutBeforeDisableAsync();
                        if (!autoSave.Success)
                        {
                            _log.Write($"Disable stopped because automatic profile save was unsafe or failed: {autoSave.ErrorMessage}");
                            ThemedMessageBox.Warn(this,
                                autoSave.ErrorMessage,
                                "Monitor Switcher", _uiSettings.DarkMode);
                            return;
                        }
                    }

                    _lifetimeCancellation.Token.ThrowIfCancellationRequested();

                    CancelQueuedStartupLayoutRestore("disable monitor");
                    CancelQueuedReconnectLayoutRestore("disable monitor");

                    MoveWindowIfHostedOn(disablingDevice);
                    await Task.Delay(100, _lifetimeCancellation.Token);
                    _lifetimeCancellation.Token.ThrowIfCancellationRequested();

                    var disable = await DisableUsingTopologyAsync(
                        disablingDevice,
                        selectedMonitor.NativeTargetPath,
                        $"native disable {disablingDevice}");
                    if (!disable.Success)
                    {
                        ThemedMessageBox.Error(this,
                            disable.ErrorMessage,
                            "Monitor Switcher", _uiSettings.DarkMode);
                    }
                    else
                    {
                        LogMonitorAction($"DISABLE {stableKey}: completed by native topology update.");
                        _log.Write($"Disable result for '{GetAliasFor(stableKey)}': completed and verified by Windows CCD.");
                    }

                    // Give the desktop a brief moment to settle.
                    await Task.Delay(400, _lifetimeCancellation.Token);
                }
                finally
                {
                    SetRowBusy(stableKey, false);
                }

                await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // The application is closing.
            }
            finally
            {
                EndDisplayAction("disable monitor");
            }
        }

        private async Task<PrimaryDisableAttempt> DisableUsingTopologyAsync(
            string disablingDevice,
            string expectedDisableTargetPath,
            string logAction)
        {
            var fallback = GetTopologyFallbackScreenForDisable(disablingDevice);
            if (fallback.DeviceName.Equals(disablingDevice, StringComparison.OrdinalIgnoreCase))
            {
                return new PrimaryDisableAttempt
                {
                    Success = false,
                    ErrorMessage = "Cannot disable the monitor because no fallback monitor is available."
                };
            }

            _log.Write($"Disabling monitor {disablingDevice} with fallback primary {fallback.DeviceName}.");
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            var fallbackTargetPath = _detected.FirstOrDefault(monitor =>
                monitor.IsPresent &&
                monitor.IsActive &&
                MonitorTargetResolver.TargetsEquivalent(monitor.DeviceName, fallback.DeviceName))?.NativeTargetPath;
            if (!NativeDisplayProfileCodec.IsStrongTargetPath(expectedDisableTargetPath) ||
                !NativeDisplayProfileCodec.IsStrongTargetPath(fallbackTargetPath))
            {
                return new PrimaryDisableAttempt
                {
                    Success = false,
                    ErrorMessage =
                        "Cannot disable the monitor because Windows did not provide unique physical identities for both affected displays."
                };
            }
            var result = _topologySvc.DisableDisplayUsingFallbackPrimary(
                disablingDevice,
                fallback.DeviceName,
                expectedDisableTargetPath,
                fallbackTargetPath!);
            LogTopologyResult(logAction, result);
            await Task.Delay(700, _lifetimeCancellation.Token);

            return new PrimaryDisableAttempt
            {
                Success = result.Success,
                ErrorMessage = result.Success
                    ? string.Empty
                    : $"Disable failed before the monitor could be deactivated. {result.Message}"
            };
        }

        private Screen GetTopologyFallbackScreenForDisable(string disablingDevice)
        {
            var primary = Screen.PrimaryScreen;
            if (primary != null &&
                !primary.DeviceName.Equals(disablingDevice, StringComparison.OrdinalIgnoreCase))
            {
                return primary;
            }

            return GetFallbackActiveScreen(disablingDevice);
        }


        private async void ButtonOn_Click(object? sender, EventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string stableKey) return;

            if (!await WaitForPendingMonitorRefreshAsync())
                return;
            if (!TryBeginDisplayAction("enable monitor"))
                return;

            try
            {
                var currentDetection = await _detectSvc.DetectWithStatusAsync(
                    _lifetimeCancellation.Token);
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                var matches = currentDetection.Monitors
                    .Where(monitor =>
                        monitor.IsPresent &&
                        !monitor.IsActive &&
                        monitor.StableKey.Equals(stableKey, StringComparison.OrdinalIgnoreCase) &&
                        NativeDisplayProfileCodec.IsStrongTargetPath(monitor.NativeTargetPath))
                    .ToList();
                if (currentDetection.UsedScreenFallback || matches.Count != 1)
                {
                    LogMonitorAction($"ENABLE {stableKey}: current native target was unavailable or ambiguous.");
                    ThemedMessageBox.Info(this,
                        "Cannot enable this monitor safely because Windows did not report one unique, currently connected inactive target. Press Refresh and try again.",
                        "Monitor Switcher", _uiSettings.DarkMode);
                    return;
                }

                var selectedMonitor = matches[0];
                var currentTarget = selectedMonitor.NativeTargetPath?.Trim();
                if (string.IsNullOrWhiteSpace(currentTarget))
                {
                    ThemedMessageBox.Info(this,
                        "Cannot enable this monitor because Windows did not provide its current native target path.",
                        "Monitor Switcher", _uiSettings.DarkMode);
                    return;
                }

                CancelQueuedStartupLayoutRestore("enable monitor");
                CancelQueuedReconnectLayoutRestore("enable monitor");
                LogMonitorAction($"ENABLE {stableKey}: current native target '{currentTarget}'.");
                _log.Write($"Enable requested for '{GetAliasFor(stableKey)}' using its current native target.");

                // Show WORKING… and lock the row
                SetRowBusy(stableKey, true);
                UpdateButtonStatus();

                try
                {
                    _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                    var result = _topologySvc.EnableDisplay(currentTarget);
                    LogTopologyResult($"native enable {stableKey}", result);
                    if (!result.Success)
                    {
                        ThemedMessageBox.Error(this,
                            $"Enable failed. {result.Message}",
                            "Monitor Switcher", _uiSettings.DarkMode);
                        await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
                        return;
                    }

                    var verification = await WaitForEnableDetectionAsync(stableKey, currentTarget);
                    if (verification.State != MonitorActivityState.Active)
                    {
                        _log.Write(
                            $"Enable for '{GetAliasFor(stableKey)}' could not be tied back to the selected identity. {verification.Detail}");
                        ThemedMessageBox.Warn(this,
                            "Windows activated a display path, but the selected monitor identity could not be verified safely. Press Refresh before another display action.",
                            "Monitor Switcher", _uiSettings.DarkMode);
                        await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
                        return;
                    }
                }
                finally
                {
                    SetRowBusy(stableKey, false);
                }

                await FinaliseSuccessfulEnableAsync();
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // The application is closing.
            }
            finally
            {
                EndDisplayAction("enable monitor");
            }
        }

        private async Task FinaliseSuccessfulEnableAsync()
        {
            await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            bool restoredLayout = false;
            var profile = SelectedLayoutProfileName();
            var path = SelectedLayoutPath();
            var savedIdentities = LayoutIdentityStore.Load(path);
            bool exactSavedSetActive = !_displayedDetectionUsedScreenFallback &&
                                       File.Exists(path) &&
                                       DisplayTopologyService.IsExactSavedMonitorSetActive(
                                           path,
                                           _detected,
                                           savedIdentities);
            if (exactSavedSetActive)
            {
                var result = await ApplySavedLayoutTopologyWithRetryAsync(
                    profile,
                    path,
                    requireReliableDetection: true);
                if (result.Success)
                {
                    _log.Write($"Auto-restored exact identity-matched layout profile '{profile}' after enable.");
                    restoredLayout = true;
                }
                else
                {
                    _log.Write(
                        result.RollbackAttempted && !result.RollbackVerified
                            ? $"Auto-restore for layout profile '{profile}' failed and rollback could not be verified. No further topology action will run."
                            : $"Auto-restore for layout profile '{profile}' failed; any attempted change was rolled back safely.");
                }

                await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                if (result.RollbackAttempted && !result.RollbackVerified)
                {
                    ThemedMessageBox.Warn(this,
                        result.Message,
                        "Monitor Switcher", _uiSettings.DarkMode);
                    return;
                }
            }
            else
            {
                _log.Write(
                    $"Auto-restore skipped after enable because the current active monitor set did not exactly match saved profile '{profile}' or detection was inconclusive.");
            }

            var primaryResult = await EnforcePrimaryMonitorOrderAsync(
                allowAutomaticFallback: !restoredLayout);
            if (HasUnverifiedRollback(primaryResult))
            {
                SurfaceUnverifiedTopology(primaryResult!, "Monitor Switcher");
                return;
            }
            if (primaryResult?.Success == true)
            {
                await Task.Delay(400, _lifetimeCancellation.Token);
                await RefreshMonitorsAndUiAsync(allowDuringDisplayAction: true);
            }
        }

        private async Task<MonitorActivityVerification> WaitForEnableDetectionAsync(
            string stableKey,
            string? deviceName)
        {
            var latest = new List<DetectedMonitor>();
            string detail = string.Empty;
            var lastState = MonitorActivityState.Inconclusive;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(500, _lifetimeCancellation.Token);
                var detection = await _detectSvc.DetectWithStatusAsync(_lifetimeCancellation.Token);
                latest = detection.Monitors;

                lastState = ResolveMonitorActivityState(
                    stableKey,
                    deviceName,
                    detection,
                    verifyingEnable: true,
                    out detail);
                if (lastState == MonitorActivityState.Active)
                    return new MonitorActivityVerification(lastState, latest, detail);
            }

            return new MonitorActivityVerification(lastState, latest, detail);
        }

        private MonitorActivityState ResolveMonitorActivityState(
            string stableKey,
            string? deviceName,
            MonitorDetectionResult detection,
            bool verifyingEnable,
            out string detail)
        {
            if (!detection.UsedScreenFallback)
            {
                if (MonitorTargetResolver.IsStableKeyActive(detection.Monitors, stableKey))
                {
                    detail = "Native detection reported the stable key active.";
                    return MonitorActivityState.Active;
                }

                if (verifyingEnable &&
                    !string.IsNullOrWhiteSpace(deviceName) &&
                    _topologySvc.TryGetDisplayActiveState(deviceName, out var activeByCcd, out _) &&
                    activeByCcd)
                {
                    detail =
                        $"CCD reported the selected target path '{deviceName}' active, but reliable detection " +
                        $"did not tie it back to stable identity '{stableKey}'.";
                    return MonitorActivityState.Inconclusive;
                }

                detail = "Native detection reported the stable key inactive.";
                return MonitorActivityState.Inactive;
            }

            var ccdError = string.Empty;
            if (!string.IsNullOrWhiteSpace(deviceName) &&
                _topologySvc.TryGetDisplayActiveState(deviceName, out var ccdActive, out ccdError))
            {
                if (verifyingEnable && !ccdActive)
                {
                    detail =
                        $"CCD did not find the pre-enable device '{deviceName}' active, but fallback detection " +
                        "cannot rule out Windows assigning the monitor a different device name.";
                    return MonitorActivityState.Inconclusive;
                }

                detail = $"CCD reported '{deviceName}' {(ccdActive ? "active" : "inactive")}.";
                return ccdActive ? MonitorActivityState.Active : MonitorActivityState.Inactive;
            }

            detail = string.IsNullOrWhiteSpace(deviceName)
                ? "Fallback detection cannot map this stable monitor identity to a current display device."
                : $"Fallback detection was used and CCD could not confirm '{deviceName}': {ccdError}";
            return MonitorActivityState.Inconclusive;
        }

        private void UpdateButtonStatus()
        {
            var palette = _uiSettings.DarkMode ? ThemePalette.Dark() : ThemePalette.Light();

            var present = _detected.Where(d => d.IsPresent).ToList();
            int activeCount = present.Count(d => d.IsActive);

            foreach (var kv in _controlsByKey)
            {
                var key = kv.Key;
                var ctrls = kv.Value;

                if (ctrls.IsBusy)
                {
                    ApplyMonitorStatus(ctrls.StatusLabel, MonitorVisualStatus.Working, palette);

                    ctrls.DisableButton.Enabled = false;
                    ctrls.EnableButton.Enabled = false;
                    Themer.ApplyButtonStyle(ctrls.DisableButton, palette);
                    Themer.ApplyButtonStyle(ctrls.EnableButton, palette);

                    continue;
                }

                var live = present.FirstOrDefault(d => d.StableKey.Equals(key, StringComparison.OrdinalIgnoreCase));
                bool isPresent = live != null;
                bool isActive = live?.IsActive == true;
                ApplyMonitorStatus(
                    ctrls.StatusLabel,
                    GetMonitorVisualStatus(isPresent, isActive),
                    palette);

                bool canDisable = !_displayedDetectionUsedScreenFallback &&
                                  isPresent &&
                                  isActive &&
                                  activeCount > 1 &&
                                  NativeDisplayProfileCodec.IsStrongTargetPath(live?.NativeTargetPath);
                bool canEnable = !_displayedDetectionUsedScreenFallback &&
                                 present.Count(d =>
                                     !d.IsActive &&
                                     d.StableKey.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrWhiteSpace(d.NativeTargetPath)) == 1;

                ctrls.DisableButton.Enabled = canDisable;
                ctrls.EnableButton.Enabled = canEnable;
                Themer.ApplyButtonStyle(ctrls.DisableButton, palette);
                Themer.ApplyButtonStyle(ctrls.EnableButton, palette);
            }
        }


        private async Task<DisplayTopologyResult?> EnforcePrimaryMonitorOrderAsync(bool allowAutomaticFallback)
        {
            var preferredResult = await EnforcePreferredPrimaryIfActiveAsync("preferred primary");
            if (preferredResult?.Success == true || HasUnverifiedRollback(preferredResult))
                return preferredResult;

            if (!allowAutomaticFallback)
                return preferredResult;

            var target = PrimaryMonitorPreference.ResolveLeftMostActiveTarget(_detected, _aliasMap);
            if (!string.IsNullOrWhiteSpace(target))
                return await SetPrimaryWithTopologyAsync(target, "left-most active primary");

            return preferredResult;
        }

        private async Task<DisplayTopologyResult?> EnforcePreferredPrimaryIfActiveAsync(string reason)
        {
            var target = PrimaryMonitorPreference.ResolvePreferredPrimaryTarget(_detected, _aliasMap);
            if (string.IsNullOrWhiteSpace(target))
                return null;

            return await SetPrimaryWithTopologyAsync(target, reason);
        }

        private Task<DisplayTopologyResult> SetPrimaryWithTopologyAsync(string target, string reason)
        {
            var targetMatches = _detected
                .Where(monitor =>
                    monitor.IsPresent &&
                    monitor.IsActive &&
                    MonitorTargetResolver.TargetsEquivalent(monitor.DeviceName, target) &&
                    NativeDisplayProfileCodec.IsStrongTargetPath(monitor.NativeTargetPath))
                .ToList();
            if (_displayedDetectionUsedScreenFallback || targetMatches.Count != 1)
            {
                _log.Write(
                    $"Skipped set primary ({reason}); '{target}' did not resolve to one reliably detected physical display.");
                return Task.FromResult(new DisplayTopologyResult
                {
                    Success = false,
                    Message =
                        $"'{target}' did not resolve to one reliably detected physical display; no primary-display change was made."
                });
            }

            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            var result = _topologySvc.SetPrimaryDisplay(target, targetMatches[0].NativeTargetPath);
            LogTopologyResult($"CCD set primary ({reason})", result);
            return Task.FromResult(result);
        }

        private static bool HasUnverifiedRollback(DisplayTopologyResult? result)
            => result?.RollbackAttempted == true && !result.RollbackVerified;

        private void SurfaceUnverifiedTopology(DisplayTopologyResult result, string title)
        {
            _log.Write(result.Message);
            if (IsNormalVisible)
            {
                ThemedMessageBox.Warn(this, result.Message, title, _uiSettings.DarkMode);
                return;
            }

            _trayIcon?.ShowBalloonTip(
                5000,
                "Monitor Switcher",
                string.IsNullOrWhiteSpace(result.Message)
                    ? "A display rollback could not be verified. Open Windows Display Settings before another monitor action."
                    : result.Message,
                ToolTipIcon.Warning);
        }

        // ---------- Helpers ----------

        private void ReconcileAliasesForDetected()
        {
            if (_aliasMap.Count == 0 || _detected.Count == 0) return;

            var detectedKeys = new HashSet<string>(_detected.Select(d => d.StableKey), StringComparer.OrdinalIgnoreCase);
            var missing = _detected.Where(d =>
                !_aliasMap.ContainsKey(d.StableKey) ||
                string.IsNullOrWhiteSpace(_aliasMap[d.StableKey].Name)).ToList();
            if (missing.Count == 0) return;

            foreach (var m in missing)
            {
                if (_aliasMap.TryGetValue(m.StableKey, out var existing) &&
                    !string.IsNullOrWhiteSpace(existing.Name))
                    continue;

                var match = FindUniqueAliasMatch(m, detectedKeys);
                if (match == null) continue;

                var oldKey = match.Value.Key;
                var info = match.Value.Value;

                _aliasMap.Remove(oldKey);
                _aliasMap[m.StableKey] = info;
            }
        }

        private KeyValuePair<string, MonitorInfo>? FindUniqueAliasMatch(DetectedMonitor m, HashSet<string> detectedKeys)
        {
            // Only consider aliases that are NOT currently detected, to avoid swaps.
            IEnumerable<KeyValuePair<string, MonitorInfo>> candidates = _aliasMap
                .Where(kv => !detectedKeys.Contains(kv.Key));

            var legacyDriverMatch = FindUniqueLegacyDriverAliasMatch(m, _detected, candidates);
            if (legacyDriverMatch != null) return legacyDriverMatch;

            KeyValuePair<string, MonitorInfo>? TryMatch(Func<MonitorInfo, string?> selector, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                var matches = candidates
                    .Where(kv => StringsEqual(selector(kv.Value), value))
                    .ToList();
                return matches.Count == 1 ? matches[0] : null;
            }

            var match = TryMatch(i => i.LastSerialNumber, m.SerialNumber);
            if (match != null) return match;

            match = TryMatch(i => i.LastInstanceId, m.InstanceId);
            if (match != null) return match;

            match = TryMatch(i => i.LastRegistryKey, m.MonitorKey);
            if (match != null) return match;

            return null;
        }

        internal static KeyValuePair<string, MonitorInfo>? FindUniqueLegacyDriverAliasMatch(
            DetectedMonitor monitor,
            IReadOnlyCollection<DetectedMonitor> detected,
            IEnumerable<KeyValuePair<string, MonitorInfo>> candidates)
        {
            if (!NativeDisplayDetection.TryNormalizeDriverSoftwareKey(
                    monitor.DriverRegistryKey,
                    out var currentDriverKey))
            {
                return null;
            }

            var currentMatches = detected.Count(candidate =>
                NativeDisplayDetection.TryNormalizeDriverSoftwareKey(
                    candidate.DriverRegistryKey,
                    out var candidateDriverKey) &&
                candidateDriverKey.Equals(currentDriverKey, StringComparison.OrdinalIgnoreCase));
            if (currentMatches != 1)
                return null;

            var legacyMatches = candidates
                .Where(candidate => candidate.Key.TrimStart().StartsWith("MK:", StringComparison.OrdinalIgnoreCase))
                .Where(candidate =>
                    NativeDisplayDetection.TryNormalizeDriverSoftwareKey(
                        candidate.Key,
                        out var legacyDriverKey) &&
                    legacyDriverKey.Equals(currentDriverKey, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();

            return legacyMatches.Count == 1 ? legacyMatches[0] : null;
        }

        private void RemoveShadowedDeviceAliases()
        {
            if (_aliasMap.Count == 0 || _detected.Count == 0) return;

            var detectedKeys = new HashSet<string>(_detected.Select(d => d.StableKey), StringComparer.OrdinalIgnoreCase);
            var presentByDevice = _detected
                .Where(d => d.IsPresent && !string.IsNullOrWhiteSpace(d.DeviceName))
                .Select(d => new
                {
                    Monitor = d,
                    Device = NormalizeDeviceNameForComparison(d.DeviceName)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Device))
                .GroupBy(x => x.Device, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Monitor, StringComparer.OrdinalIgnoreCase);

            if (presentByDevice.Count == 0) return;

            foreach (var kv in _aliasMap.ToList())
            {
                var staleKey = kv.Key;
                if (detectedKeys.Contains(staleKey) || !IsDeviceFallbackStableKey(staleKey))
                    continue;

                var source = kv.Value;
                var keyDevice = NormalizeDeviceNameForComparison(staleKey);
                var lastDevice = NormalizeDeviceNameForComparison(source.LastDeviceName);

                DetectedMonitor? live = null;
                if (!string.IsNullOrWhiteSpace(keyDevice))
                    presentByDevice.TryGetValue(keyDevice, out live);
                if (live == null && !string.IsNullOrWhiteSpace(lastDevice))
                    presentByDevice.TryGetValue(lastDevice, out live);
                if (live == null || live.StableKey.Equals(staleKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!_aliasMap.TryGetValue(live.StableKey, out var target))
                {
                    target = new MonitorInfo();
                    _aliasMap[live.StableKey] = target;
                }

                MergeMonitorInfo(target, source, live.StableKey);
                if (source.IsPreferredPrimary)
                    EnsureOnlyPreferredPrimary(live.StableKey);
                if (source.IsFallbackPrimary)
                    EnsureOnlyFallbackPrimary(live.StableKey);

                _aliasMap.Remove(staleKey);
                _log.Write($"Removed stale saved display key '{staleKey}' because '{live.StableKey}' is present on {live.DeviceName}.");
            }
        }

        private void SuppressShadowedDeviceFallbackDetections()
        {
            if (_aliasMap.Count == 0 || _detected.Count == 0) return;

            var detectedKeys = new HashSet<string>(_detected.Select(d => d.StableKey), StringComparer.OrdinalIgnoreCase);
            var missingSavedHardwareAliases = _aliasMap
                .Where(kv =>
                    !IsDeviceFallbackStableKey(kv.Key) &&
                    !detectedKeys.Contains(kv.Key) &&
                    HasRestoreTarget(kv.Value))
                .Select(kv => kv.Value)
                .ToList();

            if (missingSavedHardwareAliases.Count == 0)
                return;

            var suppressed = _detected
                .Where(d =>
                    IsDeviceFallbackStableKey(d.StableKey) &&
                    !d.IsActive &&
                    IsGeneratedDeviceFallbackAlias(d.StableKey) &&
                    MatchesMissingSavedHardwareAliasTarget(d, missingSavedHardwareAliases))
                .ToList();

            if (suppressed.Count == 0)
                return;

            var suppressedKeys = new HashSet<string>(
                suppressed.Select(d => d.StableKey),
                StringComparer.OrdinalIgnoreCase);

            _detected = _detected
                .Where(d => !suppressedKeys.Contains(d.StableKey))
                .ToList();

            foreach (var key in suppressedKeys)
            {
                if (_aliasMap.TryGetValue(key, out var info) && IsGeneratedDeviceFallbackAlias(key, info))
                    _aliasMap.Remove(key);
            }

            foreach (var monitor in suppressed)
            {
                _log.Write(
                    $"Suppressed anonymous display fallback '{monitor.StableKey}' on {monitor.DeviceName} because a saved hardware monitor is currently missing.");
            }
        }

        private static bool IsDeviceFallbackStableKey(string stableKey)
            => stableKey.Trim().StartsWith("DEV:", StringComparison.OrdinalIgnoreCase);

        private static bool HasRestoreTarget(MonitorInfo info)
            => !string.IsNullOrWhiteSpace(info.LastDeviceName) ||
               info.KnownTargets.Any(t => !string.IsNullOrWhiteSpace(t));

        private static bool MatchesMissingSavedHardwareAliasTarget(DetectedMonitor monitor, IReadOnlyCollection<MonitorInfo> missingAliases)
        {
            var device = NormalizeDeviceNameForComparison(monitor.DeviceName);
            if (string.IsNullOrWhiteSpace(device))
                device = NormalizeDeviceNameForComparison(monitor.StableKey);
            if (string.IsNullOrWhiteSpace(device))
                return false;

            return missingAliases.Any(info =>
                DeviceTargetEquals(info.LastDeviceName, device) ||
                info.KnownTargets.Any(t => IsLikelyDeviceName(t) && DeviceTargetEquals(t, device)));
        }

        private static bool DeviceTargetEquals(string? target, string device)
            => string.Equals(NormalizeDeviceNameForComparison(target), device, StringComparison.OrdinalIgnoreCase);

        private bool IsGeneratedDeviceFallbackAlias(string stableKey)
        {
            return !_aliasMap.TryGetValue(stableKey, out var info) ||
                   IsGeneratedDeviceFallbackAlias(stableKey, info);
        }

        private static bool IsGeneratedDeviceFallbackAlias(string stableKey, MonitorInfo info)
        {
            return IsPlaceholderAlias(info.Name, stableKey) &&
                   string.IsNullOrWhiteSpace(info.LastSerialNumber) &&
                   string.IsNullOrWhiteSpace(info.LastInstanceId) &&
                   string.IsNullOrWhiteSpace(info.LastRegistryKey) &&
                   string.IsNullOrWhiteSpace(info.LastMonitorId);
        }

        private static string NormalizeDeviceNameForComparison(string? value)
        {
            var t = NormaliseTarget(value ?? string.Empty).Trim();
            if (t.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase))
                t = t[4..].Trim();

            t = t.Replace('/', '\\');
            while (t.Contains("\\\\", StringComparison.Ordinal))
                t = t.Replace("\\\\", "\\");
            return t;
        }

        private static void MergeMonitorInfo(MonitorInfo target, MonitorInfo source, string targetStableKey)
        {
            if (IsPlaceholderAlias(target.Name, targetStableKey) && !string.IsNullOrWhiteSpace(source.Name))
                target.Name = source.Name;

            if (source.IsPreferredPrimary)
                target.IsPreferredPrimary = true;
            if (source.IsFallbackPrimary)
                target.IsFallbackPrimary = true;

            if (!target.PreferredOrder.HasValue && source.PreferredOrder.HasValue)
                target.PreferredOrder = source.PreferredOrder;
            if (!target.LastKnownX.HasValue && source.LastKnownX.HasValue)
                target.LastKnownX = source.LastKnownX;

            target.LastDeviceName = FirstNonBlank(target.LastDeviceName, source.LastDeviceName);
            target.LastRegistryKey = FirstNonBlank(target.LastRegistryKey, source.LastRegistryKey);
            target.LastSerialNumber = FirstNonBlank(target.LastSerialNumber, source.LastSerialNumber);
            target.LastInstanceId = FirstNonBlank(target.LastInstanceId, source.LastInstanceId);
            target.LastMonitorId = FirstNonBlank(target.LastMonitorId, source.LastMonitorId);

            foreach (var raw in source.KnownTargets)
            {
                var targetName = NormaliseTarget(raw);
                if (string.IsNullOrWhiteSpace(targetName)) continue;
                if (target.KnownTargets.Any(x => x.Equals(targetName, StringComparison.OrdinalIgnoreCase))) continue;
                target.KnownTargets.Add(targetName);
            }

            const int MaxKnownTargets = 8;
            if (target.KnownTargets.Count > MaxKnownTargets)
                target.KnownTargets.RemoveRange(MaxKnownTargets, target.KnownTargets.Count - MaxKnownTargets);
        }

        private static bool IsPlaceholderAlias(string? alias, string stableKey)
        {
            if (string.IsNullOrWhiteSpace(alias)) return true;
            var value = alias.Trim();
            return value.Equals(stableKey, StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase);
        }

        private static string? FirstNonBlank(string? current, string? fallback)
            => !string.IsNullOrWhiteSpace(current) ? current : fallback;

        private void EnsureOnlyPreferredPrimary(string stableKey)
        {
            foreach (var key in _aliasMap.Keys.ToList())
            {
                var info = _aliasMap[key];
                info.IsPreferredPrimary = key.Equals(stableKey, StringComparison.OrdinalIgnoreCase);
                _aliasMap[key] = info;
            }
        }

        private void EnsureOnlyFallbackPrimary(string stableKey)
        {
            foreach (var key in _aliasMap.Keys.ToList())
            {
                var info = _aliasMap[key];
                info.IsFallbackPrimary = key.Equals(stableKey, StringComparison.OrdinalIgnoreCase);
                _aliasMap[key] = info;
            }
        }

        private static string GetMonitorModelKey(string? monitorId)
        {
            if (string.IsNullOrWhiteSpace(monitorId)) return string.Empty;
            var t = monitorId.Trim();

            // Typical format: MONITOR\AOC2730\{guid}\0001 -> keep MONITOR\AOC2730
            var parts = t.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{parts[0]}\\{parts[1]}";

            return t;
        }

        private static bool StringsEqual(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private string GetAliasFor(string stableKey)
        {
            return _aliasMap.TryGetValue(stableKey, out var info) && !string.IsNullOrWhiteSpace(info.Name)
                ? info.Name
                : stableKey;
        }

        private string GetPresentationTitle(DetectedMonitor monitor)
        {
            if (_aliasMap.TryGetValue(monitor.StableKey, out var info) &&
                !string.IsNullOrWhiteSpace(info.Name))
            {
                return info.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(monitor.Name))
                return monitor.Name.Trim();

            if (!string.IsNullOrWhiteSpace(monitor.DeviceName))
                return monitor.DeviceName.Replace(@"\\.\", string.Empty);

            return "Detected monitor";
        }

        private void LogDetectionSnapshotIfChanged()
        {
            var rows = _detected
                .OrderBy(d => d.PositionX)
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.StableKey, StringComparer.OrdinalIgnoreCase)
                .Select(d => string.Join(", ",
                    $"key={LogValue(d.StableKey)}",
                    $"device={LogValue(d.DeviceName)}",
                    $"nativeTarget={LogValue(d.NativeTargetPath)}",
                    $"active={d.IsActive}",
                    $"present={d.IsPresent}",
                    $"x={d.PositionX}",
                    $"serial={LogValue(d.SerialNumber)}",
                    $"monitorId={LogValue(d.MonitorId)}",
                    $"instanceId={LogValue(d.InstanceId)}",
                    $"monitorKey={LogValue(d.MonitorKey)}"))
                .ToList();

            var signature = string.Join("|", rows);
            if (signature.Equals(_lastDetectionLogSignature, StringComparison.Ordinal))
                return;

            _lastDetectionLogSignature = signature;

            int active = _detected.Count(d => d.IsPresent && d.IsActive);
            int present = _detected.Count(d => d.IsPresent);
            _log.Write($"Detected monitor state changed: active={active}, present={present}, rows={_detected.Count}.");

            foreach (var row in rows)
                _log.Write($"Detected monitor: {row}.");
        }

        private static string LogValue(string? value)
            => string.IsNullOrWhiteSpace(value) ? "<blank>" : value.Trim();

        private static void LogMonitorAction(string message)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }

        private void EnsureUserLayoutPath()
        {
            if (File.Exists(_layoutPath))
                return;

            try
            {
                if (File.Exists(_bundledLayoutPath))
                {
                    var bundledIdentityPath = LayoutIdentityStore.GetIdentityPath(_bundledLayoutPath);
                    if (!File.Exists(bundledIdentityPath))
                    {
                        _log.Write(
                            "Skipped bundled layout migration because its matching physical identity sidecar was missing.");
                        return;
                    }

                    var stagedLayout = LayoutProfileTransaction.CreateStagingLayoutPath(_layoutPath);
                    try
                    {
                        File.Copy(_bundledLayoutPath, stagedLayout, overwrite: false);
                        File.Copy(
                            bundledIdentityPath,
                            LayoutIdentityStore.GetIdentityPath(stagedLayout),
                            overwrite: false);
                        var migration = LayoutProfileTransaction.Commit(stagedLayout, _layoutPath);
                        LogPersistenceResult("migrate bundled layout profile pair", migration);
                        var recovery = _profileStore.RecoverProfileTransactionsWithResult();
                        _profileTransactionsHealthy = recovery.Success;
                        LogPersistenceResult("recover layout profile transactions after migration", recovery);
                    }
                    finally
                    {
                        LayoutProfileTransaction.DeleteStagingArtifacts(stagedLayout);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write($"Bundled layout migration failed: {ex.Message}");
                var recovery = _profileStore.RecoverProfileTransactionsWithResult();
                _profileTransactionsHealthy = recovery.Success;
                LogPersistenceResult("recover layout profile transactions after failed migration", recovery);
            }
        }

        // ---------- Window bounds persistence ----------

        private void RestoreWindowBounds()
        {
            if (_uiSettings.WindowWidth > 0 && _uiSettings.WindowHeight > 0)
            {
                StartPosition = FormStartPosition.Manual;
                var rect = new Rectangle(_uiSettings.WindowX, _uiSettings.WindowY, _uiSettings.WindowWidth, _uiSettings.WindowHeight);

                // Ensure at least partially on-screen
                var isOnScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
                if (!isOnScreen)
                {
                    StartPosition = FormStartPosition.CenterScreen;
                    return;
                }
                Bounds = rect;
            }
        }

        private void SaveWindowBounds()
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _uiSettings.WindowX = bounds.X;
                _uiSettings.WindowY = bounds.Y;
                _uiSettings.WindowWidth = bounds.Width;
                _uiSettings.WindowHeight = bounds.Height;
            }
        }
    }

    // Small UI handle bag (no logic)
    internal sealed class MonitorControls
    {
        public ThemedCardPanel Card { get; set; } = new();
        public Button DisableButton { get; set; } = new();
        public Button EnableButton { get; set; } = new();
        public Label StatusLabel { get; set; } = new();
        public Label TitleLabel { get; set; } = new();
        public bool IsBusy { get; set; }
    }
}
