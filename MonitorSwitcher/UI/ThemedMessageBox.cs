#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;
using WorkMonitorSwitcher.Services; // Themer + DwmInterop

namespace WorkMonitorSwitcher.UI
{
    public static class ThemedMessageBox
    {
        public static DialogResult Info(IWin32Window owner, string text, string title, bool dark)
            => Show(owner, text, title, dark, SystemIcons.Information);

        public static DialogResult Warn(IWin32Window owner, string text, string title, bool dark)
            => Show(owner, text, title, dark, SystemIcons.Warning);

        public static DialogResult Error(IWin32Window owner, string text, string title, bool dark)
            => Show(owner, text, title, dark, SystemIcons.Error);

        private static DialogResult Show(IWin32Window owner, string text, string title, bool dark, Icon icon)
        {
            const int margin = 16;
            const int iconSize = 32;
            const int gap = 12;
            const int buttonWidth = 80;
            const int buttonHeight = 28;
            const int maxTextWidth = 440;
            const int minTextWidth = 240;

            var measureFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
            var measured = TextRenderer.MeasureText(
                text,
                SystemFonts.MessageBoxFont,
                new Size(maxTextWidth, int.MaxValue),
                measureFlags);
            int textWidth = Math.Clamp(measured.Width + 4, minTextWidth, maxTextWidth);
            measured = TextRenderer.MeasureText(
                text,
                SystemFonts.MessageBoxFont,
                new Size(textWidth, int.MaxValue),
                measureFlags);
            int contentHeight = Math.Max(iconSize, measured.Height + 4);
            int clientWidth = margin + iconSize + gap + textWidth + margin;
            int clientHeight = margin + contentHeight + 18 + buttonHeight + margin;

            // Dialog shell
            using var dlg = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ShowIcon = true,
                Icon = AppIcon.Current,
                AutoScaleMode = AutoScaleMode.Dpi,
                Font = SystemFonts.MessageBoxFont,
                ClientSize = new Size(clientWidth, clientHeight)
            };

            // Icon
            var pb = new PictureBox
            {
                Image = icon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Location = new Point(16, 20),
                Size = new Size(32, 32)
            };

            // Text
            var lbl = new Label
            {
                AutoSize = false,
                Location = new Point(margin + iconSize + gap, margin),
                Size = new Size(textWidth, contentHeight),
                Text = text,
                UseMnemonic = false
            };

            // OK button
            var ok = new ThemedButton
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Size = new Size(buttonWidth, buttonHeight),
                Tone = ThemedButtonTone.Primary
            };
            ok.Location = new Point(dlg.ClientSize.Width - ok.Width - 16,
                                    dlg.ClientSize.Height - ok.Height - 16);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            dlg.AcceptButton = ok;
            dlg.Controls.Add(pb);
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(ok);

            // Apply theme
            var palette = dark ? ThemePalette.Dark() : ThemePalette.Light();
            Themer.Apply(dlg, palette);
            try { DwmInterop.SetDarkTitleBar(dlg.Handle, dark); } catch { /* ignore */ }

            return dlg.ShowDialog(owner);
        }
    }
}
