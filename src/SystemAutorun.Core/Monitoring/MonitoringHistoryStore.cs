using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public sealed record MonitoringHistoryLimits(
    long MaximumBytes = 64L * 1024 * 1024,
    int SegmentBytes = 1024 * 1024,
    int RecordBytes = 256 * 1024,
    int MaximumFiles = 1024);

// Agent-only single writer; frontends query through IPC, never open this as another writer.
public sealed class MonitoringHistoryStore : IDisposable
{
    private static readonly Regex FilePattern = new("^history-v1-([0-9]{10})-([0-9a-f]{32})\\.jsonl$", RegexOptions.CultureInvariant);
    private static readonly Regex TempPattern = new("^history-v1-[0-9]{10}-[0-9a-f]{32}\\.jsonl\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);
    private readonly string _root;
    private readonly string _rootId;
    private readonly string _userKey;
    private readonly string _directory;
    private readonly MonitoringHistoryLimits _limits;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedSegment> _segmentCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _segmentLru = new();
    private readonly Dictionary<string, string> _internPool = new(StringComparer.Ordinal);
    private long _cachedSegmentBytes;
    private const int MaximumCachedSegments = 512;
    private const int MinimumRetainedSegments = 32;
    private const long MaximumCachedSegmentBytes = 32L * 1024 * 1024;
    private const int MaximumInternPoolEntries = 4096;
    private int _pendingWriters;
    private readonly FileStream _writerLease;
    private string? _currentFile;
    private string? _currentHour;
    private DateTimeOffset? _prunedThrough;
    private DateTimeOffset? _highWaterAt;
    private DateTimeOffset? _ambiguousFrom;
    private DateTimeOffset? _ambiguousThrough;
    private long _epochSequence = 1;
    private long _invalidBeforeEpoch;
    private bool _disposed;

    public MonitoringHistoryStore(ProjectPaths paths, MonitoringHistoryLimits? limits = null, TimeProvider? clock = null)
    {
        var endpoint = AgentProtocol.ForRoot(paths.RootDirectory);
        _root = endpoint.RootDirectory;
        _rootId = endpoint.RootId;
        _limits = limits ?? new MonitoringHistoryLimits();
        if (_limits.RecordBytes is < 1024 or > 256 * 1024 || _limits.SegmentBytes < _limits.RecordBytes
            || _limits.SegmentBytes > 4 * 1024 * 1024 || _limits.MaximumBytes < 2L * _limits.SegmentBytes
            || _limits.MaximumBytes > 64L * 1024 * 1024 || _limits.MaximumFiles is < 2 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
        _clock = clock ?? TimeProvider.System;
        _userKey = CurrentUserIdentity.Key;
        // A moved/copied root gets a fresh namespace without deleting the old root's archive.
        _directory = Path.Combine(_root, ".autorun-state", _userKey, "monitoring", _rootId);
        EnsureDirectory();
        var leasePath = Path.Combine(_directory, "writer.lock");
        VerifyFile(leasePath);
        _writerLease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            var markerPath = Path.Combine(_directory, "pruned-v1.json");
            VerifyFile(markerPath);
            if (File.Exists(markerPath))
            {
                if (new FileInfo(markerPath).Length > 4096) { throw new IOException("History retention marker is oversized."); }
                var marker = JsonSerializer.Deserialize<PrunedMarker>(File.ReadAllBytes(markerPath), AgentProtocol.JsonOptions);
                if (marker is not { SchemaVersion: 1 } || marker.RootId != _rootId)
                {
                    throw new IOException("History retention marker is invalid.");
                }
                if ((marker.AmbiguousFrom is null) != (marker.AmbiguousThrough is null)
                    || marker.AmbiguousFrom > marker.AmbiguousThrough)
                {
                    throw new IOException("History clock marker is invalid.");
                }
                if (marker.EpochSequence < 0 || marker.InvalidBeforeEpoch < 0
                    || marker.InvalidBeforeEpoch > marker.EpochSequence)
                {
                    throw new IOException("History epoch marker is invalid.");
                }
                var now = _clock.GetUtcNow();
                _prunedThrough = marker.Through is { } through && through > now ? now : marker.Through;
                _highWaterAt = marker.HighWaterAt;
                _ambiguousFrom = marker.AmbiguousFrom;
                _ambiguousThrough = marker.AmbiguousThrough;
                _epochSequence = checked(marker.EpochSequence + 1);
                _invalidBeforeEpoch = marker.InvalidBeforeEpoch;
            }
            foreach (var path in EnumerateFiles().Where(path => TempPattern.IsMatch(Path.GetFileName(path))
                || Regex.IsMatch(Path.GetFileName(path), "^pruned-v1\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant)))
            {
                VerifyFile(path);
                File.Delete(path);
            }
        }
        catch { _writerLease.Dispose(); throw; }
    }

    public string DirectoryPath => _directory;
    public long MaximumBytes => _limits.MaximumBytes;

    public async Task AppendAsync(MonitoringHistoryFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateFrame(frame);
        Interlocked.Increment(ref _pendingWriters);
        try
        {
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false)
                && !await _gate.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("History writer remained busy after one bounded contention retry; this observation was not persisted.");
            }
        }
        finally { Interlocked.Decrement(ref _pendingWriters); }
        try
        {
            EnsureDirectory();
            if (frame.ObservedAt > _clock.GetUtcNow() + TimeSpan.FromSeconds(5))
            {
                throw new ArgumentException("Cannot append a future observation.");
            }
            var ambiguousFrom = _ambiguousFrom;
            var ambiguousThrough = _ambiguousThrough;
            var epoch = _epochSequence;
            var invalidBeforeEpoch = _invalidBeforeEpoch;
            if (_highWaterAt is { } previous && frame.ObservedAt <= previous)
            {
                var from = frame.ObservedAt - MonitoringHistoryProjection.SamplingInterval;
                ambiguousFrom = ambiguousFrom is { } priorFrom && priorFrom < from ? priorFrom : from;
                ambiguousThrough = ambiguousThrough is { } priorThrough && priorThrough > previous ? priorThrough : previous;
                epoch = checked(_epochSequence + 1);
                invalidBeforeEpoch = epoch;
                _currentFile = null;
                _currentHour = null;
            }
            frame = frame with { EpochSequence = epoch };
            var encoded = JsonSerializer.SerializeToUtf8Bytes(frame, AgentProtocol.JsonOptions);
            if (encoded.Length + 1 > _limits.RecordBytes) { throw new IOException("History sample exceeds the record byte limit."); }
            var line = new byte[encoded.Length + 1];
            encoded.CopyTo(line, 0);
            line[^1] = (byte)'\n';
            // Persist timestamp/epoch before the line. A corrected clock starts a new epoch;
            // old overlapping data is unknown without suppressing fresh post-correction samples.
            await SaveMarkerAsync(new PrunedMarker(1, _rootId, _prunedThrough, frame.ObservedAt,
                ambiguousFrom, ambiguousThrough, epoch, invalidBeforeEpoch), cancellationToken).ConfigureAwait(false);
            var hour = frame.ObservedAt.UtcDateTime.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
            if (_currentHour != hour || _currentFile is null
                || new FileInfo(_currentFile).Length + line.Length > _limits.SegmentBytes)
            {
                _currentFile = null;
                _currentHour = null;
            }
            await PruneAsync(line.Length, _currentFile is null ? 1 : 0, cancellationToken).ConfigureAwait(false);
            if (_currentFile is null)
            {
                var target = Path.Combine(_directory, $"history-v1-{hour}-{Guid.CreateVersion7():N}.jsonl");
                var temporary = target + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    VerifyFile(temporary);
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await output.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                    VerifyFile(target);
                    File.Move(temporary, target);
                    _currentFile = target;
                    _currentHour = hour;
                }
                finally
                {
                    VerifyFile(temporary);
                    if (File.Exists(temporary)) { File.Delete(temporary); }
                }
            }
            else
            {
                VerifyFile(_currentFile);
                await using var output = new FileStream(_currentFile, FileMode.Append, FileAccess.Write,
                    FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await output.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Never append another JSON line onto a possibly torn previous line.
            _currentFile = null;
            _currentHour = null;
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<MonitoringHistoryResult> QueryAsync(MonitoringHistoryQuery query, string activeInstanceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _clock.GetUtcNow();
        MonitoringHistoryProjection.ValidateQuery(query, now);
        var page = await ReadPageAsync(query.From, query.To, now, activeInstanceId, cancellationToken).ConfigureAwait(false);
        return MonitoringHistoryProjection.Project(page.Frames, query, page.AsOf, page.ActiveInstanceId, page.Truncated, page.Warning);
    }

    public async Task<ResourceHistoryResult> QueryResourcesAsync(ResourceHistoryQuery query, string activeInstanceId,
        CancellationToken cancellationToken = default, string? remoteHostIdentity = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _clock.GetUtcNow(); ResourceHistoryProjection.ValidateQuery(query, now);
        if (query.Source != ResourceHistorySource.Durable) { throw new ArgumentException("Recent resource history is memory-only."); }
        var page = await ReadPageAsync(query.From, query.To, now, activeInstanceId, cancellationToken).ConfigureAwait(false);
        return ResourceHistoryProjection.ProjectDurable(page.Frames, query, page.AsOf, page.Truncated, page.Warning, remoteHostIdentity);
    }

    private async Task<HistoryReadPage> ReadPageAsync(DateTimeOffset readFrom, DateTimeOffset readTo,
        DateTimeOffset asOf, string activeInstanceId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _pendingWriters) > 0) { throw new TimeoutException("History writer has priority over new reads."); }
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(3.5), cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("History reader is busy.");
        }
        var frames = new List<MonitoringHistoryFrame>();
        var truncated = false;
        string? warning = null;
        long returnedEpoch = 0;
        try
        {
            if (Volatile.Read(ref _pendingWriters) > 0) { throw new TimeoutException("History writer has priority over queued reads."); }
            EnsureDirectory();
            truncated = _prunedThrough is { } through && readFrom < through;
            warning = truncated ? "Older history was removed by retention/capacity; missing time is unknown." : null;
            using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readBudget.CancelAfter(TimeSpan.FromSeconds(5));
            long bytesRead = 0;
            var files = OwnedFiles().Where(file => file.Hour <= readTo
                && file.Hour.AddHours(1) > readFrom - MonitoringHistoryProjection.SamplingInterval)
                .OrderByDescending(file => file.Hour).ThenByDescending(file => file.Path, StringComparer.Ordinal).ToArray();
            try
            {
                foreach (var file in files)
                {
                    VerifyFile(file.Path);
                    if (file.Length > _limits.SegmentBytes || bytesRead + file.Length > _limits.MaximumBytes)
                    {
                        truncated = true;
                        warning = "History read byte limit reached; omitted records are unknown.";
                        continue;
                    }
                    var parsed = await ReadFramesAsync(file, frames, readBudget.Token).ConfigureAwait(false);
                    bytesRead += file.Length;
                    if (!parsed) { truncated = true; warning = "Incomplete, oversized, or invalid history records were ignored."; }
                    if (frames.Count >= MonitoringHistoryProjection.MaximumInputFrames)
                    {
                        truncated = true;
                        warning = "History record limit reached; omitted records are unknown.";
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                truncated = true;
                warning = "History read time limit reached; unreturned records are unknown.";
            }
            var ambiguousFrom = _ambiguousFrom;
            var ambiguousThrough = _ambiguousThrough;
            var invalidBeforeEpoch = _invalidBeforeEpoch;
            // Compare against the current clock, not the query's pre-lock as-of time:
            // a newer sample published while this read waited is not a clock regression.
            var clockNow = _clock.GetUtcNow();
            var clockIsBehind = _highWaterAt is { } futureTime && clockNow < futureTime;
            if (clockIsBehind && _highWaterAt is { } future)
            {
                var from = clockNow - MonitoringHistoryProjection.SamplingInterval;
                ambiguousFrom = ambiguousFrom is { } earlier && earlier < from ? earlier : from;
                ambiguousThrough = ambiguousThrough is { } later && later > future ? later : future;
                invalidBeforeEpoch = checked(_epochSequence + 1);
            }
            if (ambiguousFrom is { } clockFrom && ambiguousThrough is { } clockTo
                && readTo >= clockFrom && readFrom <= clockTo)
            {
                var removed = frames.RemoveAll(frame => frame.EpochSequence < invalidBeforeEpoch
                    && frame.ObservedAt >= clockFrom && frame.ObservedAt <= clockTo);
                if (removed > 0 || clockIsBehind)
                {
                    frames.Insert(0, UnknownFrame(clockFrom, activeInstanceId));
                    truncated = true;
                    warning = "Clock changed; overlapping old-epoch observations are conservatively unknown.";
                }
            }
            if (_highWaterAt is { } attempted && attempted >= readFrom - MonitoringHistoryProjection.SamplingInterval
                && attempted <= readTo && !frames.Any(frame => frame.ObservedAt == attempted))
            {
                frames.Insert(0, UnknownFrame(attempted, activeInstanceId));
                truncated = true;
                warning ??= "An attempted history append is missing; its interval is unknown.";
            }
            returnedEpoch = _epochSequence;
        }
        finally { _gate.Release(); }
        // Projection is CPU-only and must not block the next durable sample. Higher epochs
        // win equal timestamps; a clock correction must not let older wall-time data win.
        return new HistoryReadPage(frames.OrderBy(frame => frame.EpochSequence)
                .Select(frame => frame with { AgentInstanceId = $"{frame.AgentInstanceId}:{frame.EpochSequence}" }),
            asOf, $"{activeInstanceId}:{returnedEpoch}", truncated, warning);
    }

    private sealed record HistoryReadPage(IEnumerable<MonitoringHistoryFrame> Frames, DateTimeOffset AsOf,
        string ActiveInstanceId, bool Truncated, string? Warning);

    private MonitoringHistoryFrame UnknownFrame(DateTimeOffset at, string instanceId) =>
        new(1, _rootId, instanceId, at, [], _epochSequence);

    private async Task<bool> ReadFramesAsync(OwnedFile file, List<MonitoringHistoryFrame> frames, CancellationToken cancellationToken)
    {
        var expectedLength = (int)file.Length;
        var rented = expectedLength > 0 ? ArrayPool<byte>.Shared.Rent(expectedLength) : [];
        try
        {
            var totalRead = 0;
            await using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (totalRead < expectedLength)
                {
                    var count = await input.ReadAsync(rented.AsMemory(totalRead, expectedLength - totalRead), cancellationToken).ConfigureAwait(false);
                    if (count == 0) { break; }
                    totalRead += count;
                }
            }

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(rented.AsSpan(0, totalRead), hash);
            if (_segmentCache.TryGetValue(file.Path, out var cached)
                && totalRead == expectedLength
                && cached.ContentHash.AsSpan().SequenceEqual(hash))
            {
                _segmentLru.Remove(cached.LruNode);
                _segmentLru.AddFirst(cached.LruNode);
                AppendBounded(frames, cached.Frames);
                return cached.Valid;
            }

            var segmentFrames = new List<MonitoringHistoryFrame>();
            var valid = ParseSegment(rented.AsSpan(0, totalRead), expectedLength, file, segmentFrames);
            var materialized = segmentFrames.ToArray();
            if (totalRead == expectedLength && materialized.Length < MonitoringHistoryProjection.MaximumInputFrames)
            {
                CacheSegment(file.Path, hash.ToArray(), totalRead, materialized, valid);
            }
            AppendBounded(frames, materialized);
            return valid;
        }
        finally
        {
            if (rented.Length > 0) { ArrayPool<byte>.Shared.Return(rented); }
        }
    }

    private bool ParseSegment(ReadOnlySpan<byte> bytes, int expectedLength, OwnedFile file, List<MonitoringHistoryFrame> frames)
    {
        var valid = true;
        var lastGoodIndex = -1;
        var hourStart = file.Hour.UtcDateTime;
        var hourEnd = hourStart.AddHours(1);
        var remaining = bytes;
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf((byte)'\n');
            if (newline < 0)
            {
                InvalidateAdjacentFrame(frames, lastGoodIndex);
                valid = false;
                break;
            }
            var lineSpan = remaining[..newline];
            remaining = remaining[(newline + 1)..];
            if (lineSpan.Length == 0 || lineSpan.Length + 1 > _limits.RecordBytes)
            {
                valid = false;
                InvalidateAdjacentFrame(frames, lastGoodIndex);
                continue;
            }
            try
            {
                var frame = JsonSerializer.Deserialize<MonitoringHistoryFrame>(lineSpan, AgentProtocol.JsonOptions)
                    ?? throw new JsonException("Null history record.");
                ValidateFrame(frame);
                var utc = frame.ObservedAt.UtcDateTime;
                if (utc < hourStart || utc >= hourEnd)
                {
                    throw new JsonException("History record belongs to another chunk hour.");
                }
                if (frames.Count < MonitoringHistoryProjection.MaximumInputFrames)
                {
                    lastGoodIndex = frames.Count;
                    frames.Add(InternFrame(frame));
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                valid = false;
                InvalidateAdjacentFrame(frames, lastGoodIndex);
            }
        }
        if (bytes.Length != expectedLength)
        {
            InvalidateAdjacentFrame(frames, lastGoodIndex);
            valid = false;
        }
        return valid;
    }

    private static void AppendBounded(List<MonitoringHistoryFrame> target, MonitoringHistoryFrame[] source)
    {
        var remaining = MonitoringHistoryProjection.MaximumInputFrames - target.Count;
        if (remaining <= 0 || source.Length == 0) { return; }
        if (source.Length <= remaining)
        {
            target.AddRange(source);
            return;
        }
        for (var i = 0; i < remaining; i++)
        {
            target.Add(source[i]);
        }
    }

    private static void InvalidateAdjacentFrame(List<MonitoringHistoryFrame> frames, int index)
    {
        if (index < 0) { return; }
        var frame = frames[index];
        frames[index] = frame with
        {
            Host = null,
            RemoteHosts = null,
            Tasks = frame.Tasks.Select(task => task with
            {
                Health = ObservationHealth.Unknown, CpuPercent = null, WorkingSetBytes = null, PhysicalMemoryBytes = null,
            }).ToArray(),
        };
    }

    private async Task PruneAsync(int incomingBytes, int incomingFiles, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var files = OwnedFiles().OrderBy(file => file.Hour > now ? 0 : 1)
            .ThenBy(file => file.Hour).ThenBy(file => file.Path, StringComparer.Ordinal).ToList();
        var total = files.Sum(file => file.Length);
        var count = files.Count;
        var victims = new List<OwnedFile>();
        var cutoff = now - MonitoringHistoryProjection.MaximumWindow;
        foreach (var file in files)
        {
            if (file.Path == _currentFile) { continue; }
            if (file.Hour > now.AddHours(1) || file.Hour < cutoff
                || total + incomingBytes > _limits.MaximumBytes || count + incomingFiles > _limits.MaximumFiles)
            {
                victims.Add(file);
                total -= file.Length;
                count--;
            }
        }
        if (total + incomingBytes > _limits.MaximumBytes || count + incomingFiles > _limits.MaximumFiles)
        {
            throw new IOException("History storage capacity is exhausted.");
        }
        if (victims.Count == 0) { return; }
        var latest = victims.Max(file => file.Hour.AddHours(1));
        if (_prunedThrough is { } prior && prior > latest) { latest = prior; }
        await SaveMarkerAsync(new PrunedMarker(1, _rootId, latest, _highWaterAt, _ambiguousFrom, _ambiguousThrough,
            _epochSequence, _invalidBeforeEpoch), cancellationToken).ConfigureAwait(false);
        foreach (var file in victims)
        {
            VerifyFile(file.Path);
            File.Delete(file.Path);
            RemoveCachedSegment(file.Path);
        }
    }

    private async Task SaveMarkerAsync(PrunedMarker marker, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (marker.Through is { } through && through > now) { marker = marker with { Through = now }; }
        var temporary = Path.Combine(_directory, $"pruned-v1.{Guid.NewGuid():N}.tmp");
        try
        {
            VerifyFile(temporary);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, marker, AgentProtocol.JsonOptions, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var target = Path.Combine(_directory, "pruned-v1.json");
            VerifyFile(target);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, target, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 5 && exception is UnauthorizedAccessException or IOException)
                {
                    await Task.Delay(15 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            }
            _prunedThrough = marker.Through;
            _highWaterAt = marker.HighWaterAt;
            _ambiguousFrom = marker.AmbiguousFrom;
            _ambiguousThrough = marker.AmbiguousThrough;
            _epochSequence = marker.EpochSequence;
            _invalidBeforeEpoch = marker.InvalidBeforeEpoch;
        }
        finally
        {
            VerifyFile(temporary);
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }

    private List<OwnedFile> OwnedFiles()
    {
        var files = new List<OwnedFile>();
        foreach (var path in EnumerateFiles())
        {
            var match = FilePattern.Match(Path.GetFileName(path));
            if (!match.Success) { continue; }
            if (!DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyyMMddHH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hour))
            {
                throw new IOException("History chunk has an invalid hour.");
            }
            VerifyFile(path);
            files.Add(new OwnedFile(path, hour, new FileInfo(path).Length));
        }
        return files;
    }

    private IEnumerable<string> EnumerateFiles()
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (++count > 4096) { throw new IOException("History directory entry limit reached."); }
            yield return path;
        }
    }

    private void EnsureDirectory()
    {
        var current = _root;
        foreach (var component in new[] { ".autorun-state", _userKey, "monitoring", _rootId })
        {
            current = Path.Combine(current, component);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("History state directory must not be a reparse point.");
            }
            Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("History state directory changed into a reparse point.");
            }
        }
    }

    private void VerifyFile(string path)
    {
        // Public operations verify the ancestor chain once while owning the gate;
        // per-entry checks must not re-create/stat that same chain hundreds of times.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _directory, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("History file is outside its owned directory.");
        }
        try
        {
            // File.Exists follows links and is false for a dangling link; inspect the entry itself.
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                throw new UnauthorizedAccessException("History files must be regular non-reparse files.");
            }
        }
        catch (FileNotFoundException) { }
    }

    private void ValidateFrame(MonitoringHistoryFrame frame)
    {
        // Every valid row already carries a 64-byte identity. Keep a defensive parse
        // bound, but let the existing record-byte budget admit all fitting tasks.
        if (frame.SchemaVersion != 1 || frame.RootId != _rootId || !Guid.TryParseExact(frame.AgentInstanceId, "N", out _)
            || frame.EpochSequence < 0 || frame.EpochSequence > _epochSequence
            || frame.Tasks is null || frame.Tasks.Count > _limits.RecordBytes / 64)
        {
            throw new ArgumentException("Invalid history frame identity/schema/task count.");
        }
        if (!ValidHostSample(frame.Host))
        {
            throw new ArgumentException("Invalid history host observation.");
        }
        if (frame.RemoteHosts is { } remoteHosts)
        {
            if (remoteHosts.Count > 256) { throw new ArgumentException("Invalid history remote host count."); }
            var remoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var remote in remoteHosts)
            {
                if (remote is null || string.IsNullOrWhiteSpace(remote.HostId) || remote.HostId.Length > 256
                    || remote.HostId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
                    || !remoteIds.Add(remote.HostId)
                    || remote.HostIdentity is null || remote.HostIdentity.Length != 64
                    || remote.HostIdentity.Any(character => !char.IsAsciiHexDigitLower(character))
                    || remote.SessionEpoch < 0
                    || !ValidHostSample(remote.Host))
                {
                    throw new ArgumentException("Invalid history remote host observation.");
                }
            }
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in frame.Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.TaskName) || task.TaskName.Length > 512
                || !names.Add(task.TaskName) || task.TaskIdentity is null || task.TaskIdentity.Length != 64
                || task.TaskIdentity.Any(character => !char.IsAsciiHexDigitLower(character)) || !Enum.IsDefined(task.Health)
                || task.CpuPercent is { } cpu && (!double.IsFinite(cpu) || cpu is < 0 or > 100)
                || task.WorkingSetBytes is < 0 || task.PhysicalMemoryBytes is < 0
                || InvalidRate(task.DiskReadBytesPerSecond) || InvalidRate(task.DiskWriteBytesPerSecond)
                || InvalidRate(task.NetworkReceiveBytesPerSecond) || InvalidRate(task.NetworkSendBytesPerSecond)
                || task.ResourceIdentity is { } resourceIdentity && (resourceIdentity.Length != 64
                    || resourceIdentity.Any(character => !char.IsAsciiHexDigitLower(character)))
                || task.ValidUntil is { } until && (until < frame.ObservedAt || until > frame.ObservedAt + MonitoringHistoryProjection.SamplingInterval))
            {
                throw new ArgumentException("Invalid history task observation.");
            }
        }
    }

    private static bool ValidHostSample(HostHistorySample? sample) => sample is null ||
        !((sample.CpuPercent is { } hostCpu && (!double.IsFinite(hostCpu) || hostCpu is < 0 or > 100))
        || sample.MemoryUsedBytes is < 0 || sample.PhysicalMemoryBytes is < 0
        || (sample.MemoryUsedBytes is { } used && sample.PhysicalMemoryBytes is { } total && used > total)
        || InvalidRate(sample.DiskReadBytesPerSecond) || InvalidRate(sample.DiskWriteBytesPerSecond)
        || InvalidRate(sample.NetworkReceiveBytesPerSecond) || InvalidRate(sample.NetworkSendBytesPerSecond));

    private static bool InvalidRate(double? value) => value is { } rate && (!double.IsFinite(rate) || rate < 0);

    private void CacheSegment(string path, byte[] hash, int rawBytes, MonitoringHistoryFrame[] frames, bool valid)
    {
        RemoveCachedSegment(path);
        while (_segmentLru.Last is { } oldest
            && (_segmentCache.Count >= MaximumCachedSegments
                || (_segmentCache.Count >= MinimumRetainedSegments
                    && _cachedSegmentBytes + rawBytes > MaximumCachedSegmentBytes)))
        {
            RemoveCachedSegment(oldest.Value);
        }

        var node = _segmentLru.AddFirst(path);
        _segmentCache[path] = new CachedSegment(hash, rawBytes, frames, valid, node);
        _cachedSegmentBytes += rawBytes;
    }

    private void RemoveCachedSegment(string path)
    {
        if (!_segmentCache.Remove(path, out var existing))
        {
            return;
        }

        _segmentLru.Remove(existing.LruNode);
        _cachedSegmentBytes = Math.Max(0, _cachedSegmentBytes - existing.RawBytes);
    }

    private MonitoringHistoryFrame InternFrame(MonitoringHistoryFrame frame)
    {
        var tasks = frame.Tasks;
        if (tasks.Count > 0)
        {
            var internedTasks = new TaskHistorySample[tasks.Count];
            for (var i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i];
                internedTasks[i] = task with
                {
                    TaskName = Intern(task.TaskName),
                    TaskIdentity = Intern(task.TaskIdentity),
                    ResourceIdentity = InternOrNull(task.ResourceIdentity),
                };
            }
            tasks = internedTasks;
        }

        var remotes = frame.RemoteHosts;
        if (remotes is { Count: > 0 })
        {
            var internedRemotes = new RemoteHostHistorySample[remotes.Count];
            for (var i = 0; i < remotes.Count; i++)
            {
                var remote = remotes[i];
                internedRemotes[i] = remote with
                {
                    HostId = Intern(remote.HostId),
                    HostIdentity = Intern(remote.HostIdentity),
                };
            }
            remotes = internedRemotes;
        }

        return frame with
        {
            RootId = Intern(frame.RootId),
            AgentInstanceId = Intern(frame.AgentInstanceId),
            Tasks = tasks,
            RemoteHosts = remotes,
        };
    }

    private string Intern(string value)
    {
        if (_internPool.TryGetValue(value, out var interned))
        {
            return interned;
        }
        if (_internPool.Count < MaximumInternPoolEntries)
        {
            _internPool[value] = value;
        }
        return value;
    }

    private string? InternOrNull(string? value) => value is null ? null : Intern(value);

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _segmentCache.Clear();
        _segmentLru.Clear();
        _internPool.Clear();
        _cachedSegmentBytes = 0;
        _writerLease.Dispose();
        _gate.Dispose();
    }

    private sealed record CachedSegment(byte[] ContentHash, int RawBytes, MonitoringHistoryFrame[] Frames, bool Valid, LinkedListNode<string> LruNode);
    private sealed record OwnedFile(string Path, DateTimeOffset Hour, long Length);
    private sealed record PrunedMarker(int SchemaVersion, string RootId, DateTimeOffset? Through,
        DateTimeOffset? HighWaterAt = null, DateTimeOffset? AmbiguousFrom = null, DateTimeOffset? AmbiguousThrough = null,
        long EpochSequence = 0, long InvalidBeforeEpoch = 0);
}
