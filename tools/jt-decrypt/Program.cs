using System.Security.Cryptography;

namespace JtDecrypt;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --check KEYFILE INPUT OUTFILE: decrypt INPUT and write one line per entry to OUTFILE:
        // ok|fail <tab> name <tab> sha256(value)[:16] <tab> length. Never the value itself, so a
        // build can be verified against `jt resolve` output hashes without exposing anything.
        if (args.Length == 4 && args[0] == "--check")
        {
            byte[] key = MasterKey.Load(File.ReadAllBytes(args[1]));
            List<Entry> entries = Scanner.Decrypt(key, File.ReadAllText(args[2]));
            File.WriteAllLines(args[3], entries.Select(e =>
                $"{(e.Ok ? "ok" : "fail")}\t{e.Name}\t{(e.Ok ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(e.Value)))[..16].ToLowerInvariant() : "-")}\t{e.Value.Length}"));
            return entries.Count > 0 && entries.All(e => e.Ok) ? 0 : 1;
        }
        // Optional: --key FILE preloads the master key; a remaining argument is an input file (also what a
        // file dropped onto the exe produces).
        string? keyFile = null, inputFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--key" && i + 1 < args.Length) keyFile = args[++i];
            else if (File.Exists(args[i])) inputFile = args[i];
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(keyFile, inputFile));
        return 0;
    }
}
