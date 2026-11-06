using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BlueAux
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
        Action balloonAction;

        public TrayIcon(ReceiverEngine engine, Action showWindow, Action exitApp)
        {
            this.engine = engine;
            this.showWindow = showWindow;
            this.exitApp = exitApp;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open " + AppInfo.Name);
            menu.Opening += (s, e) => BuildMenu(menu);
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) showWindow();
            };
            icon.BalloonTipClicked += (s, e) =>
            {
                var action = balloonAction ?? showWindow;
                balloonAction = null;
                action();
            };
            icon.BalloonTipClosed += (s, e) => balloonAction = null;
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
                text = AppInfo.Name + " · Bluetooth is off";
            }
            else if (streaming != null)
            {
                key = "playing";
                dot = Color.FromArgb(0x3C, 0xC8, 0x5A);
                text = AppInfo.Name + " · playing from " + streaming.DisplayName;
            }
            else
            {
                key = "ready";
                if (engine.PcIdle) text = AppInfo.Name + " · resting while you're away";
                else if (engine.AutoPaused) text = AppInfo.Name + " · disconnected";
                else text = AppInfo.Name + " · waiting for your phone";
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

        // onClick runs when the notification is clicked; without one, clicking opens the window.
        public void ShowBalloon(string title, string text, Action onClick)
        {
            balloonAction = onClick;
            icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
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
            var open = new ToolStripMenuItem("Open " + AppInfo.Name, null, (s, e) => showWindow());
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
                        menu.Items.Add(new ToolStripMenuItem("No sound? Fix " + current.DisplayName, null, (s, e) => current.FixCommand.Execute(null)));
                        menu.Items.Add(new ToolStripMenuItem("Disconnect " + current.DisplayName, null, (s, e) => current.DisconnectCommand.Execute(null)));
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

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => exitApp()));
        }
    }
}
