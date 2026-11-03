using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using Lumina.Excel.Sheets;
using Dalamud.Plugin.Services;

namespace ULKHelper.Logos;
internal sealed record LogosShard(uint Item, uint[] Mnemes);
internal sealed record LogosAction(int Number, uint Action, uint[][] Recipes);
internal sealed record LogosCatalog(LogosShard[] Shards, LogosAction[] Actions)
{
    public static LogosCatalog Load()
    {
        using var stream = typeof(LogosCatalog).Assembly.GetManifestResourceStream("ULKHelper.Logos.catalog.json")!;
        return JsonSerializer.Deserialize<LogosCatalog>(stream,new JsonSerializerOptions { PropertyNameCaseInsensitive=true })!;
    }
}
internal sealed record LogosPrice(int? World, int? Dc, int? Region);
internal sealed class LogosService : IDisposable
{
    public LogosCatalog Catalog { get; } = LogosCatalog.Load();
    private readonly HttpClient http = new() { Timeout=TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource cancel = new();
    private Task<Dictionary<uint,LogosPrice>>? request;
    private uint requestWorld, currentWorld;
    private DateTimeOffset nextRequest;
    public IReadOnlyDictionary<uint,LogosPrice> Prices { get; private set; } = new Dictionary<uint,LogosPrice>();
    public string Status { get; private set; } = "点击查询价格获取Universalis报价。";
    public DateTimeOffset? Updated { get; private set; }
    public bool Busy => request != null;
    public void Poll(uint world)
    {
        currentWorld = world;
        if (request != null && request.IsCompleted)
        {
            try { var result=request.GetAwaiter().GetResult(); if(requestWorld==world){Prices=result;Updated=DateTimeOffset.Now;Status="Universalis最低在售价；众包数据可能过期。";} }
            catch(Exception ex){Status="价格查询失败："+ex.GetBaseException().Message;}
            request=null;
        }
        if (requestWorld!=world && Prices.Count>0){Prices=new Dictionary<uint,LogosPrice>();Updated=null;}
    }
    public void Refresh(uint world)
    {
        if(world==0 || request!=null || DateTimeOffset.UtcNow<nextRequest)return;
        requestWorld=currentWorld=world;nextRequest=DateTimeOffset.UtcNow.AddSeconds(30);Status="查询中…";
        request=Fetch(world);
    }
    private async Task<Dictionary<uint,LogosPrice>> Fetch(uint world)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,$"https://universalis.app/api/v2/aggregated/{world}/{string.Join(',',Catalog.Shards.Select(s=>s.Item))}");
        req.Headers.UserAgent.ParseAdd("ULKHelper/0.1.1");
        using var response=await http.SendAsync(req,cancel.Token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false));
        return Parse(doc.RootElement);
    }
    internal static Dictionary<uint,LogosPrice> Parse(JsonElement root)
    {
        var result=new Dictionary<uint,LogosPrice>();
        foreach(var r in root.GetProperty("results").EnumerateArray())
        {
            var listing=r.GetProperty("nq").GetProperty("minListing");
            int? Price(string scope)=>listing.TryGetProperty(scope,out var entry)&&entry.ValueKind==JsonValueKind.Object
                &&entry.TryGetProperty("price",out var p)&&p.TryGetInt32(out var n)&&n>0?n:null;
            result[r.GetProperty("itemId").GetUInt32()]=new(Price("world"),Price("dc"),Price("region"));
        }
        return result;
    }
    public double? Cost(uint[] recipe,int scope)
    {
        double total=0;
        foreach(var mneme in recipe)
        {
            var values=Catalog.Shards.Where(s=>s.Mnemes.Contains(mneme)).Select(s=>Prices.GetValueOrDefault(s.Item))
                .Where(p=>p!=null).Select(p=>scope==0?p!.World:scope==1?p!.Dc:p!.Region).Where(p=>p.HasValue).Select(p=>(double)p!.Value).ToArray();
            if(values.Length==0)return null;
            total+=values.Min();
        }
        return total;
    }
    public void Dispose(){cancel.Cancel();if(request!=null)_=request.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);http.Dispose();cancel.Dispose();}
}
