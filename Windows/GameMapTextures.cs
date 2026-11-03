using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace ULKHelper.Windows;

internal sealed class GameMapTextures(ITextureProvider textures) : IMapTextures
{
    public ImTextureID? Get(string path, out string? error)
    {
        var shared = textures.GetFromGame(path);
        var loaded = shared.TryGetWrap(out var wrap, out var exception);
        error = exception == null ? null : "地图纹理加载失败。";
        return loaded ? wrap!.Handle : null;
    }
}
