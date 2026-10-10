using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FlyingThumbManager;

public sealed class FlyingThumbClient
{
    const string DemoMarker = ".flyingthumb-demo.json";
    readonly HttpClient http;
    readonly HttpClient transferHttp;
    public FlyingThumbClient(HttpClient? transport = null) {
        http = transport ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        transferHttp = transport ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    HttpRequestMessage Request(Device d,HttpMethod method,string path,string key,HttpContent? content=null){var request=new HttpRequestMessage(method,new Uri(d.BaseUri,path)){Content=content};if(!string.IsNullOrEmpty(key))request.Headers.TryAddWithoutValidation("X-FlyingThumb-Key",key);return request;}
    public static string NormalizeRemotePath(string name)
    {
        var normalized = name.Replace('\\', '/').Trim();
        while (normalized.StartsWith('/')) normalized = normalized[1..];
        if (normalized.Length == 0 || normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
            throw new ArgumentException("The drive path is invalid.", nameof(name));
        return normalized;
    }
    static string DemoPath(Device d,string name)
    {
        var root = Path.GetFullPath(d.RootPath??throw new InvalidOperationException("Demo drive folder is unavailable."));
        var path = Path.GetFullPath(Path.Combine(root, NormalizeRemotePath(name).Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The drive path is invalid.");
        return path;
    }
    static void AddFilePart(MultipartFormDataContent content, HttpContent part, string fieldName, string fileName)
    {
        var safeName = Path.GetFileName(fileName).Replace("\"", "");
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{fieldName}\"",
            FileName = $"\"{safeName}\""
        };
        content.Add(part);
    }
    static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        var detail = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error)) detail = error.GetString() ?? body;
        }
        catch { }
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Management key rejected");
        if (string.IsNullOrWhiteSpace(detail)) detail = response.ReasonPhrase ?? "request failed";
        throw new InvalidOperationException($"{operation}: {detail} (HTTP {(int)response.StatusCode})");
    }

    public async Task<List<RemoteFile>> ListAsync(Device d,string key)
    {
        if(d.IsSimulated){var root=Path.GetFullPath(d.RootPath!);return Directory.GetFiles(root,"*",SearchOption.AllDirectories).Where(path=>Path.GetFileName(path)!=DemoMarker).Select(path=>new RemoteFile{Name="/"+Path.GetRelativePath(root,path).Replace('\\','/'),Size=new FileInfo(path).Length,Type="file"}).ToList();}
        using var response=await http.SendAsync(Request(d,HttpMethod.Get,"api/list?recursive=1",key));await EnsureSuccessAsync(response,"Read file list");return await response.Content.ReadFromJsonAsync<List<RemoteFile>>()??[];
    }

    public async Task DownloadAsync(Device d,string remoteName,string destinationPath,Action<long>? progress=null,CancellationToken cancellationToken=default)
    {
        var remotePath=NormalizeRemotePath(remoteName);
        if(d.IsSimulated){File.Copy(DemoPath(d,remotePath),destinationPath,true);progress?.Invoke(new FileInfo(destinationPath).Length);return;}
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var response=await transferHttp.SendAsync(Request(d,HttpMethod.Get,"api/download?path="+Uri.EscapeDataString("/"+remotePath),""),HttpCompletionOption.ResponseHeadersRead,deadline.Token);await EnsureSuccessAsync(response,$"Download {remotePath}");await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);await using var output=File.Create(destinationPath);
        var buffer=new byte[81920];long copied=0;int read;while((read=await input.ReadAsync(buffer,deadline.Token))>0){await output.WriteAsync(buffer.AsMemory(0,read),deadline.Token);copied+=read;progress?.Invoke(copied);}
        if(response.Content.Headers.ContentLength is long expected && copied!=expected)throw new IOException($"Incomplete download: {copied} of {expected} bytes");
    }

    public async Task UploadAsync(Device d,string filePath,string remoteName,string key,Action<long>? progress=null,CancellationToken cancellationToken=default)
    {
        var remotePath=NormalizeRemotePath(remoteName);
        if(d.IsSimulated){var destination=DemoPath(d,remotePath);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(filePath,destination,true);progress?.Invoke(new FileInfo(filePath).Length);return;}
        await using var stream=File.OpenRead(filePath);await using var progressStream=new ProgressReadStream(stream,progress);using var content=new MultipartFormDataContent();using var file=new StreamContent(progressStream);AddFilePart(content,file,"file",Path.GetFileName(filePath));using var response=await transferHttp.SendAsync(Request(d,HttpMethod.Post,"upload?restart=0&path="+Uri.EscapeDataString("/"+remotePath),key,content),cancellationToken);await EnsureSuccessAsync(response,$"Upload {remotePath}");
    }

    sealed class ProgressReadStream(Stream inner,Action<long>? progress):Stream
    {
        long transferred;
        void Report(int count){if(count<=0)return;transferred+=count;progress?.Invoke(transferred);}
        public override bool CanRead=>inner.CanRead;public override bool CanSeek=>inner.CanSeek;public override bool CanWrite=>false;public override long Length=>inner.Length;
        public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override void Flush()=>inner.Flush();public override Task FlushAsync(CancellationToken token)=>inner.FlushAsync(token);
        public override int Read(byte[] buffer,int offset,int count){var read=inner.Read(buffer,offset,count);Report(read);return read;}
        public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token){var read=await inner.ReadAsync(buffer.AsMemory(offset,count),token);Report(read);return read;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){var read=await inner.ReadAsync(buffer,token);Report(read);return read;}
        public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }

    public async Task DeleteAsync(Device d,string remoteName,string key)
    {
        var remotePath=NormalizeRemotePath(remoteName);
        if(d.IsSimulated){var demoPath=DemoPath(d,remotePath);if(File.Exists(demoPath))File.Delete(demoPath);return;}
        var encodedPath=Uri.EscapeDataString("/"+remotePath);
        using var response=await http.SendAsync(Request(d,HttpMethod.Post,"delete?dir="+encodedPath,key,new StringContent("")));
        await EnsureSuccessAsync(response,$"Delete {remotePath}");
    }
    public async Task<bool> BeginFileBatchAsync(Device d,string key)
    {
        if(d.IsSimulated)return true;
        using var response=await http.SendAsync(Request(d,HttpMethod.Post,"api/files/begin",key,new StringContent("")));
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return false;
        await EnsureSuccessAsync(response,"Prepare managed file update");
        return true;
    }

    public async Task CommitFileBatchAsync(Device d,string key)
    {
        if(d.IsSimulated)return;
        using var response=await http.SendAsync(Request(d,HttpMethod.Post,"api/files/commit",key,new StringContent("")));
        await EnsureSuccessAsync(response,"Refresh USB file view");
    }
    public async Task ReleaseManagedUsbAsync(Device d,string key)
    {
        if(d.IsSimulated)return;
        using var response=await http.SendAsync(Request(d,HttpMethod.Post,"api/files/release",key,new StringContent("")));
        await EnsureSuccessAsync(response,"Return USB to writable mode");
    }

    public async Task UpgradeFirmwareAsync(Device d,string firmwarePath,string key)
    {
        if(d.IsSimulated)throw new InvalidOperationException("Firmware upgrades do not apply to simulated drives.");
        await using var stream=File.OpenRead(firmwarePath);using var content=new MultipartFormDataContent();using var firmware=new StreamContent(stream);AddFilePart(content,firmware,"firmware",firmwarePath);using var response=await http.SendAsync(Request(d,HttpMethod.Post,"api/firmware",key,content));await EnsureSuccessAsync(response,"Install firmware");
    }

    public async Task RestartAsync(Device d,string key)
    {
        if(d.IsSimulated)return;
        using var response=await http.SendAsync(Request(d,HttpMethod.Post,"api/restart",key,new StringContent("")));await EnsureSuccessAsync(response,"Restart drive");
    }

    public async Task EnterUsbRecoveryAsync(Device d, string key)
    {
        if (d.IsSimulated) throw new InvalidOperationException("USB recovery requires a physical drive.");
        using var response = await http.SendAsync(Request(d, HttpMethod.Post, "api/usb-recovery", key, new StringContent("")));
        await EnsureSuccessAsync(response, "Enter USB recovery");
    }

    public async Task RenameAsync(Device d,string name,string key)
    {
        if(d.IsSimulated){var metadata=new DemoDeviceMetadata{Id=d.Id,Name=name};File.WriteAllText(Path.Combine(d.RootPath!,DemoMarker),JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true}));d.Name=name;return;}
        var json=JsonSerializer.Serialize(new{name,key});using var response=await http.SendAsync(Request(d,HttpMethod.Post,"api/device",key,new StringContent(json,Encoding.UTF8,"application/json")));await EnsureSuccessAsync(response,"Rename drive");
    }
}
