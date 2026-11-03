using System.Numerics;
using ULKHelper;

var root = Path.GetFullPath(Path.Combine("artifacts", "settings-tests", Guid.NewGuid().ToString()));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "settings.json");
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
try
{
    var settings = new SettingsStore(path);
    Check(settings.Get("new.option", 23) == 23, "新增设置使用默认值");
    settings.Put("filter", new[] { true, false }); settings.Put("size", new Vector2(1280,780)); settings.Put("future", "保留未知键"); settings.Flush(true);
    var restored = new SettingsStore(path);
    Check(restored.Get("filter", Array.Empty<bool>()).SequenceEqual(new[] { true, false }), "筛选列表跨更新恢复");
    Check(restored.Get("size", Vector2.Zero) == new Vector2(1280,780), "窗口尺寸字段正确序列化");
    restored.Put("new.option", 9); restored.Flush(true);
    var next = new SettingsStore(path);
    Check(next.Get("future", "") == "保留未知键", "更新不删除未知版本设置");
    Check(next.Get("new.option", 0) == 9, "修改覆盖原键并原子写入");
    Check(next.Get("filter", 7) == 7, "字段类型变化回退默认");
    Check(!File.Exists(path + ".tmp"), "成功保存不遗留临时文件");
    File.WriteAllText(path, "{ invalid");
    var errors = 0; var broken = new SettingsStore(path, _ => errors++);
    Check(errors == 1 && Directory.GetFiles(root,"*.invalid-*").Length == 1, "损坏配置保留原文备份");
    broken.Put("recovered", true); broken.Flush(true);
    Check(new SettingsStore(path).Get("recovered", false), "损坏文件备份后允许重新保存");
    Console.WriteLine($"{count} settings checks passed.");
}
finally { foreach (var file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
