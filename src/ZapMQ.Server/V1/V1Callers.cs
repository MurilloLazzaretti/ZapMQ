using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZapMQ.Server.V1;

/// <summary>
/// Tells which process of this machine is at the other end of a connection.
/// </summary>
public interface IPeerResolver
{
    /// <summary>
    /// The process that owns the connection made from <paramref name="clientPort"/> to
    /// <paramref name="serverPort"/>, both on this machine. Null when it cannot be told.
    /// </summary>
    (int ProcessId, string Name)? Resolve(AddressFamily family, int clientPort, int serverPort);
}

/// <summary>
/// Who is calling over the 1.x protocol. The protocol itself says nothing about the client,
/// so a client on another machine is only its address. One on this machine is more than that:
/// the system knows which process owns the connection, and with the process comes its name
/// and number, which is what joins it to everything else that is known about it.
/// </summary>
public sealed class V1Callers(IPeerResolver resolver, TimeProvider time)
{
    private static readonly TimeSpan Remembered = TimeSpan.FromSeconds(15);

    // A client keeps its connection, and with it its port, for many calls in a row.
    private readonly ConcurrentDictionary<(AddressFamily, int), (string Party, DateTimeOffset Until)> _known = new();
    private long _nextCleaning;

    /// <summary>
    /// How the caller is written down as a party of a queue: <c>v1:address</c>, or
    /// <c>v1:address|process name|process id</c> when the process is known.
    /// </summary>
    public string Party(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        var plain = "v1:" + (address?.ToString() ?? "unknown");
        if (address is null || !IsThisMachine(address, context.Connection.LocalIpAddress))
            return plain;

        var now = time.GetUtcNow();
        var key = (address.AddressFamily, context.Connection.RemotePort);
        if (_known.TryGetValue(key, out var known) && known.Until > now)
            return known.Party;

        var party = plain;
        try
        {
            if (resolver.Resolve(address.AddressFamily, context.Connection.RemotePort, context.Connection.LocalPort) is var (processId, name))
                party = $"{plain}|{name}|{processId}";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // The process left between calling and being looked for.
        }

        _known[key] = (party, now + Remembered);
        Clean(now);
        return party;
    }

    /// <summary>
    /// What a party written by <see cref="Party"/> says: the address and, when the process
    /// was known, its name and number.
    /// </summary>
    public static (string Address, string? Name, int? ProcessId) Read(string party)
    {
        var parts = (party.StartsWith("v1:", StringComparison.Ordinal) ? party[3..] : party).Split('|');
        return parts.Length == 3 && int.TryParse(parts[2], out var processId) ? (parts[0], parts[1], processId) : (parts[0], null, null);
    }

    private static bool IsThisMachine(IPAddress remote, IPAddress? local) =>
        IPAddress.IsLoopback(remote) || (local is not null && remote.Equals(local))
        || (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4()));

    private void Clean(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextCleaning);
        if (now.UtcTicks < due || Interlocked.CompareExchange(ref _nextCleaning, (now + TimeSpan.FromMinutes(1)).UtcTicks, due) != due)
            return;
        foreach (var (key, value) in _known)
        {
            if (value.Until <= now)
                _known.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// Where the system does not tell who owns a connection.
/// </summary>
internal sealed class NoPeerResolver : IPeerResolver
{
    public (int ProcessId, string Name)? Resolve(AddressFamily family, int clientPort, int serverPort) => null;
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsPeerResolver : IPeerResolver
{
    private const int ConnectionTable = 4;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    public (int ProcessId, string Name)? Resolve(AddressFamily family, int clientPort, int serverPort)
    {
        // Rows of 24 bytes with the local port at 8, the remote one at 16 and the process at
        // 20; of 56 bytes with them at 20, 44 and 52.
        var processId = family == AddressFamily.InterNetworkV6
            ? Find(23, rowSize: 56, localAt: 20, remoteAt: 44, processAt: 52, clientPort, serverPort) ?? Find(2, 24, 8, 16, 20, clientPort, serverPort)
            : Find(2, 24, 8, 16, 20, clientPort, serverPort) ?? Find(23, 56, 20, 44, 52, clientPort, serverPort);
        if (processId is null or 0)
            return null;
        // Finding the name of what a host process runs means going through what it loaded;
        // once is enough for a while.
        var now = Environment.TickCount64;
        if (_names.TryGetValue(processId.Value, out var known) && now - known.At < 60_000)
            return (processId.Value, known.Name);
        using var process = Process.GetProcessById(processId.Value);
        var name = NameOf(process);
        _names[processId.Value] = (name, now);
        return (processId.Value, name);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Name, long At)> _names = new();

    /// <summary>
    /// The name a process is known by. The worker process of IIS is the same executable for
    /// every application it runs: it goes by the application instead, which is the largest
    /// library it loaded from outside the system (what somebody published there).
    /// </summary>
    private static string NameOf(Process process)
    {
        if (!string.Equals(process.ProcessName, "w3wp", StringComparison.OrdinalIgnoreCase))
            return process.ProcessName;
        try
        {
            string? best = null;
            long size = 0;
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    var file = module.FileName;
                    if (string.IsNullOrEmpty(file) || SystemFolders.Any(folder => file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) || module.ModuleMemorySize <= size)
                        continue;
                    (best, size) = (file, module.ModuleMemorySize);
                }
            }
            return best is null ? process.ProcessName : Path.GetFileNameWithoutExtension(best);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return process.ProcessName;
        }
    }

    private static readonly string[] SystemFolders = [.. new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }
        .Where(folder => folder.Length > 0)
        .Select(folder => folder.TrimEnd('\\') + "\\")];

    /// <summary>
    /// The client's end of the connection, as the system lists it: the port the client calls
    /// from is its local one, and the port of this server is its remote one.
    /// </summary>
    private static int? Find(int family, int rowSize, int localAt, int remoteAt, int processAt, int clientPort, int serverPort)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, ConnectionTable, 0);
        if (size <= 0)
            return null;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, ConnectionTable, 0) != 0)
                return null;
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + i * rowSize;
                if (Port(Marshal.ReadInt32(row, localAt)) == clientPort && Port(Marshal.ReadInt32(row, remoteAt)) == serverPort)
                    return Marshal.ReadInt32(row, processAt);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    // A port comes in the order of the network: its two bytes swapped.
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
