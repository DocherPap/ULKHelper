namespace ULKHelper.Data;

// Display data only. Keys are UI identifiers, not game Item RowIds.
// Provenance and scope: docs/ITEM_STATISTICS.md.
internal enum EquipmentCategory { Weapon, Artifact, Elemental, Attire }
internal sealed record JobOption(string Key, string Name, string Role);
internal sealed record MaterialCost(string Material, int Quantity);
internal sealed record EquipmentStage(string Name, bool Major, params MaterialCost[] Costs);
internal sealed record EquipmentCard(string Key, string Name, EquipmentCategory Category,
    string[] Jobs, EquipmentStage[] Stages, string Note = "", bool SpecialArmor = false);
internal sealed record MaterialOption(string Name, string? ExchangeMaterial = null, double ExchangeCost = 0,
    bool Approximate = false);

internal static class StatisticsCatalog
{
    internal static readonly string[] CategoryNames = ["主手", "70级校服", "优雷卡补正", "小黑衣 / 小红衣"];
    internal static readonly JobOption[] Jobs =
    [
        new("PLD", "骑士", "防护"), new("WAR", "战士", "防护"),
        new("DRK", "暗黑骑士", "防护"),
        new("WHM", "白魔法师", "治疗"), new("SCH", "学者", "治疗"),
        new("AST", "占星术士", "治疗"),
        new("MNK", "武僧", "强袭"), new("SAM", "武士", "强袭"),
        new("DRG", "龙骑士", "制敌"),
        new("NIN", "忍者", "游击"),
        new("BRD", "吟游诗人", "精准"), new("MCH", "机工士", "精准"),
        new("BLM", "黑魔法师", "咏咒"), new("SMN", "召唤师", "咏咒"),
        new("RDM", "赤魔法师", "咏咒"),
    ];

    internal static readonly MaterialOption[] Materials =
    [
        new("乱属性水晶", "常风水晶", 1 / 3.5, true), new("常风水晶"),
        new("帕祖祖的羽毛", "乱属性水晶", 300),
        new("结冰乱属性水晶"), new("恒冰水晶"), new("娄希的冰片", "恒冰水晶", 50),
        new("涌火水晶"), new("彭忒西勒亚的火种", "涌火水晶", 50),
        new("丰水水晶"), new("水晶龙之鳞"), new("优雷卡的断片"),
        new("神秘印章"), new("大马士革织物"),
    ];

    internal static readonly EquipmentCard[] Equipment = CreateEquipment();

    internal static readonly (string Title, string[] Materials)[] MaterialRows =
    [
        ("常风之地", ["乱属性水晶", "常风水晶", "帕祖祖的羽毛"]),
        ("恒冰之地", ["结冰乱属性水晶", "恒冰水晶", "娄希的冰片"]),
        ("涌火之地", ["涌火水晶", "彭忒西勒亚的火种"]),
        ("丰水之地", ["丰水水晶", "水晶龙之鳞", "优雷卡的断片"]),
        ("印章与织物", ["神秘印章", "大马士革织物"]),
    ];

    private static EquipmentStage[] WeaponStages() =>
    [
        new("禁地兵装", false, new MaterialCost("乱属性水晶", 100)),
        new("禁地兵装+1", false, new MaterialCost("乱属性水晶", 400)),
        new("禁地兵装+2", false, new MaterialCost("乱属性水晶", 800)),
        new("禁地兵装·常风", true, new MaterialCost("帕祖祖的羽毛", 3)),
        new("禁地兵装·恒冰", false, new MaterialCost("结冰乱属性水晶", 5)),
        new("禁地兵装·恒冰+1", false, new MaterialCost("结冰乱属性水晶", 10), new MaterialCost("恒冰水晶", 500)),
        new("禁地兵装·元素", true, new MaterialCost("结冰乱属性水晶", 16), new MaterialCost("娄希的冰片", 5)),
        new("禁地兵装·元素+1", false, new MaterialCost("涌火水晶", 150)),
        new("禁地兵装·元素+2", false, new MaterialCost("涌火水晶", 200)),
        new("禁地兵装·涌火", true, new MaterialCost("涌火水晶", 300), new MaterialCost("彭忒西勒亚的火种", 5)),
        new("禁地兵装·丰水", false, new MaterialCost("丰水水晶", 50)),
        new("禁地兵装·丰水+1", false, new MaterialCost("丰水水晶", 100)),
        new("新禁地兵装", false, new MaterialCost("丰水水晶", 100)),
        new("禁地兵装最终形态", true, new MaterialCost("丰水水晶", 100), new MaterialCost("水晶龙之鳞", 5)),
        new("禁地兵装·改装", true, new MaterialCost("优雷卡的断片", 100)),
    ];

    private static EquipmentStage[] ArtifactStages() =>
    [
        new("禁地兵装", true, new MaterialCost("乱属性水晶", 50)),
        new("禁地兵装+1", true, new MaterialCost("乱属性水晶", 150)),
        new("禁地兵装+2", true, new MaterialCost("乱属性水晶", 400)),
        new("禁地兵装·常风", true, new MaterialCost("常风水晶", 150)),
    ];

