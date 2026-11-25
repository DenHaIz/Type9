// Сборщик языковой модели Type9 (.t9lm).
//
//   dotnet run -c Release --project tools/DictBuilder -- --lang ru --freq ru_full.txt:0.35
//       --corpus rus_sentences.tsv:2:0.15 --corpus rus-ru_web-public_2019_1M-sentences.txt:1:0.3
//       --corpus rus_news_2023_1M-sentences.txt:0.5:0.2 --extra tools/DictBuilder/business_ru.txt
//       --vocab 200000 --maxbi 1600000 --maxtri 1600000 --holdout 400 --holdout-out holdout_ru.txt
//       --out src/Type9/Resources/Dict/ru.t9lm
//
// --freq  частотный список «слово число» : доля в частоте слов.
// --corpus файл предложений (берётся последний столбец через табуляцию) : вес в n-граммах : доля в частоте слов.
// --extra  свой деловой корпус: вес в n-граммах ×25 (--extraweight), его слова всегда попадают в словарь.
// --holdout N: каждое N-е предложение корпусов не учитывается и пишется в --holdout-out (для оценки качества).
//
// Источники: частотные списки hermitdave/FrequencyWords (OpenSubtitles 2018, CC BY-SA 4.0), предложения Tatoeba
// (CC BY 2.0 FR), корпуса Лейпцигского университета (wortschatz-leipzig.de, CC BY 4.0).
// Модель: словарь с частотами и формами для показа (ё, имена собственные, аббревиатуры), биграммы, триграммы
// и статистика переходов окончаний (для согласования падежей: «в этом мес…» → «месте»).
using System.Globalization;
using System.IO.Compression;
using System.Text;

var a = ParseArgs(args);
string lang = a["lang"][0];
int vocabSize = Int(a, "vocab", 200000);
int maxBi = Int(a, "maxbi", 1_600_000);
int maxTri = Int(a, "maxtri", 1_600_000);
int minSuf = Int(a, "minsuf", 16); // вес ×2: 16 = восемь употреблений
int holdout = Int(a, "holdout", 0);
int extraWeight = Int(a, "extraweight", 25);

bool IsLetter(char c) => lang == "ru" ? c is >= 'а' and <= 'я' or 'ё' : c is >= 'a' and <= 'z';
string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е');

bool ValidWord(string w)
{
    if (w.Length is 0 or > 32) return false;
    if (!IsLetter(w[0]) || !IsLetter(w[^1])) return false;
    foreach (var c in w)
        if (!IsLetter(c) && c is not '-' && !(lang == "en" && c == '\'')) return false;
    return true;
}

var corpora = a.GetValueOrDefault("corpus", []).Select(spec =>
{
    var p = spec.Split(':');
    // путь может содержать «C:» — числа берём с конца
    double share = double.Parse(p[^1], CultureInfo.InvariantCulture);
    int weight = (int)Math.Round(double.Parse(p[^2], CultureInfo.InvariantCulture) * 2); // вес ×2: допускаем 0,5
    return (Path: string.Join(':', p[..^2]), Weight: weight, Share: share);
}).ToList();
string? extraPath = a.TryGetValue("extra", out var ex) ? ex[0] : null;

// ---------------------------------------------------------------- частоты слов
var mixed = new Dictionary<string, double>(StringComparer.Ordinal);       // смешанная относительная частота
var surface = new Dictionary<string, (string Form, long Count)>(StringComparer.Ordinal); // самая частая строчная форма (с ё)
var caseStat = new Dictionary<string, (int Lower, int Cap, int Caps)>(StringComparer.Ordinal);

void NoteSurface(string key, string lower, long cnt)
{
    if (!surface.TryGetValue(key, out var f) || f.Count < cnt) surface[key] = (lower, cnt);
}

if (a.TryGetValue("freq", out var freqSpec))
{
    var p = freqSpec[0].Split(':');
    double share = p.Length > 1 && double.TryParse(p[^1], CultureInfo.InvariantCulture, out var sh) ? sh : 0.4;
    string path = p.Length > 1 && double.TryParse(p[^1], CultureInfo.InvariantCulture, out _) ? string.Join(':', p[..^1]) : freqSpec[0];
    Console.WriteLine($"Частотный список {Path.GetFileName(path)}…");
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    long total = 0;
    foreach (var line in File.ReadLines(path, Encoding.UTF8))
    {
        int sp = line.LastIndexOf(' ');
        if (sp <= 0 || !long.TryParse(line.AsSpan(sp + 1), out var cnt)) continue;
        var w = line[..sp].Trim().ToLowerInvariant();
        total += cnt;
        if (!ValidWord(w)) continue;
        var key = Norm(w);
        counts[key] = counts.GetValueOrDefault(key) + cnt;
        NoteSurface(key, w, cnt);
    }
    foreach (var (k, c) in counts) mixed[k] = mixed.GetValueOrDefault(k) + share * c / total;
}

