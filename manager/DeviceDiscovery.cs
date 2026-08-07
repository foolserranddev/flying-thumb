using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FlyingThumbManager;

interface IDeviceDiscoveryProvider
{
    Task<IReadOnlyList<Device>> FindAsync(TimeSpan duration);
}

sealed class NetworkDeviceDiscoveryProvider : IDeviceDiscoveryProvider
{
    const int Port = 4210;
    const string Request = "FLYINGTHUMB_DISCOVER_V1";
    public async Task<IReadOnlyList<Device>> FindAsync(TimeSpan duration)
    {
        using var udp = new UdpClient(0) { EnableBroadcast = true };
        var payload = Encoding.ASCII.GetBytes(Request);
        var targets = BroadcastAddresses()
            .Append(IPAddress.Broadcast)
            .Distinct()
            .Select(address => new IPEndPoint(address, Port))
            .ToArray();
        foreach (var target in targets)
        {
            try { await udp.SendAsync(payload, payload.Length, target); }
            catch (SocketException) { }
        }
        var found = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        using var timeout = new CancellationTokenSource(duration);
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                var device = JsonSerializer.Deserialize<Device>(packet.Buffer);
                if (device is { Id.Length: > 0 }) { if (string.IsNullOrWhiteSpace(device.Ip)) device.Ip = packet.RemoteEndPoint.Address.ToString(); found[device.Id] = device; }
            }
            catch (OperationCanceledException) { break; }
            catch (JsonException) { }
            catch (SocketException) { }
        }
        return found.Values.ToArray();
    }

    static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }
}

sealed class FolderDeviceDiscoveryProvider : IDeviceDiscoveryProvider
{
    const string Marker = ".flyingthumb-demo.json";
    public Task<IReadOnlyList<Device>> FindAsync(TimeSpan duration)
    {
        var root = FindDemoRoot();
        if (root is null) return Task.FromResult<IReadOnlyList<Device>>([]);
        var result = new List<Device>();
        foreach (var folder in Directory.GetDirectories(root))
        {
            var marker = Path.Combine(folder, Marker);
            if (!File.Exists(marker)) continue;
            try
            {
                var metadata = JsonSerializer.Deserialize<DemoDeviceMetadata>(File.ReadAllText(marker));
                if (metadata is null || string.IsNullOrWhiteSpace(metadata.Id)) continue;
                result.Add(new Device { Id = metadata.Id, Name = metadata.Name, Ip = "Demo folder", Port = 0, Firmware = "Demo", StorageReady = true, StorageFree = DriveInfo.GetDrives().FirstOrDefault(x => folder.StartsWith(x.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))?.AvailableFreeSpace ?? 0, Claimed = true, IsSimulated = true, RootPath = folder, Status = "Simulated" });
            }
            catch { }
        }
        return Task.FromResult<IReadOnlyList<Device>>(result);
    }

    static string? FindDemoRoot()
    {
        var starts = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var current = new DirectoryInfo(start);
            for (var depth = 0; depth < 6 && current is not null; depth++, current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, "demo-drives");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

public static class DeviceDiscovery
{
    static readonly IDeviceDiscoveryProvider[] Providers = [new NetworkDeviceDiscoveryProvider(), new FolderDeviceDiscoveryProvider()];
    public static async Task<IReadOnlyList<Device>> FindAsync(TimeSpan duration)
    {
        var groups = await Task.WhenAll(Providers.Select(provider => provider.FindAsync(duration)));
        return groups.SelectMany(x => x).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).OrderBy(x => x.Name).ToArray();
    }
}
