using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace FrameTrail.App;

internal sealed record ReleaseUpdate(Version Version, string Tag, string Notes, Uri Page, Uri? Installer, Uri? Portable, bool UsesWebFallback=false);
internal sealed class ReleaseUpdateClient(HttpClient client)
{
    internal static readonly Uri Latest = new("https://api.github.com/repos/lucasx001/frametrail/releases/latest");
    internal static bool Official(Uri uri) => uri.Scheme=="https" && uri.Host=="github.com" && uri.IsDefaultPort &&
        uri.AbsolutePath.StartsWith("/lucasx001/frametrail/releases/",StringComparison.Ordinal) && string.IsNullOrEmpty(uri.UserInfo);
    internal async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellation)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Latest);
        request.Headers.UserAgent.ParseAdd("FrameTrail/"+typeof(AppTextVersion).Assembly.GetName().Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)return await CheckOfficialPageAsync(cancellation);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>1_048_576)throw new IOException("Update response is too large.");
        await using var stream=await response.Content.ReadAsStreamAsync(cancellation);
        using var memory=new MemoryStream();var bytes=new byte[8192];int count;
        while((count=await stream.ReadAsync(bytes,cancellation))>0){if(memory.Length+count>1_048_576)throw new IOException("Update response is too large.");memory.Write(bytes,0,count);}
        using var json=JsonDocument.Parse(memory.ToArray());return Parse(json.RootElement);
    }
    private async Task<ReleaseUpdate> CheckOfficialPageAsync(CancellationToken cancellation)
    {
        // GitHub's anonymous API quota can be shared by a proxy. The official
        // stable-release redirect still identifies a version without credentials.
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://github.com/lucasx001/frametrail/releases/latest");
        request.Headers.UserAgent.ParseAdd("FrameTrail/"+typeof(ReleaseUpdateClient).Assembly.GetName().Version);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);response.EnsureSuccessStatusCode();
        var uri=response.RequestMessage?.RequestUri??throw new IOException("Missing release redirect.");
        const string prefix="/lucasx001/frametrail/releases/tag/";
        if(!Official(uri)||!uri.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal))throw new IOException("No official stable release redirect.");
        string tag=Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        if(tag.Contains('-')||tag.Contains('/')||!Version.TryParse(tag.TrimStart('v'),out var version)||version.Build<0)throw new IOException("Invalid stable release version.");
        return new(version,tag,"",uri,null,null,true);
    }
    internal static ReleaseUpdate Parse(JsonElement json)
    {
        string tag=json.GetProperty("tag_name").GetString()??"";
        if(json.GetProperty("draft").GetBoolean()||json.GetProperty("prerelease").GetBoolean()||
            !Version.TryParse(tag.TrimStart('v'),out var version)||version.Revision>0||tag.Contains('-'))throw new IOException("The release is not a stable version.");
        var page=new Uri(json.GetProperty("html_url").GetString()!);
        if(!Official(page))throw new IOException("Update source is not official.");
        Uri? Asset(string suffix){
            foreach(var asset in json.GetProperty("assets").EnumerateArray()) {
                string name=asset.GetProperty("name").GetString()??"";
                if(name.StartsWith("FrameTrail-",StringComparison.Ordinal)&&name.EndsWith(suffix,StringComparison.Ordinal)) {
                    var uri=new Uri(asset.GetProperty("browser_download_url").GetString()!);
                    if(Official(uri)&&uri.AbsolutePath.StartsWith("/lucasx001/frametrail/releases/download/"+tag+"/",StringComparison.Ordinal))return uri;
                }
            }return null;
        }
        string notes=json.TryGetProperty("body",out var body)?body.GetString()??"":"";
        return new(version,tag,notes.Length>12000?notes[..12000]:notes,page,Asset("-x64-Setup.exe"),Asset("-x64-portable.zip"));
    }
    internal static int Compare(Version release,Version current) => new Version(release.Major,release.Minor,Math.Max(0,release.Build)).CompareTo(new Version(current.Major,current.Minor,Math.Max(0,current.Build)));
    private sealed class AppTextVersion { }
}
