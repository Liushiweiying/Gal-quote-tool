using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalQuoteCollector.Services;

/// <summary>
/// System.Text.Json 默认**不能**把 double.NaN / Infinity 写成 JSON（会抛
/// "positive and negative infinity cannot be written as valid JSON"）。
/// HotkeyConfig 里 WindowLeft/Top/Width/Height 的"未设置"就是用 NaN 表示的
/// （应用窗口尺寸的代码用 double.IsNaN 判断"用户还没动过窗口"），
/// 所以这里把 NaN / ±Infinity 写成 <c>null</c>，读回来再还原成 NaN —— 语义完全不变。
/// </summary>
public sealed class NaNDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return double.NaN;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.Equals(s, "NaN", StringComparison.OrdinalIgnoreCase)) return double.NaN;
            if (string.Equals(s, "Infinity", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            if (string.Equals(s, "-Infinity", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
        }
        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value);
    }
}
