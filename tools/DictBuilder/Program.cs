// Сборщик языковой модели Type9 (.t9lm).
//
//   dotnet run -c Release --project tools/DictBuilder -- --lang ru --freq ru_full.txt --sentences rus_sentences.tsv
//       --vocab 150000 --min2 3 --min3 3 --out src/Type9/Resources/Dict/ru.t9lm
//
// Источники: частотные списки hermitdave/FrequencyWords (OpenSubtitles 2018, CC-BY-SA 4.0)
// и предложения Tatoeba (CC BY 2.0 FR). Модель: словарь с частотами, биграммы, триграммы и
// статистика переходов окончаний (для согласования падежей: «в этом мес…» → «месте»).
using System.Globalization;
using System.IO.Compression;
using System.Text;

var a = ParseArgs(args);
string lang = a["lang"];
int vocabSize = int.Parse(a.GetValueOrDefault("vocab", "150000"), CultureInfo.InvariantCulture);
int min2 = int.Parse(a.GetValueOrDefault("min2", "3"), CultureInfo.InvariantCulture);
int min3 = int.Parse(a.GetValueOrDefault("min3", "3"), CultureInfo.InvariantCulture);
int minSuf = int.Parse(a.GetValueOrDefault("minsuf", "4"), CultureInfo.InvariantCulture);

bool IsLetter(char c) => lang == "ru" ? c is >= 'а' and <= 'я' or 'ё' : c is >= 'a' and <= 'z';
string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е');

// ---------------------------------------------------------------- словарь
Console.WriteLine("Частотный список…");
var counts = new Dictionary<string, long>(StringComparer.Ordinal);
var formCounts = new Dictionary<string, (string Form, long Count)>(StringComparer.Ordinal);
foreach (var line in File.ReadLines(a["freq"], Encoding.UTF8))
{
    int sp = line.LastIndexOf(' ');
    if (sp <= 0 || !long.TryParse(line.AsSpan(sp + 1), out var cnt)) continue;
    var w = line[..sp].Trim().ToLowerInvariant();
    if (!ValidWord(w)) continue;
    var key = Norm(w);
    counts[key] = counts.GetValueOrDefault(key) + cnt;
    if (!formCounts.TryGetValue(key, out var f) || f.Count < cnt) formCounts[key] = (w, cnt);
}

bool ValidWord(string w)
{
    if (w.Length is 0 or > 32) return false;
    if (!IsLetter(w[0]) || !IsLetter(w[^1])) return false;
    foreach (var c in w)
        if (!IsLetter(c) && c is not '-' && !(lang == "en" && c == '\'')) return false;
    return true;
}

var vocab = counts.OrderByDescending(kv => kv.Value).Take(vocabSize).Select(kv => kv.Key).ToArray();
Array.Sort(vocab, StringComparer.Ordinal);
var id = new Dictionary<string, int>(vocab.Length, StringComparer.Ordinal);
for (int i = 0; i < vocab.Length; i++) id[vocab[i]] = i;
Console.WriteLine($"  слов: {vocab.Length:N0}");

// ---------------------------------------------------------------- корпус
// Имена, которыми переполнен Tatoeba, — не учитываем (заглавная форма = имя; «о том» остаётся)
var names = lang == "ru"
    ? new HashSet<string> { "Том", "Тома", "Тому", "Томом", "Томе", "Мэри", "Джон", "Джона", "Джону", "Джоном", "Джоне" }
    : new HashSet<string> { "Tom", "Mary", "John", "Tom's", "Mary's", "John's" };

var bi = new Dictionary<long, int>();
var tri = new Dictionary<long, int>();
var ctxTotal = new long[vocab.Length];
var sufPairs = new Dictionary<(string, string), int>();
var sufMarg = new Dictionary<string, long>(StringComparer.Ordinal);
long sentences = 0, tokens = 0;

Console.WriteLine("Корпус…");
foreach (var line in File.ReadLines(a["sentences"], Encoding.UTF8))
{
    var parts = line.Split('\t');
    if (parts.Length < 3) continue;
    AddSentence(parts[2], 1);
}
// Собственный корпус типовой деловой переписки — с повышенным весом
if (a.TryGetValue("extra", out var extraPath))
{
    int w = int.Parse(a.GetValueOrDefault("extraweight", "25"), CultureInfo.InvariantCulture);
    foreach (var line in File.ReadLines(extraPath, Encoding.UTF8))
        if (line.Trim().Length > 0) AddSentence(line, w);
}

