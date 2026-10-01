// Jt.cs drives the jt command line. The GUI never reads the key or the vault
// itself: jt stays the only program that decrypts, exactly like jiantieban on
// macOS. Everything here must compile with the C# 5 compiler that ships with
// Windows (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace JtGui
{
    sealed class Secret
    {
        public string Id = "", Ref = "", Name = "", Description = "", Preview = "", UpdatedAt = "";

        public string Namespace
        {
            get
            {
                int slash = Name.IndexOf('/');
                return slash < 0 ? "" : Name.Substring(0, slash);
            }
        }
    }

    sealed class VaultStatus
    {
        public string Vault = "", Key = "";
        public bool Git, Dirty;
        public int? Ahead;
    }

    sealed class JtResult
    {
        public int ExitCode;
        public string Stdout = "", Stderr = "";

        public bool Ok { get { return ExitCode == 0; } }

        // Error is one line for the status bar: git's "fatal:"/"error:" line when
        // there is one, else the first line that is not a warning, without "jt: ".
        public string Error
        {
            get
            {
                string first = null;
                foreach (string raw in (Stderr + "\n" + Stdout).Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("warning:")) continue;
                    if (line.StartsWith("fatal:") || line.StartsWith("error:")) return line;
                    if (first == null) first = line.StartsWith("jt: ") ? line.Substring(4) : line;
                }
                return first ?? "exit status " + ExitCode;
            }
        }

        // Reference finds the jt://secret/<id> that add, grab and friends print.
        public string Reference
        {
            get
            {
                Match m = Regex.Match(Stdout, @"jt://secret/[0-9A-Za-z]{8}");
                return m.Success ? m.Value : "";
            }
        }
    }

    static class Jt
    {
        public static readonly string Bin = FindBin();

        // FindBin honours JT_BIN, then jt.exe beside this program, then the
        // installer's location, and finally whatever PATH resolves.
        static string FindBin()
        {
            string env = Environment.GetEnvironmentVariable("JT_BIN");
            if (!string.IsNullOrEmpty(env)) return env;
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jt.exe");
            if (File.Exists(beside)) return beside;
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
            {
                string installed = Path.Combine(local, @"Programs\jt\jt.exe");
                if (File.Exists(installed)) return installed;
            }
            return "jt";
        }

        // Run executes jt with args; stdin, when not null, is written to the
        // process as UTF-8 and closed (add/set read the value from it).
        public static JtResult Run(string[] args, string stdin)
        {
            var info = new ProcessStartInfo(Bin, Quote(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var result = new JtResult();
            try
            {
                using (Process p = Process.Start(info))
                {
                    if (stdin != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(stdin);
                        p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        p.StandardInput.Close();
                    }
                    // Drain both pipes at once so a chatty child cannot deadlock on a full buffer.
                    Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        result.ExitCode = -1;
                        result.Stderr = "jt did not finish within 120 seconds";
                        return result;
                    }
                    result.ExitCode = p.ExitCode;
                    result.Stdout = stdout.Result;
                    result.Stderr = stderr;
                }
            }
            catch (Exception e)
            {
                result.ExitCode = -1;
                result.Stderr = "cannot run " + Bin + ": " + e.Message;
            }
            return result;
        }

        public static List<Secret> List(out string error)
        {
            error = null;
            var secrets = new List<Secret>();
            JtResult r = Run(new[] { "ls", "--json" }, null);
            if (!r.Ok)
            {
                error = r.Error;
                return secrets;
            }
            object[] rows;
            try
            {
                rows = Serializer().Deserialize<object[]>(r.Stdout);
            }
            catch (Exception e)
            {
                error = "cannot parse jt ls --json: " + e.Message;
                return secrets;
            }
            foreach (object row in rows ?? new object[0])
            {
                var fields = row as Dictionary<string, object>;
                if (fields == null) continue;
                secrets.Add(new Secret
                {
                    Id = Text(fields, "id"),
                    Ref = Text(fields, "ref"),
                    Name = Text(fields, "name"),
                    Description = Text(fields, "description"),
                    Preview = Text(fields, "preview"),
                    UpdatedAt = Text(fields, "updated_at"),
                });
            }
            return secrets;
        }

        public static VaultStatus Status(out string error)
        {
            error = null;
            JtResult r = Run(new[] { "status", "--json" }, null);
            if (!r.Ok)
            {
                error = r.Error;
                return null;
            }
            try
            {
                var fields = Serializer().Deserialize<Dictionary<string, object>>(r.Stdout);
                var status = new VaultStatus { Vault = Text(fields, "vault"), Key = Text(fields, "key") };
                object value;
                status.Git = fields.TryGetValue("git", out value) && value is bool && (bool)value;
                status.Dirty = fields.TryGetValue("dirty", out value) && value is bool && (bool)value;
                if (fields.TryGetValue("ahead", out value) && value != null) status.Ahead = Convert.ToInt32(value);
                return status;
            }
            catch (Exception e)
            {
                error = "cannot parse jt status --json: " + e.Message;
                return null;
            }
        }

        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        static string Text(Dictionary<string, object> fields, string key)
        {
            object value;
            return fields.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        // Quote joins args into a command line that CommandLineToArgvW, and so
        // Go's os.Args, splits back into exactly these strings. Descriptions may
        // contain spaces, quotes and backslashes; an empty argument becomes "".
        static string Quote(string[] args)
        {
            var sb = new StringBuilder();
            foreach (string arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                {
                    sb.Append(arg);
                    continue;
                }
                sb.Append('"');
                int backslashes = 0;
                foreach (char c in arg)
                {
                    if (c == '\\')
                    {
                        backslashes++;
                        continue;
                    }
                    if (c == '"')
                    {
                        sb.Append('\\', backslashes * 2 + 1);
                        sb.Append('"');
                        backslashes = 0;
                        continue;
                    }
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                    sb.Append(c);
                }
                sb.Append('\\', backslashes * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }
}
