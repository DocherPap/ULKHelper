using System.Numerics;

namespace ULKHelper.Weather;

internal static class WeatherPalette
{
    public static Vector4? Color(string weather) => weather switch
    {
        "强风" => new(.92f, .82f, .60f, 1),
        "暴雪" => new(.85f, .94f, 1f, 1),
        "热浪" => new(1f, .71f, .62f, 1),
        "打雷" or "雷雨" => new(.85f, .75f, 1f, 1),
        "薄雾" => new(.65f, .91f, .89f, 1),
        "灵风" => new(.84f, .94f, .62f, 1),
        _ => null,
    };
}
