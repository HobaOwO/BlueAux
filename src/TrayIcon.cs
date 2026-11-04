using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PhoneSpeaker
{
    // The notification-area icon: a green dot while playing, grey while Bluetooth is off.
    sealed class TrayIcon : IDisposable
    {
        readonly NotifyIcon icon = new NotifyIcon();
        readonly ReceiverEngine engine;
        readonly Action showWindow;
        readonly Action exitApp;
        IntPtr iconHandle;
        string iconKey;

        public TrayIcon(ReceiverEngine engine, Action showWindow, Action exitApp)
        {
            this.engine = engine;
            this.showWindow = showWindow;
            this.exitApp = exitApp;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Phone Speaker");
            menu.Opening += (s, e) => BuildMenu(menu);
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) showWindow();
            };
            icon.BalloonTipClicked += (s, e) => showWindow();
            Update();
            icon.Visible = true;
        }

        public void Update()
        {
            var streaming = engine.Sessions.FirstOrDefault(s => s.State == LinkState.Streaming);
            string key, text;
            Color? dot = null;
            if (!engine.RadioOn)
            {
                key = "off";
                dot = Color.FromArgb(0x9A, 0x9A, 0x9A);
                text = "Phone Speaker · Bluetooth is off";
            }
            else if (streaming != null)
            {
                key = "playing";
                dot = Color.FromArgb(0x3C, 0xC8, 0x5A);
                text = "Phone Speaker · playing from " + streaming.DisplayName;
            }
            else
            {
                key = "ready";
                if (engine.PcIdle) text = "Phone Speaker · resting while the PC is idle";
                else if (engine.AutoPaused) text = "Phone Speaker · auto-connect paused";
                else text = "Phone Speaker · waiting for your phone";
            }

            // NotifyIcon refuses tooltips longer than 63 characters.
            if (text.Length > 63) text = text.Substring(0, 62) + "…";
            if (icon.Text != text) icon.Text = text;

            if (key == iconKey) return;
            iconKey = key;
            int size = SystemInformation.SmallIconSize.Width;
            using (var bitmap = IconFactory.Render(size, dot))
            {
                IntPtr handle = bitmap.GetHicon();
                icon.Icon = Icon.FromHandle(handle);
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = handle;
            }
        }

        public void ShowBalloon(string title, string text)
        {
            icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            iconHandle = IntPtr.Zero;
        }

        void BuildMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var open = new ToolStripMenuItem("Open Phone Speaker", null, (s, e) => showWindow());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());

            var visible = engine.Sessions.Where(s => !s.IsHidden).ToList();
            if (engine.RadioOn)
            {
                foreach (var session in visible)
                {
                    var current = session;
                    if (current.State == LinkState.Streaming)
                    {
                        menu.Items.Add(new ToolStripMenuItem("Disconnect " + current.DisplayName, null, (s, e) => current.DisconnectCommand.Execute(null)));
                        menu.Items.Add(new ToolStripMenuItem("Fix audio for " + current.DisplayName, null, (s, e) => current.FixCommand.Execute(null)));
                    }
                    else if (current.State == LinkState.Connecting)
                    {
                        menu.Items.Add(new ToolStripMenuItem("Connecting to " + current.DisplayName + "…") { Enabled = false });
                    }
                    else
                    {
                        menu.Items.Add(new ToolStripMenuItem("Connect " + current.DisplayName, null, (s, e) => current.ConnectCommand.Execute(null)));
                    }
                }
            }
            else
            {
                menu.Items.Add(new ToolStripMenuItem("Turn on Bluetooth", null, (s, e) => engine.TurnOnBluetooth()));
            }

            if (engine.AutoPaused && engine.RadioOn)
                menu.Items.Add(new ToolStripMenuItem("Resume auto-connect", null, (s, e) => engine.SetAutoPaused(false)));

            if (visible.Count > 0 || !engine.RadioOn) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Bluetooth settings", null, (s, e) => Shell.Open("ms-settings:bluetooth")));
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => exitApp()));
        }
    }
}
