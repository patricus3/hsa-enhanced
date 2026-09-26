using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// The former tools/hunks.py and tools/seeds.py in C#, so a build needs no Python
// (Windows). Output matches the Python scripts.
static class Hunks
{
    static readonly Regex Tok = new(@"""(?:[^""\\]|\\.)*""|[A-Za-z_]\w*");
    // "    public bool ShouldShowSetRotationIntro()" / "private IEnumerator Foo(int x) {"
    static readonly Regex Signature = new(@"^\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed|abstract|async|extern|unsafe|new)\s+)+[\w<>\[\],.? ]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\([^;]*\)\s*\{?\s*$");
    static readonly HashSet<string> KW = new(("if else for foreach while do return new var true false null this base void int bool string float long uint ulong short byte char double decimal object in out ref is as case break continue switch default try catch finally throw using get set value public private protected internal static readonly override virtual sealed abstract class struct enum interface namespace typeof sizeof await async yield lock goto operator implicit explicit params checked unchecked fixed unsafe delegate event extern const partial where select from let orderby group into join on equals by ascending descending nameof when").Split(' '));

    // Turns HSA's diff.patch into per-hunk token deltas for the IL matcher (hunks.py).
    public static void Extract(string diffPath, string outPath)
    {
        // Python reads text with universal newlines
        var txt = File.ReadAllText(diffPath, new UTF8Encoding(false, false)).Replace("\r\n", "\n").Replace('\r', '\n');
        var files = Regex.Split(txt, "^diff --git ", RegexOptions.Multiline).Skip(1);
        var hunks = new List<Dictionary<string, object>>();
        foreach (var f in files)
        {
            var head = f.Split('\n')[0].Split(' ')[0];
            if (!head.StartsWith("a/Assembly-CSharp/") || !head.EndsWith(".cs") || head.Contains("/Accessibility/")) continue;
            var cls = head["a/Assembly-CSharp/".Length..^3].Replace('/', '.');
            var parts = Regex.Split(f, "^@@", RegexOptions.Multiline);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var lines = parts[i + 1].Split('\n');
                var ch = lines.Skip(1).Where(l => l.Length > 0 && (l[0] == '+' || l[0] == '-')).ToList();
                if (ch.All(l => PyStrip(l[1..]).StartsWith("using ") || PyStrip(l[1..]).Length == 0)) continue;
                var r = Count(ch.Where(l => l[0] == '-'));
                var a = Count(ch.Where(l => l[0] == '+'));
                var gained = Minus(a, r); var lost = Minus(r, a);
                var text = string.Join("\n", ch);
                // the method the hunk sits in: the last signature line before its first change
                // (a change like "return false;" at the top of a method makes the compiler drop the
                // code after it, so HSA's build no longer has the names the diff shows)
                string? enclosing = null;
                foreach (var l in lines.Skip(1))
                {
                    if (l.Length > 0 && (l[0] == '+' || l[0] == '-')) break;
                    var sig = Signature.Match(l.Length > 0 ? l[1..] : l);
                    if (sig.Success && !KW.Contains(sig.Groups[1].Value)) enclosing = sig.Groups[1].Value;
                }
                if (enclosing == null)
                {
                    // or the function name git put in the hunk header
                    var sig = Signature.Match(lines[0].Contains("@@") ? lines[0][(lines[0].LastIndexOf("@@") + 2)..] : "");
                    if (sig.Success && !KW.Contains(sig.Groups[1].Value)) enclosing = sig.Groups[1].Value;
                }
                var hunk = new Dictionary<string, object>
                {
                    ["cls"] = cls, ["id"] = $"{cls}#{i}", ["header"] = Cut(lines[0], 120),
                    ["gained"] = gained, ["lost"] = lost, ["text"] = Cut(text, 1500),
                };
                if (enclosing != null) hunk["enclosing"] = enclosing;
                if (gained.Count == 0 && lost.Count == 0)
                {
                    // only operators changed (a?.b, ??, !, numbers): keep the names on the changed
                    // lines so the matcher can still find the method (tools/hunks.py dropped these)
                    var touched = r.Keys.Union(a.Keys).Where(t => !t.StartsWith("S:")).ToList();
                    string Bare(char side) => string.Join("\n", ch.Where(l => l[0] == side).Select(l => Regex.Replace(l[1..], @"\s+", "")).Where(l => l.Length > 0).OrderBy(l => l, StringComparer.Ordinal));
                    if (Bare('-') == Bare('+')) continue;                          // formatting only
                    if (touched.Count == 0 && enclosing == null) continue;
                    hunk["touched"] = touched;
                }
                hunks.Add(hunk);
            }
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(hunks));
        Console.WriteLine($"{hunks.Count} hunks with token deltas");
    }

    static string Cut(string s, int n) => s.Length <= n ? s : s[..n];
    static string PyStrip(string s) => s.Trim();

    static Dictionary<string, int> Count(IEnumerable<string> lines)
    {
        var c = new Dictionary<string, int>();
        foreach (var l in lines) foreach (var t in Toks(l)) c[t] = c.GetValueOrDefault(t) + 1;
        return c;
    }

    static Dictionary<string, int> Minus(Dictionary<string, int> x, Dictionary<string, int> y)
    {
        var d = new Dictionary<string, int>();
        foreach (var (k, v) in x) { var n = v - y.GetValueOrDefault(k); if (n > 0) d[k] = n; }
        return d;
    }

    static IEnumerable<string> Toks(string l)
    {
        var code = Regex.Replace(l[1..], "//.*$", "");
        code = Regex.Replace(code, @"/\*.*?\*/", "");
        foreach (System.Text.RegularExpressions.Match m in Tok.Matches(code))
        {
            var t = m.Value;
            if (t.StartsWith('"')) yield return "S:" + UnicodeEscape(Encoding.UTF8.GetBytes(t[1..^1]));
            else if (!KW.Contains(t)) yield return t;
        }
    }

    // Python's bytes.decode('unicode_escape', 'ignore'): bytes are Latin-1, escapes are decoded
    static string UnicodeEscape(byte[] b)
    {
        var sb = new StringBuilder();
        int i = 0;
        int Hex(int start, int len)
        {
            if (start + len > b.Length) return -1;
            int v = 0;
            for (int k = 0; k < len; k++) { int d = HexDigit(b[start + k]); if (d < 0) return -1; v = v * 16 + d; }
            return v;
        }
        while (i < b.Length)
        {
            if (b[i] != '\\') { sb.Append((char)b[i++]); continue; }
            if (i + 1 >= b.Length) break; // lone trailing backslash: ignored
            char c = (char)b[i + 1];
            i += 2;
            switch (c)
            {
                case '\n': break;
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'v': sb.Append('\v'); break;
                case >= '0' and <= '7':
                    {
                        int v = c - '0';
                        for (int k = 0; k < 2 && i < b.Length && b[i] >= '0' && b[i] <= '7'; k++) v = v * 8 + (b[i++] - '0');
                        sb.Append((char)v); break;
                    }
                case 'x': case 'u': case 'U':
                    {
                        int len = c == 'x' ? 2 : c == 'u' ? 4 : 8;
                        int v = Hex(i, len);
                        if (v < 0) { while (i < b.Length && HexDigit(b[i]) >= 0) i++; break; } // malformed: ignored
                        i += len;
                        if (v <= 0x10FFFF) sb.Append(char.ConvertFromUtf32(v >= 0xD800 && v <= 0xDFFF ? 0xFFFD : v));
                        break;
                    }
                case 'N':
                    while (i < b.Length && b[i] != '}') i++;
                    if (i < b.Length) i++;
                    break;
                default: sb.Append('\\').Append(c); break;
            }
        }
        return sb.ToString();
    }

    static int HexDigit(byte x) => x >= '0' && x <= '9' ? x - '0' : x >= 'a' && x <= 'f' ? x - 'a' + 10 : x >= 'A' && x <= 'F' ? x - 'A' + 10 : -1;

    // Filters hunk matches: drops hunks that only carry decompile/recompile fixes
    // (span/format helpers, separators, whitespace in literals), keeps HSA behaviour (seeds.py).
    static readonly HashSet<string> Noise = new() { "AsSpan", "ToString", "Substring", "Deconstruct", "Key", "Value", "IsInfinity", "IsFinite", "UnixEpoch", "Copy", "Index", "ID", "CurrencyId",
        "OriginalString", "StartIndex", "BufferSize", "GetTokenValueAsString", "Format", "Concat", "Join", "Mathf", "Math" };
    // reviewed by hand: recompile-only fixes
    // Box::OnSetRotationButtonPressed: HSA disables the set-rotation button (an early return); the
    // game's own handler stays, so the button on the box keeps working
    static readonly string[] Skip = { "Blizzard.T5.WebView.WebViewService::OnNetCacheFeaturesReady", "AssetLoader::SendMissingAssetTelemetry", "Box::OnSetRotationButtonPressed" };
    static bool IsNoise(string tok) => Noise.Contains(tok) || (tok.StartsWith("S:") && tok[2..].Trim().Length <= 1);

    public static void Seeds(string detailPath, string outPath)
    {
        var keep = new HashSet<string>(); var drop = new HashSet<string>();
        var rx = new Regex(@"gained=\[(.*?)\] lost=\[(.*)\]$");
        foreach (var line in File.ReadAllLines(detailPath))
        {
            var p = line.Split('\t');
            if (p.Length != 3) throw new Exception("bad line in " + detailPath + ": " + line);
            var (meth, rest) = (p[0], p[2]);
            if (Skip.Any(meth.Contains)) { drop.Add(meth); continue; }
            var g = rx.Match(rest);
            var toks = g.Groups[1].Value.Split(',').Concat(g.Groups[2].Value.Split(',')).Where(t => t.Length > 0).ToList();
            var strs = toks.Where(t => t.StartsWith("S:")).Select(t => Regex.Replace(t, @"\s+", " ")).ToList();
            if (toks.Count > 0 && toks.All(IsNoise)) { drop.Add(meth); continue; }
            if (toks.Count > 0 && toks.All(t => t.StartsWith("S:")) && strs.Distinct().Count() < strs.Count) { drop.Add(meth); continue; }
            keep.Add(meth);
        }
        File.WriteAllText(outPath, string.Join("\n", keep.OrderBy(x => x, StringComparer.Ordinal)) + "\n");
        Console.WriteLine($"keep {keep.Count} dropped as recompile noise {drop.Except(keep).Count()}");
    }
}
