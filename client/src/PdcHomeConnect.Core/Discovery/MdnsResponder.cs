using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PdcHomeConnect.Core.Discovery;

/// <summary>Service advertised via mDNS / DNS-SD.</summary>
public sealed class MdnsServiceInfo
{
    public required string InstanceName { get; init; }

    /// <summary>Service type without domain, e.g. "_pdc-homeconnect._tcp".</summary>
    public required string ServiceType { get; init; }

    /// <summary>Host label without domain. Must be unique on the network.</summary>
    public required string HostName { get; init; }

    public required ushort Port { get; init; }

    public IReadOnlyDictionary<string, string> Txt { get; init; } = new Dictionary<string, string>();

    public string ServiceFqdn => $"{ServiceType}.local";

    public string InstanceFqdn => $"{InstanceName.Replace('.', '-')}.{ServiceFqdn}";

    public string HostFqdn => $"{HostName.Replace('.', '-')}.local";
}

/// <summary>Builds mDNS responses and announcements. Pure functions, unit-testable.</summary>
public static class MdnsResponseBuilder
{
    public const uint SharedTtl = 4500;
    public const uint HostTtl = 120;
    public const string ServicesEnumeration = "_services._dns-sd._udp.local";

    private enum Rec
    {
        EnumPtr,
        Ptr,
        Srv,
        Txt,
        A,
    }

    /// <summary>Builds a response for a query, or null if none of the questions concern this service.</summary>
    public static byte[]? BuildResponse(DnsQuery query, MdnsServiceInfo service, IReadOnlyList<IPAddress> addresses, bool legacyUnicast)
    {
        var answers = new List<Rec>();
        var additionals = new List<Rec>();
        foreach (var question in query.Questions)
        {
            var name = question.Name.TrimEnd('.');
            var type = question.Type;
            if (Eq(name, ServicesEnumeration) && type is DnsType.Ptr or DnsType.Any)
            {
                Add(answers, Rec.EnumPtr);
            }
            else if (Eq(name, service.ServiceFqdn) && type is DnsType.Ptr or DnsType.Any)
            {
                Add(answers, Rec.Ptr);
                Add(additionals, Rec.Srv);
                Add(additionals, Rec.Txt);
                Add(additionals, Rec.A);
            }
            else if (Eq(name, service.InstanceFqdn))
            {
                if (type is DnsType.Srv or DnsType.Any)
                {
                    Add(answers, Rec.Srv);
                    Add(additionals, Rec.A);
                }

                if (type is DnsType.Txt or DnsType.Any)
                {
                    Add(answers, Rec.Txt);
                }
            }
            else if (Eq(name, service.HostFqdn) && type is DnsType.A or DnsType.Any)
            {
                Add(answers, Rec.A);
            }
        }

        if (answers.Count == 0)
        {
            return null;
        }

        additionals.RemoveAll(answers.Contains);
        if (addresses.Count == 0)
        {
            answers.Remove(Rec.A);
            additionals.Remove(Rec.A);
            if (answers.Count == 0)
            {
                return null;
            }
        }

        // Legacy unicast responses must repeat the question and use short TTLs (RFC 6762, 6.7).
        var ttlCap = legacyUnicast ? 10u : uint.MaxValue;
        var writer = new DnsWriter();
        var answerCount = Count(answers, addresses);
        var additionalCount = Count(additionals, addresses);
        writer.Header(legacyUnicast ? query.Id : (ushort)0, 0x8400, legacyUnicast ? (ushort)query.Questions.Count : (ushort)0, answerCount, 0, additionalCount);
        if (legacyUnicast)
        {
            foreach (var question in query.Questions)
            {
                writer.Question(question.Name, question.Type, question.Class);
            }
        }

        foreach (var record in answers.Concat(additionals))
        {
            Write(writer, record, service, addresses, ttlCap, cacheFlush: !legacyUnicast);
        }

        return writer.ToArray();
    }

    /// <summary>Unsolicited announcement (or goodbye with ttl 0).</summary>
    public static byte[] BuildAnnouncement(MdnsServiceInfo service, IReadOnlyList<IPAddress> addresses, bool goodbye = false)
    {
        var records = new List<Rec> { Rec.Ptr, Rec.Srv, Rec.Txt };
        if (addresses.Count > 0)
        {
            records.Add(Rec.A);
        }

        var writer = new DnsWriter();
        writer.Header(0, 0x8400, 0, Count(records, addresses), 0, 0);
        foreach (var record in records)
        {
            Write(writer, record, service, addresses, goodbye ? 0u : uint.MaxValue, cacheFlush: true);
        }

        return writer.ToArray();
    }

    private static ushort Count(List<Rec> records, IReadOnlyList<IPAddress> addresses) =>
        (ushort)records.Sum(r => r == Rec.A ? addresses.Count : 1);

    private static void Write(DnsWriter writer, Rec record, MdnsServiceInfo service, IReadOnlyList<IPAddress> addresses, uint ttlCap, bool cacheFlush)
    {
        var shared = Math.Min(SharedTtl, ttlCap);
        var host = Math.Min(HostTtl, ttlCap);
        switch (record)
        {
            case Rec.EnumPtr:
                writer.Ptr(ServicesEnumeration, shared, service.ServiceFqdn);
                break;
            case Rec.Ptr:
                writer.Ptr(service.ServiceFqdn, shared, service.InstanceFqdn);
                break;
            case Rec.Srv:
                writer.Srv(service.InstanceFqdn, host, service.Port, service.HostFqdn, cacheFlush);
                break;
            case Rec.Txt:
                writer.Txt(service.InstanceFqdn, shared, service.Txt, cacheFlush);
                break;
            case Rec.A:
                foreach (var address in addresses)
                {
                    writer.A(service.HostFqdn, host, address, cacheFlush);
                }
                break;
        }
    }

