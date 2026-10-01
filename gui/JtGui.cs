// JtGui.cs is the Windows counterpart of jiantieban's secret view: a tray
// application with a global hotkey that lists the jt vault, grabs the
// clipboard into jt and copies references or values back. Every action is a
// jt command (see Jt.cs); nothing here decrypts. C# 5 only: this file is
// compiled by the csc.exe that ships with Windows.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace JtGui
{
    static class Program
    {
        // PrintEnvFlag turns this exe into the child of `jt resolve --exec`: it
        // writes the named variable to stdout as raw UTF-8 so the GUI can show the
        // current value in the update dialog. jt itself still never prints.
        public const string PrintEnvFlag = "--print-env";

        // ExePath is this program's file, used as the --exec child; unlike
        // Application.ExecutablePath it is right even when the assembly is hosted.
        public static readonly string ExePath = typeof(Program).Assembly.Location;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == PrintEnvFlag)
            {
                string value = Environment.GetEnvironmentVariable(args[1]);
                if (value == null) return 3;
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
                using (var stdout = Console.OpenStandardOutput())
                {
                    stdout.Write(bytes, 0, bytes.Length);
                }
                return 0;
            }
            bool tray = Array.IndexOf(args, "--tray") >= 0;
            bool created;
            using (var mutex = new Mutex(true, @"Local\jt-gui", out created))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\jt-gui-show"))
            {
                if (!created)
                {
                    // Already running: a plain launch brings the panel forward, an autostart launch stays quiet.
                    if (!tray) show.Set();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(tray, show));
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    // Settings keeps the two preferences in HKCU; the autostart entry is the standard Run key.
    static class Settings
    {
        const string Key = @"Software\jt-gui";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool ClearHistory
        {
            get { return Read("ClearHistory") != 0; }
            set { Write("ClearHistory", value ? 1 : 0); }
        }

        public static bool Autostart
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k != null && k.GetValue("jt-gui") != null;
                }
            }
            set
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("jt-gui", "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue("jt-gui", false);
                }
            }
        }

        static int Read(string name)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object value = k == null ? null : k.GetValue(name);
                return value is int ? (int)value : 0;
            }
        }

        static void Write(string name, int value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue(name, value, RegistryValueKind.DWord);
            }
        }
    }

    sealed class PromptField
    {
        public string Label = "", Value = "", Hint = "";
        public bool Masked;
        public bool Ascii; // names and tokens: IME disabled so pinyin never intercepts
        public TextBox Box;
    }

    // ValueBox is a TextBox that turns itself multi-line when multi-line text is
    // pasted or when asked, so a block of several lines never silently loses
    // everything after the first line in a single-line, masked field.
    sealed class ValueBox : TextBox
    {
        const int WM_PASTE = 0x0302;

        public event EventHandler BecameMultiline;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PASTE && !Multiline && ClipboardHasLineBreak())
            {
                // Multiline recreates the handle; paste again into the new one.
                MakeMultiline();
                Paste();
                return;
            }
            base.WndProc(ref m);
        }

        static bool ClipboardHasLineBreak()
        {
            try
            {
                return Clipboard.ContainsText() && Clipboard.GetText().IndexOf('\n') >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // MakeMultiline is one-way: a multi-line value cannot be masked, Enter
        // inserts a line break, and the dialog's OK button is reached with Tab.
        public void MakeMultiline()
        {
            if (Multiline) return;
            UseSystemPasswordChar = false;
            Multiline = true;
            AcceptsReturn = true;
            ScrollBars = ScrollBars.Vertical;
            Height = Dpi.Px(120);
            if (BecameMultiline != null) BecameMultiline(this, EventArgs.Empty);
        }
    }

    // ClipboardHistory clears Win+V history the same way `jt grab --clear-history`
    // does, for the split flow that stores through `jt add`.
    static class ClipboardHistory
    {
        public static void Clear()
        {
            const string script = "[Windows.ApplicationModel.DataTransfer.Clipboard,Windows.ApplicationModel.DataTransfer,ContentType=WindowsRuntime] | Out-Null; [Windows.ApplicationModel.DataTransfer.Clipboard]::ClearHistory() | Out-Null";
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = System.Diagnostics.Process.Start(info))
                {
                    p.WaitForExit(15000);
                }
            }
            catch (Exception) { }
        }
    }

    // Dpi scales 96-dpi pixel literals to the system DPI. The process is DPI
    // aware through the manifest, so GDI reports the real value (192 at 200%).
    // WinForms' own AutoScaleMode is left off: this program carries no
    // TargetFramework attribute or app.config, which puts it in the legacy
    // path where DeviceDpi stays 96 and Dpi auto scaling is a no-op.
    static class Dpi
    {
        static readonly double Factor = Detect();

        static double Detect()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return Math.Max(1.0, g.DpiX / 96.0);
            }
        }

        public static int Px(int logical)
        {
            return (int)Math.Round(logical * Factor);
        }

        public static Size Size(int width, int height)
        {
            return new Size(Px(width), Px(height));
        }

        public static Padding Pad(int left, int top, int right, int bottom)
        {
            return new Padding(Px(left), Px(top), Px(right), Px(bottom));
        }
    }

    // GroupListView makes group headers behave like Explorer's: clicking one
    // selects the group, right-clicking opens the context menu for it,
    // double-clicking raises GroupActivated. The native control would otherwise
    // start a rubber-band selection on the header and clear the selection.
    sealed class GroupListView : ListView
    {
        const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        bool swallowUp; // the button-up that follows a header click must not reach the native control either

        public event EventHandler GroupActivated;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK || m.Msg == WM_RBUTTONDOWN)
            {
                int packed = unchecked((int)(long)m.LParam);
                int x = (short)(packed & 0xFFFF), y = (short)((packed >> 16) & 0xFFFF);
                ListViewGroup group = GroupAt(x, y);
                if (group != null)
                {
                    Focus();
                    SelectGroup(group);
                    swallowUp = true;
                    if (m.Msg == WM_RBUTTONDOWN)
                    {
                        if (ContextMenuStrip != null) ContextMenuStrip.Show(this, new Point(x, y));
                    }
                    else if (m.Msg == WM_LBUTTONDBLCLK && GroupActivated != null)
                    {
                        GroupActivated(this, EventArgs.Empty);
                    }
                    return;
                }
            }
            if ((m.Msg == WM_LBUTTONUP || m.Msg == WM_RBUTTONUP) && swallowUp)
            {
                swallowUp = false;
                return;
            }
            base.WndProc(ref m);
        }

        // GroupAt finds the group whose header (or the gap above its first row) is at
        // the point: the nearest row below the point belongs to it.
        public ListViewGroup GroupAt(int x, int y)
        {
            if (GetItemAt(x, y) != null) return null;
            ListViewItem below = null;
            foreach (ListViewItem item in Items)
            {
                if (item.Bounds.Top > y && (below == null || item.Bounds.Top < below.Bounds.Top)) below = item;
            }
            return below == null ? null : below.Group;
        }

        public void SelectGroup(ListViewGroup group)
        {
            BeginUpdate();
            SelectedItems.Clear();
            foreach (ListViewItem item in group.Items) item.Selected = true;
            EndUpdate();
            if (group.Items.Count > 0) FocusedItem = group.Items[0];
        }
    }

    // PromptForm is the one dialog for every text input: labelled fields, an
    // optional masked field with a "show" toggle, and optional checkboxes.
    sealed class PromptForm : Form
    {
        readonly PromptField[] fields;
        readonly CheckBox[] options;

        public PromptForm(string title, PromptField[] fields, string optionText, bool optionChecked)
            : this(title, fields, optionText == null ? new string[0] : new[] { optionText }, new[] { optionChecked })
        {
        }

        public PromptForm(string title, PromptField[] fields, string[] optionTexts, bool[] optionChecked)
        {
            options = new CheckBox[optionTexts.Length];
            this.fields = fields;
            Text = title;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = Dpi.Pad(14, 12, 14, 10),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;
            foreach (PromptField field in fields)
            {
                var label = new Label { Text = field.Label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = Dpi.Pad(0, 7, 10, 4) };
                var box = new ValueBox { Width = Dpi.Px(380), Margin = Dpi.Pad(0, 4, 0, 4), UseSystemPasswordChar = field.Masked };
                if (field.Ascii) box.ImeMode = ImeMode.Disable;
                field.Box = box;
                table.Controls.Add(label, 0, row);
                table.Controls.Add(box, 1, row);
                row++;
                if (field.Masked)
                {
                    var toggles = new FlowLayoutPanel { AutoSize = true, Margin = Dpi.Pad(0, 0, 0, 4), WrapContents = false };
                    var show = new CheckBox { Text = "显示", AutoSize = true };
                    var multi = new CheckBox { Text = "多行", AutoSize = true, Margin = Dpi.Pad(12, 3, 3, 3) };
                    ValueBox target = box;
                    show.CheckedChanged += (s, e) => target.UseSystemPasswordChar = !show.Checked && !target.Multiline;
                    multi.CheckedChanged += (s, e) =>
                    {
                        if (multi.Checked) target.MakeMultiline();
                    };
                    // Multi-line text cannot be masked: once the box grows, "show" is forced on.
                    box.BecameMultiline += delegate
                    {
                        show.Checked = true;
                        show.Enabled = false;
                        multi.Checked = true;
                        multi.Enabled = false;
                    };
                    toggles.Controls.Add(show);
                    toggles.Controls.Add(multi);
                    table.Controls.Add(toggles, 1, row);
                    row++;
                }
                if (field.Value.IndexOf('\n') >= 0) box.MakeMultiline();
                box.Text = field.Value;
                if (field.Hint.Length > 0)
                {
                    var hint = new Label { Text = field.Hint, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(Dpi.Px(380), 0), Margin = Dpi.Pad(0, 0, 0, 6) };
                    table.Controls.Add(hint, 1, row);
                    row++;
                }
            }
            for (int i = 0; i < optionTexts.Length; i++)
            {
                options[i] = new CheckBox { Text = optionTexts[i], Checked = optionChecked[i], AutoSize = true, MaximumSize = new Size(Dpi.Px(380), 0), Margin = Dpi.Pad(0, 4, 0, 4) };
                table.Controls.Add(options[i], 1, row);
                row++;
            }
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = Dpi.Pad(0, 8, 0, 0) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons, 0, row);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public bool OptionChecked { get { return Option(0); } }

        public bool Option(int index) { return options.Length > index && options[index].Checked; }

        public string this[int index] { get { return fields[index].Box.Text; } }
    }

    sealed class MainForm : Form
    {
        const int WM_HOTKEY = 0x0312;
        const int EM_SETCUEBANNER = 0x1501;
        const int HotkeyPanel = 1, HotkeyGrab = 2;
        const uint ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
        const uint VkG = 0x47, VkJ = 0x4A;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        readonly bool startHidden;
        bool shownOnce, exiting;
        readonly TextBox search = new TextBox();
        readonly GroupListView list = new GroupListView();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem autostart = new ToolStripMenuItem("开机自动启动到托盘");
        List<Secret> secrets = new List<Secret>();
        string hotkeyNote = "";
        string syncNote = "";

        public MainForm(bool startHidden, EventWaitHandle showSignal)
        {
            this.startHidden = startHidden;
            Text = "jt";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            // Pixel sizes are written for 96 dpi and scaled through Dpi; system fonts arrive already scaled.
            ClientSize = Dpi.Size(960, 540);
            MinimumSize = Dpi.Size(680, 380);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            // Docking runs in reverse order of Controls: add the filling list first.
            BuildList();
            BuildSearch();
            BuildToolbar();
            BuildStatusBar();
            BuildTray();

            Load += delegate
            {
                CheckBinary();
                Reload();
                FitColumns();
            };
            Resize += delegate { FitColumns(); };
            FormClosing += (s, e) =>
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
            // A second launch signals this event; bring the panel forward.
            var waiter = new Thread(() =>
            {
                while (true)
                {
                    showSignal.WaitOne();
                    try { BeginInvoke(new Action(ShowPanel)); }
                    catch (Exception) { }
                }
            }) { IsBackground = true };
            waiter.Start();
        }

        // --- layout ---------------------------------------------------------

        void BuildToolbar()
        {
            var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Padding = Dpi.Pad(6, 3, 6, 3), Dock = DockStyle.Top };
            bar.Items.Add(Button("抓取剪贴板", "把剪贴板里的密钥存进 jt，并把引用放回剪贴板 (Ctrl+Shift+G)", (s, e) => GrabFromClipboard()));
            bar.Items.Add(Button("新建…", "手动输入一条密钥", (s, e) => AddTyped()));
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(Button("复制引用", "jt://secret/<id> 到剪贴板 (Enter)", (s, e) => CopyReference(false)));
            bar.Items.Add(Button("复制整组", "这一组（同一命名空间）所有引用到剪贴板 (Ctrl+Enter)", (s, e) => CopyGroup(false)));
            bar.Items.Add(Button("复制明文", "真值到剪贴板，不进剪贴板历史 (Ctrl+Shift+C)", (s, e) => CopyValue()));
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(Button("改名", "F2", (s, e) => Rename()));
            bar.Items.Add(Button("描述", "改描述；留空清除", (s, e) => Describe()));
            bar.Items.Add(Button("查看/更新值", "看当前值（默认遮住，可点“显示”），改了就更新；引用不变", (s, e) => SetValue()));
            bar.Items.Add(Button("删除", "Delete", (s, e) => Delete()));
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(Button("同步", "jt sync：拉取、提交、推送", (s, e) => Sync()));
            bar.Items.Add(Button("刷新", "F5", (s, e) => Reload()));
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(Button("AI 设置", "接入 DeepSeek 等 OpenAI 兼容模型，抓取时自动拆分命名", (s, e) => AiSetup()));
            Controls.Add(bar);
        }

        static ToolStripButton Button(string text, string tip, EventHandler onClick)
        {
            var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
            button.Click += onClick;
            return button;
        }

        void BuildSearch()
        {
            var panel = new Panel { Dock = DockStyle.Top, Padding = Dpi.Pad(8, 6, 8, 6) };
            search.Font = new Font(Font.FontFamily, Font.Size + 2);
            search.Dock = DockStyle.Fill;
            // Names and IDs are ASCII, and ImeMode.Off is ignored by the Windows 10
            // Microsoft Pinyin IME, so the IME is disabled here (like a password
            // box). Chinese description text can still be pasted.
            search.ImeMode = ImeMode.Disable;
            panel.Height = search.PreferredHeight + panel.Padding.Vertical;
            search.TextChanged += delegate { ApplyFilter(); };
            search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
                {
                    MoveSelection(e.KeyCode == Keys.Down ? 1 : -1);
                    e.Handled = e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    CopySelection(true);
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            search.HandleCreated += delegate { SendMessage(search.Handle, EM_SETCUEBANNER, (IntPtr)1, "搜索名称、描述或 ID"); };
            panel.Controls.Add(search);
            Controls.Add(panel);
        }

        void BuildList()
        {
            list.View = View.Details;
            list.ShowGroups = true;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = true;
            list.ShowItemToolTips = true;
            list.BorderStyle = BorderStyle.None;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("名称", Dpi.Px(240));
            list.Columns.Add("引用", Dpi.Px(170));
            list.Columns.Add("预览", Dpi.Px(140));
            list.Columns.Add("描述", Dpi.Px(240));
            list.Columns.Add("更新时间", Dpi.Px(130));
            list.DoubleClick += delegate { CopySelection(true); };
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    CopySelection(true);
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            // Double-clicking a group header copies the whole group and hides the panel.
            list.GroupActivated += delegate { CopySelection(true); };
            var menu = new ContextMenuStrip();
            var copyRefs = menu.Items.Add("复制引用", null, (s, e) => CopySelection(false));
            var copyGroup = menu.Items.Add("复制整组引用", null, (s, e) => CopyGroup(false));
            menu.Items.Add("复制明文", null, (s, e) => CopyValue());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("改名…", null, (s, e) => Rename());
            menu.Items.Add("描述…", null, (s, e) => Describe());
            menu.Items.Add("查看 / 更新值…", null, (s, e) => SetValue());
            var remove = menu.Items.Add("删除", null, (s, e) => Delete());
            menu.Opening += (s, e) =>
            {
                int count = list.SelectedItems.Count;
                e.Cancel = count == 0;
                Secret first = Selected();
                copyRefs.Text = count > 1 ? "复制选中的 " + count + " 条引用" : "复制引用";
                copyGroup.Text = first != null && first.Namespace.Length > 0 ? "复制整组引用（" + first.Namespace + "）" : "复制整组引用";
                copyGroup.Enabled = first != null && first.Namespace.Length > 0;
                remove.Text = count > 1 ? "删除选中的 " + count + " 条" : "删除";
            };
            list.ContextMenuStrip = menu;
            Controls.Add(list);
        }

        void BuildStatusBar()
        {
            var strip = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            strip.Items.Add(status);
            Controls.Add(strip);
        }

        void BuildTray()
        {
            tray.Icon = Icon;
            tray.Text = "jt — Ctrl+Shift+J 呼出";
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示 / 隐藏面板\tCtrl+Shift+J", null, (s, e) => TogglePanel());
            menu.Items.Add("抓取剪贴板…\tCtrl+Shift+G", null, (s, e) => GrabFromClipboard());
            menu.Items.Add("同步", null, (s, e) => Sync());
            menu.Items.Add(new ToolStripSeparator());
            autostart.Checked = Settings.Autostart;
            autostart.Click += delegate
            {
                Settings.Autostart = !autostart.Checked;
                autostart.Checked = Settings.Autostart;
            };
            menu.Items.Add(autostart);
            menu.Items.Add("AI 设置…", null, (s, e) => AiSetup());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) TogglePanel();
            };
            tray.Visible = true;
        }

        void FitColumns()
        {
            if (list.Columns.Count < 5) return;
            int used = 0;
            for (int i = 0; i < list.Columns.Count; i++)
            {
                if (i != 3) used += list.Columns[i].Width;
            }
            list.Columns[3].Width = Math.Max(Dpi.Px(160), list.ClientSize.Width - used - 4);
        }

        // --- window lifecycle ---------------------------------------------------

        protected override void SetVisibleCore(bool value)
        {
            // --tray: create the handle (hotkeys and the show signal need it) but stay hidden.
            if (startHidden && !shownOnce)
            {
                shownOnce = true;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            bool panel = RegisterHotKey(Handle, HotkeyPanel, ModControl | ModShift | ModNoRepeat, VkJ);
            bool grab = RegisterHotKey(Handle, HotkeyGrab, ModControl | ModShift | ModNoRepeat, VkG);
            hotkeyNote = panel && grab ? "" : "热键被其它程序占用：" + (panel ? "" : "Ctrl+Shift+J ") + (grab ? "" : "Ctrl+Shift+G");
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotKey(Handle, HotkeyPanel);
            UnregisterHotKey(Handle, HotkeyGrab);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyPanel) TogglePanel();
                else if (id == HotkeyGrab) GrabFromClipboard();
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    if (search.Text.Length > 0) search.Clear();
                    else Hide();
                    return true;
                case Keys.F5:
                    Reload();
                    return true;
                case Keys.F2:
                    Rename();
                    return true;
                case Keys.Control | Keys.F:
                    search.Focus();
                    search.SelectAll();
                    return true;
                case Keys.Control | Keys.Enter:
                    CopyGroup(true);
                    return true;
                case Keys.Control | Keys.Shift | Keys.C:
                    CopyValue();
                    return true;
                case Keys.Control | Keys.C:
                    if (!search.Focused || search.SelectionLength == 0)
                    {
                        CopySelection(false);
                        return true;
                    }
                    break;
                case Keys.Delete:
                    if (!search.Focused)
                    {
                        Delete();
                        return true;
                    }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ShowPanel()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            SetForegroundWindow(Handle);
            search.Focus();
            search.SelectAll();
            Reload();
        }

        void TogglePanel()
        {
            if (Visible && WindowState != FormWindowState.Minimized && GetForegroundWindow() == Handle) Hide();
            else ShowPanel();
        }

        void Quit()
        {
            exiting = true;
            tray.Visible = false;
            Close();
        }

        // --- data -----------------------------------------------------------

        void CheckBinary()
        {
            JtResult r = Jt.Run(new[] { "version" }, null);
            if (r.Ok) return;
            MessageBox.Show(this, "无法运行 jt 命令行（" + Jt.Bin + "）：\n" + r.Error + "\n\n请先安装 jt.exe，或用环境变量 JT_BIN 指向它。", "jt", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void Reload()
        {
            string error;
            List<Secret> items = Jt.List(out error);
            if (error != null)
            {
                SetStatus("读取失败：" + error);
                return;
            }
            secrets = items;
            VaultStatus st = Jt.Status(out error);
            if (st == null) syncNote = error;
            else if (!st.Git) syncNote = "未配置同步（jt init --repo URL）";
            else if (st.Dirty) syncNote = "有改动未同步";
            else if (!st.Ahead.HasValue) syncNote = "已提交，还没有和远端比对过";
            else if (st.Ahead.Value > 0) syncNote = st.Ahead.Value + " 个提交未推送";
            else syncNote = "已同步";
            ApplyFilter();
            ShowSummary();
        }

        // ApplyFilter rebuilds the rows, grouped by namespace so a block that was
        // split into several entries reads as one unit.
        void ApplyFilter()
        {
            string query = search.Text.Trim();
            string lower = query.ToLowerInvariant();
            Secret keep = Selected();
            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (Secret s in secrets)
            {
                if (query.Length > 0 && !s.Name.ToLowerInvariant().Contains(lower) && !s.Description.ToLowerInvariant().Contains(lower) && !s.Id.Contains(query)) continue;
                var item = new ListViewItem(new[] { s.Name, s.Ref, s.Preview, OneLine(s.Description), LocalTime(s.UpdatedAt) }) { Tag = s, ToolTipText = s.Description };
                string ns = s.Namespace;
                ListViewGroup group;
                if (!groups.TryGetValue(ns, out group))
                {
                    group = new ListViewGroup(ns.Length == 0 ? "未分组" : ns) { Tag = ns };
                    groups[ns] = group;
                    list.Groups.Add(group);
                }
                item.Group = group;
                list.Items.Add(item);
                if (keep != null && keep.Ref == s.Ref) item.Selected = true;
            }
            foreach (ListViewGroup group in list.Groups)
            {
                string ns = (string)group.Tag;
                group.Header = ns.Length == 0 ? "未分组 · " + group.Items.Count + " 条" : ns + " · " + group.Items.Count + " 条 · 整组引用 jt://env/" + ns + " · 一次注入 jt env " + ns + " -- <命令>";
            }
            list.EndUpdate();
            if (list.Items.Count > 0 && list.SelectedItems.Count == 0) list.Items[0].Selected = true;
            if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
        }

        void ShowSummary()
        {
            string text = secrets.Count + " 条密钥 · " + syncNote;
            if (hotkeyNote.Length > 0) text += " · " + hotkeyNote;
            status.Text = text;
        }

        // ShowEntry drops the search filter so a newly created entry is visible and selected.
        void ShowEntry(string reference)
        {
            search.Clear();
            Reload();
            Select(reference);
        }

        static string OneLine(string text)
        {
            return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        }

        static string LocalTime(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return iso;
            return t.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        string NamespaceHint()
        {
            var names = new SortedSet<string>();
            foreach (Secret s in secrets)
            {
                if (s.Namespace.Length > 0) names.Add(s.Namespace);
            }
            string hint = "格式：命名空间/VAR_NAME，例如 cf/CLOUDFLARE_API_TOKEN。";
            if (names.Count > 0) hint += " 已有命名空间：" + string.Join(", ", names);
            return hint;
        }

        // Selected is the row actions act on: the focused row when several are selected.
        Secret Selected()
        {
            if (list.SelectedItems.Count == 0) return null;
            if (list.FocusedItem != null && list.FocusedItem.Selected) return (Secret)list.FocusedItem.Tag;
            return (Secret)list.SelectedItems[0].Tag;
        }

        List<Secret> SelectedAll()
        {
            var all = new List<Secret>();
            foreach (ListViewItem item in list.SelectedItems) all.Add((Secret)item.Tag);
            return all;
        }

        // CopySelection copies one reference, or the selected rows as "name  reference"
        // lines in the jt ref <namespace> format, with the jt://env/<ns> token for
        // every group that is selected completely.
        void CopySelection(bool hideAfter)
        {
            List<Secret> chosen = SelectedAll();
            if (chosen.Count == 0) return;
            if (chosen.Count == 1)
            {
                CopyReference(hideAfter);
                return;
            }
            var sb = new System.Text.StringBuilder();
            var done = new HashSet<string>();
            foreach (Secret s in chosen)
            {
                string ns = s.Namespace;
                if (ns.Length > 0 && done.Add(ns))
                {
                    int inGroup = 0, picked = 0;
                    foreach (Secret other in secrets)
                    {
                        if (other.Namespace == ns) inGroup++;
                    }
                    foreach (Secret other in chosen)
                    {
                        if (other.Namespace == ns) picked++;
                    }
                    if (picked == inGroup) sb.Append("jt://env/").Append(ns).Append("  （整组注入：jt env ").Append(ns).Append(" -- <命令>）\n");
                }
                sb.Append(s.Name).Append("  ").Append(s.Ref).Append('\n');
            }
            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception e)
            {
                SetStatus("写剪贴板失败：" + e.Message);
                return;
            }
            Notify("已复制 " + chosen.Count + " 条引用", chosen[0].Name + " …");
            if (hideAfter) Hide();
        }

        void Select(string reference)
        {
            foreach (ListViewItem item in list.Items)
            {
                if (((Secret)item.Tag).Ref == reference)
                {
                    list.SelectedItems.Clear();
                    item.Selected = true;
                    list.FocusedItem = item;
                    item.EnsureVisible();
                    return;
                }
            }
        }

        void MoveSelection(int delta)
        {
            if (list.Items.Count == 0) return;
            int index = list.FocusedItem != null && list.FocusedItem.Selected ? list.FocusedItem.Index : (list.SelectedIndices.Count == 0 ? -1 : list.SelectedIndices[0]);
            index = Math.Max(0, Math.Min(list.Items.Count - 1, index + delta));
            list.SelectedItems.Clear();
            list.Items[index].Selected = true;
            list.FocusedItem = list.Items[index];
            list.Items[index].EnsureVisible();
        }

        // --- actions --------------------------------------------------------

        void CopyReference(bool hideAfter)
        {
            Secret s = Selected();
            if (s == null) return;
            JtResult r = Jt.Run(new[] { "ref", s.Ref }, null);
            if (!r.Ok)
            {
                Fail("复制引用失败", r);
                return;
            }
            Notify("引用已复制，直接粘贴给 AI", s.Name + "\n" + s.Ref);
            if (hideAfter) Hide();
        }

        // CopyGroup copies the selected entry's whole namespace: jt://env/<ns> and
        // every name/reference line, the format jt ref <namespace> produces.
        void CopyGroup(bool hideAfter)
        {
            Secret s = Selected();
            if (s == null) return;
            if (s.Namespace.Length == 0)
            {
                SetStatus(s.Name + " 不在任何分组里（名称里没有 /）");
                return;
            }
            JtResult r = Jt.Run(new[] { "ref", s.Namespace }, null);
            if (!r.Ok)
            {
                Fail("复制整组引用失败", r);
                return;
            }
            int count = 0;
            foreach (Secret other in secrets)
            {
                if (other.Namespace == s.Namespace) count++;
            }
            Notify("整组 " + s.Namespace + " 的 " + count + " 个引用已复制", "jt env " + s.Namespace + " -- <命令> 一次注入");
            if (hideAfter) Hide();
        }

        void CopyValue()
        {
            Secret s = Selected();
            if (s == null) return;
            JtResult r = Jt.Run(new[] { "copy", s.Ref }, null);
            if (!r.Ok)
            {
                Fail("复制明文失败", r);
                return;
            }
            Notify("明文已复制（不进剪贴板历史）", s.Name + "  " + s.Preview);
        }

        // GrabFromClipboard stores the clipboard. A block with labels ("帐户 ID是…",
        // "API Token: …") is offered as several named entries first; a bare value
        // goes straight to the single-entry dialog.
        void GrabFromClipboard()
        {
            string text = ClipboardText();
            if (text != null)
            {
                MaskResult masked = Split.Mask(text);
                bool structured = masked.Tokens.Count > 0 && masked.Tokens[0].Value.Trim() != text.Trim();
                if (structured && !OrganizeAndStore(masked)) return;
            }
            var name = new PromptField { Label = "名称", Hint = NamespaceHint(), Ascii = true };
            var desc = new PromptField { Label = "描述", Hint = "明文元数据，会进 Git：只写用途、归属，不写密钥" };
            using (var dialog = new PromptForm("抓取剪贴板里的密钥", new[] { name, desc }, "顺便清空 Windows 剪贴板历史 (Win+V)", Settings.ClearHistory))
            {
                if (ShowDialogOnTop(dialog) != DialogResult.OK) return;
                string n = dialog[0].Trim();
                if (n.Length == 0)
                {
                    SetStatus("名称不能为空");
                    return;
                }
                Settings.ClearHistory = dialog.OptionChecked;
                var args = new List<string> { "grab", n };
                AddDescription(args, dialog[1]);
                if (dialog.OptionChecked) args.Add("--clear-history");
                JtResult r = Jt.Run(args.ToArray(), null);
                if (!r.Ok)
                {
                    Fail("抓取失败", r);
                    return;
                }
                Notify("已存进 jt，引用在剪贴板里", n + "\n" + r.Reference);
                ShowEntry(r.Reference);
            }
        }

        static string ClipboardText()
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // OrganizeAndStore names the masked tokens (AI when configured, table
        // otherwise), confirms with the user and stores each row. Returns true
        // when the user asked to store the block whole instead.
        bool OrganizeAndStore(MaskResult masked)
        {
            // Local names first; the dialog itself offers AI naming after showing what would be sent.
            using (var dialog = new SplitForm(masked, Split.LocalNamespace(masked), Split.LocalFields(masked), Settings.ClearHistory, AiSettings.Enabled, AiSettings.AutoSend))
            {
                ShowDialogOnTop(dialog);
                if (dialog.Result == SplitForm.Outcome.Cancel) return false;
                if (dialog.Result == SplitForm.Outcome.Whole) return true;
                Settings.ClearHistory = dialog.ClearHistory;
                string first = "";
                int added = 0;
                foreach (SplitField f in dialog.Chosen())
                {
                    string full = dialog.Namespace + "/" + f.Name;
                    var args = new List<string> { "add", full };
                    AddDescription(args, f.Description);
                    JtResult r = Jt.Run(args.ToArray(), f.Value);
                    if (!r.Ok)
                    {
                        Fail("存入 " + full + " 失败（之前的 " + added + " 条已存入）", r);
                        break;
                    }
                    if (first.Length == 0) first = r.Reference;
                    added++;
                }
                if (added == 0) return false;
                if (dialog.ClearHistory) ClipboardHistory.Clear();
                // The whole group goes on the clipboard: jt://env/<ns> plus every name and reference.
                Jt.Run(new[] { "ref", dialog.Namespace }, null);
                Notify("已拆成 " + added + " 条存进 jt，整组引用在剪贴板里", "jt env " + dialog.Namespace + " -- <命令> 一次注入；也可以在面板里复制单条");
                ShowEntry(first);
                return false;
            }
        }

        // AiSetup configures the OpenAI-compatible endpoint; the key goes into jt, not the registry.
        void AiSetup()
        {
            var url = new PromptField { Label = "接口地址", Value = AiSettings.BaseUrl, Ascii = true, Hint = "OpenAI 兼容接口。DeepSeek 填 https://api.deepseek.com；其它服务填到 /v1 为止" };
            var model = new PromptField { Label = "模型", Value = AiSettings.Model, Ascii = true };
            var keyName = new PromptField { Label = "密钥在 jt 里的名称", Value = AiSettings.KeyName, Ascii = true, Hint = "API Key 本身存在 jt 里，和别的密钥一样加密、同步" };
            var key = new PromptField { Label = "API Key", Masked = true, Ascii = true, Hint = "留空表示沿用 jt 里已有的那条" };
            using (var dialog = new PromptForm("AI 设置", new[] { url, model, keyName, key },
                new[] { "启用 AI 命名（只发送标签、占位符和值的长度/类型，发送前校验，真值不出本机）", "抓取时自动发送，不先停在预览（关掉则每次手动点“用 AI 命名”）" },
                new[] { AiSettings.Enabled, AiSettings.AutoSend }))
            {
                if (ShowDialogOnTop(dialog) != DialogResult.OK) return;
                if (dialog[0].Trim().Length == 0 || dialog[1].Trim().Length == 0 || dialog[2].Trim().Length == 0)
                {
                    SetStatus("接口地址、模型和密钥名称都不能为空");
                    return;
                }
                AiSettings.BaseUrl = dialog[0].Trim();
                AiSettings.Model = dialog[1].Trim();
                AiSettings.KeyName = dialog[2].Trim();
                if (dialog[3].Length > 0)
                {
                    bool exists = false;
                    foreach (Secret s in secrets)
                    {
                        if (s.Name == AiSettings.KeyName) exists = true;
                    }
                    JtResult r = exists
                        ? Jt.Run(new[] { "set", AiSettings.KeyName }, dialog[3])
                        : Jt.Run(new[] { "add", AiSettings.KeyName, "--description=AI 整理用的 API Key（jt-gui）" }, dialog[3]);
                    if (!r.Ok)
                    {
                        Fail("保存 API Key 失败", r);
                        return;
                    }
                    Reload();
                }
                AiSettings.Enabled = dialog.Option(0);
                AiSettings.AutoSend = dialog.Option(1);
                if (!dialog.Option(0))
                {
                    SetStatus("AI 整理已关闭");
                    return;
                }
                UseWaitCursor = true;
                string error;
                bool ok = Ai.Test(out error);
                UseWaitCursor = false;
                if (ok) Notify("AI 连接正常", AiSettings.BaseUrl + " · " + AiSettings.Model);
                else MessageBox.Show(this, error + "\n\n设置已保存；修好后再试一次。", "AI 连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void AddTyped()
        {
            var name = new PromptField { Label = "名称", Hint = NamespaceHint(), Ascii = true };
            var value = new PromptField { Label = "值", Masked = true, Ascii = true };
            var desc = new PromptField { Label = "描述", Hint = "明文元数据，会进 Git：只写用途、归属，不写密钥" };
            using (var dialog = new PromptForm("新建密钥", new[] { name, value, desc }, null, false))
            {
                if (ShowDialogOnTop(dialog) != DialogResult.OK) return;
                string n = dialog[0].Trim();
                if (n.Length == 0 || dialog[1].Length == 0)
                {
                    SetStatus("名称和值都不能为空");
                    return;
                }
                var args = new List<string> { "add", n };
                AddDescription(args, dialog[2]);
                JtResult r = Jt.Run(args.ToArray(), dialog[1]);
                if (!r.Ok)
                {
                    Fail("新建失败", r);
                    return;
                }
                Jt.Run(new[] { "ref", r.Reference }, null);
                Notify("已存进 jt，引用在剪贴板里", n + "\n" + r.Reference);
                ShowEntry(r.Reference);
            }
        }

        static void AddDescription(List<string> args, string description)
        {
            if (description.Length == 0) return;
            args.Add("--description=" + description);
        }

        // SetValue shows the current value (masked, with a "show" toggle) so it can
        // be inspected or edited in place. The value reaches the GUI the only way
        // jt hands values out: injected into a child process, which here is this
        // same exe in --print-env mode.
        void SetValue()
        {
            Secret s = Selected();
            if (s == null) return;
            JtResult current = Jt.Run(new[] { "resolve", s.Ref, "--exec", Program.ExePath, Program.PrintEnvFlag, "JT_SECRET" }, null);
            if (!current.Ok)
            {
                Fail("读取当前值失败", current);
                return;
            }
            string before = current.Stdout;
            var value = new PromptField
            {
                Label = "值",
                Value = before,
                Masked = true,
                Ascii = true,
                Hint = s.Name + " 的引用 " + s.Ref + " 保持不变；改完确定即更新。粘贴多行内容会自动变成多行框。",
            };
            using (var dialog = new PromptForm("查看 / 更新值 " + s.Name, new[] { value }, null, false))
            {
                if (ShowDialogOnTop(dialog) != DialogResult.OK) return;
                string after = dialog[0];
                if (after.Length == 0)
                {
                    SetStatus("值不能为空");
                    return;
                }
                if (after == before)
                {
                    SetStatus("值没有变化");
                    return;
                }
                JtResult r = Jt.Run(new[] { "set", s.Ref }, after);
                if (!r.Ok)
                {
                    Fail("更新失败", r);
                    return;
                }
                Notify("已更新", s.Name);
                Reload();
            }
        }

        void Rename()
        {
            Secret s = Selected();
            if (s == null) return;
            var name = new PromptField { Label = "新名称", Value = s.Name, Hint = NamespaceHint(), Ascii = true };
            using (var dialog = new PromptForm("改名", new[] { name }, null, false))
            {
                if (ShowDialogOnTop(dialog) != DialogResult.OK) return;
                string n = dialog[0].Trim();
                if (n.Length == 0 || n == s.Name) return;
                JtResult r = Jt.Run(new[] { "mv", s.Ref, n }, null);
                if (!r.Ok)
                {
                    Fail("改名失败", r);
                    return;
                }
                Reload();
                Select(s.Ref);
            }
        }

        void Describe()
        {
            Secret s = Selected();
            if (s == null) return;
            var desc = new PromptField { Label = "描述", Value = s.Description, Hint = "明文元数据，会进 Git：只写用途、归属，不写密钥。留空即清除。" };
            using (var dialog = new PromptForm("描述 " + s.Name, new[] { desc }, null, false))
            {
                if (ShowDialogOnTop(dialog) != DialogResult.OK) return;
                if (dialog[0] == s.Description) return;
                JtResult r = Jt.Run(new[] { "describe", s.Ref, dialog[0] }, null);
                if (!r.Ok)
                {
                    Fail("修改描述失败", r);
                    return;
                }
                Reload();
                Select(s.Ref);
            }
        }

        // Delete removes every selected row (a whole group when its header was clicked).
        void Delete()
        {
            List<Secret> chosen = SelectedAll();
            if (chosen.Count == 0) return;
            string what = chosen.Count == 1
                ? "删除 " + chosen[0].Name + "？\n引用 " + chosen[0].Ref + " 会失效，已贴出去的地方不再能解析。"
                : "删除选中的 " + chosen.Count + " 条？\n它们的引用都会失效，已贴出去的地方不再能解析。";
            DialogResult answer = MessageBox.Show(this, what, "删除密钥", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            foreach (Secret s in chosen)
            {
                JtResult r = Jt.Run(new[] { "rm", s.Ref }, null);
                if (!r.Ok)
                {
                    Fail("删除 " + s.Name + " 失败", r);
                    break;
                }
            }
            Reload();
        }

        void Sync()
        {
            SetStatus("正在同步…");
            UseWaitCursor = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                JtResult r = Jt.Run(new[] { "sync" }, null);
                BeginInvoke(new Action(() =>
                {
                    UseWaitCursor = false;
                    Reload();
                    if (r.Ok) Notify("同步完成", "vault 已与远端一致");
                    else Fail("同步失败", r);
                }));
            });
        }

        // --- feedback -------------------------------------------------------

        DialogResult ShowDialogOnTop(Form dialog)
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                dialog.StartPosition = FormStartPosition.CenterParent;
                return dialog.ShowDialog(this);
            }
            // Opened from the hotkey or tray while the panel is hidden: make sure it comes to the front.
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.TopMost = true;
            dialog.Shown += delegate
            {
                dialog.Activate();
                SetForegroundWindow(dialog.Handle);
            };
            return dialog.ShowDialog();
        }

        void SetStatus(string text)
        {
            status.Text = OneLine(text);
        }

        void Notify(string title, string text)
        {
            SetStatus(title);
            tray.ShowBalloonTip(3000, title, text.Length > 0 ? text : title, ToolTipIcon.None);
        }

        void Fail(string title, JtResult r)
        {
            SetStatus(title + "：" + r.Error);
            string detail = r.Stderr.Trim();
            if (detail.Length == 0) detail = r.Error;
            MessageBox.Show(Visible ? this : null, detail, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
