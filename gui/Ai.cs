// Ai.cs asks an OpenAI-compatible chat model (DeepSeek by default) to name the
// placeholders of a masked block. The model receives labels, placeholders and
// value shapes only, never a value. The API key is itself a jt secret and is
// fetched the same way the update dialog fetches values. C# 5 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace JtGui
{
    static class AiSettings
    {
        const string Key = @"Software\jt-gui";

        public static string BaseUrl
        {
            get { return Read("AiBaseUrl", "https://api.deepseek.com"); }
            set { Write("AiBaseUrl", value); }
        }

        public static string Model
        {
            get { return Read("AiModel", "deepseek-chat"); }
            set { Write("AiModel", value); }
        }

        // KeyName is the jt entry that holds the API key.
        public static string KeyName
        {
            get { return Read("AiKeyName", "ai/DEEPSEEK_API_KEY"); }
            set { Write("AiKeyName", value); }
        }

        public static bool Enabled
        {
            get { return Read("AiEnabled", "0") == "1"; }
            set { Write("AiEnabled", value ? "1" : "0"); }
        }

        static string Read(string name, string fallback)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object value = k == null ? null : k.GetValue(name);
                string text = value as string;
                return string.IsNullOrEmpty(text) ? fallback : text;
            }
        }

        static void Write(string name, string value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue(name, value ?? "", RegistryValueKind.String);
            }
        }
    }

    sealed class AiPlan
    {
        public string Namespace = "";
        public readonly List<SplitField> Fields = new List<SplitField>();
    }

    static class Ai
    {
        static readonly Regex EnvName = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");

        const string SystemPrompt =
            "你是密钥管理助手。用户粘贴了一段账号信息，其中所有疑似密钥、ID、邮箱的值都已被替换成占位符（<V1>、<V2>、<EMAIL_1>…），你看不到真值，也不要猜测真值。" +
            "请根据每行的标签和上下文，为每个占位符起一个环境变量风格的名称，并写一句简短的中文描述，再给整组起一个命名空间。规则：" +
            "1) 名称用大写蛇形，优先用常见工具实际读取的变量名，例如 CLOUDFLARE_API_TOKEN、CLOUDFLARE_ACCOUNT_ID、CLOUDFLARE_ZONE_ID、CLOUDFLARE_EMAIL、AWS_ACCESS_KEY_ID、AWS_SECRET_ACCESS_KEY、GITHUB_TOKEN、OPENAI_API_KEY、DATABASE_URL、SMTP_PASSWORD；" +
            "Cloudflare R2 的访问密钥用 AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY。2) 命名空间是小写短词，如 cf、github、aws、openai、db；一组信息只有一个命名空间。" +
            "3) 邮箱、用户名等非机密也要列出，secret 设为 false。4) 描述里不要出现任何值。5) 每个占位符恰好出现一次。" +
            "只输出 JSON，格式：{\"namespace\":\"cf\",\"entries\":[{\"placeholder\":\"V1\",\"name\":\"CLOUDFLARE_ACCOUNT_ID\",\"description\":\"Cloudflare 帐户 ID\",\"secret\":false}]}";

        // Organize returns the model's naming for every token, or null with error.
        public static AiPlan Organize(MaskResult masked, out string error)
        {
            string key = ApiKey(out error);
            if (key == null) return null;
            var shapes = new StringBuilder();
            foreach (MaskedToken t in masked.Tokens)
            {
                shapes.Append('<').Append(t.Placeholder).Append(">: ").Append(t.Shape).Append('\n');
            }
            string user = "文本（值已替换为占位符）：\n" + masked.Text + "\n\n占位符的形状：\n" + shapes;
            var request = new Dictionary<string, object>
            {
                { "model", AiSettings.Model },
                { "temperature", 0 },
                { "response_format", new Dictionary<string, object> { { "type", "json_object" } } },
                { "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", SystemPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", user } },
                    }
                },
            };
            string body = Http("POST", "/chat/completions", key, Serializer().Serialize(request), out error);
            if (body == null) return null;
            try
            {
                var response = Serializer().Deserialize<Dictionary<string, object>>(body);
                // JavaScriptSerializer yields ArrayList or object[] for arrays depending on nesting.
                var choices = response["choices"] as IList;
                var message = ((Dictionary<string, object>)choices[0])["message"] as Dictionary<string, object>;
                string content = (message["content"] ?? "").ToString().Trim();
                content = Regex.Replace(content, @"^```[a-zA-Z]*\s*|\s*```$", "");
                var plan = Serializer().Deserialize<Dictionary<string, object>>(content);
                return ToPlan(plan, masked);
            }
            catch (Exception e)
            {
                error = "无法解析模型的回复：" + e.Message;
                return null;
            }
        }

        static AiPlan ToPlan(Dictionary<string, object> plan, MaskResult masked)
        {
            var result = new AiPlan();
            object ns;
            if (plan.TryGetValue("namespace", out ns) && ns != null)
            {
                result.Namespace = Regex.Replace(ns.ToString().ToLowerInvariant(), "[^a-z0-9_-]", "");
            }
            var seen = new HashSet<string>();
            var named = new HashSet<string>();
            object entries;
            if (plan.TryGetValue("entries", out entries) && entries is IList)
            {
                foreach (object item in (IList)entries)
                {
                    var entry = item as Dictionary<string, object>;
                    if (entry == null) continue;
                    MaskedToken token = masked.Find(Text(entry, "placeholder").Trim('<', '>', ' '));
                    if (token == null || !seen.Add(token.Placeholder)) continue;
                    string name = Text(entry, "name").Trim().ToUpperInvariant();
                    if (!EnvName.IsMatch(name)) name = Split.SuggestName(token.Label, masked.Cloudflare);
                    for (int n = 2; !named.Add(name); n++) name = name + "_" + n;
                    string description = Text(entry, "description");
                    result.Fields.Add(new SplitField { Name = name, Label = token.Label, Value = token.Value, Description = description.Length > 0 ? description : token.Label });
                }
            }
            // Tokens the model skipped still get a local name so nothing is silently dropped.
            foreach (SplitField local in Split.LocalFields(masked))
            {
                MaskedToken token = null;
                foreach (MaskedToken t in masked.Tokens)
                {
                    if (t.Value == local.Value && t.Label == local.Label) token = t;
                }
                if (token == null || seen.Contains(token.Placeholder)) continue;
                string name = local.Name;
                for (int n = 2; !named.Add(name); n++) name = local.Name + "_" + n;
                local.Name = name;
                result.Fields.Add(local);
            }
            return result;
        }

        // Test checks the endpoint and key with the cheap model list call.
        public static bool Test(out string error)
        {
            string key = ApiKey(out error);
            if (key == null) return false;
            return Http("GET", "/models", key, null, out error) != null;
        }

        // ApiKey fetches the key from jt through a child process, like the update dialog.
        static string ApiKey(out string error)
        {
            error = null;
            JtResult r = Jt.Run(new[] { "resolve", AiSettings.KeyName, "--exec", Program.ExePath, Program.PrintEnvFlag, "JT_SECRET" }, null);
            if (!r.Ok || r.Stdout.Trim().Length == 0)
            {
                error = "读取 AI 密钥 " + AiSettings.KeyName + " 失败：" + r.Error;
                return null;
            }
            return r.Stdout.Trim();
        }

        static string Http(string method, string path, string key, string json, out string error)
        {
            error = null;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string url = AiSettings.BaseUrl.TrimEnd('/') + path;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = 90000;
                request.Headers["Authorization"] = "Bearer " + key;
                request.UserAgent = "jt-gui";
                if (json != null)
                {
                    request.ContentType = "application/json";
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    using (Stream s = request.GetRequestStream())
                    {
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException e)
            {
                string detail = e.Message;
                var response = e.Response as HttpWebResponse;
                if (response != null)
                {
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        detail = "HTTP " + (int)response.StatusCode + " " + ErrorMessage(text);
                    }
                }
                error = method + " " + url + " 失败：" + detail;
                return null;
            }
            catch (Exception e)
            {
                error = method + " " + url + " 失败：" + e.Message;
                return null;
            }
        }

        static string ErrorMessage(string body)
        {
            try
            {
                var parsed = Serializer().Deserialize<Dictionary<string, object>>(body);
                var err = parsed["error"] as Dictionary<string, object>;
                if (err != null) return Text(err, "message");
            }
            catch (Exception) { }
            return body.Length > 300 ? body.Substring(0, 300) : body;
        }

        static string Text(Dictionary<string, object> d, string key)
        {
            object value;
            return d.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }
    }
}
