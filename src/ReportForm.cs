// Window for the long reports: full VCP scan, diagnostics, shortcut list.
//
// These used to be message boxes. A message box grows with its text, and the
// scan report grows with the monitor: on one that answers many codes the box
// became taller than the screen, with its only button past the bottom edge and
// no way to scroll. Here the text scrolls, the window resizes, and it never
// exceeds the working area of the screen it opens on.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Deskside
{
    public sealed class ReportForm : Form
    {
        public ReportForm(string title, string text, Icon appIcon)
        {
            Text = title;
            if (appIcon != null) Icon = appIcon;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimizeBox = false;
            KeyPreview = true;
            Font = SystemFonts.MessageBoxFont;
            MinimumSize = new Size(360, 220);

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.WordWrap = false;
            box.ScrollBars = ScrollBars.Both;
            box.Font = new Font("Consolas", 9f);
            box.BackColor = Color.White;
            box.Dock = DockStyle.Fill;
            box.Text = text;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 44;

            Button close = new Button();
            close.Text = L.T("Close");
            close.Size = new Size(96, 27);
            close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            close.Click += delegate { Close(); };

            Button copy = new Button();
            copy.Text = L.T("Copy");
            copy.Size = new Size(96, 27);
            copy.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            copy.Click += delegate
            {
                try { Clipboard.SetText(text); copy.Text = L.T("Copied"); }
                catch { copy.Text = L.T("Copy failed"); }
            };

            bottom.Controls.Add(copy);
            bottom.Controls.Add(close);
            bottom.Resize += delegate
            {
                close.Location = new Point(bottom.Width - close.Width - 12, 9);
                copy.Location = new Point(close.Left - copy.Width - 8, 9);
            };

            Controls.Add(box);
            Controls.Add(bottom);
            CancelButton = close;
            // the window is not modal, so CancelButton alone does not catch Esc
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

            // as big as the text needs, never bigger than the working area of
            // the screen the pointer is on
            Size need = TextRenderer.MeasureText(text, box.Font);
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(
                Math.Max(440, Math.Min(need.Width + 44, (int)(wa.Width * 0.9))),
                Math.Max(240, Math.Min(need.Height + 64, (int)(wa.Height * 0.9))));
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

            Shown += delegate { box.Select(0, 0); close.Focus(); };
        }

        /// <summary>Shows a report without blocking the rest of the application.</summary>
        public static void Open(string title, string text, Icon appIcon)
        {
            ReportForm f = new ReportForm(title, text, appIcon);
            f.Show();
            f.Activate();
        }
    }
}
