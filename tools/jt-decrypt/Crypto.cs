// Crypto.cs: the jt data formats, mirrored from cmd/jt (main.go seal/open, key*.go).
//   ciphertext = base64( nonce[12] || AES-256-GCM(plaintext) || tag[16] ), no associated data
//   master key = 32 bytes; stored raw (Linux/WSL), as "JTDPAPI1"+DPAPI blob (Windows), or
//                exported as base64 by `jt key export`
// Everything here is pure computation; nothing touches the network or the jt vault.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JtDecrypt;

/// <summary>One ciphertext found in the input and the result of opening it.</summary>
public sealed record Entry(string Name, string Value, string Source, string Ciphertext, bool Ok);

public static class MasterKey
{
    const string Magic = "JTDPAPI1";
    static readonly byte[] Entropy = Encoding.ASCII.GetBytes("jt master key");

    /// <summary>Default location of the Windows key file written by jt init.</summary>
    public static string LocalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jt", "key");

    /// <summary>Accepts a raw 32-byte key file, the Windows DPAPI key file (same user, same machine only),
    /// or the base64 text produced by jt key export.</summary>
    public static byte[] Load(byte[] data)
    {
        if (data.Length == 32) return (byte[])data.Clone();
        if (data.Length > Magic.Length && Encoding.ASCII.GetString(data, 0, Magic.Length) == Magic)
        {
            byte[] raw;
            try
            {
                raw = ProtectedData.Unprotect(data[Magic.Length..], Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException e)
            {
                throw new InvalidDataException("这是 Windows DPAPI 保护的密钥文件，只有创建它的那个 Windows 用户在那台电脑上能读。请改用 jt key export 导出的 base64 密钥。", e);
            }
            if (raw.Length != 32) throw new InvalidDataException("DPAPI 密钥文件解出来不是 32 字节");
            return raw;
        }
        return Parse(Encoding.UTF8.GetString(data));
    }

    /// <summary>Parses the base64 form (whitespace and BOM tolerated).</summary>
    public static byte[] Parse(string text)
    {
        string s = Regex.Replace(text.Trim('\uFEFF'), @"\s+", "");
        try
        {
            byte[] raw = Convert.FromBase64String(s);
            if (raw.Length == 32) return raw;
        }
        catch (FormatException) { }
        throw new InvalidDataException("密钥应为 jt key export 导出的 44 个字符的 base64（解码后 32 字节）");
    }

    /// <summary>Short stable identifier so two copies of a key can be compared without showing it.</summary>
    public static string Fingerprint(byte[] key) => Convert.ToHexString(SHA256.HashData(key))[..8].ToLowerInvariant();
}

public static class Cipher
{
    const int NonceSize = 12, TagSize = 16;

    public static bool TryOpen(byte[] key, string encoded, out string value)
    {
        value = "";
        byte[] combined;
        try { combined = Convert.FromBase64String(encoded); }
        catch (FormatException) { return false; }
        if (combined.Length < NonceSize + TagSize) return false;
        ReadOnlySpan<byte> nonce = combined.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> body = combined.AsSpan(NonceSize);
        ReadOnlySpan<byte> ciphertext = body[..^TagSize];
        ReadOnlySpan<byte> tag = body[^TagSize..];
        byte[] plain = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plain);
        }
        catch (CryptographicException) { return false; }
        value = Encoding.UTF8.GetString(plain);
        return true;
    }
}

/// <summary>Finds everything decryptable in arbitrary text: a vault.json (alone, inside a Markdown code
/// fence, or embedded in a page), a Notion table copied as text or exported as CSV, or a bare ciphertext.
/// GCM authentication makes "try every base64-looking token" safe: a wrong key or random base64 fails.</summary>
public static class Scanner
{
    // 29 bytes (nonce + tag + 1 byte of plaintext) is 40 base64 characters; jt never stores empty values.
    static readonly Regex Token = new(@"[A-Za-z0-9+/]{38,}={0,2}", RegexOptions.Compiled);
    static readonly Regex Fence = new(@"```[a-zA-Z0-9]*[ \t]*\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JtName = new(@"(?<![A-Za-z0-9_./-])[A-Za-z0-9_.-]+/[A-Za-z_][A-Za-z0-9_]*(?![A-Za-z0-9_/])", RegexOptions.Compiled);
    static readonly Regex Ref = new(@"jt://secret/[0-9A-Za-z]{8}", RegexOptions.Compiled);

    public static List<Entry> Decrypt(byte[] key, string text)
    {
        var results = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string json in JsonCandidates(text))
            foreach (Entry e in FromVault(key, json))
                if (seen.Add(e.Ciphertext)) results.Add(e);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            foreach (Match m in Token.Matches(line))
            {
                string token = m.Value;
                if (seen.Contains(token) || !Cipher.TryOpen(key, token, out string value)) continue;
                seen.Add(token);
                results.Add(new Entry(LabelFor(line, token), value, "文本", token, true));
            }
        }
        return results;
    }

    static IEnumerable<string> JsonCandidates(string text)
    {
        yield return text;
        foreach (Match m in Fence.Matches(text)) yield return m.Groups[1].Value;
        int at = text.IndexOf("\"secrets\"", StringComparison.Ordinal);
        if (at < 0) yield break;
        int start = text.LastIndexOf('{', at);
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start) yield return text.Substring(start, end - start + 1);
    }

    static IEnumerable<Entry> FromVault(byte[] key, string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF')); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("secrets", out JsonElement secrets)
                || secrets.ValueKind != JsonValueKind.Array) yield break;
            foreach (JsonElement s in secrets.EnumerateArray())
            {
                string ct = Str(s, "ciphertext");
                if (ct.Length == 0) continue;
                string name = Str(s, "name");
                string id = Str(s, "id");
                string desc = Str(s, "description");
                string label = name.Length > 0 ? name : id;
                string source = "vault.json" + (desc.Length > 0 ? " · " + desc : "");
                if (Cipher.TryOpen(key, ct, out string value)) yield return new Entry(label, value, source, ct, true);
                else yield return new Entry(label, "", "无法解密：密钥不对或密文损坏", ct, false);
            }
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Best name for a ciphertext found in free text: a jt name (ns/VAR) or reference on the same
    /// line, else the line itself without the token.</summary>
    static string LabelFor(string line, string token)
    {
        string rest = line.Replace(token, " ");
        Match name = JtName.Match(rest);
        if (name.Success) return name.Value;
        Match r = Ref.Match(rest);
        if (r.Success) return r.Value;
        rest = Regex.Replace(rest, @"[\s|,\t\x22]+", " ").Trim(' ', '-', ':', '=');
        if (rest.Length == 0) return "（单独的密文）";
        return rest.Length > 60 ? rest[..60] + "…" : rest;
    }
}
