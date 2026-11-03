using System.Numerics;
using ULKHelper.Data;

namespace ULKHelper.Windows;

internal static class StatisticsPalette
{
    // Anemos, Pagos, Pyros, Hydatos, final upgrade/exchange.
    private static readonly (Vector4 Pale, Vector4 Bright)[] Colors =
    [
        (new(.80f,.86f,.56f,1), new(.71f,.86f,.17f,1)),
        (new(.58f,.78f,.96f,1), new(.19f,.56f,1,1)),
        (new(1,.64f,.64f,1), new(.98f,.28f,.29f,1)),
        (new(.55f,.88f,.84f,1), new(.13f,.79f,.70f,1)),
        (new(.76f,.64f,.50f,1), new(.66f,.43f,.24f,1)),
    ];
    public static (Vector4 Pale, Vector4 Bright) Material(string name) => Colors[name switch
    {
        "结冰乱属性水晶" or "恒冰水晶" or "娄希的冰片" => 1,
        "涌火水晶" or "彭忒西勒亚的火种" => 2,
        "丰水水晶" or "水晶龙之鳞" => 3,
        "优雷卡的断片" or "神秘印章" or "大马士革织物" => 4,
        _ => 0,
    }];
    public static (Vector4 Pale, Vector4 Bright) Stage(EquipmentCard card, int index)
    {
        var material = card.Stages[index].Costs.FirstOrDefault()?.Material;
        return material == null ? Colors[4] : Material(material);
    }
}
