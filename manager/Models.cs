using System.Text.Json.Serialization;
namespace FlyingThumbManager;

public sealed class Device
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 80;
    [JsonPropertyName("firmware")] public string Firmware { get; set; } = "";
    [JsonPropertyName("storageFree")] public long StorageFree { get; set; }
    [JsonPropertyName("storageReady")] public bool StorageReady { get; set; }
    [JsonPropertyName("claimed")] public bool Claimed { get; set; }
    [JsonPropertyName("usbManaged")] public bool UsbManaged { get; set; }
    [JsonIgnore] public bool Selected { get; set; } = true;
    [JsonIgnore] public string Status { get; set; } = "Ready";
    [JsonIgnore] public bool IsSimulated { get; set; }
    [JsonIgnore] public string? RootPath { get; set; }
    [JsonIgnore] public string Free => IsSimulated || StorageReady ? FormatBytes(StorageFree) : "No card";
    [JsonIgnore] public Uri BaseUri => new($"http://{Ip}:{Port}/");
    static string FormatBytes(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):0.0} GB" : $"{value / (double)(1L << 20):0} MB";
}

public sealed class RemoteFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public sealed class DemoDeviceMetadata
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
