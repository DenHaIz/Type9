using System.IO;
using System.IO.Compression;
using System.Reflection;
using Type9.Core;
using Type9.Input;

namespace Type9.Prediction;

/// <summary>
/// Неизменяемый частотный словарь: отсортированный массив ключей + бинарный поиск по префиксу.
/// Источники: встроенные ресурсы Resources/Dict/*.txt и файлы %AppData%\Type9\dict\*.txt(.gz).
/// Формат строки: «слово» или «слово частота»; строки идут по убыванию частоты.
/// </summary>
public sealed class BaseDictionary
{
    private readonly string[] _keys;
    private readonly string[] _forms;
    private readonly float[] _scores;

    public static readonly BaseDictionary Empty = new([], [], []);

    public int Count => _keys.Length;

    private BaseDictionary(string[] keys, string[] forms, float[] scores)
    {
        _keys = keys;
        _forms = forms;
        _scores = scores;
    }

    public static BaseDictionary Build(IEnumerable<IEnumerable<string>> sources)
    {
        var best = new Dictionary<string, (string form, float score)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var words = new List<string>();
            foreach (var raw in source)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = line.IndexOfAny([' ', '\t']);
                var word = sp > 0 ? line[..sp] : line;
                if (word.Length < 2 || !word.All(c => char.IsLetter(c) || c == '-')) continue;
                words.Add(word);
            }

            // Оценка по рангу: 1 для самого частого, плавно к 0 для редких
            double logN = Math.Log(words.Count + 1);
            for (int rank = 0; rank < words.Count; rank++)
            {
                var form = words[rank];
                var key = CaseHelper.Normalize(form);
                float score = (float)(1 - Math.Log(rank + 1) / logN);
                if (!best.TryGetValue(key, out var cur) || cur.score < score)
                    best[key] = (form.Any(char.IsUpper) && form.Skip(1).Any(char.IsUpper) ? form : form.ToLowerInvariant(), score);
            }
        }

        var keys = best.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        var forms = new string[keys.Length];
        var scores = new float[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            var v = best[keys[i]];
            forms[i] = v.form;
            scores[i] = v.score;
        }
        return new BaseDictionary(keys, forms, scores);
    }

    public static BaseDictionary LoadDefault()
    {
        var sources = new List<IEnumerable<string>>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Dict.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            sources.Add(ReadLines(s));
        }

        try
        {
            if (Directory.Exists(Paths.DictDir))
            {
                foreach (var file in Directory.EnumerateFiles(Paths.DictDir))
                {
                    if (file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        sources.Add(File.ReadAllLines(file));
                    else if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                    {
                        using var fs = File.OpenRead(file);
                        using var gz = new GZipStream(fs, CompressionMode.Decompress);
                        sources.Add(ReadLines(gz));
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("Ошибка чтения пользовательских словарей", ex); }

        return Build(sources);
    }

    private static List<string> ReadLines(Stream s)
    {
        var list = new List<string>();
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line) list.Add(line);
        return list;
    }

    /// <summary>Все слова с данным префиксом (ключ нормализован).</summary>
    public IEnumerable<(string Key, string Form, float Score)> ByPrefix(string keyPrefix)
    {
        int lo = 0, hi = _keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(_keys[mid], keyPrefix) < 0) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _keys.Length && _keys[i].StartsWith(keyPrefix, StringComparison.Ordinal); i++)
            yield return (_keys[i], _forms[i], _scores[i]);
    }

    public bool Contains(string key) =>
        Array.BinarySearch(_keys, key, StringComparer.Ordinal) >= 0;
}
