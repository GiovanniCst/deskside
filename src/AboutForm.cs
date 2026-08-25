using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Deskside
{
    public sealed class AboutForm : Form
    {
        public AboutForm(Icon appIcon)
        {
            Text = L.F("About {0}", AppInfo.Name);
            Icon = appIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 286);
            Font = SystemFonts.MessageBoxFont;

            PictureBox logo = new PictureBox();
            logo.Image = appIcon.ToBitmap();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Location = new Point(20, 20);
            logo.Size = new Size(64, 64);

            Label name = new Label();
            name.Text = AppInfo.Name + " " + AppInfo.Version;
            name.Font = new Font(Font.FontFamily, 14f, FontStyle.Bold);
            name.Location = new Point(100, 20);
            name.Size = new Size(310, 26);

            Label tagline = new Label();
            tagline.Text = L.T(AppInfo.Tagline);
            tagline.ForeColor = SystemColors.GrayText;
            tagline.Location = new Point(100, 48);
            tagline.Size = new Size(310, 18);

            Label blurb = new Label();
            // Two lines that each fit the label width; anything longer wraps to
            // a third line and gets clipped.
            blurb.Text = L.T("Monitor control over DDC/CI, and keyboard\r\n"
                           + "layout locking, for docked Windows laptops.");
            blurb.Location = new Point(100, 70);
            blurb.Size = new Size(310, 40);

            Label by = new Label();
            by.Text = L.F("Created by {0}", AppInfo.Author);
            by.Location = new Point(20, 126);
            by.Size = new Size(390, 18);

            LinkLabel site = Link(AppInfo.AuthorUrl, 20, 148);
            LinkLabel repo = Link(AppInfo.ProjectUrl, 20, 170);

            Label lic = new Label();
            lic.Text = L.F("Licensed under the {0}. You may use, modify and\r\n"
                         + "redistribute it, provided the original attribution is kept.",
                           AppInfo.License);
            lic.Location = new Point(20, 198);
            lic.Size = new Size(390, 36);

            Button close = new Button();
            close.Text = L.T("Close");
            close.DialogResult = DialogResult.OK;
            // kept clear of the licence text above it
            close.Location = new Point(334, 246);
            close.Size = new Size(76, 26);

            Controls.AddRange(new Control[] { logo, name, tagline, blurb, by, site, repo, lic, close });
            AcceptButton = close;
            CancelButton = close;
        }

        static LinkLabel Link(string url, int x, int y)
        {
            LinkLabel l = new LinkLabel();
            l.Text = url;
            l.Location = new Point(x, y);
            l.Size = new Size(390, 18);
            l.LinkClicked += delegate
            {
                // a broken default-browser association should not take the app down
                try { Process.Start(url); }
                catch (Exception ex) { MessageBox.Show(L.F("Could not open the link: {0}", ex.Message)); }
            };
            return l;
        }
    }
}
