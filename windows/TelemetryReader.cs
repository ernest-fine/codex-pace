using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPace;

internal sealed class TelemetryReader
{
    private const int WeeklyMinutes = 7 * 24 * 60;
    private const int MaxRolloutBytes = 3 * 1024 * 1024;
    private const int MaxScanBytes = 48 * 1024 * 1024;
    private const int MaxCatalogBytes = 10 * 1024 * 1024;
    private const int MaxFiles = 96;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly EnumerationOptions Enumeration = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string home;
    private Dictionary<string, CachedRollout> files = new(PathComparer);
    private CachedCatalog? catalog;
    private LiveTelemetry? live;
    private string? liveExecutable;
    private DateTimeOffset lastLiveAttempt = DateTimeOffset.MinValue;

    public TelemetryReader()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        home = ExpandPath(Environment.GetEnvironmentVariable("CODEX_HOME"))
            ?? Path.Combine(profile, ".codex");
    }

    public async Task<Telemetry> ReadAsync(DateTimeOffset now, bool force, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadCoreAsync(now, force, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Telemetry> ReadCoreAsync(DateTimeOffset now, bool force, CancellationToken cancellationToken)
    {
        DateTimeOffset current = DateTimeOffset.UtcNow;
        if (current - lastLiveAttempt >= TimeSpan.FromSeconds(force ? 15 : 110))
        {
            lastLiveAttempt = current;
            live = await ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<Rollout> rollouts = await ReadRolloutsAsync(now, cancellationToken).ConfigureAwait(false);
        IEnumerable<QuotaSample> localSamples = rollouts.SelectMany(rollout => rollout.Quotas)
            .Where(sample => sample.ObservedAt <= now);
        QuotaSample[] samples = localSamples.Concat(live?.Quotas ?? []).ToArray();
        QuotaSample? weekly = LatestQuota(samples, now, weekly: true);
        QuotaSample? shortQuota = LatestQuota(samples, now, weekly: false);

        QuotaSample[] history = weekly is null ? [] : samples
            .Where(sample => sample.WindowMinutes == WeeklyMinutes && sample.ResetsAt == weekly.ResetsAt)
            .GroupBy(sample => sample.ObservedAt)
            .Select(group => group.Last())
            .OrderBy(sample => sample.ObservedAt)
            .ToArray();

        IReadOnlyList<ModelOption> models = live is { Models.Count: > 0 }
            ? live.Models
            : await ReadCachedModelsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string source = weekly is null ? "Waiting for Codex usage"
            : live?.Quotas.Contains(weekly) == true ? "Codex account" : "Local Codex history";
        string? error = weekly is null
            ? "No current weekly limit has been observed. It will appear after Codex reports your account usage."
            : null;
        return new Telemetry(weekly, shortQuota, history, RecentActivity(rollouts, now), models, source, error);
    }

    private static QuotaSample? LatestQuota(IEnumerable<QuotaSample> samples, DateTimeOffset now, bool weekly) =>
        samples.Where(sample => (sample.WindowMinutes == WeeklyMinutes) == weekly && sample.ResetsAt > now)
            .MaxBy(sample => sample.ObservedAt);

    private static Activity? RecentActivity(IReadOnlyList<Rollout> rollouts, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.AddHours(-1);
        Turn[] turns = rollouts.SelectMany(rollout => rollout.Turns).Where(turn => turn.At <= now).ToArray();
        Turn? latest = turns.MaxBy(turn => turn.At);
        if (latest is null)
            return null;

        int turnCount = turns.Where(turn => turn.At >= cutoff).Select(turn => turn.Id).Distinct().Count();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long tokens = 0;
        foreach (Usage usage in rollouts.SelectMany(rollout => rollout.Usage))
        {
            if (usage.At >= cutoff && usage.At <= now && seen.Add(usage.Id))
                tokens += Math.Min(usage.Tokens, long.MaxValue - tokens);
        }

        return new Activity(latest.At, latest.Model, latest.Effort, turnCount, tokens);
    }

    private async Task<IReadOnlyList<Rollout>> ReadRolloutsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var next = new Dictionary<string, CachedRollout>(PathComparer);
        int bytesRemaining = MaxScanBytes;
        var rollouts = new List<Rollout>();
        foreach (FileStamp file in RolloutFiles(now).OrderByDescending(file => file.ModifiedAt).Take(MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = (int)Math.Min(file.Length, MaxRolloutBytes);
            files.TryGetValue(file.Path, out CachedRollout? cached);
            if (cached?.Stamp == file || size > bytesRemaining)
            {
                if (cached is not null)
                {
                    next[file.Path] = cached;
                    rollouts.Add(cached.Rollout);
                }
                continue;
            }

            bytesRemaining -= size;
            Rollout? rollout = await ReadRolloutAsync(file, size, cancellationToken).ConfigureAwait(false);
            if (rollout is not null)
            {
                next[file.Path] = new CachedRollout(file, rollout);
                rollouts.Add(rollout);
            }
        }

        files = next;
        return rollouts;
    }

    private IEnumerable<FileStamp> RolloutFiles(DateTimeOffset now)
    {
        var folders = new List<string> { Path.Combine(home, "archived_sessions") };
        for (int offset = -8; offset <= 1; offset++)
        {
            DateTime date = now.UtcDateTime.AddDays(offset);
            folders.Add(Path.Combine(home, "sessions", date.ToString("yyyy", CultureInfo.InvariantCulture),
                date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture)));
        }

        DateTimeOffset cutoff = now.AddDays(-8);
        foreach (string folder in folders)
        {
            FileInfo[] candidates;
            try
            {
                candidates = new DirectoryInfo(folder).GetFiles("*.jsonl", Enumeration);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (FileInfo candidate in candidates)
            {
                FileStamp? stamp = GetStamp(candidate);
                if (stamp is not null && stamp.ModifiedAt >= cutoff)
                    yield return stamp;
            }
        }
    }

    private static FileStamp? GetStamp(FileInfo file)
    {
        try
        {
            if (file.Exists)
                return new FileStamp(file.FullName, file.LastWriteTimeUtc, file.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The file may have moved into the archive during this poll.
        }

        return null;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<Rollout?> ReadRolloutAsync(
        FileStamp file, int size, CancellationToken cancellationToken)
    {
        if (size == 0)
            return new Rollout();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            using FileStream stream = OpenRead(file.Path);
            long offset = file.Length - size;
            stream.Seek(offset, SeekOrigin.Begin);
            int count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, size), size,
                throwOnEndOfStream: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseRollout(file.Path, buffer, count, offset > 0, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static Rollout ParseRollout(string path, byte[] buffer, int count, bool skipFirst,
        CancellationToken cancellationToken)
    {
        var rollout = new Rollout();
        var legacyUsage = new List<Usage>();
        long? lastCumulative = null;
        int start = skipFirst ? Array.IndexOf(buffer, (byte)'\n', 0, count) + 1 : 0;
        if (skipFirst && start == 0)
            return rollout;

        while (start < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Array.IndexOf(buffer, (byte)'\n', start, count - start);
            end = end < 0 ? count : end;
            ReadOnlyMemory<byte> line = buffer.AsMemory(start, end - start);
            start = end + 1;

            if (line.Span.IndexOf("\"token_count\""u8) < 0
                && line.Span.IndexOf("\"turn_context\""u8) < 0
                && line.Span.IndexOf("\"token_usage_record\""u8) < 0)
                continue;

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement record = document.RootElement;
                JsonElement timestamp = Property(record, "timestamp");
                if (timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var at))
                    continue;

                JsonElement payload = Property(record, "payload");
                switch (Text(record, "type"))
                {
                    case "event_msg" when Text(payload, "type") == "token_count":
                        JsonElement limits = Property(payload, "rate_limits");
                        if (Text(limits, "limit_id") is null or "codex")
                            ReadWindows(limits, at, "used_percent", "window_minutes", "resets_at", rollout.Quotas);

                        JsonElement info = Property(payload, "info");
                        if (UncachedTokens(Property(info, "total_token_usage")) is long cumulative)
                        {
                            long delta = lastCumulative is long previous
                                ? (cumulative >= previous ? cumulative - previous : cumulative)
                                : UncachedTokens(Property(info, "last_token_usage")) ?? 0;
                            lastCumulative = cumulative;
                            if (delta > 0)
                                legacyUsage.Add(new Usage($"{Path.GetFileName(path)}:{at:O}", at, delta));
                        }
                        break;
                    case "turn_context":
                        string? rootId = Text(payload, "root_turn_id");
                        string? turnId = Text(payload, "turn_id") ?? rootId;
                        if (turnId is not null && (rootId is null || rootId == turnId)
                            && Text(payload, "model") is string model && model != "codex-auto-review")
                            rollout.Turns.Add(new Turn(turnId, at, model, Text(payload, "effort")));
                        break;
                    case "token_usage_record":
                        if (Text(payload, "response_id") is string responseId
                            && UncachedTokens(Property(payload, "usage")) is long tokens && tokens > 0)
                            rollout.Usage.Add(new Usage(responseId, at, tokens));
                        break;
                }
            }
            catch (JsonException)
            {
                // A running Codex session may still be appending its last record.
            }
        }

        if (rollout.Usage.Count == 0)
            rollout.Usage.AddRange(legacyUsage);
        return rollout;
    }

    private async Task<IReadOnlyList<ModelOption>> ReadCachedModelsAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(home, "models_cache.json");
        FileStamp? stamp = GetStamp(new FileInfo(path));
        if (stamp is null || stamp.Length is <= 0 or > MaxCatalogBytes)
            return [];
        if (catalog?.Stamp == stamp)
            return catalog.Models;

        try
        {
            using FileStream stream = OpenRead(path);
            byte[] buffer = new byte[(int)stamp.Length];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(buffer);
            var models = new List<ModelOption>();
            foreach (JsonElement entry in Items(document.RootElement, "models"))
            {
                if (Text(entry, "visibility") == "list" && Text(entry, "slug") is string id)
                {
                    string[] efforts = Items(entry, "supported_reasoning_levels")
                        .Select(effort => Text(effort, "effort")).OfType<string>().ToArray();
                    models.Add(new ModelOption(id, Text(entry, "display_name") ?? id, efforts,
                        Text(entry, "default_reasoning_level"), models.Count == 0));
                }
            }
            catalog = new CachedCatalog(stamp, models);
            return models;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private IEnumerable<string> CodexExecutables()
    {
        string? configured = ExpandPath(Environment.GetEnvironmentVariable("CODEX_PACE_CODEX"));
        if (configured is not null && (!OperatingSystem.IsWindows()
                || Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            yield return configured;

        if (liveExecutable is not null)
            yield return liveExecutable;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string? installDir = ExpandPath(Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR"));
        if (installDir is not null)
            yield return Path.Combine(installDir, "codex.exe");
        if (Path.IsPathFullyQualified(local))
            yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe");

        string currentPackage = Path.Combine(home, "packages", "standalone", "current");
        yield return Path.Combine(currentPackage, "bin", "codex.exe");
        yield return Path.Combine(currentPackage, "codex.exe");
        string currentDaemon = Path.Combine(home, "packages", "app-server-daemon", "current");
        yield return Path.Combine(currentDaemon, "bin", "codex.exe");
        yield return Path.Combine(currentDaemon, "codex.exe");

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator).Select(ExpandPath).OfType<string>().ToList();
        if (Path.IsPathFullyQualified(roaming))
            directories.Add(Path.Combine(roaming, "npm"));

        foreach (string directory in directories.Distinct(PathComparer))
        {
            yield return Path.Combine(directory, "codex.exe");
            foreach (string binary in NpmBinaries(directory))
                yield return binary;
        }

        if (Path.IsPathFullyQualified(local))
            yield return Path.Combine(local, "Microsoft", "WindowsApps", "codex.exe");
    }

    private static IEnumerable<string> NpmBinaries(string directory)
    {
        string scope = Path.Combine(directory, "node_modules", "@openai");
        string main = Path.Combine(scope, "codex");
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            foreach (string binary in NpmBinaries(scope, main, "codex-win32-arm64", "aarch64-pc-windows-msvc"))
                yield return binary;
        }

        foreach (string binary in NpmBinaries(scope, main, "codex-win32-x64", "x86_64-pc-windows-msvc"))
            yield return binary;
    }

    private static IEnumerable<string> NpmBinaries(string scope, string main, string platform, string target)
    {
        // npm may hoist the native package or keep it inside the main Codex package.
        foreach (string package in new[] { Path.Combine(scope, platform),
                     Path.Combine(main, "node_modules", "@openai", platform), main })
        {
            string vendor = Path.Combine(package, "vendor", target);
            yield return Path.Combine(vendor, "bin", "codex.exe");
            yield return Path.Combine(vendor, "codex", "codex.exe");
        }
    }

    private static string? ExpandPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        string path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    private async Task<LiveTelemetry?> ReadLiveAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        LiveTelemetry? partial = null;
        try
        {
            foreach (string executable in CodexExecutables().Distinct(PathComparer).Where(File.Exists).Take(3))
            {
                LiveTelemetry? result = await FetchAppServerAsync(executable, timeout.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                partial ??= result;
                if (result?.Quotas.Any(quota => quota.WindowMinutes == WeeklyMinutes
                                                && quota.ResetsAt > DateTimeOffset.UtcNow) == true)
                {
                    liveExecutable = executable;
                    return result;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timed-out or unavailable app server leaves local readings available.
        }

        return partial;
    }

    private static async Task<LiveTelemetry?> FetchAppServerAsync(string executable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--stdio");

        try
        {
            if (!process.Start())
                return null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }

        Task stderr = DrainErrorsAsync(process.StandardError.BaseStream, cancellationToken);
        var quotas = new List<QuotaSample>();
        var models = new List<ModelOption>();
        try
        {
            var reader = new RpcLineReader(process.StandardOutput);
            await SendAsync(process.StandardInput,
                """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"codex_pace","title":"Codex Pace","version":"1.0.0"}}}""",
                cancellationToken).ConfigureAwait(false);

            bool initialized = false;
            var expected = new HashSet<int> { 2, 3 };
            while (expected.Count > 0
                   && await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement response = document.RootElement;
                    long? id = Integer(response, "id");
                    JsonElement result = Property(response, "result");
                    if (id == 1 && !initialized)
                    {
                        if (result.ValueKind != JsonValueKind.Object)
                            break;
                        initialized = true;
                        await SendAsync(process.StandardInput, """{"method":"initialized"}""", cancellationToken)
                            .ConfigureAwait(false);
                        await SendAsync(process.StandardInput, """{"id":2,"method":"account/rateLimits/read"}""",
                            cancellationToken).ConfigureAwait(false);
                        await SendAsync(process.StandardInput,
                            """{"id":3,"method":"model/list","params":{"includeHidden":false}}""",
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (id == 2 && expected.Remove(2))
                    {
                        JsonElement snapshot = Property(Property(result, "rateLimitsByLimitId"), "codex");
                        if (snapshot.ValueKind != JsonValueKind.Object)
                            snapshot = Property(result, "rateLimits");
                        if (Text(snapshot, "limitId") is null or "codex")
                            ReadWindows(snapshot, DateTimeOffset.UtcNow, "usedPercent",
                                "windowDurationMins", "resetsAt", quotas);
                    }
                    else if (id == 3 && expected.Remove(3))
                    {
                        foreach (JsonElement model in Items(result, "data"))
                        {
                            string? idText = Text(model, "model") ?? Text(model, "id");
                            if (idText is null || Property(model, "hidden").ValueKind == JsonValueKind.True)
                                continue;

                            string[] efforts = Items(model, "supportedReasoningEfforts")
                                .Select(effort => Text(effort, "reasoningEffort")).OfType<string>().ToArray();
                            models.Add(new ModelOption(idText, Text(model, "displayName") ?? idText,
                                efforts, Text(model, "defaultReasoningEffort"),
                                Property(model, "isDefault").ValueKind == JsonValueKind.True));
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore startup chatter or malformed protocol lines.
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The child may exit while the parent is writing or reading.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && quotas.Count + models.Count > 0)
        {
            // Retain a complete quota or model response already collected before the deadline.
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                // The child may have already exited.
            }

            try
            {
                await stderr.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Disposing the process closes the pipe.
            }
        }

        return quotas.Count + models.Count == 0 ? null : new LiveTelemetry(quotas, models);
    }

    private static async Task SendAsync(StreamWriter writer, string line, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainErrorsAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Stderr is deliberately neither stored nor displayed.
        }
    }

    private static void ReadWindows(JsonElement snapshot, DateTimeOffset at, string percentKey, string minutesKey,
        string resetKey, List<QuotaSample> quotas)
    {
        foreach (string key in new[] { "primary", "secondary" })
        {
            JsonElement window = Property(snapshot, key);
            if (Number(window, percentKey) is double percent && percent is >= 0 and <= 100
                && Integer(window, minutesKey) is long minutes && minutes is > 0 and <= WeeklyMinutes
                && Integer(window, resetKey) is long reset && reset is > 0 and <= 253402300799)
                quotas.Add(new QuotaSample(at, DateTimeOffset.FromUnixTimeSeconds(reset), (int)minutes, percent));
        }
    }

    private static long? UncachedTokens(JsonElement usage) =>
        Integer(usage, "total_tokens") is long total && total >= 0
            ? Math.Max(0, total - Math.Max(0, Integer(usage, "cached_input_tokens") ?? 0))
            : null;

    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement item, string name)
    {
        JsonElement property = Property(item, name);
        return property.ValueKind == JsonValueKind.Array ? property.EnumerateArray() : [];
    }

    private static string? Text(JsonElement item, string name)
    {
        JsonElement property = Property(item, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static long? Integer(JsonElement item, string name)
    {
        JsonElement property = Property(item, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long result) ? result : null;
    }

    private static double? Number(JsonElement item, string name)
    {
        JsonElement property = Property(item, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double result) ? result : null;
    }

    private sealed class RpcLineReader(StreamReader stream)
    {
        private readonly char[] buffer = new char[8192];
        private int remaining = 8 * 1024 * 1024;
        private int start;
        private int end;

        public async Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (start == end)
                {
                    start = 0;
                    end = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask()
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (end == 0)
                        return line.Length == 0 ? null : line.ToString();
                    remaining -= end;
                    if (remaining < 0)
                        return null;
                }

                int newline = Array.IndexOf(buffer, '\n', start, end - start);
                int boundary = newline < 0 ? end : newline;
                line.Append(buffer, start, boundary - start);
                start = newline < 0 ? end : newline + 1;
                if (newline >= 0)
                    return line.ToString();
            }
        }
    }

    private sealed record FileStamp(string Path, DateTimeOffset ModifiedAt, long Length);
    private sealed record CachedRollout(FileStamp Stamp, Rollout Rollout);
    private sealed record CachedCatalog(FileStamp Stamp, IReadOnlyList<ModelOption> Models);
    private sealed record Turn(string Id, DateTimeOffset At, string Model, string? Effort);
    private sealed record Usage(string Id, DateTimeOffset At, long Tokens);
    private sealed record LiveTelemetry(IReadOnlyList<QuotaSample> Quotas, IReadOnlyList<ModelOption> Models);

    private sealed class Rollout
    {
        public List<QuotaSample> Quotas { get; } = [];
        public List<Turn> Turns { get; } = [];
        public List<Usage> Usage { get; } = [];
    }
}
