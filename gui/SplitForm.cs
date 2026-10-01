// SplitForm shows how a pasted block will be split into named secrets and lets
// the user fix names, drop rows, or fall back to storing the block whole.
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace JtGui
{
    sealed class SplitForm : Form
    {
        public enum Outcome { Cancel, Split, Whole }

        static readonly Regex EnvName = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");

        public Outcome Result = Outcome.Cancel;
        readonly List<SplitField> fields;
        readonly ValueBox ns = new ValueBox { ImeMode = ImeMode.Disable };
        readonly ListView grid = new ListView { View = View.Details, CheckBoxes = true, LabelEdit = true, FullRowSelect = true, HideSelection = false };
        readonly CheckBox clear;

        public SplitForm(string note, string maskedText, string nsValue, List<SplitField> fields, bool clearHistory)
        {
            this.fields = fields;
            Text = "拆成多条密钥";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var table = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Dpi.Pad(14, 12, 14, 10) };
            int width = Dpi.Px(820);

            table.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(width, 0), Margin = Dpi.Pad(0, 0, 0, 8) });

            var nsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Dpi.Pad(0, 0, 0, 6) };
            nsRow.Controls.Add(new Label { Text = "命名空间", AutoSize = true, Margin = Dpi.Pad(0, 7, 8, 0) });
            ns.Width = Dpi.Px(160);
            ns.Text = nsValue;
            nsRow.Controls.Add(ns);
            nsRow.Controls.Add(new Label { Text = "存入后名称 = 命名空间/名称；jt env <命名空间> 一次注入整组", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = Dpi.Pad(12, 7, 0, 0) });
            table.Controls.Add(nsRow);

            grid.Width = width;
            grid.Height = Dpi.Px(Math.Min(320, 60 + 28 * Math.Max(3, fields.Count)));
            grid.Columns.Add("名称（双击改）", Dpi.Px(250));
            grid.Columns.Add("值", Dpi.Px(130));
            grid.Columns.Add("原文标签", Dpi.Px(170));
            grid.Columns.Add("描述", width - Dpi.Px(250 + 130 + 170 + 8));
            foreach (SplitField f in fields)
            {
                var item = new ListViewItem(new[] { f.Name, Preview(f.Value), f.Label, f.Description }) { Checked = f.Include, Tag = f };
                grid.Items.Add(item);
            }
            grid.Margin = Dpi.Pad(0, 0, 0, 6);
            table.Controls.Add(grid);

            if (maskedText != null)
            {
                table.Controls.Add(new Label { Text = "发给 AI 的内容（值已换成占位符，真值没有离开这台电脑）：", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = Dpi.Pad(0, 4, 0, 2) });
                var shown = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = width, Height = Dpi.Px(90), Text = maskedText.Replace("\n", "\r\n"), Margin = Dpi.Pad(0, 0, 0, 6) };
                table.Controls.Add(shown);
            }

            clear = new CheckBox { Text = "顺便清空 Windows 剪贴板历史 (Win+V)", Checked = clearHistory, AutoSize = true, Margin = Dpi.Pad(0, 2, 0, 6) };
            table.Controls.Add(clear);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Width = width, Margin = Dpi.Pad(0, 6, 0, 0) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            var whole = new Button { Text = "整块存为一条", AutoSize = true };
            var split = new Button { Text = "拆分存入", AutoSize = true };
            whole.Click += delegate
            {
                Result = Outcome.Whole;
                DialogResult = DialogResult.OK;
            };
            split.Click += delegate
            {
                string problem = Problem();
                if (problem != null)
                {
                    MessageBox.Show(this, problem, "拆分存入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Result = Outcome.Split;
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(whole);
            buttons.Controls.Add(split);
            table.Controls.Add(buttons);

            Controls.Add(table);
            AcceptButton = split;
            CancelButton = cancel;
        }

        public string Namespace { get { return ns.Text.Trim(); } }

        public bool ClearHistory { get { return clear.Checked; } }

        // Chosen applies edited names and check marks and returns the rows to store.
        public List<SplitField> Chosen()
        {
            var chosen = new List<SplitField>();
            foreach (ListViewItem item in grid.Items)
            {
                var f = (SplitField)item.Tag;
                f.Name = item.Text.Trim();
                f.Include = item.Checked;
                if (f.Include) chosen.Add(f);
            }
            return chosen;
        }

        string Problem()
        {
            if (Namespace.Length == 0 || Namespace.IndexOf('/') >= 0) return "命名空间不能为空，也不能包含 /";
            var names = new HashSet<string>();
            int checkedRows = 0;
            foreach (ListViewItem item in grid.Items)
            {
                if (!item.Checked) continue;
                checkedRows++;
                string name = item.Text.Trim();
                if (!EnvName.IsMatch(name)) return "名称 \"" + name + "\" 不是合法的环境变量名（字母、数字、下划线，不能以数字开头）";
                if (!names.Add(name)) return "名称 \"" + name + "\" 重复了";
            }
            return checkedRows == 0 ? "至少勾选一条" : null;
        }

        static string Preview(string value)
        {
            if (value.Length <= 8) return new string('*', value.Length);
            return value.Substring(0, 2) + "****" + value.Substring(value.Length - 2);
        }
    }
}