long sentenceNo = 0;
bool writeHoldout = true; // отложенные предложения пишем только при первом проходе
var holdoutWriter = holdout > 0 && a.TryGetValue("holdout-out", out var ho) ? new StreamWriter(ho[0], false, new UTF8Encoding(false)) : null;

IEnumerable<string> Sentences(string path, bool training)
{
    foreach (var line in File.ReadLines(path, Encoding.UTF8))
    {
        int tab = line.LastIndexOf('\t');
        var text = tab >= 0 ? line[(tab + 1)..] : line;
        if (text.Length == 0) continue;
        if (!training) { yield return text; continue; }
        sentenceNo++;
        if (holdout > 0 && sentenceNo % holdout == 0)
        {
            if (writeHoldout) holdoutWriter?.WriteLine(text);
            continue;
        }
        yield return text;
    }
}

// Имена, которыми переполнен Tatoeba, — не учитываем (заглавная форма = имя; «о том» остаётся)
var names = lang == "ru"
    ? new HashSet<string> { "Том", "Тома", "Тому", "Томом", "Томе", "Мэри", "Джон", "Джона", "Джону", "Джоном", "Джоне" }
    : new HashSet<string> { "Tom", "Mary", "John", "Tom's", "Mary's", "John's" };

Console.WriteLine("Корпуса: частоты слов…");
foreach (var c in corpora)
{
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    long tokens = 0;
    sentenceNo = 0;
    foreach (var text in Sentences(c.Path, true))
    {
        bool first = true;
        foreach (var (tok, boundary) in Tokenize(text))
        {
            if (boundary) { first = true; continue; }
            tokens++;
            var lower = tok.ToLowerInvariant();
            if (!ValidWord(lower)) { first = false; continue; }
            var key = Norm(lower);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (!first)
            {
                var cs = caseStat.GetValueOrDefault(key);
                bool cap = char.IsUpper(tok[0]);
                bool caps = cap && tok.Length > 1 && tok.All(ch => !char.IsLetter(ch) || char.IsUpper(ch));
                caseStat[key] = caps ? cs with { Caps = cs.Caps + 1 } : cap ? cs with { Cap = cs.Cap + 1 } : cs with { Lower = cs.Lower + 1 };
            }
            if (!surface.ContainsKey(key)) surface[key] = (lower, 0);
            first = false;
        }
    }
    Console.WriteLine($"  {Path.GetFileName(c.Path)}: слов {tokens:N0}");
    foreach (var (k, n) in counts) mixed[k] = mixed.GetValueOrDefault(k) + c.Share * n / Math.Max(1, tokens);
}

writeHoldout = false;

// Слова делового корпуса — всегда в словаре
var extraLines = extraPath is null ? [] : File.ReadAllLines(extraPath, Encoding.UTF8).Where(l => l.Trim().Length > 0 && !l.StartsWith('#')).ToArray();
var forced = new HashSet<string>(StringComparer.Ordinal);
foreach (var line in extraLines)
    foreach (var (tok, boundary) in Tokenize(line))
        if (!boundary && ValidWord(tok.ToLowerInvariant()))
        {
            var key = Norm(tok);
            forced.Add(key);
            NoteSurface(key, tok.ToLowerInvariant(), 0);
        }

var vocab = mixed.OrderByDescending(kv => kv.Value).Take(vocabSize).Select(kv => kv.Key)
    .Concat(forced).Distinct().ToArray();
Array.Sort(vocab, StringComparer.Ordinal);
var id = new Dictionary<string, int>(vocab.Length, StringComparer.Ordinal);
for (int i = 0; i < vocab.Length; i++) id[vocab[i]] = i;
double minMixed = vocab.Where(mixed.ContainsKey).Min(v => mixed[v]);
// Частота хранится целым: доли на миллиард
long[] uni = vocab.Select(v => Math.Max(1, (long)Math.Round(Math.Max(mixed.GetValueOrDefault(v), minMixed) * 1e9))).ToArray();
Console.WriteLine($"  словарь: {vocab.Length:N0} слов");

// Форма для показа: ё, имена собственные (заглавная в середине предложения ≥ 85 %), аббревиатуры (капсом ≥ 70 %)
string FormOf(string key)
{
    var f = surface.TryGetValue(key, out var s) ? s.Form : key;
    if (lang == "en" && (f == "i" || f.StartsWith("i'", StringComparison.Ordinal))) return "I" + f[1..];
    if (caseStat.TryGetValue(key, out var cs))
    {
        int all = cs.Lower + cs.Cap + cs.Caps;
        if (cs.Caps >= 10 && cs.Caps >= 0.7 * all && f.Length <= 6) return f.ToUpperInvariant();
        if (cs.Cap + cs.Caps >= 30 && cs.Cap >= 0.85 * all) return char.ToUpperInvariant(f[0]) + f[1..];
    }
    return f;
}

