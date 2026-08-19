using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// Simple palette for light/dark themes.
    /// </summary>
    internal sealed class ThemePalette
    {
        public Color Back { get; init; }
        public Color Surface { get; init; }
        public Color SurfaceMuted { get; init; }
        public Color Border { get; init; }
        public Color Text { get; init; }
        public Color TextSubtle { get; init; }
        public Color Accent { get; init; }
        public Color AccentHover { get; init; }
        public Color AccentPressed { get; init; }
        public Color AccentText { get; init; }
        public Color Danger { get; init; }
        public Color DangerSurface { get; init; }
        public Color StatusOk { get; init; }
        public Color StatusOkBack { get; init; }
        public Color StatusWarn { get; init; }
        public Color StatusWarnBack { get; init; }
        public Color StatusBusy { get; init; }
        public Color StatusBusyBack { get; init; }
        public Color StatusOfflineBack { get; init; }

        public static ThemePalette Light() => new ThemePalette
        {
            Back = Color.FromArgb(244, 246, 248),
            Surface = Color.FromArgb(255, 255, 255),
            SurfaceMuted = Color.FromArgb(238, 241, 245),
            Border = Color.FromArgb(214, 219, 226),
            Text = Color.FromArgb(31, 41, 55),
            TextSubtle = Color.FromArgb(102, 112, 133),
            Accent = Color.FromArgb(37, 99, 235),
            AccentHover = Color.FromArgb(29, 78, 216),
            AccentPressed = Color.FromArgb(30, 64, 175),
            AccentText = Color.White,
            Danger = Color.FromArgb(180, 35, 24),
            DangerSurface = Color.FromArgb(254, 235, 233),
            StatusOk = Color.FromArgb(22, 114, 60),
            StatusOkBack = Color.FromArgb(229, 246, 235),
            StatusWarn = Color.FromArgb(181, 71, 8),
            StatusWarnBack = Color.FromArgb(255, 239, 224),
            StatusBusy = Color.FromArgb(154, 103, 0),
            StatusBusyBack = Color.FromArgb(255, 244, 204),
            StatusOfflineBack = Color.FromArgb(234, 237, 241)
        };

        public static ThemePalette Dark() => new ThemePalette
        {
            Back = Color.FromArgb(30, 30, 30),
            Surface = Color.FromArgb(39, 43, 51),
            SurfaceMuted = Color.FromArgb(48, 53, 63),
            Border = Color.FromArgb(62, 69, 82),
            Text = Color.FromArgb(242, 244, 247),
            TextSubtle = Color.FromArgb(166, 176, 193),
            Accent = Color.FromArgb(37, 99, 235),
            AccentHover = Color.FromArgb(29, 78, 216),
            AccentPressed = Color.FromArgb(30, 64, 175),
            AccentText = Color.White,
            Danger = Color.FromArgb(255, 139, 132),
            DangerSurface = Color.FromArgb(79, 43, 44),
            StatusOk = Color.FromArgb(117, 209, 139),
            StatusOkBack = Color.FromArgb(31, 68, 44),
            StatusWarn = Color.FromArgb(255, 184, 107),
            StatusWarnBack = Color.FromArgb(74, 51, 31),
            StatusBusy = Color.FromArgb(255, 204, 102),
            StatusBusyBack = Color.FromArgb(73, 58, 27),
            StatusOfflineBack = Color.FromArgb(49, 54, 64)
        };
    }

    internal enum ThemedButtonTone
    {
        Neutral,
        Primary,
        Danger
    }

    internal enum MonitorVisualStatus
    {
        Online,
        Disabled,
        Offline,
        Working
    }

    internal sealed class ThemedButton : Button
    {
        private bool _accessibilitySelected;

        public ThemedButtonTone Tone { get; set; }

        /// <summary>
        /// Exposes a real MSAA/UI Automation selected state when this button is
        /// used as a custom page tab. Visual tone alone is not discoverable by
        /// screen readers.
        /// </summary>
        public bool AccessibilitySelected
        {
            get => _accessibilitySelected;
            set
            {
                if (_accessibilitySelected == value)
                    return;

                _accessibilitySelected = value;
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
            => new ThemedButtonAccessibleObject(this);

        private sealed class ThemedButtonAccessibleObject : ControlAccessibleObject
        {
            private readonly ThemedButton _owner;

            public ThemedButtonAccessibleObject(ThemedButton owner)
                : base(owner)
            {
                _owner = owner;
            }

            public override AccessibleStates State
            {
                get
                {
                    var state = base.State;
                    if (_owner.AccessibleRole == AccessibleRole.PageTab)
                        state |= AccessibleStates.Selectable;
                    if (_owner.AccessibilitySelected)
                        state |= AccessibleStates.Selected;
                    return state;
                }
            }
        }
    }

    internal sealed class ThemedCardPanel : Panel
    {
        private const int DefaultCornerRadius = 10;

        public Color BorderColor { get; set; } = Color.Transparent;
        public int CornerRadius { get; set; } = DefaultCornerRadius;

        public ThemedCardPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRoundedRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 2 || Height < 2)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedGeometry.Create(bounds, CornerRadius);
            using var border = new Pen(BorderColor);
            e.Graphics.DrawPath(border, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var roundedRegion = Region;
                Region = null;
                roundedRegion?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void UpdateRoundedRegion()
        {
            if (Width < 2 || Height < 2)
                return;

            using var path = RoundedGeometry.Create(new Rectangle(0, 0, Width, Height), CornerRadius);
            var previous = Region;
            Region = new Region(path);
            previous?.Dispose();
        }
    }

    internal sealed class MonitorDragHandle : Control
    {
        public MonitorDragHandle()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.Grip;
            AccessibleName = "Reorder monitor";
            AccessibleDescription = "Drag with the mouse, or press Alt plus Up or Alt plus Down to move this monitor card.";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ForeColor, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            int barWidth = Math.Max(10, (int)Math.Round(Width * 0.58));
            int left = Math.Max(2, (Width - barWidth) / 2);
            int right = Math.Min(Width - 2, left + barWidth);
            int centreY = Height / 2;
            int spacing = Math.Max(6, Height / 10);
            for (int offset = -spacing; offset <= spacing; offset += spacing)
                e.Graphics.DrawLine(pen, left, centreY + offset, right, centreY + offset);

            if (Focused && ShowFocusCues)
            {
                var focusBounds = ClientRectangle;
                focusBounds.Inflate(-2, -2);
                ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, ForeColor, BackColor);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
            => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    }

    internal sealed class StatusBadge : Label
    {
        public Color FillColor { get; set; } = Color.Transparent;
        public MonitorVisualStatus VisualStatus { get; set; }

        public StatusBadge()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            base.OnPaintBackground(pevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundedGeometry.Create(bounds, Height / 2))
            using (var fill = new SolidBrush(FillColor))
                e.Graphics.FillPath(fill, path);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter);
        }
    }

    internal static class RoundedGeometry
    {
        public static GraphicsPath Create(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = Math.Max(2, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2));
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>
    /// Applies a ThemePalette to a Form and its entire control tree.
    /// </summary>
    internal static class Themer
    {
        public static void Apply(Form form, ThemePalette p)
        {
            if (form == null) return;

            form.BackColor = p.Back;
            form.ForeColor = p.Text;

            foreach (Control c in form.Controls)
                ApplyToControl(c, p);

            form.Invalidate(true);
            form.Update();
        }

        private static void ApplyToControl(Control c, ThemePalette p)
        {
            switch (c)
            {
                case MonitorDragHandle dragHandle:
                    dragHandle.BackColor = Color.Transparent;
                    dragHandle.ForeColor = p.TextSubtle;
                    dragHandle.Invalidate();
                    break;

                case StatusBadge badge:
                    ApplyStatusBadge(badge, p);
                    break;

                case Label lbl:
                    lbl.BackColor = lbl.BorderStyle == BorderStyle.None ? Color.Transparent : p.Back;
                    lbl.ForeColor = p.Text;
                    break;

                case Button btn:
                    ApplyButtonStyle(btn, p);
                    break;

                case DataGridView dgv:
                    dgv.BackColor = p.Surface;
                    dgv.BackgroundColor = p.Back;
                    dgv.GridColor = p.Border;
                    dgv.EnableHeadersVisualStyles = false;

                    dgv.ColumnHeadersDefaultCellStyle.BackColor = p.Surface;
                    dgv.ColumnHeadersDefaultCellStyle.ForeColor = p.Text;
                    dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Surface;
                    dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Text;

                    dgv.DefaultCellStyle.BackColor = p.Surface;
                    dgv.DefaultCellStyle.ForeColor = p.Text;
                    dgv.DefaultCellStyle.SelectionBackColor = p.SurfaceMuted;
                    dgv.DefaultCellStyle.SelectionForeColor = p.Text;
                    dgv.AlternatingRowsDefaultCellStyle.BackColor = p.Back;
                    dgv.AlternatingRowsDefaultCellStyle.ForeColor = p.Text;

                    dgv.RowHeadersDefaultCellStyle.BackColor = p.Surface;
                    dgv.RowHeadersDefaultCellStyle.ForeColor = p.Text;
                    dgv.RowHeadersDefaultCellStyle.SelectionBackColor = p.Surface;
                    dgv.RowHeadersDefaultCellStyle.SelectionForeColor = p.Text;
                    EnsureNativeScrollTheme(dgv);
                    break;

                case ComboBox combo:
                    combo.BackColor = p.Surface;
                    combo.ForeColor = p.Text;
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.DrawMode = DrawMode.OwnerDrawFixed;
                    combo.DrawItem -= ComboDrawItem;
                    combo.DrawItem += ComboDrawItem;
                    combo.Tag = p;
                    break;

                case TextBox txt:
                    txt.BackColor = p.Surface;
                    txt.ForeColor = p.Text;
                    txt.BorderStyle = BorderStyle.FixedSingle;
                    EnsureNativeScrollTheme(txt);
                    break;

                case TabControl tab:
                    tab.BackColor = p.Back;
                    tab.ForeColor = p.Text;
                    tab.DrawMode = TabDrawMode.OwnerDrawFixed;
                    tab.DrawItem -= TabDrawItem;
                    tab.DrawItem += TabDrawItem;
                    tab.Tag = p;
                    break;

                case TabPage page:
                    page.BackColor = p.Back;
                    page.ForeColor = p.Text;
                    break;

                case ThemedCardPanel card:
                    card.BackColor = p.Surface;
                    card.ForeColor = p.Text;
                    card.BorderColor = p.Border;
                    card.Invalidate();
                    break;

                case Panel pnl:
                    // Let 1px rule lines keep their custom color; other panels follow background
                    if (pnl.Height > 2)
                        pnl.BackColor = pnl.BorderStyle == BorderStyle.None ? p.Back : p.Surface;
                    break;

                default:
                    c.BackColor = p.Back;
                    c.ForeColor = p.Text;
                    break;
            }

            foreach (Control child in c.Controls)
                ApplyToControl(child, p);
        }

        internal static void ApplyButtonStyle(Button btn, ThemePalette p)
        {
            var tone = btn is ThemedButton themed ? themed.Tone : ThemedButtonTone.Neutral;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;
            btn.UseVisualStyleBackColor = false;

            if (!btn.Enabled)
            {
                btn.BackColor = p.SurfaceMuted;
                btn.ForeColor = p.TextSubtle;
                btn.FlatAppearance.BorderColor = p.Border;
                btn.FlatAppearance.MouseOverBackColor = p.SurfaceMuted;
                btn.FlatAppearance.MouseDownBackColor = p.SurfaceMuted;
                return;
            }

            switch (tone)
            {
                case ThemedButtonTone.Primary:
                    btn.BackColor = p.Accent;
                    btn.ForeColor = p.AccentText;
                    btn.FlatAppearance.BorderColor = p.Accent;
                    btn.FlatAppearance.MouseOverBackColor = p.AccentHover;
                    btn.FlatAppearance.MouseDownBackColor = p.AccentPressed;
                    break;

                case ThemedButtonTone.Danger:
                    btn.BackColor = p.Surface;
                    btn.ForeColor = p.Danger;
                    btn.FlatAppearance.BorderColor = p.Danger;
                    btn.FlatAppearance.MouseOverBackColor = p.DangerSurface;
                    btn.FlatAppearance.MouseDownBackColor = p.DangerSurface;
                    break;

                default:
                    btn.BackColor = p.Surface;
                    btn.ForeColor = p.Text;
                    btn.FlatAppearance.BorderColor = p.Border;
                    btn.FlatAppearance.MouseOverBackColor = p.SurfaceMuted;
                    btn.FlatAppearance.MouseDownBackColor = p.SurfaceMuted;
                    break;
            }
        }

        internal static void ApplyStatusBadge(StatusBadge badge, ThemePalette p)
        {
            switch (badge.VisualStatus)
            {
                case MonitorVisualStatus.Online:
                    badge.ForeColor = p.StatusOk;
                    badge.FillColor = p.StatusOkBack;
                    break;

                case MonitorVisualStatus.Disabled:
                    badge.ForeColor = p.StatusWarn;
                    badge.FillColor = p.StatusWarnBack;
                    break;

                case MonitorVisualStatus.Working:
                    badge.ForeColor = p.StatusBusy;
                    badge.FillColor = p.StatusBusyBack;
                    break;

                default:
                    badge.ForeColor = p.TextSubtle;
                    badge.FillColor = p.StatusOfflineBack;
                    break;
            }

            badge.BackColor = Color.Transparent;
            badge.Invalidate();
        }

        private static void EnsureNativeScrollTheme(Control control)
        {
            ApplyNativeScrollTheme(control);
            control.HandleCreated -= NativeScrollThemeHandleCreated;
            control.HandleCreated += NativeScrollThemeHandleCreated;
        }

        private static void NativeScrollThemeHandleCreated(object? sender, EventArgs e)
        {
            if (sender is Control control)
                ApplyNativeScrollTheme(control);
        }

        private static void ApplyNativeScrollTheme(Control control)
        {
            if (!control.IsHandleCreated) return;
            bool dark = control.BackColor.GetBrightness() < 0.5f;
            try { SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch { /* best effort only */ }
        }

        private static void ComboDrawItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ComboBox combo || e.Index < 0)
                return;

            var p = combo.Tag as ThemePalette ?? ThemePalette.Light();
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using var back = new SolidBrush(selected ? ControlPaint.Light(p.Surface, 0.12f) : p.Back);
            using var fore = new SolidBrush(p.Text);

            e.Graphics.FillRectangle(back, e.Bounds);
            var text = combo.GetItemText(combo.Items[e.Index]);
            TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        }

        private static void TabDrawItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not TabControl tab || e.Index < 0)
                return;

            var p = tab.Tag as ThemePalette ?? ThemePalette.Light();
            bool selected = tab.SelectedIndex == e.Index;
            var bounds = tab.GetTabRect(e.Index);
            using var back = new SolidBrush(selected ? p.Back : p.Surface);
            using var border = new Pen(p.Border);

            e.Graphics.FillRectangle(back, bounds);
            e.Graphics.DrawRectangle(border, bounds);
            TextRenderer.DrawText(
                e.Graphics,
                tab.TabPages[e.Index].Text,
                tab.Font,
                bounds,
                p.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
    }
}
