// Split.cs turns a pasted block of account information into separate secrets.
//
// Masking is default-deny: the text that may leave the machine for AI naming
// keeps only (a) non-ASCII text such as Chinese labels, (b) ASCII words that
// are on the label/brand allow-list, (c) ASCII runs of at most three
// characters, and (d) punctuation. Every other ASCII run, every email address
// and the whole right-hand side of a "label: value" line becomes a
// placeholder. IsSafeToSend re-checks the outgoing text against exactly that
// grammar, so a bug in masking fails closed instead of leaking. The values are
// mapped back locally. Without AI the names come from a table. C# 5 only.
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
        static readonly Regex CopulaAny = new Regex(@"^(?<label>.+?)\s*(?:是|为)\s*(?<value>\S.*?)\s*$");
        static readonly Regex SecretLabel = new Regex(@"密码|口令|密钥|秘钥|令牌|凭证|凭据|暗号|答案|token|secret|password|passwd|pass|key|pin|code", RegexOptions.IgnoreCase);
        // Placeholders are matched whole so their letters and digits are never masked again.
        static readonly Regex RunOrPlaceholder = new Regex(@"<[A-Z_0-9]+>|[A-Za-z0-9][A-Za-z0-9_\-./+=~%@]*");

        // Words that may stay in the outgoing text: label vocabulary and brand
        // names. Nothing here can be a secret; everything else is masked.
        static readonly HashSet<string> AllowedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            // label vocabulary
            "api", "apis", "token", "tokens", "key", "keys", "secret", "secrets", "access", "account", "accounts", "id", "ids",
            "email", "mail", "user", "users", "username", "name", "login", "password", "passwd", "pass", "pwd", "host", "hostname",
            "port", "url", "uri", "endpoint", "domain", "zone", "zones", "region", "bucket", "database", "server", "client",
            "app", "application", "project", "workspace", "global", "private", "public", "service", "value", "info",
            "information", "config", "configuration", "settings", "setting", "default", "production", "staging", "development",
            "prod", "dev", "test", "admin", "root", "auth", "bearer", "basic", "oauth", "webhook", "callback", "redirect",
            "scope", "scopes", "permission", "permissions", "role", "roles", "expires", "expiry", "created", "note", "notes",
            "remark", "description", "label", "type", "kind", "env", "environment", "version", "new", "old", "backup", "main",
            "primary", "secondary", "read", "write", "readonly", "full", "limited", "edit", "dns", "worker", "workers", "pages",
            "tunnel", "origin", "ssl", "tls", "cert", "certificate", "connection", "string", "credential", "credentials", "http", "https", "www",
            "client_id", "client_secret", "api_key", "api_token", "access_key", "secret_key", "account_id", "zone_id",
            "access_key_id", "secret_access_key", "apikey", "apitoken", "accesskey", "secretkey", "accountid", "zoneid",
            "accesskeyid", "secretaccesskey", "clientid", "clientsecret", "database_url", "endpoint_url",
            // brands and products
            "cloudflare", "cloudfalre", "github", "gitlab", "bitbucket", "aws", "amazon", "azure", "microsoft", "google", "gcp",
            "openai", "deepseek", "anthropic", "claude", "gemini", "telegram", "discord", "slack", "twilio", "sendgrid",
            "mailgun", "stripe", "paypal", "alipay", "wechat", "weixin", "aliyun", "alibaba", "tencent", "huawei", "baidu",
            "vercel", "netlify", "heroku", "digitalocean", "linode", "vultr", "docker", "npm", "pypi", "postgres", "postgresql",
            "mysql", "redis", "mongodb", "mongo", "supabase", "firebase", "notion", "feishu", "lark", "dingtalk", "zoom",
            "apple", "icloud", "wrangler", "terraform", "kubernetes", "k8s",
        };

        // Vendor prefixes worth telling the model about; anything else stays hidden.
        static readonly string[] KnownPrefixes = {
            "github_pat_", "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "glpat-", "cfat_", "cfut_", "sk-ant-", "sk-proj-", "sk-",
            "xoxb-", "xoxp-", "xoxa-", "xapp-", "npm_", "pypi-", "hf_", "r8_", "dop_v1_", "doo_v1_", "pk_live_", "sk_live_",
            "pk_test_", "sk_test_", "rk_live_", "whsec_", "shpat_", "shpss_", "lin_api_", "figd_", "AKIA", "ASIA", "AIza", "ya29.", "SG.",
        };

        // Mask replaces every value-like part of text with <V1>, <V2>… (<EMAIL_n> for
        // addresses). On a "label: value" line the whole right-hand side is one value.
        public static MaskResult Mask(string text)
        {
            var result = new MaskResult { Cloudflare = Regex.IsMatch(text, "cloudf", RegexOptions.IgnoreCase) };
            var sb = new StringBuilder();
            var counters = new int[2];
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
                // "密码是我爱北京": after a secret-like label, even a Chinese value is a value.
                if (!m.Success)
                {
                    Match any = CopulaAny.Match(line);
                    if (any.Success && SecretLabel.IsMatch(any.Groups["label"].Value)) m = any;
                }
                if (m.Success)
                {
                    string label = m.Groups["label"].Value.Trim();
                    string value = m.Groups["value"].Value.Trim();
                    bool isEmail = Email.Match(value).Value == value;
                    string placeholder = Next(counters, isEmail);
                    result.Tokens.Add(new MaskedToken { Placeholder = placeholder, Value = value, Label = label, Shape = isEmail ? "邮箱地址" : Shape(value) });
                    // The label itself may carry ASCII that is not vocabulary; mask it too.
                    sb.Append(MaskRuns(label, result, counters, label)).Append(": <").Append(placeholder).Append(">\n");
                    continue;
                }
                sb.Append(MaskRuns(line, result, counters, null)).Append('\n');
            }
            result.Text = sb.ToString().TrimEnd('\n');
            return result;
        }

        static string Next(int[] counters, bool email)
        {
            return email ? "EMAIL_" + (++counters[1]) : "V" + (++counters[0]);
        }

        // MaskRuns masks addresses and every ASCII run that is not short or allowed.
        // fixedLabel is used as the token label when the line has one; otherwise the
        // text before the run serves as its label.
        static string MaskRuns(string line, MaskResult result, int[] counters, string fixedLabel)
        {
            string withEmails = Email.Replace(line, match =>
            {
                string placeholder = Next(counters, true);
                result.Tokens.Add(new MaskedToken { Placeholder = placeholder, Value = match.Value, Label = fixedLabel ?? LabelBefore(line, match.Index), Shape = "邮箱地址" });
                return "<" + placeholder + ">";
            });
            return RunOrPlaceholder.Replace(withEmails, match =>
            {
                string run = match.Value;
                if (run.StartsWith("<") || IsAllowedRun(run)) return run;
                string placeholder = Next(counters, false);
                result.Tokens.Add(new MaskedToken { Placeholder = placeholder, Value = run, Label = fixedLabel ?? LabelBefore(withEmails, match.Index), Shape = Shape(run) });
                return "<" + placeholder + ">";
            });
        }

        // IsAllowedRun: three characters cannot be a secret; longer runs must be vocabulary.
        static bool IsAllowedRun(string run)
        {
            return run.Length <= 3 || AllowedWords.Contains(run);
        }

        // IsSafeToSend re-derives the guarantee from the outgoing text alone: only
        // placeholders, allowed words, short runs, non-ASCII text and punctuation.
        // offending names the first run that breaks the rule.
        public static bool IsSafeToSend(string outgoing, out string offending)
        {
            offending = null;
            if (outgoing.IndexOf('@') >= 0)
            {
                offending = "@";
                return false;
            }
            foreach (Match m in RunOrPlaceholder.Matches(outgoing))
            {
                string run = m.Value;
                if (run.StartsWith("<"))
                {
                    if (!Regex.IsMatch(run, @"^<(V[0-9]+|EMAIL_[0-9]+)>$"))
                    {
                        offending = run;
                        return false;
                    }
                    continue;
                }
                if (!IsAllowedRun(run))
                {
                    offending = run;
                    return false;
                }
            }
            return true;
        }

        static string LabelBefore(string line, int index)
        {
            string before = line.Substring(0, index);
            int cut = before.LastIndexOf('>');
            if (cut >= 0) before = before.Substring(cut + 1);
            return before.Trim(' ', '\t', ':', '：', '=', '是', '为', '，', ',', '、');
        }

        // Shape describes a value without revealing it: length, alphabet, and a
        // vendor prefix from the known list when there is one.
        public static string Shape(string value)
        {
            bool hex = Regex.IsMatch(value, "^[0-9a-fA-F]+$");
            bool digits = Regex.IsMatch(value, "^[0-9]+$");
            bool alnum = Regex.IsMatch(value, "^[A-Za-z0-9]+$");
            string kind = digits ? "纯数字" : hex ? "十六进制" : alnum ? "字母数字" : value.IndexOf(' ') >= 0 ? "含空格的文本" : "含符号的字符串";
            string shape = value.Length + " 个字符，" + kind;
            foreach (string prefix in KnownPrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    shape += "，前缀 " + prefix;
                    break;
                }
            }
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
