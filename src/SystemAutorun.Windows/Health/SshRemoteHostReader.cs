using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.Windows.Health;

public sealed class SshRemoteHostReader : IRemoteHostReader, IRemoteHostAdaptiveStreamSource
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private const int MaximumErrorCharacters = 4096;
    private const int MinimumIntervalMilliseconds = 250;
    private const int MaximumIntervalMilliseconds = 60_000;

    private const string WindowsProbeCore = """
        $ErrorActionPreference='Stop'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $OutputEncoding=[Console]::OutputEncoding
        $os=Get-CimInstance Win32_OperatingSystem
        $cs=Get-CimInstance Win32_ComputerSystem
        $logical=[int]$cs.NumberOfLogicalProcessors
        $totalMem=[int64]$cs.TotalPhysicalMemory
        $caption=$os.Caption
        $boot=$os.LastBootUpTime
        function Read-Nics {
          $list=@()
          $rx=[int64]0; $tx=[int64]0; $primary=$null
          foreach($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()){
            if($ni.OperationalStatus -ne 'Up'){ continue }
            $t=$ni.NetworkInterfaceType.ToString()
            if($t -eq 'Loopback' -or $t -eq 'Tunnel'){ continue }
            $st=$ni.GetIPv4Statistics()
            $ua=$ni.GetIPProperties().UnicastAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' -and -not $_.Address.ToString().StartsWith('169.254.') } | Select-Object -First 1
            $ip=if($ua){$ua.Address.IPAddressToString}else{'--'}
            if($ip -ne '--' -and -not $primary){ $primary=$ip }
            $list+=[pscustomobject]@{name=$ni.Name;address=$ip;bytesReceived=[int64]$st.BytesReceived;bytesSent=[int64]$st.BytesSent}
            $rx+=[int64]$st.BytesReceived; $tx+=[int64]$st.BytesSent
            if($list.Count -ge 32){ break }
          }
          [pscustomobject]@{list=$list;rx=$rx;tx=$tx;primary=if($primary){$primary}else{'--'}}
        }
        function Read-Disks {
          @([System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | Sort-Object Name | ForEach-Object {
            [pscustomobject]@{name=$_.Name.TrimEnd('\');label=if([string]::IsNullOrWhiteSpace($_.VolumeLabel)){$_.Name}else{$_.VolumeLabel};totalBytes=[int64]$_.TotalSize;freeBytes=[int64]$_.AvailableFreeSpace}
          })
        }
        function Emit-Frame($prevNics, $dtSec, $elapsedMs, $disks, $refreshOs){
          if($refreshOs){ $script:os=Get-CimInstance Win32_OperatingSystem }
          $cpu=Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
          $system=Get-CimInstance Win32_PerfFormattedData_PerfOS_System
          $disk=Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'"
          $cur=Read-Nics
          $rxRate=$null; $txRate=$null
          if($null -ne $prevNics -and $dtSec -gt 0 -and $prevNics.list.Count -gt 0 -and $prevNics.list.Count -eq $cur.list.Count){
            $ok=$true; $dr=[double]0; $ds=[double]0
            $map=@{}; foreach($p in $prevNics.list){ $map[$p.name]=$p }
            foreach($c in $cur.list){
              if(-not $map.ContainsKey($c.name)){ $ok=$false; break }
              $p=$map[$c.name]
              if($c.bytesReceived -lt $p.bytesReceived -or $c.bytesSent -lt $p.bytesSent){ $ok=$false; break }
              $dr+=($c.bytesReceived-$p.bytesReceived); $ds+=($c.bytesSent-$p.bytesSent)
            }
            if($ok){ $rxRate=[Math]::Round($dr/$dtSec,2); $txRate=[Math]::Round($ds/$dtSec,2) }
          }
          $cpuVal=[Math]::Max(0.0,[Math]::Min(100.0,[double]$cpu.PercentProcessorTime))
          $diskR=if($null -ne $disk){[double][Math]::Max(0,$disk.DiskReadBytesPersec)}else{$null}
          $diskW=if($null -ne $disk){[double][Math]::Max(0,$disk.DiskWriteBytesPersec)}else{$null}
          $obj=[pscustomobject]@{schema=1;platform='windows';hostname=$env:COMPUTERNAME;os=$caption;uptimeSeconds=[int64]((Get-Date)-$boot).TotalSeconds;cpuPercent=[Math]::Round($cpuVal,2);totalMemoryBytes=$totalMem;availableMemoryBytes=[int64]$script:os.FreePhysicalMemory*1KB;logicalProcessorCount=$logical;processCount=[int]$script:os.NumberOfProcesses;threadCount=[int]$system.Threads;networkAddress=$cur.primary;bytesReceived=[int64]$cur.rx;bytesSent=[int64]$cur.tx;diskReadBytesPerSecond=$diskR;diskWriteBytesPerSecond=$diskW;networkReceiveBytesPerSecond=$rxRate;networkSendBytesPerSecond=$txRate;networks=$cur.list;disks=$disks}
          if($null -ne $elapsedMs){ $obj | Add-Member -NotePropertyName elapsedMs -NotePropertyValue ([int64]$elapsedMs) }
          [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 5))
          [Console]::Out.Flush()
          $cur
        }
        """;

    private const string WindowsProbeScript = WindowsProbeCore + "\n" + """
        $disks=Read-Disks
        $sw=[System.Diagnostics.Stopwatch]::StartNew()
        $prev=Read-Nics
        Start-Sleep -Milliseconds 150
        $dt=$sw.Elapsed.TotalSeconds
        $null=Emit-Frame $prev $dt $null $disks $true
        """;

    private const string LinuxProbeCore = """
        import fcntl, json, os, socket, struct, time
        FS_TYPES = {'ext2', 'ext3', 'ext4', 'xfs', 'btrfs', 'zfs', 'f2fs', 'vfat', 'exfat', 'ntfs', 'ntfs3', 'fuseblk', 'overlay', 'reiserfs', 'jfs'}
        SKIP_PREFIX = ('/proc', '/sys', '/dev', '/run', '/snap', '/var/lib/docker', '/var/lib/containerd', '/boot/efi')
        SKIP_NET_PREFIX = ('veth', 'docker', 'br-', 'cni', 'flannel', 'cali', 'virbr')
        def decode_mount(text):
            out = []
            i = 0
            n = len(text)
            while i < n:
                if text[i] == '\\' and i + 3 < n and all(c in '01234567' for c in text[i + 1:i + 4]):
                    out.append(chr(int(text[i + 1:i + 4], 8)))
                    i += 4
                else:
                    out.append(text[i])
                    i += 1
            return ''.join(out)
        def read_mem():
            values = {}
            with open('/proc/meminfo', encoding='utf-8') as handle:
                for line in handle:
                    key, value = line.split(':', 1)
                    parts = value.strip().split()
                    if parts:
                        mult = 1024 if len(parts) > 1 and parts[1].lower() == 'kb' else 1
                        values[key] = int(parts[0]) * mult
            return values.get('MemTotal', 0), values.get('MemAvailable', values.get('MemFree', 0))
        def read_cpu_raw():
            with open('/proc/stat', encoding='utf-8') as handle:
                values = [int(value) for value in handle.readline().split()[1:]]
            return sum(values), values[3] + (values[4] if len(values) > 4 else 0)
        def whole_blocks():
            try:
                names = set(os.listdir('/sys/block'))
            except OSError:
                return None
            phys = {n for n in names if not n.startswith(('loop', 'ram', 'zram', 'sr', 'fd', 'dm-'))}
            return phys if phys else {n for n in names if not n.startswith(('loop', 'ram', 'zram', 'sr', 'fd'))}
        def read_disk_raw(blocks):
            if blocks is not None and not blocks:
                return None
            r = w = matched = 0
            try:
                with open('/proc/diskstats', encoding='utf-8') as handle:
                    for line in handle:
                        parts = line.split()
                        if len(parts) < 14:
                            continue
                        name = parts[2]
                        if blocks is not None:
                            if name not in blocks:
                                continue
                        elif name.startswith(('loop', 'ram', 'zram', 'sr', 'fd')) or name[-1].isdigit():
                            continue
                        r += int(parts[5]) * 512
                        w += int(parts[9]) * 512
                        matched += 1
            except OSError:
                return None
            return (r, w) if matched > 0 else None
        def iface_ipv4(sock, ifname):
            try:
                ifreq = struct.pack('256s', ifname.encode('utf-8')[:15])
                res = fcntl.ioctl(sock.fileno(), 0x8915, ifreq)
                return socket.inet_ntoa(res[20:24])
            except OSError:
                return '--'
        def read_net_addrs(names):
            addrs = {}
            sock = None
            try:
                sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                for n in names:
                    addrs[n] = iface_ipv4(sock, n)
            except OSError:
                pass
            finally:
                if sock is not None:
                    try:
                        sock.close()
                    except OSError:
                        pass
            return addrs
        def read_net_raw():
            nics = {}
            with open('/proc/net/dev', encoding='utf-8') as handle:
                for line in handle.readlines()[2:]:
                    name, values = line.split(':', 1)
                    name = name.strip()
                    if name == 'lo' or name.startswith(SKIP_NET_PREFIX):
                        continue
                    try:
                        with open(f'/sys/class/net/{name}/operstate', encoding='utf-8') as state:
                            if state.read().strip() == 'down':
                                continue
                    except OSError:
                        pass
                    fields = values.split()
                    nics[name] = (int(fields[0]), int(fields[8]))
            return nics
        def primary_ip(addrs, nics):
            for n in sorted(nics.keys(), key=lambda k: -(nics[k][0] + nics[k][1])):
                ip = addrs.get(n)
                if ip and ip != '--' and not ip.startswith('127.'):
                    return ip
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                try:
                    s.connect(('1.1.1.1', 80))
                    ip = s.getsockname()[0]
                    if ip and not ip.startswith('127.'):
                        return ip
                finally:
                    s.close()
            except OSError:
                pass
            try:
                return socket.gethostbyname(socket.gethostname())
            except OSError:
                return '--'
        def read_disks():
            seen = set()
            candidates = []
            try:
                with open('/proc/mounts', encoding='utf-8') as handle:
                    for line in handle:
                        parts = line.split()
                        if len(parts) < 3:
                            continue
                        dev, mount, fstype = parts[0], decode_mount(parts[1]), parts[2]
                        if mount != '/' and (fstype not in FS_TYPES or not dev.startswith('/') or dev.startswith('/dev/loop') or any(mount == p or mount.startswith(p + '/') for p in SKIP_PREFIX)):
                            continue
                        candidates.append((mount, fstype))
            except OSError:
                candidates = [('/', 'rootfs')]
            candidates.sort(key=lambda x: (0 if x[0] == '/' else 1, len(x[0]), x[0]))
            out = []
            for mount, fstype in candidates:
                try:
                    st = os.stat(mount)
                    if st.st_dev in seen:
                        continue
                    u = os.statvfs(mount)
                    tot = u.f_blocks * u.f_frsize
                    if tot <= 0:
                        continue
                    seen.add(st.st_dev)
                    out.append({'name': mount, 'label': fstype, 'totalBytes': tot, 'freeBytes': u.f_bavail * u.f_frsize})
                    if len(out) >= 32:
                        break
                except OSError:
                    pass
            if not out:
                u = os.statvfs('/')
                out.append({'name': '/', 'label': '/', 'totalBytes': u.f_blocks * u.f_frsize, 'freeBytes': u.f_bavail * u.f_frsize})
            return out
        def read_uptime():
            with open('/proc/uptime', encoding='utf-8') as handle:
                return int(float(handle.read().split()[0]))
        def read_threads():
            with open('/proc/loadavg', encoding='utf-8') as handle:
                return int(handle.read().split()[3].split('/')[1])
        def read_proc_counts():
            return sum(1 for name in os.listdir('/proc') if name.isdigit()), read_threads()
        os_name = 'Linux'
        try:
            with open('/etc/os-release', encoding='utf-8') as handle:
                data = dict(line.rstrip().split('=', 1) for line in handle if '=' in line)
            os_name = data.get('PRETTY_NAME', os_name).strip('"')
        except OSError:
            pass
        hostname = socket.gethostname()
        logical_processor_count = os.cpu_count() or 0
        blocks = whole_blocks()
        def sample_and_build(prev_t, prev_cpu, prev_disk, prev_nics, addrs, disks, proc_counts, elapsed_ms):
            now_t = time.monotonic()
            dt = max(1e-6, now_t - prev_t)
            total2, idle2 = read_cpu_raw()
            delta = max(1, total2 - prev_cpu[0])
            cpu_pct = max(0.0, min(100.0, (delta - (idle2 - prev_cpu[1])) * 100.0 / delta))
            total_memory, available_memory = read_mem()
            cur_disk = read_disk_raw(blocks)
            dr = dw = None
            if prev_disk is not None and cur_disk is not None and cur_disk[0] >= prev_disk[0] and cur_disk[1] >= prev_disk[1]:
                dr = round((cur_disk[0] - prev_disk[0]) / dt, 2)
                dw = round((cur_disk[1] - prev_disk[1]) / dt, 2)
            cur_nics = read_net_raw()
            rx = sum(v[0] for v in cur_nics.values())
            tx = sum(v[1] for v in cur_nics.values())
            rx_rate = tx_rate = None
            if prev_nics and set(prev_nics.keys()) == set(cur_nics.keys()):
                if all(cur_nics[k][0] >= prev_nics[k][0] and cur_nics[k][1] >= prev_nics[k][1] for k in cur_nics):
                    rx_rate = round((rx - sum(v[0] for v in prev_nics.values())) / dt, 2)
                    tx_rate = round((tx - sum(v[1] for v in prev_nics.values())) / dt, 2)
            top_nics = sorted(cur_nics.items(), key=lambda kv: -(kv[1][0] + kv[1][1]))[:32]
            top_nics.sort(key=lambda kv: (0 if addrs.get(kv[0], '--') != '--' else 1, kv[0]))
            nic_rows = [{'name': k, 'address': addrs.get(k, '--'), 'bytesReceived': v[0], 'bytesSent': v[1]} for k, v in top_nics]
            process_count, thread_count = proc_counts
            payload = {
                'schema': 1, 'platform': 'linux', 'hostname': hostname, 'os': os_name,
                'uptimeSeconds': read_uptime(), 'cpuPercent': round(cpu_pct, 2),
                'totalMemoryBytes': total_memory, 'availableMemoryBytes': available_memory,
                'logicalProcessorCount': logical_processor_count, 'processCount': process_count, 'threadCount': thread_count,
                'networkAddress': primary_ip(addrs, cur_nics), 'bytesReceived': rx, 'bytesSent': tx,
                'diskReadBytesPerSecond': dr, 'diskWriteBytesPerSecond': dw,
                'networkReceiveBytesPerSecond': rx_rate, 'networkSendBytesPerSecond': tx_rate,
                'networks': nic_rows, 'disks': disks,
            }
            if elapsed_ms is not None:
                payload['elapsedMs'] = int(elapsed_ms)
            print(json.dumps(payload, separators=(',', ':')), flush=True)
            return now_t, (total2, idle2), cur_disk, cur_nics
        """;

    private const string LinuxProbeScript = LinuxProbeCore + "\n" + """
        prev_t = time.monotonic()
        prev_cpu = read_cpu_raw()
        prev_disk = read_disk_raw(blocks)
        prev_nics = read_net_raw()
        disks = read_disks()
        addrs = read_net_addrs(prev_nics.keys())
        proc_counts = read_proc_counts()
        time.sleep(0.15)
        sample_and_build(prev_t, prev_cpu, prev_disk, prev_nics, addrs, disks, proc_counts, None)
        """;

    private readonly HiddenProcessRunner _runner = new();

    public async Task<RemoteHostStatusSnapshot> ReadAsync(
        RemoteHostDefinition definition,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoteHostDefinitionValidator.Validate(definition);
            if (!definition.Enabled) { return RemoteHostStatusSnapshot.Offline(definition, "host_disabled"); }
            var ssh = ResolveSshExecutable();
            using var leases = SshLeaseBundle.Open(definition);
            await using var proxyTunnel = SshLocalProxyTunnel.StartIfConfigured(definition, cancellationToken);
            var result = await _runner.RunAsync(
                ssh,
                BuildArguments(definition, proxyTunnel?.LocalPort),
                TimeSpan.FromSeconds(definition.CommandTimeoutSeconds),
                cancellationToken, maximumOutputCharacters: MaximumOutputCharacters).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var errorMessage = string.IsNullOrWhiteSpace(result.StandardError)
                    ? proxyTunnel?.TunnelError is { } tunnelErr
                        ? $"SSH proxy tunnel failed: {tunnelErr}"
                        : $"SSH exited with code {result.ExitCode}."
                    : proxyTunnel?.TunnelError is { } tunnelDetail
                        ? $"{result.StandardError} ({tunnelDetail})"
                        : result.StandardError;
                throw new InvalidOperationException(Redact(errorMessage, definition));
            }

            return ParseSnapshot(definition, result.StandardOutput);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            return RemoteHostStatusSnapshot.Offline(definition, Redact(exception.Message, definition));
        }
    }

    public IAsyncEnumerable<RemoteHostStatusSnapshot> StreamAsync(
        RemoteHostDefinition definition,
        TimeSpan samplingInterval,
        CancellationToken cancellationToken = default) =>
        StreamCoreAsync(definition, samplingInterval, samplingIntervalUpdates: null, cancellationToken);

    public IAsyncEnumerable<RemoteHostStatusSnapshot> StreamAdaptiveAsync(
        RemoteHostDefinition definition,
        TimeSpan initialSamplingInterval,
        IAsyncEnumerable<TimeSpan> samplingIntervalUpdates,
        CancellationToken cancellationToken = default) =>
        StreamCoreAsync(definition, initialSamplingInterval, samplingIntervalUpdates, cancellationToken);

    private async IAsyncEnumerable<RemoteHostStatusSnapshot> StreamCoreAsync(
        RemoteHostDefinition definition,
        TimeSpan samplingInterval,
        IAsyncEnumerable<TimeSpan>? samplingIntervalUpdates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RemoteHostDefinitionValidator.Validate(definition);
        if (!definition.Enabled) { yield break; }
        var intervalMs = ClampIntervalMilliseconds(samplingInterval);
        var ssh = ResolveSshExecutable();
        SshLeaseBundle? leases = SshLeaseBundle.Open(definition);
        SshLocalProxyTunnel? proxyTunnel = null;
        Process? process = null;
        var stderrBuffer = new StringBuilder();
        Task? stderrTask = null;
        Task? intervalTask = null;
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            proxyTunnel = SshLocalProxyTunnel.StartIfConfigured(definition, streamCts.Token);
            process = _runner.StartStreaming(ssh, BuildStreamArguments(definition, intervalMs, proxyTunnel?.LocalPort));
            stderrTask = DrainErrorAsync(process.StandardError, stderrBuffer, streamCts.Token);
            if (samplingIntervalUpdates is not null)
            {
                intervalTask = PumpSamplingIntervalUpdatesAsync(
                    process.StandardInput,
                    intervalMs,
                    samplingIntervalUpdates,
                    streamCts.Token);
            }

            var firstTimeout = TimeSpan.FromSeconds(Math.Max(definition.ConnectTimeoutSeconds + 8, definition.CommandTimeoutSeconds));
            var nextTimeout = TimeSpan.FromSeconds(Math.Max(25, definition.CommandTimeoutSeconds));
            DateTimeOffset? anchorUtc = null;
            DateTimeOffset? lastCapturedAt = null;
            var firstSample = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                using var lineTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lineTimeout.CancelAfter(firstSample ? firstTimeout : nextTimeout);
                string? line;
                try
                {
                    line = await ReadBoundedLineAsync(process.StandardOutput, MaximumOutputCharacters, lineTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(firstSample
                        ? "Timed out waiting for initial remote telemetry frame."
                        : "Remote telemetry stream stalled.");
                }

                if (line is null)
                {
                    if (firstSample)
                    {
                        var exitCode = process.HasExited ? process.ExitCode : -1;
                        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); exitCode = process.ExitCode; } catch { }
                        if (stderrTask is not null) { try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); } catch { } }
                        var rawError = string.IsNullOrWhiteSpace(stderrBuffer.ToString())
                            ? proxyTunnel?.TunnelError is { } tunnelErr
                                ? $"SSH proxy tunnel failed: {tunnelErr}"
                                : $"SSH stream ended before producing telemetry (exit {exitCode})."
                            : proxyTunnel?.TunnelError is { } tunnelDetail
                                ? $"{stderrBuffer} ({tunnelDetail})"
                                : stderrBuffer.ToString();
                        throw new InvalidOperationException(Redact(rawError, definition));
                    }
                    break;
                }

                if (string.IsNullOrWhiteSpace(line)) { continue; }
                var arrivalUtc = DateTimeOffset.UtcNow;
                var parsed = ParseSnapshotCore(definition, line, arrivalUtc);
                if (firstSample)
                {
                    // Release pinned input leases once SSH authentication/handshake succeeds so
                    // known_hosts or key files are not write-locked for the lifetime of the stream.
                    leases?.Dispose();
                    leases = null;
                    firstSample = false;
                }

                var capturedAt = ComputeMonotonicTimestamp(arrivalUtc, parsed.ElapsedMilliseconds, ref anchorUtc, ref lastCapturedAt);
                if (capturedAt is null) { continue; }
                yield return parsed.Snapshot with { CapturedAt = capturedAt.Value };
            }
        }
        finally
        {
            streamCts.Cancel();
            leases?.Dispose();
            if (process is not null)
            {
                await HiddenProcessRunner.TerminateProcessTreeAsync(process).ConfigureAwait(false);
                if (stderrTask is not null)
                {
                    try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
                }
                if (intervalTask is not null)
                {
                    try { await intervalTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
                }
                process.Dispose();
            }
            if (proxyTunnel is not null)
            {
                await proxyTunnel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static int ClampIntervalMilliseconds(TimeSpan samplingInterval) =>
        (int)Math.Clamp(Math.Round(samplingInterval.TotalMilliseconds), MinimumIntervalMilliseconds, MaximumIntervalMilliseconds);

    private static async Task PumpSamplingIntervalUpdatesAsync(
        StreamWriter stdin,
        int currentMs,
        IAsyncEnumerable<TimeSpan> updates,
        CancellationToken token)
    {
        try
        {
            await foreach (var interval in updates.WithCancellation(token).ConfigureAwait(false))
            {
                var nextMs = ClampIntervalMilliseconds(interval);
                if (nextMs == currentMs)
                {
                    continue;
                }
                currentMs = nextMs;
                await stdin.WriteAsync($"{nextMs.ToString(CultureInfo.InvariantCulture)}\n".AsMemory(), token).ConfigureAwait(false);
                await stdin.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    internal static IReadOnlyList<string> BuildArguments(RemoteHostDefinition definition, int? localProxyPort = null) =>
        BuildArgumentsCore(definition, streamIntervalMs: null, localProxyPort);

    internal static IReadOnlyList<string> BuildStreamArguments(RemoteHostDefinition definition, int intervalMilliseconds, int? localProxyPort = null)
    {
        if (intervalMilliseconds is < MinimumIntervalMilliseconds or > MaximumIntervalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        }
        return BuildArgumentsCore(definition, intervalMilliseconds, localProxyPort);
    }

    private static IReadOnlyList<string> BuildArgumentsCore(RemoteHostDefinition definition, int? streamIntervalMs, int? localProxyPort = null)
    {
        RemoteHostDefinitionValidator.Validate(definition);
        if (localProxyPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(localProxyPort));
        }
        var arguments = new List<string>
        {
            "-T", "-F", "none",
            "-o", "PermitLocalCommand=no", "-o", "ClearAllForwardings=yes",
            "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "Tunnel=no",
            "-o", "ProxyCommand=none", "-o", "ProxyJump=none",
            "-o", "ControlMaster=no", "-o", "ControlPath=none", "-o", "CanonicalizeHostname=no",
            "-o", "UpdateHostKeys=no", "-o", "GlobalKnownHostsFile=none", "-o", "LogLevel=ERROR",
            "-o", "NumberOfPasswordPrompts=0", "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no", "-o", "PreferredAuthentications=publickey",
            "-o", "CertificateFile=none",
            "-o", "BatchMode=yes",
            "-o", $"ConnectTimeout={definition.ConnectTimeoutSeconds}",
            "-o", "ConnectionAttempts=1",
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile=\"{RemoteHostDefinitionValidator.ResolveLocalFilePath(definition.KnownHostsFile).Replace('\\', '/')}\"",
        };
        if (localProxyPort is { } proxyPort)
        {
            arguments.Add("-o");
            arguments.Add($"HostKeyAlias={FormatHostKeyAlias(definition.Host, definition.Port)}");
            arguments.Add("-p");
            arguments.Add(proxyPort.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            arguments.Add("-p");
            arguments.Add(definition.Port.ToString(CultureInfo.InvariantCulture));
        }
        arguments.Add("-l");
        arguments.Add(definition.UserName);
        if (streamIntervalMs is not null)
        {
            arguments.AddRange(["-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=2"]);
        }
        if (definition.Authentication == RemoteAuthenticationMode.PrivateKey)
        {
            arguments.Add("-i");
            arguments.Add(RemoteHostDefinitionValidator.ResolveLocalFilePath(definition.IdentityFile!));
            arguments.AddRange(["-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none"]);
        }
        else
        {
            arguments.AddRange(["-o", "IdentityFile=none", "-o", "IdentitiesOnly=no"]);
        }

        arguments.Add("--");
        arguments.Add(localProxyPort is not null ? "127.0.0.1" : definition.Host);
        arguments.Add(BuildRemoteCommand(definition.Platform, streamIntervalMs));
        return arguments;
    }

    internal static string FormatHostKeyAlias(string host, int port)
    {
        var trimmed = host.Trim();
        var unwrapped = trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2
            ? trimmed[1..^1]
            : trimmed;
        var normalized = unwrapped.ToLowerInvariant();
        return port == 22
            ? normalized
            : $"[{normalized}]:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static RemoteHostStatusSnapshot ParseSnapshot(
        RemoteHostDefinition definition,
        string output) => ParseSnapshotCore(definition, output, DateTimeOffset.UtcNow).Snapshot;

    private static (RemoteHostStatusSnapshot Snapshot, long? ElapsedMilliseconds) ParseSnapshotCore(
        RemoteHostDefinition definition,
        string output,
        DateTimeOffset defaultCapturedAt)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length > MaximumOutputCharacters)
        {
            throw new JsonException("Remote probe returned no JSON.");
        }

        using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.GetProperty("schema").GetInt32() != 1
            || !string.Equals(
                root.GetProperty("platform").GetString(),
                definition.Platform == RemoteHostPlatform.Windows ? "windows" : "linux",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("Remote probe returned an unsupported payload.");
        }

        if (root.TryGetProperty("disks", out var disks) && (disks.ValueKind != JsonValueKind.Array || disks.GetArrayLength() > 64))
        { throw new JsonException("Remote disks exceed the shape budget."); }
        var drives = disks.ValueKind == JsonValueKind.Array
            ? disks.EnumerateArray().Select(drive => new DriveStatusSnapshot(
                CleanText(drive.GetProperty("name").GetString() ?? "?"),
                CleanText(drive.TryGetProperty("label", out var label) ? label.GetString() ?? "" : ""),
                drive.GetProperty("totalBytes").GetInt64(), drive.GetProperty("freeBytes").GetInt64())).ToArray()
            : Array.Empty<DriveStatusSnapshot>();

        IReadOnlyList<NetworkStatusSnapshot>? networkRows = null;
        if (root.TryGetProperty("networks", out var networks) && networks.ValueKind != JsonValueKind.Null)
        {
            if (networks.ValueKind != JsonValueKind.Array || networks.GetArrayLength() > 64)
            { throw new JsonException("Remote networks exceed the shape budget."); }
            networkRows = networks.EnumerateArray().Select(nic =>
            {
                var rx = nic.GetProperty("bytesReceived").GetInt64();
                var tx = nic.GetProperty("bytesSent").GetInt64();
                if (rx < 0 || tx < 0) { throw new JsonException("Remote network counters cannot be negative."); }
                return new NetworkStatusSnapshot(
                    CleanText(nic.GetProperty("name").GetString() ?? "?"),
                    CleanText(nic.TryGetProperty("address", out var addr) ? addr.GetString() ?? "--" : "--"),
                    rx, tx);
            }).ToArray();
        }

        var uptime = root.GetProperty("uptimeSeconds").GetInt64();
        var cpu = root.GetProperty("cpuPercent").GetDouble();
        var totalMemory = root.GetProperty("totalMemoryBytes").GetInt64();
        var availableMemory = root.GetProperty("availableMemoryBytes").GetInt64();
        var diskRead = ReadOptionalNonNegativeRate(root, "diskReadBytesPerSecond");
        var diskWrite = ReadOptionalNonNegativeRate(root, "diskWriteBytesPerSecond");
        var netReceive = ReadOptionalNonNegativeRate(root, "networkReceiveBytesPerSecond");
        var netSend = ReadOptionalNonNegativeRate(root, "networkSendBytesPerSecond");
        long? elapsedMs = root.TryGetProperty("elapsedMs", out var elapsedProp) && elapsedProp.ValueKind != JsonValueKind.Null
            ? elapsedProp.GetInt64()
            : null;
        if (elapsedMs < 0) { throw new JsonException("Remote elapsedMs cannot be negative."); }

        if (uptime < 0 || uptime > TimeSpan.MaxValue.TotalSeconds || !double.IsFinite(cpu) || cpu is < 0 or > 100
            || totalMemory < 0 || availableMemory < 0 || availableMemory > totalMemory
            || drives.Any(drive => drive.TotalBytes < 0 || drive.FreeBytes < 0 || drive.FreeBytes > drive.TotalBytes))
        { throw new JsonException("Remote metrics are outside their valid ranges."); }
        var snapshot = new RemoteHostStatusSnapshot(
            definition.Id,
            definition.Name,
            definition.Platform,
            definition.Host,
            true,
            null,
            defaultCapturedAt,
            CleanText(root.GetProperty("hostname").GetString() ?? definition.Host),
            CleanText(root.TryGetProperty("os", out var os) ? os.GetString() ?? definition.Platform.ToString() : definition.Platform.ToString()),
            TimeSpan.FromSeconds(uptime),
            cpu,
            totalMemory,
            availableMemory,
            drives,
            CleanText(root.TryGetProperty("networkAddress", out var address) ? address.GetString() ?? "--" : "--"),
            root.TryGetProperty("bytesReceived", out var received) ? Math.Max(0, received.GetInt64()) : 0,
            root.TryGetProperty("bytesSent", out var sent) ? Math.Max(0, sent.GetInt64()) : 0,
            root.TryGetProperty("logicalProcessorCount", out var logicalProcessorCount)
                ? Math.Max(0, logicalProcessorCount.GetInt32())
                : 0,
            root.TryGetProperty("processCount", out var processCount) ? Math.Max(0, processCount.GetInt32()) : 0,
            root.TryGetProperty("threadCount", out var threadCount) ? Math.Max(0, threadCount.GetInt32()) : 0,
            diskRead,
            diskWrite,
            netReceive,
            netSend,
            networkRows);
        return (snapshot, elapsedMs);
    }

    private static double? ReadOptionalNonNegativeRate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null) { return null; }
        var value = prop.GetDouble();
        if (!double.IsFinite(value) || value < 0) { throw new JsonException($"Remote rate '{name}' is invalid."); }
        return value;
    }

    private static DateTimeOffset? ComputeMonotonicTimestamp(
        DateTimeOffset arrivalUtc,
        long? elapsedMs,
        ref DateTimeOffset? anchorUtc,
        ref DateTimeOffset? lastCapturedAt)
    {
        DateTimeOffset candidate;
        if (elapsedMs is { } ms)
        {
            if (anchorUtc is null)
            {
                anchorUtc = arrivalUtc.AddMilliseconds(-ms);
                candidate = arrivalUtc;
            }
            else
            {
                candidate = anchorUtc.Value.AddMilliseconds(ms);
                if (candidate > arrivalUtc || arrivalUtc - candidate > TimeSpan.FromSeconds(5))
                {
                    // Re-anchor to minimum observed RTT (when arrival beat projection) or after a genuine long stall.
                    anchorUtc = arrivalUtc.AddMilliseconds(-ms);
                    candidate = arrivalUtc;
                }
            }
        }
        else
        {
            candidate = arrivalUtc;
        }

        if (lastCapturedAt is { } prior && candidate <= prior)
        {
            return null;
        }
        lastCapturedAt = candidate;
        return candidate;
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 1024));
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            var ch = buffer[0];
            if (ch == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            if (builder.Length >= maxChars)
            {
                throw new IOException("Remote telemetry line exceeded its bounded character limit.");
            }
            builder.Append(ch);
        }
    }

    private static async Task DrainErrorAsync(StreamReader reader, StringBuilder target, CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) { break; }
                lock (target)
                {
                    if (target.Length < MaximumErrorCharacters)
                    {
                        var take = Math.Min(read, MaximumErrorCharacters - target.Length);
                        target.Append(buffer, 0, take);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static string CleanText(string value, int maximum = 256) => new(value
        .Where(character => !char.IsControl(character) && char.GetUnicodeCategory(character) != UnicodeCategory.Format)
        .Take(maximum).ToArray());

    private static string BuildRemoteCommand(RemoteHostPlatform platform, int? streamIntervalMs = null)
    {
        string script;
        if (platform == RemoteHostPlatform.Windows)
        {
            script = streamIntervalMs is { } ms
                ? WindowsProbeCore + "\n" + $$"""
                    $interval={{ms}}
                    $disks=Read-Disks
                    $sw=[System.Diagnostics.Stopwatch]::StartNew()
                    $prev=Read-Nics
                    $prevTicks=$sw.ElapsedMilliseconds
                    Start-Sleep -Milliseconds 150
                    $origin=$sw.ElapsedMilliseconds
                    $slowAt=$origin
                    $nextDue=$origin
                    $prev=Emit-Frame $prev (($origin-$prevTicks)/1000.0) 0 $disks $true
                    $prevTicks=$origin
                    $inTask=[Console]::In.ReadLineAsync()
                    $rng=[System.Random]::new()
                    while($true){
                      $jitter=if($interval -ge 2000){ [int]($interval * ($rng.NextDouble() * 0.4 - 0.2)) }else{ 0 }
                      $nextDue+=$interval+$jitter
                      $wait=[int]($nextDue-$sw.ElapsedMilliseconds)
                      if($wait -gt 0 -and -not $inTask.IsCompleted){ [void]$inTask.Wait($wait) }
                      if($inTask.IsCompleted){
                        while($inTask.IsCompleted){
                          $line=$inTask.Result
                          if($null -eq $line){ exit 0 }
                          $parsed=0
                          if([int]::TryParse($line.Trim(), [ref]$parsed)){
                            $interval=[Math]::Max(250, [Math]::Min(60000, $parsed))
                          }
                          $inTask=[Console]::In.ReadLineAsync()
                        }
                        $nextDue=$sw.ElapsedMilliseconds
                      }elseif(($sw.ElapsedMilliseconds - $nextDue) -gt ($interval * 3)){
                        $nextDue=$sw.ElapsedMilliseconds
                      }
                      $nowMs=$sw.ElapsedMilliseconds
                      $refresh=($nowMs-$slowAt) -ge 5000
                      if($refresh){ $disks=Read-Disks; $slowAt=$nowMs }
                      $prev=Emit-Frame $prev (($nowMs-$prevTicks)/1000.0) ($nowMs-$origin) $disks $refresh
                      $prevTicks=$nowMs
                    }
                    """
                : WindowsProbeScript;
        }
        else
        {
            script = streamIntervalMs is { } ms
                ? LinuxProbeCore + "\n" + $$"""
                    import random, select, sys
                    interval = {{(ms / 1000d).ToString("0.###", CultureInfo.InvariantCulture)}}
                    fd = sys.stdin.fileno()
                    os.set_blocking(fd, False)
                    stdin_state = {'buf': b'', 'eof': False}
                    def poll_interval(cur_interval, timeout_sec):
                        r, _, _ = select.select([fd], [], [], max(0.0, timeout_sec))
                        if not r:
                            return cur_interval, False
                        updated = False
                        while True:
                            try:
                                chunk = os.read(fd, 4096)
                            except BlockingIOError:
                                break
                            if not chunk:
                                stdin_state['eof'] = True
                                break
                            stdin_state['buf'] += chunk
                        while b'\n' in stdin_state['buf']:
                            line, stdin_state['buf'] = stdin_state['buf'].split(b'\n', 1)
                            try:
                                new_ms = int(line.decode('utf-8', 'ignore').strip())
                                cur_interval = max(0.25, min(60.0, new_ms / 1000.0))
                                updated = True
                            except ValueError:
                                pass
                        return cur_interval, updated
                    prev_t = time.monotonic()
                    prev_cpu = read_cpu_raw()
                    prev_disk = read_disk_raw(blocks)
                    prev_nics = read_net_raw()
                    disks = read_disks()
                    addrs = read_net_addrs(prev_nics.keys())
                    proc_counts = read_proc_counts()
                    time.sleep(0.15)
                    origin = time.monotonic()
                    slow_at = origin
                    next_due = origin
                    prev_t, prev_cpu, prev_disk, prev_nics = sample_and_build(prev_t, prev_cpu, prev_disk, prev_nics, addrs, disks, proc_counts, 0)
                    try:
                        while not stdin_state['eof']:
                            jitter = interval * random.uniform(-0.2, 0.2) if interval >= 2.0 else 0.0
                            next_due += interval + jitter
                            while True:
                                delay = next_due - time.monotonic()
                                if delay <= 0:
                                    if -delay > interval * 3:
                                        next_due = time.monotonic()
                                    interval, _ = poll_interval(interval, 0.0)
                                    break
                                interval, updated = poll_interval(interval, delay)
                                if stdin_state['eof']:
                                    break
                                if updated:
                                    next_due = time.monotonic()
                                    break
                            if stdin_state['eof']:
                                break
                            now_t = time.monotonic()
                            if now_t - slow_at >= 5.0:
                                disks = read_disks()
                                addrs = read_net_addrs(read_net_raw().keys())
                                proc_counts = read_proc_counts()
                                slow_at = now_t
                            prev_t, prev_cpu, prev_disk, prev_nics = sample_and_build(prev_t, prev_cpu, prev_disk, prev_nics, addrs, disks, proc_counts, round((now_t - origin) * 1000))
                    except (BrokenPipeError, EOFError):
                        pass
                    """
                : LinuxProbeScript;
        }

        var encoded = Convert.ToBase64String(
            (platform == RemoteHostPlatform.Windows ? Encoding.Unicode : Encoding.UTF8).GetBytes(script));
        return platform == RemoteHostPlatform.Windows
            ? $"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}"
            : $"python3 -c \"import base64;exec(base64.b64decode('{encoded}'))\"";
    }

    private static string ResolveSshExecutable()
    {
        var systemSsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "OpenSSH",
            "ssh.exe");
        return File.Exists(systemSsh) ? systemSsh : throw new FileNotFoundException("Pinned system OpenSSH is unavailable; no PATH fallback is allowed.");
    }

    private static string Redact(string message, RemoteHostDefinition definition)
    {
        var redacted = message;
        foreach (var value in new[]
                 {
                     definition.IdentityFile,
                     definition.IdentityFile is { } key ? Environment.ExpandEnvironmentVariables(key) : null,
                     definition.KnownHostsFile,
                     Environment.ExpandEnvironmentVariables(definition.KnownHostsFile ?? ""),
                     definition.ProxyUri,
                     definition.UserName,
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                 }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            redacted = redacted.Replace(value!, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }

        redacted = CleanText(redacted, 600);
        return redacted.Length <= 600 ? redacted : redacted[..600];
    }

    private sealed class SshLeaseBundle : IDisposable
    {
        private readonly SshInputFileLease _knownHosts;
        private readonly SshInputFileLease? _identity;
        private readonly SshInputFileLease? _publicKey;
        private readonly SshInputFileLease? _certificate;
        private readonly SshInputFileLease? _certificatePublic;

        private SshLeaseBundle(SshInputFileLease knownHosts, SshInputFileLease? identity,
            SshInputFileLease? publicKey, SshInputFileLease? certificate, SshInputFileLease? certificatePublic)
        {
            _knownHosts = knownHosts; _identity = identity; _publicKey = publicKey;
            _certificate = certificate; _certificatePublic = certificatePublic;
        }

        internal static SshLeaseBundle Open(RemoteHostDefinition definition)
        {
            var knownHosts = SshInputFileLease.Open(definition.KnownHostsFile, 2 * 1024 * 1024);
            try
            {
                var identity = definition.Authentication == RemoteAuthenticationMode.PrivateKey
                    ? SshInputFileLease.Open(definition.IdentityFile!, 128 * 1024) : null;
                try
                {
                    var pub = identity is not null ? SshInputFileLease.OpenOptional(definition.IdentityFile! + ".pub", 128 * 1024) : null;
                    try
                    {
                        var cert = identity is not null ? SshInputFileLease.OpenOptional(definition.IdentityFile! + "-cert.pub", 128 * 1024) : null;
                        try
                        {
                            var certPub = identity is not null ? SshInputFileLease.OpenOptional(definition.IdentityFile! + "-cert.pub.pub", 128 * 1024) : null;
                            return new(knownHosts, identity, pub, cert, certPub);
                        }
                        catch { cert?.Dispose(); throw; }
                    }
                    catch { pub?.Dispose(); throw; }
                }
                catch { identity?.Dispose(); throw; }
            }
            catch { knownHosts.Dispose(); throw; }
        }

        public void Dispose()
        {
            _certificatePublic?.Dispose();
            _certificate?.Dispose();
            _publicKey?.Dispose();
            _identity?.Dispose();
            _knownHosts.Dispose();
        }
    }
}
