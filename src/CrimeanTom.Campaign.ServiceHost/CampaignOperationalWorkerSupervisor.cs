using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.Transport.V1;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed record CampaignOperationalWorkerOptions(string GrpcUrl);

public sealed class CampaignOperationalWorkerSupervisor(
    CampaignHostPaths paths,
    CampaignCoordinator coordinator,
    CampaignSessionRegistry sessions,
    CampaignOperationalWorkerOptions options) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromMilliseconds(1);
    private const int MaxLogLineCharacters = 16 * 1024;
    private const long MaxLogBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly object _logGate = new();
    private readonly Lazy<WindowsKillOnCloseJob> _job = new(
        () => new WindowsKillOnCloseJob(),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Dictionary<string, Process> _liveProcesses = new(StringComparer.Ordinal);

    public IReadOnlyList<CampaignSimWorkerView> List(CampaignId campaignId)
    {
        lock (_gate)
        {
            return ReadRecords(campaignId)
                .Select(Refresh)
                .OrderBy(item => item.WorkerId, StringComparer.Ordinal)
                .Select(ToView)
                .ToArray();
        }
    }

    public CampaignSimWorkerView Spawn(CampaignId campaignId, CampaignSimWorkerSpawnRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Kind)
            && !string.Equals(request.Kind.Trim(), CampaignWorkerKind.Operational, StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict(
                "unsupported-sim-worker-kind",
                "Only Track A operational workers can be spawned. Track B Sea Power farms are not implemented.");
        }

        var workerId = NormalizeWorkerId(request.WorkerId);
        var capacity = request.Capacity is 0 ? 8 : request.Capacity;
        var reserve = request.Reserve;
        if (capacity is < 1 or > 10_000 || reserve < 0 || reserve > capacity)
        {
            throw new CampaignStoreConflict("invalid-sim-worker-capacity", "Capacity and reserve are invalid.");
        }

        lock (_gate)
        {
            var existing = ReadRecord(campaignId, workerId);
            if (existing is not null && MatchesLiveProcess(existing))
            {
                throw new CampaignStoreConflict(
                    "sim-worker-already-running",
                    "Overlapping spawn for the same worker id is rejected.");
            }

            if (existing is not null)
            {
                if (!TryStopRecord(existing))
                {
                    throw new CampaignStoreConflict(
                        "sim-worker-process-identity-mismatch",
                        "The prior worker PID is still live but no longer matches its fenced process identity.");
                }
            }

            var executable = CampaignOperationalWorkerPaths.Resolve();
            var recordDirectory = RecordDirectory(campaignId, workerId);
            Directory.CreateDirectory(recordDirectory);
            var clientInstanceId = Guid.NewGuid().ToString("N");
            var spawnId = Guid.NewGuid().ToString("N");
            var spawnCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["CRIMEAN_TOM_CAMPAIGN_ID"] = campaignId.ToString();
            start.Environment["CRIMEAN_TOM_CAMPAIGN_GRPC_URL"] = options.GrpcUrl;
            start.Environment["CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE"] = Path.Combine(paths.RunDirectory, "service.token");
            start.Environment["CRIMEAN_TOM_CLIENT_INSTANCE_ID"] = clientInstanceId;
            start.Environment["CRIMEAN_TOM_SIM_WORKER_SPAWN_ID"] = spawnId;
            start.Environment["CRIMEAN_TOM_SIM_WORKER_SPAWN_CREDENTIAL"] = spawnCredential;
            start.Environment["CRIMEAN_TOM_SIM_WORKER_RECORD_DIRECTORY"] = recordDirectory;
            start.Environment["CRIMEAN_TOM_SIM_WORKER_CAPACITY"] = capacity.ToString();
            start.Environment["CRIMEAN_TOM_SIM_WORKER_RESERVE"] = reserve.ToString();

            var stdoutPath = Path.Combine(recordDirectory, "worker.stdout.log");
            var stderrPath = Path.Combine(recordDirectory, "worker.stderr.log");
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => AppendLog(stdoutPath, args.Data);
            process.ErrorDataReceived += (_, args) => AppendLog(stderrPath, args.Data);
            try
            {
                if (!process.Start())
                {
                    throw new CampaignStoreConflict(
                        "sim-worker-spawn-failed",
                        "The operational worker process did not start.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _job.Value.Assign(process);
                if (process.HasExited)
                {
                    throw new CampaignStoreConflict(
                        "sim-worker-exited-during-spawn",
                        "The Operational worker exited before its supervised identity could be recorded.");
                }
                var record = new CampaignSimWorkerRecord(
                    workerId,
                    campaignId.ToString(),
                    clientInstanceId,
                    spawnId,
                    HashCredential(spawnCredential),
                    process.Id,
                    ToUtc(process.StartTime),
                    Path.GetFullPath(executable),
                    capacity,
                    reserve,
                    DateTimeOffset.UtcNow,
                    Alive: true);
                WriteRecord(record);
                _liveProcesses[LiveKey(campaignId, workerId)] = process;
                return ToView(record);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5_000);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    process.Dispose();
                }

                throw;
            }
        }
    }

    public bool ValidateOperationalHello(CampaignId campaignId, CompanionHello hello)
    {
        lock (_gate)
        {
            var matches = ReadRecords(campaignId)
                .Where(record => string.Equals(
                    record.ClientInstanceId,
                    hello.ClientInstanceId,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            var record = matches[0];
            if (!string.Equals(record.SpawnId, hello.OperationalSpawnId, StringComparison.Ordinal)
                || record.Capacity != hello.OperationalEntityCapacity
                || record.Reserve != hello.OperationalReserveCapacity
                || !MatchesLiveProcess(record)
                || string.IsNullOrWhiteSpace(hello.OperationalSpawnCredential))
            {
                return false;
            }

            var suppliedHash = HashCredential(hello.OperationalSpawnCredential);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.SpawnCredentialSha256),
                Encoding.ASCII.GetBytes(suppliedHash));
        }
    }

    public async Task<CampaignSimWorkerView> StopAsync(
        CampaignId campaignId,
        string workerId,
        CancellationToken cancellationToken)
    {
        CampaignSimWorkerRecord record;
        lock (_gate)
        {
            record = ReadRecord(campaignId, NormalizeWorkerId(workerId))
                ?? throw new CampaignStoreConflict("sim-worker-missing", "The operational worker record does not exist.");
        }

        CampaignSimWorkerRecord stopped;
        lock (_gate)
        {
            if (!TryStopRecord(record))
            {
                throw new CampaignStoreConflict(
                    "sim-worker-process-identity-mismatch",
                    "The worker PID is live but no longer matches its fenced process identity.");
            }
            _ = sessions.CloseClient(record.ClientInstanceId, "operational-worker-stopped");
            stopped = record with { Alive = false, Pid = 0 };
            WriteRecord(stopped);
        }

        await coordinator.ReleaseOperationalWorkerAssignmentsAsync(
            campaignId,
            record.ClientInstanceId,
            cancellationToken);
        return ToView(stopped);
    }

    public void ReapStaleChildren()
    {
        lock (_gate)
        {
            var root = Path.Combine(paths.RunDirectory, "sim-workers");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var campaignDirectory in Directory.GetDirectories(root))
            {
                var campaignName = Path.GetFileName(campaignDirectory);
                if (!Guid.TryParseExact(campaignName, "N", out var campaignGuid))
                {
                    continue;
                }

                var campaignId = new CampaignId(campaignGuid);
                foreach (var record in ReadRecords(campaignId))
                {
                    if (MatchesLiveProcess(record))
                    {
                        _ = TryStopRecord(record);
                    }
                }
            }
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            var root = Path.Combine(paths.RunDirectory, "sim-workers");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var campaignDirectory in Directory.GetDirectories(root))
            {
                var campaignName = Path.GetFileName(campaignDirectory);
                if (!Guid.TryParseExact(campaignName, "N", out var campaignGuid))
                {
                    continue;
                }

                foreach (var record in ReadRecords(new CampaignId(campaignGuid)))
                {
                    _ = TryStopRecord(record);
                }
            }
        }
    }

    public void Dispose()
    {
        StopAll();
        if (_job.IsValueCreated)
        {
            _job.Value.Dispose();
        }
    }

    private bool TryStopRecord(CampaignSimWorkerRecord record)
    {
        try
        {
            var key = LiveKey(new CampaignId(Guid.ParseExact(record.CampaignId, "N")), record.WorkerId);
            if (_liveProcesses.TryGetValue(key, out var tracked))
            {
                if (!MatchesLiveProcess(record, tracked))
                {
                    return false;
                }

                _liveProcesses.Remove(key);
                var exited = tracked.HasExited;
                try
                {
                    if (!exited)
                    {
                        tracked.Kill(entireProcessTree: true);
                        exited = tracked.WaitForExit(5_000);
                    }
                }
                finally
                {
                    tracked.Dispose();
                }

                return exited;
            }

            if (record.Pid <= 0)
            {
                return true;
            }
            using var process = Process.GetProcessById(record.Pid);
            if (process.HasExited)
            {
                return true;
            }
            if (!MatchesLiveProcess(record, process))
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string LiveKey(CampaignId campaignId, string workerId) =>
        $"{campaignId}/{workerId}";

    private static string HashCredential(string credential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private CampaignSimWorkerRecord Refresh(CampaignSimWorkerRecord record) =>
        record with { Alive = MatchesLiveProcess(record) };

    private static CampaignSimWorkerView ToView(CampaignSimWorkerRecord record) => new(
        record.WorkerId,
        record.CampaignId,
        record.ClientInstanceId,
        record.SpawnId,
        record.Pid,
        record.ProcessStartTimeUtc,
        record.ExecutablePath,
        record.Capacity,
        record.Reserve,
        record.SpawnedAtUtc,
        record.Alive);

    private static bool MatchesLiveProcess(CampaignSimWorkerRecord record)
    {
        if (record.Pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(record.Pid);
            return MatchesLiveProcess(record, process);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool MatchesLiveProcess(CampaignSimWorkerRecord record, Process process)
    {
        if (process.HasExited || process.Id != record.Pid)
        {
            return false;
        }

        var start = ToUtc(process.StartTime);
        var delta = start > record.ProcessStartTimeUtc
            ? start - record.ProcessStartTimeUtc
            : record.ProcessStartTimeUtc - start;
        if (delta > StartTimeTolerance)
        {
            return false;
        }

        var executable = NativeProcess.GetExecutablePath(process.Id);
        return !string.IsNullOrWhiteSpace(executable)
            && string.Equals(
                Path.GetFullPath(executable),
                Path.GetFullPath(record.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<CampaignSimWorkerRecord> ReadRecords(CampaignId campaignId)
    {
        var directory = Path.Combine(paths.RunDirectory, "sim-workers", campaignId.ToString());
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetDirectories(directory)
            .Select(item => Path.Combine(item, "worker.json"))
            .Where(File.Exists)
            .Select(path => JsonSerializer.Deserialize<CampaignSimWorkerRecord>(File.ReadAllText(path), JsonOptions))
            .Where(item => item is not null)
            .Cast<CampaignSimWorkerRecord>()
            .ToArray();
    }

    private CampaignSimWorkerRecord? ReadRecord(CampaignId campaignId, string workerId)
    {
        var path = Path.Combine(RecordDirectory(campaignId, workerId), "worker.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<CampaignSimWorkerRecord>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    private void WriteRecord(CampaignSimWorkerRecord record)
    {
        var directory = RecordDirectory(new CampaignId(Guid.ParseExact(record.CampaignId, "N")), record.WorkerId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "worker.json");
        var temporaryPath = Path.Combine(directory, $"worker.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string RecordDirectory(CampaignId campaignId, string workerId) =>
        Path.Combine(paths.RunDirectory, "sim-workers", campaignId.ToString(), workerId);

    private static string NormalizeWorkerId(string? workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId)
            || workerId.Length > 64
            || workerId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            throw new CampaignStoreConflict("invalid-sim-worker-id", "Worker id must be a short ascii token.");
        }

        return workerId.Trim();
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private void AppendLog(string path, string? line)
    {
        if (line is null)
        {
            return;
        }

        var boundedLine = line.Length <= MaxLogLineCharacters
            ? line
            : line[..MaxLogLineCharacters] + " [truncated]";
        lock (_logGate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes)
                {
                    File.WriteAllText(path, "[previous worker log truncated at 1 MiB]" + Environment.NewLine);
                }

                File.AppendAllText(path, boundedLine + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging failure must not crash the campaign service or bypass
                // process supervision. Liveness remains visible through the API.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public sealed record CampaignSimWorkerSpawnRequest(
    string WorkerId,
    int Capacity = 8,
    int Reserve = 0,
    string Kind = CampaignWorkerKind.Operational);

internal sealed record CampaignSimWorkerRecord(
    string WorkerId,
    string CampaignId,
    string ClientInstanceId,
    string SpawnId,
    string SpawnCredentialSha256,
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath,
    int Capacity,
    int Reserve,
    DateTimeOffset SpawnedAtUtc,
    bool Alive);

public sealed record CampaignSimWorkerView(
    string WorkerId,
    string CampaignId,
    string ClientInstanceId,
    string SpawnId,
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath,
    int Capacity,
    int Reserve,
    DateTimeOffset SpawnedAtUtc,
    bool Alive);

internal static class CampaignOperationalWorkerPaths
{
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("CRIMEAN_TOM_OPERATIONAL_WORKER_PATH");
        foreach (var candidate in Candidates(configured))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new CampaignStoreConflict(
            "operational-worker-missing",
            "CrimeanTom.Campaign.OperationalWorker was not built next to the campaign service.");
    }

    private static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "operational-worker", "CrimeanTom.Campaign.OperationalWorker.exe");
        yield return Path.Combine(baseDirectory, "operational-worker", "CrimeanTom.Campaign.OperationalWorker");
        yield return Path.Combine(baseDirectory, "CrimeanTom.Campaign.OperationalWorker.exe");
        yield return Path.Combine(baseDirectory, "CrimeanTom.Campaign.OperationalWorker");
        yield return Path.Combine(
            baseDirectory,
            "..",
            "CrimeanTom.Campaign.OperationalWorker",
            "debug",
            "CrimeanTom.Campaign.OperationalWorker.exe");
        yield return Path.Combine(
            baseDirectory,
            "..",
            "CrimeanTom.Campaign.OperationalWorker",
            "debug",
            "CrimeanTom.Campaign.OperationalWorker");
        yield return Path.Combine(
            baseDirectory,
            "..",
            "CrimeanTom.Campaign.OperationalWorker",
            "release",
            "CrimeanTom.Campaign.OperationalWorker.exe");
        yield return Path.Combine(
            baseDirectory,
            "..",
            "CrimeanTom.Campaign.OperationalWorker",
            "release",
            "CrimeanTom.Campaign.OperationalWorker");

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrimeanTom.sln")))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    yield return Path.Combine(
                        directory.FullName,
                        "src",
                        "CrimeanTom.Campaign.OperationalWorker",
                        "bin",
                        configuration,
                        "net8.0",
                        "CrimeanTom.Campaign.OperationalWorker.exe");
                    yield return Path.Combine(
                        directory.FullName,
                        "src",
                        "CrimeanTom.Campaign.OperationalWorker",
                        "bin",
                        configuration,
                        "net8.0",
                        "CrimeanTom.Campaign.OperationalWorker");
                }

                yield break;
            }

            directory = directory.Parent;
        }
    }
}

internal sealed class CampaignOperationalWorkerHostedService(
    CampaignOperationalWorkerSupervisor supervisor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        supervisor.ReapStaleChildren();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        supervisor.StopAll();
        return Task.CompletedTask;
    }
}

internal static class NativeProcess
{
    public static string? GetExecutablePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName))
            {
                return process.MainModule.FileName;
            }
        }
        catch (Exception)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var capacity = 32768;
            var builder = new StringBuilder(capacity);
            var size = (uint)builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size)
                ? builder.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        nint process,
        int flags,
        StringBuilder exeName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed class WindowsKillOnCloseJob : IDisposable
{
    private nint _handle;

    public WindowsKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _handle = CreateJobObject(nint.Zero, null);
        if (_handle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Operational worker job object.");
        }
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(
                _handle,
                JobObjectExtendedLimitInformationClass,
                pointer,
                (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = nint.Zero;
                throw new Win32Exception(
                    error,
                    "Could not configure kill-on-close for the Operational worker job object.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Assign(Process process)
    {
        if (_handle == nint.Zero || process.HasExited)
        {
            return;
        }

        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not assign the Operational worker to the supervisor job object.");
        }
    }

    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            CloseHandle(_handle);
            _handle = nint.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        nint job,
        int infoClass,
        nint info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
