// Split.cs turns a pasted block of account information into separate secrets.
// Everything that looks like a value is replaced by a placeholder first, so the
// text that may be shown to an AI for naming never contains a real value; the
// values are mapped back locally. Without AI the names come from a table.
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace JtGui
{
    sealed class MaskedToken
    {
        public string Placeholder = ""; // V1, EMAIL_1
        public string Value = "";
        public string Label = "";       // text before the value on its line
        public string Shape = "";       // "32 个字符，十六进制", never the value itself
    }

    sealed class MaskResult
    {
        public string Text = "";                                 // original with placeholders
        public readonly List<MaskedToken> Tokens = new List<MaskedToken>();
        public bool Cloudflare;                                  // the block mentions Cloudflare

        public MaskedToken Find(string placeholder)
        {
            foreach (MaskedToken t in Tokens)
            {
                if (t.Placeholder == placeholder) return t;
            }
            return null;
        }
    }

    sealed class SplitField
    {
        public string Name = "", Label = "", Value = "", Description = "";
        public bool Include = true;
    }

    static class Split
    {
        static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
        // label<sep>value: ASCII separators first, then 是/为 followed by an ASCII run.
        static readonly Regex Separated = new Regex(@"^(?<label>.+?)\s*[:：=]\s*(?<value>\S.*?)\s*$");
        static readonly Regex Copula = new Regex(@"^(?<label>.+?)\s*(?:是|为)(?=\s*[A-Za-z0-9])\s*(?<value>\S.*?)\s*$");
        // Placeholders are matched whole so their letters and digits are never masked again.
        static readonly Regex RunOrPlaceholder = new Regex(@"<[A-Z_0-9]+>|[A-Za-z0-9][A-Za-z0-9_\-./+=~]*");
        static readonly HashSet<string> LabelWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "api", "token", "tokens", "key", "keys", "secret", "secrets", "access", "account", "accounts", "id", "email",
            "password", "passwd", "user", "username", "login", "cloudflare", "cloudfalre", "aws", "github", "openai",
            "deepseek", "zone", "endpoint", "url", "host", "hostname", "port", "database", "region", "bucket", "client",
            "app", "application", "project", "workspace", "global", "private", "public", "server", "service", "name",
            "value", "info", "information", "config", "settings", "default", "production", "staging", "development", "prod", "dev",
        };

        // Mask replaces every value-like part of text with <V1>, <V2>… (<EMAIL_n> for
        // addresses). On a "label: value" line the whole right-hand side is one value.
        public static MaskResult Mask(string text)
        {
            var result = new MaskResult { Cloudflare = Regex.IsMatch(text, "cloudf", RegexOptions.IgnoreCase) };
            var sb = new StringBuilder();
            int values = 0, emails = 0;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    sb.Append('\n');
                    continue;
                }
                Match m = Separated.Match(line);
                if (!m.Success) m = Copula.Match(line);
                if (m.Success && m.Groups["value"].Value.Length >= 4)
                {
                    string label = m.Groups["label"].Value.Trim();
                    string value = m.Groups["value"].Value.Trim();
                    bool isEmail = Email.IsMatch(value) && Email.Match(value).Value == value;
                    string placeholder = isEmail ? "EMAIL_" + (++emails) : "V" + (++values);
                    result.Tokens.Add(new MaskedToken { Placeholder = placeholder, Value = value, Label = label, Shape = isEmail ? "邮箱地址" : Shape(value) });
                    sb.Append(label).Append(": <").Append(placeholder).Append(">\n");
                    continue;
                }
                // No separator: mask addresses and value-like ASCII runs in place.
                string withEmails = Email.Replace(line, match =>
                {
                    string placeholder = "EMAIL_" + (++emails);
                    result.Tokens.Add(new MaskedToken { Placeholder = placeholder, Value = match.Value, Label = LabelBefore(line, match.Index), Shape = "邮箱地址" });
                    return "<" + placeholder + ">";
                });
                string masked = RunOrPlaceholder.Replace(withEmails, match =>
                {
                    string run = match.Value;
                    if (run.StartsWith("<") || !LooksLikeValue(run)) return run;
                    string placeholder = "V" + (++values);
                    result.Tokens.Add(new MaskedToken { Placeholder = placeholder, Value = run, Label = LabelBefore(withEmails, match.Index), Shape = Shape(run) });
                    return "<" + placeholder + ">";
                });
                sb.Append(masked).Append('\n');
            }
            result.Text = sb.ToString().TrimEnd('\n');
            return result;
        }

        static bool LooksLikeValue(string run)
        {
            if (LabelWords.Contains(run)) return false;
            bool digit = false;
            foreach (char c in run)
            {
                if (char.IsDigit(c)) digit = true;
            }
            return (digit && run.Length >= 4) || run.Length >= 8;
        }

        static string LabelBefore(string line, int index)
        {
            string before = line.Substring(0, index);
            int cut = before.LastIndexOf('>');
            if (cut >= 0) before = before.Substring(cut + 1);
            return before.Trim(' ', '\t', ':', '：', '=', '是', '为', '，', ',', '、');
        }

        // Shape describes a value without revealing it: length, alphabet, and a
        // vendor prefix like "cfat_" or "ghp_" when there is one.
        public static string Shape(string value)
        {
            bool hex = Regex.IsMatch(value, "^[0-9a-fA-F]+$");
            bool digits = Regex.IsMatch(value, "^[0-9]+$");
            bool alnum = Regex.IsMatch(value, "^[A-Za-z0-9]+$");
            string kind = digits ? "纯数字" : hex ? "十六进制" : alnum ? "字母数字" : value.IndexOf(' ') >= 0 ? "含空格的文本" : "含符号的字符串";
            Match prefix = Regex.Match(value, "^[A-Za-z]{2,6}[_-]");
            string shape = value.Length + " 个字符，" + kind;
            if (prefix.Success) shape += "，前缀 " + prefix.Value;
            return shape;
        }

        // LocalFields names the tokens from a table when no AI is configured.
        public static List<SplitField> LocalFields(MaskResult masked)
        {
            var fields = new List<SplitField>();
            var seen = new HashSet<string>();
            foreach (MaskedToken t in masked.Tokens)
            {
                string name = t.Placeholder.StartsWith("EMAIL") ? (masked.Cloudflare ? "CLOUDFLARE_EMAIL" : "EMAIL") : SuggestName(t.Label, masked.Cloudflare);
                string unique = name;
                for (int n = 2; !seen.Add(unique); n++) unique = name + "_" + n;
                fields.Add(new SplitField { Name = unique, Label = t.Label, Value = t.Value, Description = t.Label });
            }
            return fields;
        }

        public static string LocalNamespace(MaskResult masked)
        {
            if (masked.Cloudflare) return "cf";
            foreach (string word in new[] { "github", "aws", "openai", "deepseek", "google", "azure", "aliyun", "tencent" })
            {
                if (masked.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return word;
            }
            return "app";
        }

        // Order matters: the first alternative that the normalised label contains wins.
        static readonly string[][] Known = {
            new[] { "accesskeyid|访问密钥id|访问密钥标识|accesskey", "AWS_ACCESS_KEY_ID" },
            new[] { "secretaccesskey|秘密访问密钥|私密访问密钥|机密访问密钥|secretkey", "AWS_SECRET_ACCESS_KEY" },
            new[] { "accountid|帐户id|账户id|账号id|帐号id|帐户标识", "{CF}ACCOUNT_ID" },
            new[] { "zoneid|区域id|域id", "{CF}ZONE_ID" },
            new[] { "apitoken|api令牌|令牌|token", "{CF}API_TOKEN" },
            new[] { "globalapikey|apikey|api密钥|apisecret|api秘钥", "{CF}API_KEY" },
            new[] { "email|邮箱|邮件|电子邮件", "{CF}EMAIL" },
            new[] { "username|用户名|登录名|帐号|账号|user", "USERNAME" },
            new[] { "password|密码|口令|passwd", "PASSWORD" },
            new[] { "endpoint|端点|url|链接|地址|host|主机", "ENDPOINT" },
            new[] { "secret|密钥|秘钥", "SECRET" },
            new[] { "key|钥", "KEY" },
            new[] { "id|标识|编号", "ID" },
        };

        public static string SuggestName(string label, bool cloudflare)
        {
            string norm = Regex.Replace(label.ToLowerInvariant(), @"[\s\-_（）()\[\]【】]", "");
            foreach (string[] entry in Known)
            {
                foreach (string alt in entry[0].Split('|'))
                {
                    if (norm.Contains(alt)) return entry[1].Replace("{CF}", cloudflare ? "CLOUDFLARE_" : "");
                }
            }
            string ascii = Regex.Replace(label.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
            return ascii.Length > 0 ? ascii : "FIELD";
        }
    }
}
