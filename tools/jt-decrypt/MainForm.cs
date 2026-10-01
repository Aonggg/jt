// MainForm.cs: load a master key, paste or import anything that contains jt ciphertext, decrypt offline.
using System.Text;

namespace JtDecrypt;

public sealed class MainForm : Form
{
    byte[]? key;
    List<Entry> entries = [];

    readonly Label keyStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(6, 6, 0, 0) };
    readonly TextBox input = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsReturn = true, AcceptsTab = true, Dock = DockStyle.Fill, AllowDrop = true, Font = new Font("Consolas", 9.5f) };
    readonly CheckBox reveal = new() { Text = "显示明文", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    readonly Label summary = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 9, 0, 0) };
    readonly ListView results = new() { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };
    readonly Button copyOne = new() { Text = "复制选中的明文", AutoSize = true, Enabled = false };
    readonly Button copyAll = new() { Text = "复制全部（名称=明文）", AutoSize = true, Enabled = false };
    readonly Button saveAll = new() { Text = "另存为文本…", AutoSize = true, Enabled = false };

    public MainForm(string? keyFile, string? initialFile)
    {
        Text = "jt 解密工具";
        Font = SystemFonts.MessageBoxFont!;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        // Sizes below are 96-dpi units; fonts scale by themselves, the window and columns must follow.
        float dpi = DeviceDpi / 96f;
        MinimumSize = Scale(760, 560, dpi);
        Size = Scale(980, 720, dpi);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        // 1. key
        var keyBox = new GroupBox { Text = "1. 主密钥（jt key export 导出的 base64，或 key 文件）", Dock = DockStyle.Fill, AutoSize = true };
        var keyRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4) };
        keyRow.Controls.Add(Button("选择密钥文件…", (_, _) => PickKeyFile()));
        keyRow.Controls.Add(Button("粘贴密钥", (_, _) => PasteKey()));
        keyRow.Controls.Add(Button("读取本机 jt 的密钥", (_, _) => LoadLocalKey()));
        keyRow.Controls.Add(keyStatus);
        keyBox.Controls.Add(keyRow);
        layout.Controls.Add(keyBox, 0, 0);

        // 2. input
        var inBox = new GroupBox { Text = "2. 要解密的内容（密文 / vault.json / Notion 导出的 .md、.csv / 表格复制的文字；文件可直接拖进来）", Dock = DockStyle.Fill };
        var inLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4) };
        inLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inLayout.Controls.Add(input, 0, 0);
        var inButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        inButtons.Controls.Add(Button("导入文件…", (_, _) => ImportFile()));
        inButtons.Controls.Add(Button("粘贴", (_, _) => { if (Clipboard.ContainsText()) SetInput(Clipboard.GetText()); }));
        inButtons.Controls.Add(Button("清空", (_, _) => { input.Clear(); ShowResults([]); }));
        inLayout.Controls.Add(inButtons, 1, 0);
        inBox.Controls.Add(inLayout);
        layout.Controls.Add(inBox, 0, 1);
        input.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        input.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) LoadInputFile(files[0]); };

        // 3. action row
        var actRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var decrypt = new Button { Text = "3. 解密", AutoSize = true, Padding = new Padding(16, 4, 16, 4), Font = new Font(Font, FontStyle.Bold) };
        decrypt.Click += (_, _) => Decrypt();
        actRow.Controls.Add(decrypt);
        actRow.Controls.Add(reveal);
        actRow.Controls.Add(summary);
        reveal.CheckedChanged += (_, _) => ShowResults(entries);
        layout.Controls.Add(actRow, 0, 2);
        AcceptButton = decrypt;

        // 4. results
        results.Columns.Add("名称", (int)(280 * dpi));
        results.Columns.Add("明文", (int)(360 * dpi));
        results.Columns.Add("来源 / 描述", (int)(290 * dpi));
        results.SelectedIndexChanged += (_, _) => copyOne.Enabled = results.SelectedItems.Count == 1 && ((Entry)results.SelectedItems[0].Tag!).Ok;
        results.DoubleClick += (_, _) => CopySelected();
        layout.Controls.Add(results, 0, 3);

        // 5. output row
        var outRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        copyOne.Click += (_, _) => CopySelected();
        copyAll.Click += (_, _) => CopySecret(Export(), "已复制全部明文（名称=明文，每行一条）");
        saveAll.Click += (_, _) => SaveAll();
        outRow.Controls.Add(copyOne);
        outRow.Controls.Add(copyAll);
        outRow.Controls.Add(saveAll);
        outRow.Controls.Add(new Label { Text = "全部在本机完成，不联网。复制出去的明文不进 Win+V 剪贴板历史、不上云。", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 9, 0, 0) });
        layout.Controls.Add(outRow, 0, 4);

        SetKey(null, "还没有密钥");
        if (keyFile != null) TryLoadKey(() => MasterKey.Load(File.ReadAllBytes(keyFile)), Path.GetFileName(keyFile));
        if (initialFile != null) Shown += (_, _) => LoadInputFile(initialFile);
    }

    static Size Scale(int w, int h, float dpi) => new((int)(w * dpi), (int)(h * dpi));

    /// <summary>A TextBox only breaks lines on CRLF; files and clipboard text from other systems use LF.</summary>
    void SetInput(string text) => input.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        b.Click += onClick;
        return b;
    }

    // --- key ---------------------------------------------------------------

    void PickKeyFile()
    {
        using var dialog = new OpenFileDialog { Title = "选择主密钥文件", Filter = "密钥文件|*.txt;*.key;key|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        TryLoadKey(() => MasterKey.Load(File.ReadAllBytes(dialog.FileName)), Path.GetFileName(dialog.FileName));
    }

    void PasteKey()
    {
        using var dialog = new KeyPrompt();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        TryLoadKey(() => MasterKey.Parse(dialog.Value), "粘贴的密钥");
    }

    void LoadLocalKey()
    {
        string path = MasterKey.LocalPath;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "这台电脑上没有 jt 的密钥文件：\n" + path, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        TryLoadKey(() => MasterKey.Load(File.ReadAllBytes(path)), "本机 jt 密钥");
    }

    void TryLoadKey(Func<byte[]> load, string label)
    {
        try
        {
            SetKey(load(), label);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "密钥无法使用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void SetKey(byte[]? k, string label)
    {
        key = k;
        keyStatus.Text = k == null ? label : $"✓ {label}，指纹 {MasterKey.Fingerprint(k)}";
        keyStatus.ForeColor = k == null ? SystemColors.GrayText : Color.FromArgb(0, 120, 0);
        if (entries.Count > 0) Decrypt();
    }

    // --- input -------------------------------------------------------------

    void ImportFile()
    {
        using var dialog = new OpenFileDialog { Title = "导入要解密的文件", Filter = "文本、Markdown、JSON、CSV|*.txt;*.md;*.json;*.csv|所有文件|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadInputFile(dialog.FileName);
    }

    void LoadInputFile(string path)
    {
        try
        {
            SetInput(File.ReadAllText(path, Encoding.UTF8));
            summary.Text = "已导入 " + Path.GetFileName(path);
            if (key != null) Decrypt();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // --- decrypt -----------------------------------------------------------

    void Decrypt()
    {
        if (key == null)
        {
            MessageBox.Show(this, "先在第 1 步载入主密钥。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (input.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "第 2 步还是空的：粘贴密文或导入文件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ShowResults(Scanner.Decrypt(key, input.Text));
        int ok = entries.Count(e => e.Ok), failed = entries.Count - ok;
        summary.Text = entries.Count == 0
            ? "没有找到能用这把密钥解开的内容。检查密钥指纹是否和加密时的一致，或内容里是否真的有密文。"
            : $"解开 {ok} 条" + (failed > 0 ? $"，{failed} 条解不开（密钥不对或密文损坏）" : "");
    }

    void ShowResults(List<Entry> list)
    {
        entries = list;
        results.BeginUpdate();
        results.Items.Clear();
        foreach (Entry e in entries)
        {
            string shown = !e.Ok ? "—" : reveal.Checked ? OneLine(e.Value) : new string('•', Math.Min(Math.Max(e.Value.Length, 6), 24));
            var item = new ListViewItem([e.Name, shown, e.Source]) { Tag = e };
            if (!e.Ok) item.ForeColor = Color.Firebrick;
            results.Items.Add(item);
        }
        results.EndUpdate();
        copyOne.Enabled = false;
        copyAll.Enabled = saveAll.Enabled = entries.Any(e => e.Ok);
    }

    static string OneLine(string s) => s.Contains('\n') ? s.Replace("\r", "").Replace("\n", "⏎") : s;

    // --- output ------------------------------------------------------------

    void CopySelected()
    {
        if (results.SelectedItems.Count != 1) return;
        var e = (Entry)results.SelectedItems[0].Tag!;
        if (e.Ok) CopySecret(e.Value, "已复制 " + e.Name + " 的明文");
    }

    void CopySecret(string text, string note)
    {
        try
        {
            SecretClipboard.Set(Handle, text);
            summary.Text = note;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OutOfMemoryException)
        {
            MessageBox.Show(this, ex.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    string Export()
    {
        var sb = new StringBuilder();
        foreach (Entry e in entries.Where(e => e.Ok)) sb.Append(e.Name).Append('=').Append(e.Value).Append("\r\n");
        return sb.ToString();
    }

    void SaveAll()
    {
        using var dialog = new SaveFileDialog { Title = "另存为文本（明文！）", Filter = "文本文件|*.txt", FileName = "jt-明文导出.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, "文件里是明文密码和令牌，任何能打开它的人都能直接使用。\n用完请删除。继续？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            File.WriteAllText(dialog.FileName, Export(), new UTF8Encoding(true));
            summary.Text = "已保存到 " + dialog.FileName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>Masked single-line prompt for a pasted base64 key.</summary>
sealed class KeyPrompt : Form
{
    readonly TextBox box = new() { UseSystemPasswordChar = true, Width = 420 };
    public string Value => box.Text;

    public KeyPrompt()
    {
        Text = "粘贴主密钥";
        Font = SystemFonts.MessageBoxFont!;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12), WrapContents = false };
        panel.Controls.Add(new Label { Text = "jt key export 导出的那串 base64（44 个字符）：", AutoSize = true });
        panel.Controls.Add(box);
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        panel.Controls.Add(buttons);
        Controls.Add(panel);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { if (Clipboard.ContainsText()) { string t = Clipboard.GetText().Trim(); if (t.Length == 44 && !t.Contains(' ')) box.Text = t; } };
    }
}
