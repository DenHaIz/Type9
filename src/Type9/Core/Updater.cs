using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Type9.Core;

public sealed record ReleaseInfo(Version Version, string Tag, string AssetUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Автообновление из GitHub Releases. Загружается Type9.exe, проверяется SHA-256 из SHA256SUMS.txt, затем текущий файл переименовывается в .old (запущенный exe
/// переименовывать можно), новый ставится на его место и запускается. Данные пользователя в %AppData%
/// (словарь, фразы, настройки) не затрагиваются.
/// </summary>
internal static class Updater
{
    private const string Repo = "DenHaIz/Type9";
    private static readonly HttpClient Http = CreateClient();

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Обновлять файл можно только у собранного single-file exe (не при запуске из IDE).</summary>
#pragma warning disable IL3000 // пустой Location — как раз признак single-file
    public static bool CanSelfUpdate => string.IsNullOrEmpty(typeof(Updater).Assembly.Location) && Environment.ProcessPath is not null;
#pragma warning restore IL3000


    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Type9-Updater/{typeof(Updater).Assembly.GetName().Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version? ParseTag(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Последний релиз, если он новее текущей версии; иначе null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version is null || version <= Current) return null;

        const string wanted = "Type9.exe";
        string? assetUrl = null, sumsUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == wanted) { assetUrl = url; size = asset.GetProperty("size").GetInt64(); }
            else if (name == "SHA256SUMS.txt") sumsUrl = url;
        }
        if (assetUrl is null) return null;

        string? sha = null;
        if (sumsUrl is not null)
        {
            var sums = await Http.GetStringAsync(sumsUrl, ct);
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == wanted) sha = parts[0].ToLowerInvariant();
            }
        }
        return new ReleaseInfo(version, tag, assetUrl, size, sha, root.GetProperty("html_url").GetString() ?? "");
    }

    /// <summary>Скачивает, проверяет и подменяет exe. После успеха вызывающий должен завершить программу.</summary>
    public static async Task InstallAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!CanSelfUpdate) throw new InvalidOperationException(Loc.T("U.DevBuild"));
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + ".download");
        var old = exe + ".old";

        using (var resp = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? release.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        try
        {
            if (release.Size > 0 && new FileInfo(tmp).Length != release.Size)
                throw new InvalidDataException("размер файла не совпадает");
            if (release.Sha256 is not null)
            {
                await using var fs = File.OpenRead(tmp);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (hash != release.Sha256) throw new InvalidDataException("контрольная сумма не совпадает");
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch
            {
                File.Move(old, exe); // откат
                throw;
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }

        Log.Info($"Установлено обновление {release.Tag}");
        Process.Start(new ProcessStartInfo(exe, $"--restart --updated {release.Version.ToString(3)}") { UseShellExecute = false });
    }

    /// <summary>После обновления удаляем старый exe (при первом запуске новой версии).</summary>
    public static void CleanupOld()
    {
        if (Environment.ProcessPath is not { } exe) return;
        var old = exe + ".old";
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(300); }
        }
    }
}
