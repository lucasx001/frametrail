using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FrameTrail.Domain;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class P2WorkflowTests
{
    [Fact] public async Task DenseRequestsKeepOneReplacementAndRejectTheOldResult()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls=new List<int>();var accepted=new List<int>();LatestRequestQueue<int> queue=null!;
        queue=new(async(value,version)=>{calls.Add(value);if(value==0){started.SetResult();await release.Task;}if(queue.IsCurrent(version))accepted.Add(value);});
        var task=queue.Request(0);await started.Task;
        for(int i=1;i<=1000;i++)_ = queue.Request(i);
        release.SetResult();await task;
        Assert.Equal(new[]{0,1000},calls);Assert.Equal(new[]{1000},accepted);
    }
    [Fact] public async Task CancellationRejectsAnInflightResultAndAnOldFailureKeepsTheNewRequest()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int accepted=0;LatestRequestQueue<int> queue=null!;
        queue=new(async(value,version)=>{if(value==1){started.SetResult();await release.Task;throw new IOException("old request");}if(queue.IsCurrent(version))accepted=value;});
        var task=queue.Request(1);await started.Task;queue.Cancel();_ = queue.Request(3);release.SetResult();await task;Assert.Equal(3,accepted);
    }
    [Fact] public void IntervalQueriesMatchBruteForceIncludingLongOverlapsAndEdges()
    {
        var random=new Random(17);var intervals=Enumerable.Range(0,10000).Select(i=>(Id:i,Start:(long)random.Next(100000),Length:random.Next(1,50000))).ToArray();
        var index=new TimelineIntervalIndex<(int Id,long Start,int Length)>(intervals,v=>v.Start,v=>v.Start+v.Length);
        for(long start=0;start<150000;start+=997)Assert.Equal(intervals.Where(v=>v.Start<=start+100&&v.Start+v.Length>=start).Select(v=>v.Id).Order(),index.Query(start,start+100).Select(v=>v.Id).Order());
        Assert.Empty(new TimelineIntervalIndex<int>([],v=>v,v=>v).Query(0,1));
    }
    [Fact] public void LanguagesHaveCompleteKeysAndMatchingFormatArguments()
    {
        var zh=AppText.Resources("zh-Hans");var en=AppText.Resources("en");Assert.True(zh.Count>=670);Assert.Equal(zh.Keys.Order(),en.Keys.Order());
        foreach(var pair in zh){Assert.False(string.IsNullOrWhiteSpace(en[pair.Key]));Assert.Equal(CompositeFormat.Parse(pair.Value).MinimumArgumentCount,CompositeFormat.Parse(en[pair.Key]).MinimumArgumentCount);Assert.DoesNotMatch("[\\p{IsCJKUnifiedIdeographs}]",en[pair.Key]);}
        Assert.Equal("en",AppText.Resolve("system","de-DE"));Assert.Equal("zh-Hans",AppText.Resolve("system","zh-TW"));Assert.Equal("en",AppText.Resolve("en","zh-CN"));
    }
    private static string Release(string tag="v0.5.0",string page="https://github.com/lucasx001/frametrail/releases/tag/v0.5.0",bool prerelease=false)=>JsonSerializer.Serialize(new{tag_name=tag,html_url=page,prerelease,draft=false,body="Release notes",assets=new[]{new{name="FrameTrail-0.5.0-x64-Setup.exe",browser_download_url="https://github.com/lucasx001/frametrail/releases/download/v0.5.0/FrameTrail-0.5.0-x64-Setup.exe"}}});
    [Fact] public async Task OfficialReleaseComparisonAndHttpFailuresAreExplicit()
    {
        using var http=new HttpClient(new Handler(HttpStatusCode.OK,Release()));var release=await new ReleaseUpdateClient(http).CheckAsync(default);Assert.NotNull(release!.Installer);
        Assert.True(ReleaseUpdateClient.Compare(release.Version,new Version(0,4,0,0))>0);Assert.Equal(0,ReleaseUpdateClient.Compare(release.Version,new Version(0,5,0,0)));Assert.True(ReleaseUpdateClient.Compare(release.Version,new Version(0,6,0,0))<0);
        using var missing=new HttpClient(new Handler(HttpStatusCode.NotFound,""));Assert.Null(await new ReleaseUpdateClient(missing).CheckAsync(default));
        using var limited=new HttpClient(new Handler(HttpStatusCode.Forbidden,""));await Assert.ThrowsAsync<HttpRequestException>(()=>new ReleaseUpdateClient(limited).CheckAsync(default));
        using var bad=new HttpClient(new Handler(HttpStatusCode.OK,"{}"));await Assert.ThrowsAnyAsync<Exception>(()=>new ReleaseUpdateClient(bad).CheckAsync(default));
    }
    [Theory][InlineData("https://evil.example/releases/")][InlineData("https://github.com/other/project/releases/tag/v1")][InlineData("http://github.com/lucasx001/frametrail/releases/latest")]
    public void ReleasesCannotOfferUnofficialLinks(string page)
    {
        using var json=JsonDocument.Parse(Release(page:page));Assert.Throws<IOException>(()=>ReleaseUpdateClient.Parse(json.RootElement));
        using var preview=JsonDocument.Parse(Release(prerelease:true));Assert.Throws<IOException>(()=>ReleaseUpdateClient.Parse(preview.RootElement));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AnonymousQuotaFallbackUsesOnlyAnOfficialStableRedirect(bool malicious)
    {
        using var client=new HttpClient(new QuotaHandler(malicious));var checker=new ReleaseUpdateClient(client);
        if(malicious){await Assert.ThrowsAsync<IOException>(()=>checker.CheckAsync(default));return;}
        var release=await checker.CheckAsync(default);Assert.True(release!.UsesWebFallback);Assert.Equal(new Version(0,5,0),release.Version);Assert.Null(release.Installer);Assert.Null(release.Portable);
    }
    [Fact] public void EveryUiLookupReferencesAnEmbeddedResource()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"Directory.Build.props")))directory=directory.Parent;
        Assert.NotNull(directory);var keys=AppText.Resources("en").Keys.ToHashSet();
        foreach(var project in new[]{"FrameTrail.App","FrameTrail.Workspace","FrameTrail.Domain"})
        foreach(var path in Directory.EnumerateFiles(Path.Combine(directory!.FullName,"src",project),"*.*",SearchOption.TopDirectoryOnly).Where(p=>p.EndsWith(".cs")||p.EndsWith(".xaml"))) {
            string text=File.ReadAllText(path);
            foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,"L\\.T\\(\"([^\"]+)\"|local:Loc Key=([^}]+)"))
                Assert.Contains(match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value,keys);
        }
    }
    private sealed class QuotaHandler(bool malicious):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(request.RequestUri==ReleaseUpdateClient.Latest?
            new HttpResponseMessage(HttpStatusCode.Forbidden):new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=new HttpRequestMessage(HttpMethod.Get,malicious?"https://evil.example/tag/v0.5.0":"https://github.com/lucasx001/frametrail/releases/tag/v0.5.0")});
    }
    private sealed class Handler(HttpStatusCode code,string body):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(code){Content=new StringContent(body)});}
}
