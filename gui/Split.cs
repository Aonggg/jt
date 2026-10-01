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

        // Brands maps a mention (Chinese or English, matched case-insensitively) to
        // the namespace it implies. The earliest mention in the text wins.
        static readonly string[][] Brands = {
            new[] { "cloudflare", "cf" }, new[] { "cloudfalre", "cf" }, new[] { "github", "github" }, new[] { "gitlab", "gitlab" },
            new[] { "aws", "aws" }, new[] { "amazon", "aws" }, new[] { "azure", "azure" }, new[] { "google", "gcp" }, new[] { "gcp", "gcp" },
            new[] { "openai", "openai" }, new[] { "deepseek", "deepseek" }, new[] { "anthropic", "anthropic" }, new[] { "claude", "anthropic" },
            new[] { "telegram", "telegram" }, new[] { "discord", "discord" }, new[] { "slack", "slack" }, new[] { "twilio", "twilio" },
            new[] { "sendgrid", "sendgrid" }, new[] { "stripe", "stripe" }, new[] { "paypal", "paypal" }, new[] { "vercel", "vercel" },
            new[] { "netlify", "netlify" }, new[] { "heroku", "heroku" }, new[] { "digitalocean", "do" }, new[] { "supabase", "supabase" },
            new[] { "firebase", "firebase" }, new[] { "notion", "notion" }, new[] { "docker", "docker" }, new[] { "npm", "npm" },
            new[] { "阿里云", "aliyun" }, new[] { "aliyun", "aliyun" }, new[] { "腾讯云", "tencentcloud" }, new[] { "tencent", "tencent" },
            new[] { "华为云", "huaweicloud" }, new[] { "企业微信", "wecom" }, new[] { "微信支付", "wechatpay" }, new[] { "微信", "wechat" },
            new[] { "wechat", "wechat" }, new[] { "支付宝", "alipay" }, new[] { "alipay", "alipay" }, new[] { "钉钉", "dingtalk" },
            new[] { "dingtalk", "dingtalk" }, new[] { "飞书", "feishu" }, new[] { "feishu", "feishu" }, new[] { "lark", "feishu" },
            new[] { "百度", "baidu" }, new[] { "baidu", "baidu" }, new[] { "七牛", "qiniu" }, new[] { "qiniu", "qiniu" }, new[] { "又拍云", "upyun" },
            new[] { "抖音", "douyin" }, new[] { "字节", "bytedance" }, new[] { "淘宝", "taobao" }, new[] { "京东", "jd" }, new[] { "美团", "meituan" },
            new[] { "高德", "amap" }, new[] { "网易", "netease" },
        };

        public static string LocalNamespace(MaskResult masked)
        {
            int best = int.MaxValue;
            string ns = "app";
            foreach (string[] brand in Brands)
            {
                int at = masked.Text.IndexOf(brand[0], StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && at < best)
                {
                    best = at;
                    ns = brand[1];
                }
            }
            return ns;
        }

        // Terms is the domain vocabulary that appears in credential blocks: each
        // Chinese word maps to an environment-variable fragment (empty = drop the
        // word). Longest match wins, so 数据库 beats 库 and 访问密钥 beats 密钥.
        const string TermTable =
            "数据库连接串=DATABASE_URL;数据库连接字符串=DATABASE_URL;连接字符串=URL;连接串=URL;连接地址=URL;数据库=DATABASE;库=DB;表=TABLE;缓存=CACHE;队列=QUEUE;日志=LOG;" +
            "秘密访问密钥=SECRET_ACCESS_KEY;私密访问密钥=SECRET_ACCESS_KEY;机密访问密钥=SECRET_ACCESS_KEY;访问密钥=ACCESS_KEY;访问令牌=ACCESS_TOKEN;刷新令牌=REFRESH_TOKEN;令牌=TOKEN;" +
            "密钥对=KEYPAIR;私钥=PRIVATE_KEY;公钥=PUBLIC_KEY;密钥=SECRET;秘钥=SECRET;密码=PASSWORD;口令=PASSWORD;暗号=PASSWORD;秘密=SECRET;机密=SECRET;私密=SECRET;" +
            "凭证=CREDENTIAL;凭据=CREDENTIAL;票据=TICKET;会话=SESSION;授权=AUTH;认证=AUTH;登录名=USERNAME;登录=LOGIN;签名=SIGN;验证码=CODE;盐=SALT;种子=SEED;哈希=HASH;" +
            "用户名=USERNAME;用户=USER;管理员=ADMIN;后台=ADMIN;账号=ACCOUNT;帐号=ACCOUNT;账户=ACCOUNT;帐户=ACCOUNT;商户号=MERCHANT_ID;商户=MERCHANT;" +
            "邮箱=EMAIL;电子邮件=EMAIL;邮件=MAIL;手机号=PHONE;手机=PHONE;电话=PHONE;" +
            "服务器=SERVER;主机=HOST;域名=DOMAIN;网址=URL;链接=URL;地址=URL;端口=PORT;端点=ENDPOINT;接口=API;网关=GATEWAY;代理=PROXY;隧道=TUNNEL;" +
            "区域=REGION;地域=REGION;可用区=ZONE;存储桶=BUCKET;桶=BUCKET;对象存储=OSS;存储=STORAGE;镜像=IMAGE;仓库=REPO;分支=BRANCH;项目=PROJECT;组织=ORG;" +
            "命名空间=NAMESPACE;空间=SPACE;实例=INSTANCE;集群=CLUSTER;节点=NODE;容器=CONTAINER;" +
            "回调=CALLBACK;通知=NOTIFY;推送=PUSH;消息=MESSAGE;短信=SMS;模板=TEMPLATE;证书=CERT;" +
            "小程序=MINIPROGRAM;公众号=MP;企业微信=WECOM;微信支付=WECHAT_PAY;微信=WECHAT;支付宝=ALIPAY;钉钉=DINGTALK;飞书=FEISHU;" +
            "阿里云=ALIYUN;腾讯云=TENCENTCLOUD;华为云=HUAWEICLOUD;百度=BAIDU;七牛=QINIU;又拍云=UPYUN;抖音=DOUYIN;字节=BYTEDANCE;淘宝=TAOBAO;京东=JD;美团=MEITUAN;高德=AMAP;腾讯=TENCENT;网易=NETEASE;" +
            "机器人=BOT;群=GROUP;频道=CHANNEL;应用=APP;开放平台=OPEN;平台=PLATFORM;控制台=CONSOLE;面板=PANEL;支付=PAY;服务=SERVICE;配置=CONFIG;" +
            "生产=PROD;正式=PROD;测试=TEST;开发=DEV;预发=STAGING;环境=ENV;备用=BACKUP;备份=BACKUP;主=PRIMARY;新=NEW;旧=OLD;临时=TEMP;过期=EXPIRES;时间=TIME;版本=VERSION;根=ROOT;" +
            "编号=ID;标识=ID;名称=NAME;名=NAME;号=ID;数据=DATA;" +
            "的=;和=;与=;是=;为=;我的=;第=;个=;信息=;资料=;内容=;如下=;相关=";

        static readonly KeyValuePair<string, string>[] Terms = LoadTerms();

        // English words in labels that carry no meaning for a variable name.
        static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "of", "for", "your", "my", "is", "are", "and", "to", "in", "on", "info", "information", "value", "here" };

        static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "passwd", "PASSWORD" }, { "pwd", "PASSWORD" }, { "pass", "PASSWORD" }, { "usr", "USER" }, { "acct", "ACCOUNT" }, { "addr", "URL" },
            { "tok", "TOKEN" }, { "sec", "SECRET" }, { "cfg", "CONFIG" }, { "db", "DATABASE" }, { "e-mail", "EMAIL" }, { "mail", "EMAIL" },
            // Brand names written in camel case stay one word.
            { "github", "GITHUB" }, { "gitlab", "GITLAB" }, { "bitbucket", "BITBUCKET" }, { "openai", "OPENAI" }, { "deepseek", "DEEPSEEK" },
            { "paypal", "PAYPAL" }, { "wechat", "WECHAT" }, { "alipay", "ALIPAY" }, { "cloudflare", "CLOUDFLARE" }, { "linkedin", "LINKEDIN" },
            { "youtube", "YOUTUBE" }, { "mongodb", "MONGODB" }, { "postgresql", "POSTGRESQL" }, { "sendgrid", "SENDGRID" }, { "mailgun", "MAILGUN" },
            { "digitalocean", "DIGITALOCEAN" }, { "dingtalk", "DINGTALK" }, { "tencentcloud", "TENCENTCLOUD" }, { "huaweicloud", "HUAWEICLOUD" },
        };

        static KeyValuePair<string, string>[] LoadTerms()
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (string pair in TermTable.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) list.Add(new KeyValuePair<string, string>(pair.Substring(0, eq), pair.Substring(eq + 1)));
            }
            list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            return list.ToArray();
        }

        // SuggestName builds a variable name from a label: Chinese words through
        // the vocabulary, English words split on case and separators, then a few
        // conventional spellings (AWS_ACCESS_KEY_ID, CLOUDFLARE_API_TOKEN, …).
        public static string SuggestName(string label, bool cloudflare)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < label.Length)
            {
                char c = label[i];
                if (IsAsciiWord(c))
                {
                    int j = i;
                    while (j < label.Length && IsAsciiWord(label[j])) j++;
                    parts.AddRange(SplitAscii(label.Substring(i, j - i)));
                    i = j;
                    continue;
                }
                bool matched = false;
                foreach (KeyValuePair<string, string> term in Terms)
                {
                    if (string.CompareOrdinal(label, i, term.Key, 0, term.Key.Length) == 0)
                    {
                        if (term.Value.Length > 0) parts.Add(term.Value);
                        i += term.Key.Length;
                        matched = true;
                        break;
                    }
                }
                if (!matched) i++; // punctuation or a word outside the vocabulary
            }
            var merged = new List<string>();
            foreach (string part in parts)
            {
                foreach (string piece in part.Split('_'))
                {
                    if (piece.Length > 0 && (merged.Count == 0 || merged[merged.Count - 1] != piece)) merged.Add(piece);
                }
            }
            string name = string.Join("_", merged);
            return name.Length == 0 ? "FIELD" : Canonical(name, cloudflare);
        }

        static bool IsAsciiWord(char c)
        {
            return c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }

        // SplitAscii turns "AccessKeyId", "api_token" or "e-mail" into upper fragments.
        static IEnumerable<string> SplitAscii(string run)
        {
            string synonym;
            if (Synonyms.TryGetValue(run, out synonym)) return new[] { synonym };
            string spaced = Regex.Replace(run, @"([A-Za-z])([vV]\d)", "$1_$2"); // APIv3 -> API_v3
            spaced = Regex.Replace(spaced, "([a-z0-9])([A-Z])", "$1_$2");
            spaced = Regex.Replace(spaced, "([A-Z]+)([A-Z][a-z])", "$1_$2");
            var pieces = new List<string>();
            foreach (string piece in spaced.Split('_', '-'))
            {
                if (piece.Length == 0 || StopWords.Contains(piece)) continue;
                pieces.Add(Synonyms.TryGetValue(piece, out synonym) ? synonym : piece.ToUpperInvariant());
            }
            return pieces;
        }

        // Canonical applies the spellings tools actually read.
        static string Canonical(string name, bool cloudflare)
        {
            switch (name)
            {
                case "USER":
                case "USER_NAME":
                case "LOGIN_NAME":
                case "LOGIN":
                    return "USERNAME";
                case "ACCESS_KEY":
                case "ACCESS_KEY_ID":
                    return "AWS_ACCESS_KEY_ID";
                case "ACCESS_KEY_SECRET":
                case "ACCESS_SECRET":
                case "SECRET_ACCESS_KEY":
                    return "AWS_SECRET_ACCESS_KEY";
                case "TOKEN":
                    return cloudflare ? "CLOUDFLARE_API_TOKEN" : "TOKEN";
            }
            // Vendor-specific conventions for S3-style key pairs.
            name = Regex.Replace(name, "^ALIYUN_(ACCESS_KEY_ID|ACCESS_KEY_SECRET|SECRET_ACCESS_KEY|ACCESS_KEY)$", m => "ALIBABA_CLOUD_" + (m.Groups[1].Value.Contains("SECRET") ? "ACCESS_KEY_SECRET" : "ACCESS_KEY_ID"));
            name = Regex.Replace(name, "^TENCENTCLOUD_(ACCESS_KEY_ID|SECRET_ID|ACCESS_KEY)$", "TENCENTCLOUD_SECRET_ID");
            name = Regex.Replace(name, "^TENCENTCLOUD_(SECRET_ACCESS_KEY|ACCESS_KEY_SECRET|SECRET_KEY)$", "TENCENTCLOUD_SECRET_KEY");
            if (cloudflare)
            {
                switch (name)
                {
                    case "ACCOUNT_ID":
                    case "API_TOKEN":
                    case "API_KEY":
                    case "ZONE_ID":
                    case "EMAIL":
                        return "CLOUDFLARE_" + name;
                }
            }
            return name;
        }
    }
}