// ---------------------------------------------------------------- n-граммы
var sufPairs = new Dictionary<(string, string), int>();
var skipPairs = new Dictionary<(string, string), int>(); // окончание слова через одно: «поиском и возможностью»
var sufMarg = new Dictionary<string, long>(StringComparer.Ordinal);
var ctxTotal = new long[vocab.Length];

IEnumerable<(string Text, int Weight)> Training()
{
    foreach (var c in corpora)
    {
        sentenceNo = 0;
        foreach (var t in Sentences(c.Path, true)) yield return (t, c.Weight);
    }
    foreach (var l in extraLines) yield return (l, extraWeight * 2);
}

// Проход 1: биграммы и окончания
Console.WriteLine("Биграммы…");
var bi = new Dictionary<long, int>(1 << 22);
foreach (var (text, weight) in Training())
{
    int p1 = -1;
    string? prevWord = null, prevPrev = null;
    foreach (var (tok, boundary) in Tokenize(text))
    {
        if (boundary || names.Contains(tok)) { p1 = -1; prevWord = prevPrev = null; continue; }
        var key = Norm(tok);
        bool valid = ValidWord(key);
        if (prevWord is not null && valid)
        {
            var pair = (PrevClass(prevWord), NextSuffix(key));
            sufPairs[pair] = sufPairs.GetValueOrDefault(pair) + weight;
            sufMarg[pair.Item2] = sufMarg.GetValueOrDefault(pair.Item2) + weight;
            if (prevPrev is not null)
            {
                var skip = (PrevClass(prevPrev), pair.Item2);
                skipPairs[skip] = skipPairs.GetValueOrDefault(skip) + weight;
            }
        }
        prevPrev = valid ? prevWord : null;
        prevWord = valid ? key : null;
        if (!id.TryGetValue(key, out var wid)) { p1 = -1; continue; }
        if (p1 >= 0)
        {
            long k2 = ((long)p1 << 20) | (uint)wid;
            bi[k2] = bi.GetValueOrDefault(k2) + weight;
            ctxTotal[p1] += weight;
        }
        p1 = wid;
    }
}
int min2 = Threshold(bi.Values, maxBi);
var biKeys = bi.Where(kv => kv.Value >= min2).Select(kv => kv.Key).ToArray();
Array.Sort(biKeys);
var biSet = new HashSet<long>(biKeys);
Console.WriteLine($"  биграмм {bi.Count:N0}, оставлено {biKeys.Length:N0} (порог {min2 / 2.0})");

// Проход 2: триграммы — только из оставленных биграмм
Console.WriteLine("Триграммы…");
var tri = new Dictionary<long, int>(1 << 22);
foreach (var (text, weight) in Training())
{
    int p2 = -1, p1 = -1;
    foreach (var (tok, boundary) in Tokenize(text))
    {
        if (boundary || names.Contains(tok)) { p2 = p1 = -1; continue; }
        if (!id.TryGetValue(Norm(tok), out var wid)) { p2 = p1 = -1; continue; }
        if (p2 >= 0 && biSet.Contains(((long)p2 << 20) | (uint)p1) && biSet.Contains(((long)p1 << 20) | (uint)wid))
        {
            long k3 = ((long)p2 << 40) | ((long)p1 << 20) | (uint)wid;
            tri[k3] = tri.GetValueOrDefault(k3) + weight;
        }
        p2 = p1;
        p1 = wid;
    }
}
int min3 = Threshold(tri.Values, maxTri);
var triKeys = tri.Where(kv => kv.Value >= min3).Select(kv => kv.Key).ToArray();
Array.Sort(triKeys);
Console.WriteLine($"  триграмм {tri.Count:N0}, оставлено {triKeys.Length:N0} (порог {min3 / 2.0})");
// Классы, встреченные реже 50 раз, модель всё равно не использует (SuffixLift возвращает 1) — не храним
var classSum = new Dictionary<string, long>(StringComparer.Ordinal);
foreach (var ((cls, _), v) in sufPairs) classSum[cls] = classSum.GetValueOrDefault(cls) + v;
var sufList = sufPairs.Where(kv => kv.Value >= minSuf && classSum[kv.Key.Item1] >= 100).ToList();
var skipSum = new Dictionary<string, long>(StringComparer.Ordinal);
foreach (var ((cls, _), v) in skipPairs) skipSum[cls] = skipSum.GetValueOrDefault(cls) + v;
holdoutWriter?.Dispose();

