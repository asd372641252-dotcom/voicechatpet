using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static class TransparentPetJson
{
    public static object Parse(string json)
    {
        return new Parser(json).ParseValue();
    }

    public static string Stringify(object value, bool pretty = true)
    {
        StringBuilder builder = new StringBuilder();
        WriteValue(builder, value, pretty, 0);
        return builder.ToString();
    }

    public static Dictionary<string, object> AsObject(object value)
    {
        return value as Dictionary<string, object>;
    }

    public static List<object> AsArray(object value)
    {
        return value as List<object>;
    }

    public static string GetString(Dictionary<string, object> data, string key, string fallback = "")
    {
        if (data == null || !data.TryGetValue(key, out object value) || value == null)
        {
            return fallback;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public static bool GetBool(Dictionary<string, object> data, string key, bool fallback = false)
    {
        if (data == null || !data.TryGetValue(key, out object value) || value == null)
        {
            return fallback;
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed) ? parsed : fallback;
    }

    public static int GetInt(Dictionary<string, object> data, string key, int fallback = 0)
    {
        if (data == null || !data.TryGetValue(key, out object value))
        {
            return fallback;
        }

        return ToInt(value, fallback);
    }

    public static float GetFloat(Dictionary<string, object> data, string key, float fallback = 0f)
    {
        if (data == null || !data.TryGetValue(key, out object value))
        {
            return fallback;
        }

        return ToFloat(value, fallback);
    }

    public static int ToInt(object value, int fallback = 0)
    {
        return MathfRoundToInt(ToFloat(value, fallback));
    }

    public static float ToFloat(object value, float fallback = 0f)
    {
        if (value == null)
        {
            return fallback;
        }

        if (value is float f) return f;
        if (value is double d) return (float)d;
        if (value is long l) return l;
        if (value is int i) return i;
        if (value is decimal m) return (float)m;
        return float.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;
    }

    private static int MathfRoundToInt(float value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static void WriteValue(StringBuilder builder, object value, bool pretty, int indent)
    {
        if (value == null)
        {
            builder.Append("null");
            return;
        }

        if (value is string text)
        {
            WriteString(builder, text);
            return;
        }

        if (value is bool boolValue)
        {
            builder.Append(boolValue ? "true" : "false");
            return;
        }

        if (value is Dictionary<string, object> objectValue)
        {
            WriteObject(builder, objectValue, pretty, indent);
            return;
        }

        if (value is List<object> arrayValue)
        {
            WriteArray(builder, arrayValue, pretty, indent);
            return;
        }

        if (TryWriteNumber(builder, value))
        {
            return;
        }

        WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static void WriteObject(StringBuilder builder, Dictionary<string, object> data, bool pretty, int indent)
    {
        builder.Append('{');
        if (data.Count == 0)
        {
            builder.Append('}');
            return;
        }

        int index = 0;
        foreach (KeyValuePair<string, object> pair in data)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            if (pretty)
            {
                builder.AppendLine();
                WriteIndent(builder, indent + 1);
            }

            WriteString(builder, pair.Key);
            builder.Append(pretty ? ": " : ":");
            WriteValue(builder, pair.Value, pretty, indent + 1);
            index++;
        }

        if (pretty)
        {
            builder.AppendLine();
            WriteIndent(builder, indent);
        }

        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, List<object> data, bool pretty, int indent)
    {
        builder.Append('[');
        if (data.Count == 0)
        {
            builder.Append(']');
            return;
        }

        for (int i = 0; i < data.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            if (pretty)
            {
                builder.AppendLine();
                WriteIndent(builder, indent + 1);
            }

            WriteValue(builder, data[i], pretty, indent + 1);
        }

        if (pretty)
        {
            builder.AppendLine();
            WriteIndent(builder, indent);
        }

        builder.Append(']');
    }

    private static bool TryWriteNumber(StringBuilder builder, object value)
    {
        switch (value)
        {
            case byte byteValue:
                builder.Append(byteValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case sbyte sbyteValue:
                builder.Append(sbyteValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case short shortValue:
                builder.Append(shortValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case ushort ushortValue:
                builder.Append(ushortValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case int intValue:
                builder.Append(intValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case uint uintValue:
                builder.Append(uintValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case long longValue:
                builder.Append(longValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case ulong ulongValue:
                builder.Append(ulongValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case float floatValue:
                builder.Append(float.IsNaN(floatValue) || float.IsInfinity(floatValue)
                    ? "0"
                    : floatValue.ToString("R", CultureInfo.InvariantCulture));
                return true;
            case double doubleValue:
                builder.Append(double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)
                    ? "0"
                    : doubleValue.ToString("R", CultureInfo.InvariantCulture));
                return true;
            case decimal decimalValue:
                builder.Append(decimalValue.ToString(CultureInfo.InvariantCulture));
                return true;
            default:
                return false;
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        string text = value ?? string.Empty;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 32 || c > 126)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }

        builder.Append('"');
    }

    private static void WriteIndent(StringBuilder builder, int indent)
    {
        for (int i = 0; i < indent; i++)
        {
            builder.Append("  ");
        }
    }

    private sealed class Parser
    {
        private readonly string _json;
        private int _index;

        public Parser(string json)
        {
            _json = json ?? string.Empty;
        }

        public object ParseValue()
        {
            SkipWhitespace();
            if (_index >= _json.Length)
            {
                return null;
            }

            char c = _json[_index];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (c == 't' && Match("true")) return true;
            if (c == 'f' && Match("false")) return false;
            if (c == 'n' && Match("null")) return null;
            return ParseNumber();
        }

        private Dictionary<string, object> ParseObject()
        {
            Dictionary<string, object> data = new Dictionary<string, object>(StringComparer.Ordinal);
            _index++;
            while (true)
            {
                SkipWhitespace();
                if (_index >= _json.Length)
                {
                    return data;
                }

                if (_json[_index] == '}')
                {
                    _index++;
                    return data;
                }

                string key = ParseString();
                SkipWhitespace();
                if (_index < _json.Length && _json[_index] == ':')
                {
                    _index++;
                }

                data[key] = ParseValue();
                SkipWhitespace();
                if (_index < _json.Length && _json[_index] == ',')
                {
                    _index++;
                }
            }
        }

        private List<object> ParseArray()
        {
            List<object> data = new List<object>();
            _index++;
            while (true)
            {
                SkipWhitespace();
                if (_index >= _json.Length)
                {
                    return data;
                }

                if (_json[_index] == ']')
                {
                    _index++;
                    return data;
                }

                data.Add(ParseValue());
                SkipWhitespace();
                if (_index < _json.Length && _json[_index] == ',')
                {
                    _index++;
                }
            }
        }

        private string ParseString()
        {
            StringBuilder builder = new StringBuilder();
            if (_index < _json.Length && _json[_index] == '"')
            {
                _index++;
            }

            while (_index < _json.Length)
            {
                char c = _json[_index++];
                if (c == '"')
                {
                    break;
                }

                if (c != '\\' || _index >= _json.Length)
                {
                    builder.Append(c);
                    continue;
                }

                char escaped = _json[_index++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (_index + 4 <= _json.Length)
                        {
                            string hex = _json.Substring(_index, 4);
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                builder.Append((char)code);
                            }
                            _index += 4;
                        }
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return builder.ToString();
        }

        private object ParseNumber()
        {
            int start = _index;
            while (_index < _json.Length)
            {
                char c = _json[_index];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                {
                    _index++;
                    continue;
                }

                break;
            }

            string token = _json.Substring(start, _index - start);
            if (token.IndexOf('.') >= 0 || token.IndexOf('e') >= 0 || token.IndexOf('E') >= 0)
            {
                return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDouble) ? parsedDouble : 0d;
            }

            return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLong) ? parsedLong : 0L;
        }

        private bool Match(string text)
        {
            if (_index + text.Length > _json.Length || string.CompareOrdinal(_json, _index, text, 0, text.Length) != 0)
            {
                return false;
            }

            _index += text.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
            {
                _index++;
            }
        }
    }
}