void AddSentence(string text, int weight)
{
    sentences++;
    int p2 = -1, p1 = -1;
    string? prevWord = null;
    foreach (var (tok, boundary) in Tokenize(text))
    {
        if (boundary) { p2 = p1 = -1; prevWord = null; continue; }
        tokens++;
        if (names.Contains(tok)) { p2 = p1 = -1; prevWord = null; continue; }
        var key = Norm(tok);
        bool valid = ValidWord(key);

        if (prevWord is not null && valid)
        {
            var cls = PrevClass(prevWord);
            var suf = NextSuffix(key);
            sufPairs[(cls, suf)] = sufPairs.GetValueOrDefault((cls, suf)) + weight;
            sufMarg[suf] = sufMarg.GetValueOrDefault(suf) + weight;
        }
        prevWord = valid ? key : null;

        if (!id.TryGetValue(key, out var wid)) { p2 = p1 = -1; continue; }
        if (p1 >= 0)
        {
            long k2 = ((long)p1 << 20) | (uint)wid;
            bi[k2] = bi.GetValueOrDefault(k2) + weight;
            ctxTotal[p1] += weight;
            if (p2 >= 0)
            {
                long k3 = ((long)p2 << 40) | ((long)p1 << 20) | (uint)wid;
                tri[k3] = tri.GetValueOrDefault(k3) + weight;
            }
        }
        p2 = p1;
        p1 = wid;
    }
}
Console.WriteLine($"  предложений: {sentences:N0}, слов: {tokens:N0}, биграмм: {bi.Count:N0}, триграмм: {tri.Count:N0}");

static string PrevClass(string w) => w.Length <= 3 ? w : w[^3..];
static string NextSuffix(string w) => w.Length <= 2 ? w : w[^2..];

IEnumerable<(string Token, bool Boundary)> Tokenize(string s)
{
    var sb = new StringBuilder();
    for (int i = 0; i <= s.Length; i++)
    {
        char c = i < s.Length ? s[i] : '.';
        bool letter = char.IsLetter(c);
        bool joiner = (c is '-' or '\'' or '’') && sb.Length > 0 && i + 1 < s.Length && char.IsLetter(s[i + 1]);
        if (letter || joiner)
        {
            sb.Append(c == '’' ? '\'' : c);
            continue;
        }
        if (sb.Length > 0)
        {
            yield return (sb.ToString(), false);
            sb.Clear();
        }
        if (c is '.' or '!' or '?' or '…' or ';' || char.IsDigit(c)) yield return ("", true);
    }
}

// ---------------------------------------------------------------- запись
var biKeys = bi.Where(kv => kv.Value >= min2).Select(kv => kv.Key).ToArray();
Array.Sort(biKeys);
var triKeys = tri.Where(kv => kv.Value >= min3).Select(kv => kv.Key).ToArray();
Array.Sort(triKeys);
var sufList = sufPairs.Where(kv => kv.Value >= minSuf).ToList();
Console.WriteLine($"После отсечения: биграмм {biKeys.Length:N0}, триграмм {triKeys.Length:N0}, пар окончаний {sufList.Count:N0}");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(a["out"]))!);
using (var fs = File.Create(a["out"]))
using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
using (var bw = new BinaryWriter(gz, Encoding.UTF8))
{
    bw.Write("T9LM"u8.ToArray());
    bw.Write(1); // версия формата
    bw.Write(lang);
    bw.Write(vocab.Length);
    long total = vocab.Sum(v => counts[v]);
    bw.Write(total);
    foreach (var v in vocab) bw.Write(v);
    foreach (var v in vocab) bw.Write(counts[v]);

    // Формы для показа, отличающиеся от ключа (ё, I/I'm)
    var forms = new List<(int, string)>();
    for (int i = 0; i < vocab.Length; i++)
    {
        var f = formCounts[vocab[i]].Form;
        if (lang == "en" && (f == "i" || f.StartsWith("i'", StringComparison.Ordinal))) f = "I" + f[1..];
        if (f != vocab[i]) forms.Add((i, f));
    }
    bw.Write(forms.Count);
    foreach (var (i, f) in forms) { bw.Write(i); bw.Write(f); }

    foreach (var t in ctxTotal) bw.Write((int)Math.Min(t, int.MaxValue));

    bw.Write(biKeys.Length);
    foreach (var k in biKeys) bw.Write(k);
    foreach (var k in biKeys) bw.Write((ushort)Math.Min(bi[k], ushort.MaxValue));

    bw.Write(triKeys.Length);
    foreach (var k in triKeys) bw.Write(k);
    foreach (var k in triKeys) bw.Write((ushort)Math.Min(tri[k], ushort.MaxValue));

    // Модель окончаний
    var classes = sufList.Select(kv => kv.Key.Item1).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    var sufs = sufMarg.Where(kv => kv.Value >= minSuf).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
    var sufId = sufs.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);
    var clsId = classes.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);
    bw.Write(classes.Count);
    foreach (var c in classes) bw.Write(c);
    bw.Write(sufs.Count);
    foreach (var s in sufs) { bw.Write(s); bw.Write(sufMarg[s]); }
    var entries = sufList.Where(kv => sufId.ContainsKey(kv.Key.Item2))
        .Select(kv => (Key: ((long)clsId[kv.Key.Item1] << 20) | (uint)sufId[kv.Key.Item2], kv.Value))
        .OrderBy(e => e.Key).ToList();
    bw.Write(entries.Count);
    foreach (var e in entries) { bw.Write(e.Key); bw.Write(e.Value); }
}
Console.WriteLine($"Готово: {a["out"]} ({new FileInfo(a["out"]).Length / 1024.0 / 1024:N1} МБ)");

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i + 1 < args.Length; i += 2) d[args[i].TrimStart('-')] = args[i + 1];
    return d;
}