/// Наименьший порог, при котором записей не больше max (и не меньше 2 — одиночные сочетания не храним).
static int Threshold(IEnumerable<int> values, int max)
{
    var hist = new SortedDictionary<int, int>();
    foreach (var v in values) hist[v] = hist.GetValueOrDefault(v) + 1;
    long kept = hist.Values.Sum(x => (long)x);
    foreach (var (v, n) in hist)
    {
        if (kept <= max && v >= 4) return v; // вес ×2: 4 = два употребления
        kept -= n;
    }
    return int.MaxValue;
}

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
        if (c is '.' or '!' or '?' or '…' or ';' or '(' or ')' or '"' or '«' or '»' or ':' || char.IsDigit(c)) yield return ("", true);
    }
}

// ---------------------------------------------------------------- запись
static ushort Cnt(int weighted) => (ushort)Math.Min((weighted + 1) / 2, ushort.MaxValue);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(a["out"][0]))!);
using (var fs = File.Create(a["out"][0]))
using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
using (var bw = new BinaryWriter(gz, Encoding.UTF8))
{
    bw.Write("T9LM"u8.ToArray());
    bw.Write(2); // версия формата (2: + таблица окончаний через слово)
    bw.Write(lang);
    bw.Write(vocab.Length);
    bw.Write(uni.Sum());
    foreach (var v in vocab) bw.Write(v);
    foreach (var u in uni) bw.Write(u);

    var forms = new List<(int, string)>();
    for (int i = 0; i < vocab.Length; i++)
    {
        var f = FormOf(vocab[i]);
        if (f != vocab[i]) forms.Add((i, f));
    }
    bw.Write(forms.Count);
    foreach (var (i, f) in forms) { bw.Write(i); bw.Write(f); }

    foreach (var t in ctxTotal) bw.Write((int)Math.Min((t + 1) / 2, int.MaxValue));

    bw.Write(biKeys.Length);
    foreach (var k in biKeys) bw.Write(k);
    foreach (var k in biKeys) bw.Write(Cnt(bi[k]));

    bw.Write(triKeys.Length);
    foreach (var k in triKeys) bw.Write(k);
    foreach (var k in triKeys) bw.Write(Cnt(tri[k]));

    // Модель окончаний
    var classes = sufList.Select(kv => kv.Key.Item1).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    var sufs = sufMarg.Where(kv => kv.Value >= 4).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
    var sufId = sufs.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);
    var clsId = classes.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);
    bw.Write(classes.Count);
    foreach (var c in classes) bw.Write(c);
    bw.Write(sufs.Count);
    foreach (var s in sufs) { bw.Write(s); bw.Write((sufMarg[s] + 1) / 2); }
    var entries = sufList.Where(kv => sufId.ContainsKey(kv.Key.Item2))
        .Select(kv => (Key: ((long)clsId[kv.Key.Item1] << 20) | (uint)sufId[kv.Key.Item2], Value: (kv.Value + 1) / 2))
        .OrderBy(e => e.Key).ToList();
    bw.Write(entries.Count);
    foreach (var e in entries) { bw.Write(e.Key); bw.Write(e.Value); }

    // Окончания через слово: свои классы, общие с основной таблицей окончания
    var skipList = skipPairs.Where(kv => kv.Value >= minSuf && skipSum[kv.Key.Item1] >= 100 && sufId.ContainsKey(kv.Key.Item2)).ToList();
    var classes2 = skipList.Select(kv => kv.Key.Item1).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    var cls2Id = classes2.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);
    bw.Write(classes2.Count);
    foreach (var c in classes2) bw.Write(c);
    var entries2 = skipList.Select(kv => (Key: ((long)cls2Id[kv.Key.Item1] << 20) | (uint)sufId[kv.Key.Item2], Value: (kv.Value + 1) / 2))
        .OrderBy(e => e.Key).ToList();
    bw.Write(entries2.Count);
    foreach (var e in entries2) { bw.Write(e.Key); bw.Write(e.Value); }
    Console.WriteLine($"Форм для показа: {forms.Count:N0}, пар окончаний: {entries.Count:N0} (классов {classes.Count:N0}), через слово: {entries2.Count:N0} (классов {classes2.Count:N0})");
}
Console.WriteLine($"Готово: {a["out"][0]} ({new FileInfo(a["out"][0]).Length / 1024.0 / 1024:N1} МБ)");

static int Int(Dictionary<string, List<string>> a, string key, int def) =>
    a.TryGetValue(key, out var v) ? int.Parse(v[0], CultureInfo.InvariantCulture) : def;

static Dictionary<string, List<string>> ParseArgs(string[] args)
{
    var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i + 1 < args.Length; i += 2)
    {
        var k = args[i].TrimStart('-');
        if (!d.TryGetValue(k, out var list)) d[k] = list = [];
        list.Add(args[i + 1]);
    }
    return d;
}
