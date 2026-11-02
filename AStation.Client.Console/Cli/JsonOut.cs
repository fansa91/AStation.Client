using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AStation_Client.Cli;

/// <summary>
/// <c>--json</c> 统一输出：缩进 + 不转义中文 + 不输出 null 字段。
/// 管道给别的工具用时直接是可用 JSON。
/// </summary>
public static class JsonOut
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 服务层模型是 PascalCase；JSON 里用 camelCase 更顺手（jq / JS 直接读）
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Options));

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);
}