    private static void Add(List<Rec> list, Rec record)
    {
        if (!list.Contains(record))
        {
            list.Add(record);
        }
    }

    private static bool Eq(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Minimal mDNS responder that advertises one DNS-SD service on all IPv4 interfaces.
/// Coexists with the Windows mDNS service (shared UDP port 5353).
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private readonly ILogger _logger;
    private readonly object _sendGate = new();
    private readonly CancellationTokenSource _cts = new();
    private MdnsServiceInfo _service;
    private Socket? _socket;
    private List<InterfaceAddress> _interfaces = [];
    private Timer? _networkChangeTimer;

    public MdnsResponder(MdnsServiceInfo service, ILogger<MdnsResponder> logger)
    {
        _service = service;
        _logger = logger;
    }

    private sealed record InterfaceAddress(int Index, IPAddress Address, IPAddress Mask);

    public bool IsRunning => _socket is not null;

    public void Start()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = false;
            }

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            _socket = socket;
            RefreshInterfaces();
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            _ = AnnounceRepeatedlyAsync();
            _logger.LogInformation("mDNS responder started for {Instance} on {Count} interface(s)", _service.InstanceFqdn, _interfaces.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS responder could not start; automatic discovery is unavailable");
            _socket?.Dispose();
            _socket = null;
        }
    }

    /// <summary>Updates the advertised service (e.g. after a rename) and re-announces.</summary>
    public void Update(MdnsServiceInfo service)
    {
        SendGoodbye();
        _service = service;
        _ = AnnounceRepeatedlyAsync();
    }

    /// <summary>Announces the service on all interfaces (e.g. after resume from sleep).</summary>
    public void Announce()
    {
        if (_socket is null)
        {
            return;
        }

        foreach (var group in _interfaces.GroupBy(i => i.Index))
        {
            var addresses = group.Select(i => i.Address).ToList();
            var packet = MdnsResponseBuilder.BuildAnnouncement(_service, addresses);
            SendMulticast(packet, group.First().Address);
        }
    }

    public void SendGoodbye()
    {
        if (_socket is null)
        {
            return;
        }

        foreach (var group in _interfaces.GroupBy(i => i.Index))
        {
            var addresses = group.Select(i => i.Address).ToList();
            var packet = MdnsResponseBuilder.BuildAnnouncement(_service, addresses, goodbye: true);
            SendMulticast(packet, group.First().Address);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        try
        {
            SendGoodbye();
        }
        catch
        {
            // Best effort.
        }

        _cts.Cancel();
        _networkChangeTimer?.Dispose();
        _socket?.Dispose();
        _socket = null;
    }

    private async Task AnnounceRepeatedlyAsync()
    {
        // RFC 6762 recommends at least two announcements, one second apart.
        for (var i = 0; i < 3 && !_cts.IsCancellationRequested; i++)
        {
            try
            {
                Announce();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mDNS announcement failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1 << i), _cts.Token).ConfigureAwait(false);
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Debounce: address changes often come in bursts.
        _networkChangeTimer?.Dispose();
        _networkChangeTimer = new Timer(_ =>
        {
            try
            {
                RefreshInterfaces();
                _ = AnnounceRepeatedlyAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mDNS network refresh failed");
            }
        }, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private void RefreshInterfaces()
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var interfaces = new List<InterfaceAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                    !nic.SupportsMulticast)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();
                if (ipv4 is null)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    var bytes = address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    interfaces.Add(new InterfaceAddress(ipv4.Index, address, unicast.IPv4Mask ?? IPAddress.Any));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping interface {Name}", nic.Name);
            }
        }

        foreach (var index in interfaces.Select(i => i.Index).Distinct())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, index));
            }
            catch (SocketException)
            {
                // Already a member of the group on this interface.
            }
        }

        _interfaces = interfaces;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[9000];
        while (!cancellationToken.IsCancellationRequested && _socket is { } socket)
        {
            try
            {
                var result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken).ConfigureAwait(false);
                var query = DnsReader.TryParse(buffer.AsSpan(0, result.ReceivedBytes));
                if (query is null || query.IsResponse || query.Questions.Count == 0)
                {
                    continue;
                }

                var remote = (IPEndPoint)result.RemoteEndPoint;
                var candidates = AddressesFor(result.PacketInformation.Interface, remote.Address);
                if (candidates.Count == 0)
                {
                    continue;
                }

                var legacy = remote.Port != MdnsPort;
                var response = MdnsResponseBuilder.BuildResponse(query, _service, candidates.Select(c => c.Address).ToList(), legacy);
                if (response is null)
                {
                    continue;
                }

                if (legacy)
                {
                    await socket.SendToAsync(response, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    SendMulticast(response, candidates[0].Address);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mDNS receive error");
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private List<InterfaceAddress> AddressesFor(int interfaceIndex, IPAddress remote)
    {
        var interfaces = _interfaces;
        var byIndex = interfaces.Where(i => i.Index == interfaceIndex).ToList();
        if (byIndex.Count > 0)
        {
            return byIndex;
        }

        var bySubnet = interfaces.Where(i => SameSubnet(i.Address, remote, i.Mask)).ToList();
        return bySubnet.Count > 0 ? bySubnet : interfaces;
    }

    private void SendMulticast(byte[] packet, IPAddress interfaceAddress)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        lock (_sendGate)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, interfaceAddress.GetAddressBytes());
                socket.SendTo(packet, new IPEndPoint(Group, MdnsPort));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mDNS send via {Address} failed", interfaceAddress);
            }
        }
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        if (b.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++)
        {
            if ((ab[i] & mb[i]) != (bb[i] & mb[i]))
            {
                return false;
            }
        }
        return true;
    }
}
