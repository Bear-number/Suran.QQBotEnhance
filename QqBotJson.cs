using System.Collections.Generic;
using System.Text.Json;

namespace Suran.QQBotEnhance;

/// <summary>
/// 官方机器人接口的 JSON 读法。成功响应直接给业务数据、失败给 {err_code,message,trace_id}，
/// 而同一批字段里数字和字符串会互换（expires_in 文档示例就是字符串），所以每个读数都两种都吃。
/// </summary>
static class QqBotJson
{
    public static JsonElement Child(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child))
        {
            return child;
        }
        return default;
    }

    public static string Text(JsonElement element, string name)
    {
        JsonElement child = Child(element, name);
        if (child.ValueKind == JsonValueKind.String)
        {
            return child.GetString() ?? "";
        }
        return "";
    }

    public static long Number(JsonElement element, long fallback, string name)
    {
        JsonElement child = Child(element, name);
        if (child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out long value))
        {
            return value;
        }
        if (child.ValueKind == JsonValueKind.String
            && long.TryParse(child.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }
        return fallback;
    }

    public static int Integer(JsonElement element, int fallback, string name)
    {
        return (int)Number(element, fallback, name);
    }

    public static List<JsonElement> Array(JsonElement element, string name)
    {
        List<JsonElement> items = [];
        JsonElement child = Child(element, name);
        if (child.ValueKind != JsonValueKind.Array)
        {
            return items;
        }
        foreach (JsonElement item in child.EnumerateArray())
        {
            items.Add(item);
        }
        return items;
    }

    public static bool Bool(JsonElement element, string name)
    {
        return Child(element, name).ValueKind == JsonValueKind.True;
    }
}
