namespace ULKHelper.Logos;
internal sealed record LogosOwnership(IReadOnlyDictionary<uint,int?> Items,string Status)
{
    public static readonly LogosOwnership Empty=new(new Dictionary<uint,int?>(),"碎晶数量：背包；记忆库存：请打开游戏文理碎晶列表获取。");
}

internal static class LogosStockDecoder
{
    // EurekaLogosShardList: distinct count then 4-value entries starting at 3; item at +2, quantity at +1.
    public static Dictionary<uint,int>? Decode(ReadOnlySpan<int> values,IReadOnlySet<uint> allowed)
    {
        if(values.Length==0||values[0]<0||values[0]>allowed.Count||3L+values[0]*4L>values.Length)return null;
        var result=allowed.ToDictionary(id=>id,_=>0);
        var used=new HashSet<uint>();
        for(var entry=0;entry<values[0];entry++)
        {
            var offset=3+entry*4;
            var item=values[offset+2];var amount=values[offset+1];
            if(item<=0||amount<0||!allowed.Contains((uint)item)||!used.Add((uint)item))return null;
            result[(uint)item]=amount;
        }
        return result;
    }
}
