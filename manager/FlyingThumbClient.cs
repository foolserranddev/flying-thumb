using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FlyingThumbManager;

public sealed class FlyingThumbClient
{
    const string DemoMarker = ".flyingthumb-demo.json";
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };

    HttpRequestMessage Request(Device d,HttpMethod method,string path,string key,HttpContent? content=null){var request=new HttpRequestMessage(method,new Uri(d.BaseUri,path)){Content=content};if(!string.IsNullOrEmpty(key))request.Headers.TryAddWithoutValidation("X-FlyingThumb-Key",key);return request;}
    static string DemoPath(Device d,string name)=>Path.Combine(d.RootPath??throw new InvalidOperationException("Demo drive folder is unavailable."),Path.GetFileName(name));
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
        if(d.IsSimulated)return Directory.GetFiles(d.RootPath!).Where(path=>Path.GetFileName(path)!=DemoMarker).Select(path=>new RemoteFile{Name="/"+Path.GetFileName(path),Size=new FileInfo(path).Length,Type="file"}).ToList();
        using var response=await http.SendAsync(Request(d,HttpMethod.Get,"api/list?dir=/",key));await EnsureSuccessAsync(response,"Read file list");return await response.Content.ReadFromJsonAsync<List<RemoteFile>>()??[];
    }

    public async Task DownloadAsync(Device d,string remoteName,string destinationPath,Action<long>? progress=null)
    {
        if(d.IsSimulated){File.Copy(DemoPath(d,remoteName),destinationPath,true);progress?.Invoke(new FileInfo(destinationPath).Length);return;}
        var path=Uri.EscapeDataString(Path.GetFileName(remoteName));using var response=await http.GetAsync(new Uri(d.BaseUri,path),HttpCompletionOption.ResponseHeadersRead);await EnsureSuccessAsync(response,$"Download {Path.GetFileName(remoteName)}");await using var input=await response.Content.ReadAsStreamAsync();await using var output=File.Create(destinationPath);
        var buffer=new byte[81920];long copied=0;int read;while((read=await input.ReadAsync(buffer))>0){await output.WriteAsync(buffer.AsMemory(0,read));copied+=read;progress?.Invoke(copied);}
    }

    public async Task UploadAsync(Device d,string filePath,string key,Action<long>? progress=null)
    {
        if(d.IsSimulated){File.Copy(filePath,DemoPath(d,filePath),true);progress?.Invoke(new FileInfo(filePath).Length);return;}
        await using var stream=File.OpenRead(filePath);await using var progressStream=new ProgressReadStream(stream,progress);using var content=new MultipartFormDataContent();using var file=new StreamContent(progressStream);AddFilePart(content,file,"file",filePath);using var response=await http.SendAsync(Request(d,HttpMethod.Post,"upload?restart=0",key,content));await EnsureSuccessAsync(response,$"Upload {Path.GetFileName(filePath)}");
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
        if(d.IsSimulated){var demoPath=DemoPath(d,remoteName);if(File.Exists(demoPath))File.Delete(demoPath);return;}
        var encodedPath=Uri.EscapeDataString("/"+Path.GetFileName(remoteName));
        using var response=await http.SendAsync(Request(d,HttpMethod.Post,"delete?dir="+encodedPath,key,new StringContent("")));
        await EnsureSuccessAsync(response,$"Delete {Path.GetFileName(remoteName)}");
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

    public async Task RenameAsync(Device d,string name,string key)
    {
        if(d.IsSimulated){var metadata=new DemoDeviceMetadata{Id=d.Id,Name=name};File.WriteAllText(Path.Combine(d.RootPath!,DemoMarker),JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true}));d.Name=name;return;}
        var json=JsonSerializer.Serialize(new{name,key});using var response=await http.SendAsync(Request(d,HttpMethod.Post,"api/device",key,new StringContent(json,Encoding.UTF8,"application/json")));await EnsureSuccessAsync(response,"Rename drive");
    }
}