    private static EquipmentStage[] ElementalStages(bool largeSlot) =>
    [
        new("元素防具", true, new MaterialCost("涌火水晶", 40)),
        new("元素防具+1", true, new MaterialCost("丰水水晶", largeSlot ? 50 : 30)),
        new("元素防具+2", true, new MaterialCost("优雷卡的断片", largeSlot ? 35 : 21)),
    ];

    private static EquipmentCard[] CreateEquipment()
    {
        var cards = new List<EquipmentCard>();
        (string Job, string Weapon, string[] Armor)[] artifactSets =
        [
            ("PLD", "安忒亚", ["侠义头冠", "侠义战甲", "侠义手铠", "侠义腿甲", "侠义锁甲靴"]),
            ("WAR", "沙玛什", ["残暴面罩", "残暴兜甲", "残暴手铠", "残暴马裤", "残暴锁甲靴"]),
            ("DRK", "剑鱼", ["深渊轻盔", "深渊胸甲", "深渊手铠", "深渊重足铠", "深渊锁甲靴"]),
            ("WHM", "夜蔷薇", ["七天头冠", "七天上装", "七天半指护手", "七天打底裤", "七天长靴"]),
            ("SCH", "杰巴特", ["雄辩方帽", "雄辩外套", "雄辩护袖", "雄辩打底裤", "雄辩皮靴"]),
            ("AST", "辇道增七", ["星座包头巾", "星座上装", "星座护臂", "星座长裤", "星座凉鞋"]),
            ("MNK", "杜穆齐德", ["和平头冠", "和平坎肩", "和平护臂", "和平短裤", "和平筒靴"]),
            ("SAM", "鸟头太刀", ["明珍阵笠", "明珍羽织", "明珍手甲", "明珍袴", "明珍木屐"]),
            ("DRG", "璎珞蛇", ["真血战盔", "真血战铠", "真血手铠", "真血长裤", "真血胫甲"]),
            ("NIN", "鹊", ["影隐总面", "影隐锁帷子", "影隐手甲", "影隐袴", "影隐缠足"]),
            ("BRD", "泽鹰", ["述事礼帽", "述事外套", "述事手套", "述事打底裤", "述事长靴"]),
            ("MCH", "玛丽弗里斯", ["炮手护目镜", "炮手外套", "炮手手套", "炮手马裤", "炮手长靴"]),
            ("BLM", "座头鲸", ["七狱阔边帽", "七狱外套", "七狱手套", "七狱马裤", "七狱长靴"]),
            ("SMN", "图亚", ["通灵长角", "通灵坎肩", "通灵护臂", "通灵五分裤", "通灵筒靴"]),
            ("RDM", "布鲁奈罗", ["斗剑礼帽", "斗剑罩衣", "斗剑手套", "斗剑马裤", "斗剑长靴"]),
        ];
        foreach (var (job, weapon, armor) in artifactSets)
        {
            cards.Add(new($"weapon-{job}", weapon, EquipmentCategory.Weapon, [job], WeaponStages(),
                job == "PLD" ? "骑士按剑盾整套展示与计材，包含柏勒洛丰；不重复累计副手。" : ""));
            for (var slot = 0; slot < armor.Length; slot++)
                cards.Add(new($"artifact-{job}-{slot}", armor[slot], EquipmentCategory.Artifact,
                    [job], ArtifactStages()));
        }

        (string Role, string[] Armor, string Special)[] elementalSets =
        [
            ("防护", ["元素御敌头盔", "元素御敌战甲", "元素御敌手铠", "元素御敌软甲裤", "元素御敌皮靴"], "麒麟豪士大袖"),
            ("治疗", ["元素治愈头冠", "元素治愈长衣", "元素治愈手套", "元素治愈打底裤", "元素治愈皮靴"], "朱红学士斗篷"),
            ("强袭", ["元素强袭狩猎帽", "元素强袭短衣", "元素强袭手套", "元素强袭马裤", "元素强袭长靴"], "麒麟拳士大袖"),
            ("制敌", ["元素制敌头盔", "元素制敌战甲", "元素制敌手铠", "元素制敌软甲裤", "元素制敌皮靴"], "麒麟枪士大袖"),
            ("游击", ["元素游击头冠", "元素游击短衣", "元素游击手套", "元素游击马裤", "元素游击长靴"], "麒麟间士大袖"),
            ("精准", ["元素精准狩猎帽", "元素精准短衣", "元素精准手套", "元素精准马裤", "元素精准长靴"], "麒麟弓士大袖"),
            ("咏咒", ["元素咏咒头冠", "元素咏咒长衣", "元素咏咒手套", "元素咏咒打底裤", "元素咏咒皮靴"], "朱红术士斗篷"),
        ];
        foreach (var (role, armor, special) in elementalSets)
        {
            var jobs = Jobs.Where(j => j.Role == role).Select(j => j.Key).ToArray();
            for (var slot = 0; slot < armor.Length; slot++)
                cards.Add(new($"elemental-{role}-{slot}", armor[slot], EquipmentCategory.Elemental,
                    jobs, ElementalStages(slot is 1 or 3)));
            var magic = role is "治疗" or "咏咒";
            cards.Add(new($"special-{role}", special, EquipmentCategory.Attire, jobs,
                [new("兑换成品", true, new MaterialCost(magic ? "大马士革织物" : "神秘印章", magic ? 3 : 2))], SpecialArmor: true));
        }
        return cards.ToArray();
    }
}
