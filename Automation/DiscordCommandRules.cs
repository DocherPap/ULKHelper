using System.Text;
using System.Text.RegularExpressions;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal readonly record struct DiscordTarget(uint World = 0, uint Territory = 0, ushort Fate = 0);

internal sealed class DiscordCommandRules
{
    private static readonly (uint Id, string[] Names)[] Islands =
    [
        (732, ["风", "常风", "常风之地"]),
        (763, ["冰", "恒冰", "恒冰之地"]),
        (795, ["火", "涌火", "涌火之地"]),
        (827, ["水", "丰水", "丰水之地"]),
    ];
    private static readonly (ushort Id, string Alias)[] Specials =
    [
        (1329, "pzz"), (1365, "kx"), (1364, "lx"), (1360, "px"),
        (1404, "pnz"), (1403, "狗子"), (1402, "yy"), (1398, "gbl"),
        (1414, "ml"), (1418, "mw"), (1423, "水晶龙"), (1419, "lk"),
        (1421, "kt"), (1420, "bl"), (1424, "ufo"),
    ];
    private static readonly (ushort Id, string Aliases)[] Pinyin =
    [
        (1329, "pazuzu"), (1365, "kaixi"), (1364, "louxi"), (1360, "pangxie"),
        (1404, "pengnvshi pengtexileiya"), (1403, "gouzi skul"), (1402, "yinyang"),
        (1398, "gebulin"), (1414, "moluo"), (1418, "mawang"), (1423, "shuijinglong swz"),
        (1419, "liuke"), (1421, "ketuo"), (1420, "balong"),
        (1332, "xrz xianrenzhang"), (1348, "zy zhangyu"), (1333, "niao"),
        (1328, "qt qingting"), (1344, "xiong"), (1347, "qo qunou"), (1345, "tf taifeng"),
        (1334, "bl baolong"), (1335, "gy gaiyin"), (1336, "jgg jugaogao"),
        (1339, "xz xiezi"), (1346, "mjh mojiehua"), (1343, "bqs baiqishi"),
        (1337, "dy duyan"), (1342, "kbxmn kuobuximuniao"), (1341, "jqwxwz jiqiweixianwuzhi"),
        (1331, "ffn fafuna"), (1340, "amlk amaluoke"), (1338, "lmst lamashitu"),
        (1351, "zdy zhoudongyu"), (1369, "dsr dushuren"), (1353, "hjl huijinlong"),
        (1354, "mc mochong"), (1355, "anb annabo"), (1366, "bz baize"),
        (1357, "xww xuewuwang"), (1356, "asg asage"), (1352, "sy shanyang"),
        (1358, "sn shuangniu"), (1361, "sn shengniu"), (1362, "by beiye"),
        (1359, "hls helusi"), (1363, "dy dayan"),
        (1388, "cj canjiao"), (1389, "ls leishou"), (1390, "gbz guibianzhe"),
        (1391, "ttl tatalu"), (1392, "af afu"), (1393, "dg dagong"),
        (1394, "ln leiniao"), (1395, "xz xiezi"), (1396, "hjr huojuren"),
        (1397, "hy haiyan"), (1399, "lj leijun"), (1400, "sr shuren"), (1401, "mm mingmou"),
        (1412, "my moyu"), (1413, "xiang"), (1415, "pn piniao"), (1416, "lh laohu"),
        (1417, "dfn dafonie"), (1422, "gly guanglingyao"),
    ];
    private readonly (string Name, DiscordTarget Target)[] aliases;

    public DiscordCommandRules(IEnumerable<(string Name, uint Id)> worlds, Func<ushort, string> fateName)
    {
        aliases = worlds.Select(w => (Normalize(w.Name), new DiscordTarget(World: w.Id)))
            .Concat(Islands.SelectMany(i => i.Names.Select(n => (n, new DiscordTarget(Territory: i.Id)))))
            .Concat(FateSeeds.All.Where(f => !f.Bunny).SelectMany(f => new[] { f.ShortName, fateName(f.Id) }
                .Select(n => (Normalize(n), new DiscordTarget(Territory: f.Territory, Fate: f.Id)))))
            .Concat(Specials.Select(f => (f.Alias, new DiscordTarget(Territory: FateSeeds.All.Single(s => s.Id == f.Id).Territory, Fate: f.Id))))
            .Concat(Pinyin.SelectMany(f => f.Aliases.Split(' ').Select(alias => (alias,
                new DiscordTarget(Territory: FateSeeds.All.Single(s => s.Id == f.Id).Territory, Fate: f.Id)))))
            .Where(a => a.Item1.Length > 0).Distinct().ToArray();
    }

    public static string Normalize(string input) =>
        Regex.Replace(input.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^a-z\u3400-\u9fff]", "");

    // Verified against the local CN World sheet. IsPublic is false for these CN rows.
    public static bool ChineseWorld(uint id) => id is
        1042 or 1044 or 1060 or 1081 or 1167 or 1173 or 1174 or 1175
        or 1076 or 1113 or 1121 or 1166 or 1170 or 1171 or 1172 or 1176
        or 1043 or 1045 or 1106 or 1169 or 1177 or 1178 or 1179
        or 1180 or 1183 or 1186 or 1192 or 1200 or 1201;

    public static bool Special(ushort id) => Specials.Any(f => f.Id == id);

    public static CommonAction Entry(uint island) => island switch
    {
        732 => CommonAction.Anemos, 763 => CommonAction.Pagos,
        795 => CommonAction.Pyros, 827 => CommonAction.Hydatos,
        _ => throw new ArgumentOutOfRangeException(nameof(island)),
    };

    public DiscordTarget[] Parse(string input, uint preferredIsland)
    {
        var text = Normalize(input);
        if (text.StartsWith("进岛", StringComparison.Ordinal)) text = text[2..];
        if (text.Length == 0) return [];
        var results = new HashSet<DiscordTarget>();
        Visit(text, default, 0);
        return results.ToArray();

        void Visit(string remaining, DiscordTarget selected, int used)
        {
            if (remaining.Length == 0)
            {
                if (preferredIsland == 0 || selected.Territory == 0 || selected.Territory == preferredIsland)
                    results.Add(selected);
                return;
            }
            foreach (var (name, token) in aliases)
            {
                var kind = token.World != 0 ? 1 : token.Fate != 0 ? 4 : 2;
                if ((used & kind) != 0 || !remaining.StartsWith(name, StringComparison.Ordinal)
                    || selected.Territory != 0 && token.Territory != 0 && selected.Territory != token.Territory) continue;
                Visit(remaining[name.Length..], new(
                    token.World == 0 ? selected.World : token.World,
                    token.Territory == 0 ? selected.Territory : token.Territory,
                    token.Fate == 0 ? selected.Fate : token.Fate), used | kind);
            }
        }
    }
}
