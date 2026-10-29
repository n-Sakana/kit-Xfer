using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Xfer
{
    internal static class JsonCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Serialize(object value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value);
            return Utf8.GetBytes(builder.ToString());
        }

        public static Dictionary<string, object> ParseObject(byte[] bytes)
        {
            string text;
            try
            {
                text = Utf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ArgumentException("JSON request body is not valid UTF-8.", exception);
            }

            var parser = new Parser(text);
            var value = parser.Parse();
            var result = value as Dictionary<string, object>;
            if (result == null)
            {
                throw new ArgumentException("JSON request body must be an object.");
            }

            return result;
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            var text = value as string;
            if (text != null)
            {
                WriteString(builder, text);
                return;
            }

            if (value is char)
            {
                WriteString(builder, value.ToString());
                return;
            }

            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is DateTimeOffset)
            {
                WriteString(builder, ((DateTimeOffset)value).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                return;
            }

            if (value is DateTime)
            {
                WriteString(builder, ((DateTime)value).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                return;
            }

            var type = value.GetType();
            if (type.IsEnum)
            {
                WriteString(builder, value.ToString());
                return;
            }

            if (IsNumber(type))
            {
                WriteNumber(builder, value);
                return;
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                WriteDictionary(builder, dictionary);
                return;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                WriteArray(builder, enumerable);
                return;
            }

            WriteObject(builder, value);
        }

        private static bool IsNumber(Type type)
        {
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong)
                || type == typeof(float)
                || type == typeof(double)
                || type == typeof(decimal);
        }

        private static void WriteNumber(StringBuilder builder, object value)
        {
            if (value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value)))
            {
                builder.Append("null");
                return;
            }

            if (value is float && (float.IsNaN((float)value) || float.IsInfinity((float)value)))
            {
                builder.Append("null");
                return;
            }

            builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteDictionary(StringBuilder builder, IDictionary dictionary)
        {
            builder.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                WriteString(builder, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                builder.Append(':');
                WriteValue(builder, entry.Value);
                first = false;
            }

            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, IEnumerable values)
        {
            builder.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                WriteValue(builder, value);
                first = false;
            }

            builder.Append(']');
        }

        private static void WriteObject(StringBuilder builder, object value)
        {
            var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(properties, delegate (PropertyInfo left, PropertyInfo right)
            {
                return StringComparer.Ordinal.Compare(left.Name, right.Name);
            });

            builder.Append('{');
            var first = true;
            foreach (var property in properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(',');
                }

                WriteString(builder, CamelCase(property.Name));
                builder.Append(':');
                WriteValue(builder, property.GetValue(value, null));
                first = false;
            }

            builder.Append('}');
        }

        private static string CamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            {
                return name;
            }

            if (name.Length == 1)
            {
                return char.ToLowerInvariant(name[0]).ToString();
            }

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
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
                        if (character < 0x20 || character == '\u2028' || character == '\u2029')
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }

            builder.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _position;

            public Parser(string text)
            {
                _text = text ?? string.Empty;
            }

            public object Parse()
            {
                SkipWhitespace();
                var value = ParseValue();
                SkipWhitespace();
                if (_position != _text.Length)
                {
                    Fail();
                }

                return value;
            }

            private object ParseValue()
            {
                if (_position >= _text.Length)
                {
                    Fail();
                }

                switch (_text[_position])
                {
                    case '{':
                        return ParseObject();
                    case '[':
                        return ParseArray();
                    case '"':
                        return ParseString();
                    case 't':
                        Expect("true");
                        return true;
                    case 'f':
                        Expect("false");
                        return false;
                    case 'n':
                        Expect("null");
                        return null;
                    default:
                        return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _position++;
                SkipWhitespace();
                if (Take('}'))
                {
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (_position >= _text.Length || _text[_position] != '"')
                    {
                        Fail();
                    }

                    var key = ParseString();
                    SkipWhitespace();
                    Require(':');
                    SkipWhitespace();
                    result[key] = ParseValue();
                    SkipWhitespace();

                    if (Take('}'))
                    {
                        return result;
                    }

                    Require(',');
                }
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                _position++;
                SkipWhitespace();
                if (Take(']'))
                {
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (Take(']'))
                    {
                        return result;
                    }

                    Require(',');
                }
            }

            private string ParseString()
            {
                Require('"');
                var result = new StringBuilder();
                while (_position < _text.Length)
                {
                    var character = _text[_position++];
                    if (character == '"')
                    {
                        return result.ToString();
                    }

                    if (character < 0x20)
                    {
                        Fail();
                    }

                    if (character != '\\')
                    {
                        result.Append(character);
                        continue;
                    }

                    if (_position >= _text.Length)
                    {
                        Fail();
                    }

                    character = _text[_position++];
                    switch (character)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            result.Append(character);
                            break;
                        case 'b':
                            result.Append('\b');
                            break;
                        case 'f':
                            result.Append('\f');
                            break;
                        case 'n':
                            result.Append('\n');
                            break;
                        case 'r':
                            result.Append('\r');
                            break;
                        case 't':
                            result.Append('\t');
                            break;
                        case 'u':
                            result.Append(ParseUnicodeEscape());
                            break;
                        default:
                            Fail();
                            break;
                    }
                }

                Fail();
                return null;
            }

            private char ParseUnicodeEscape()
            {
                if (_position + 4 > _text.Length)
                {
                    Fail();
                }

                int code;
                if (!int.TryParse(
                    _text.Substring(_position, 4),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out code))
                {
                    Fail();
                }

                _position += 4;
                return (char)code;
            }

            private object ParseNumber()
            {
                var start = _position;
                if (Take('-'))
                {
                }

                if (Take('0'))
                {
                    // A leading zero stands alone.
                }
                else
                {
                    RequireDigits();
                }

                if (Take('.'))
                {
                    RequireDigits();
                }

                if (Take('e') || Take('E'))
                {
                    if (Take('+') || Take('-'))
                    {
                    }

                    RequireDigits();
                }

                var raw = _text.Substring(start, _position - start);
                decimal decimalValue;
                if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue))
                {
                    return decimalValue;
                }

                double doubleValue;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue)
                    && !double.IsNaN(doubleValue)
                    && !double.IsInfinity(doubleValue))
                {
                    return doubleValue;
                }

                Fail();
                return null;
            }

            private void RequireDigits()
            {
                var start = _position;
                while (_position < _text.Length && char.IsDigit(_text[_position]))
                {
                    _position++;
                }

                if (_position == start)
                {
                    Fail();
                }
            }

            private void Expect(string expected)
            {
                if (_position + expected.Length > _text.Length
                    || string.CompareOrdinal(_text, _position, expected, 0, expected.Length) != 0)
                {
                    Fail();
                }

                _position += expected.Length;
            }

            private bool Take(char expected)
            {
                if (_position < _text.Length && _text[_position] == expected)
                {
                    _position++;
                    return true;
                }

                return false;
            }

            private void Require(char expected)
            {
                if (!Take(expected))
                {
                    Fail();
                }
            }

            private void SkipWhitespace()
            {
                while (_position < _text.Length)
                {
                    var character = _text[_position];
                    if (character != ' ' && character != '\t' && character != '\r' && character != '\n')
                    {
                        return;
                    }

                    _position++;
                }
            }

            private void Fail()
            {
                throw new ArgumentException(string.Format(
                    "JSON request body is invalid near character {0}.",
                    _position + 1));
            }
        }
    }
}
