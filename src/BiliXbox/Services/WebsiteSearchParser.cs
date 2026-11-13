using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BiliXbox.Services
{
    /// <summary>Reads the public search page's serialized data; never runs page JavaScript.</summary>
    internal static class WebsiteSearchParser
    {
        private static readonly TimeSpan HtmlMatchTimeout = TimeSpan.FromSeconds(1);

        internal static JToken Parse(string html, string keyword, int page)
            => ParseTyped(html, keyword, page, "/video", "video");

        internal static JToken ParseUsers(string html, string keyword, int page)
            => ParseTyped(html, keyword, page, "/upuser", "bili_user");

        private static JToken ParseTyped(string html, string keyword, int page, string path, string resultType)
        {
            try
            {
                if (html == null || html.Length > 6 * 1024 * 1024) throw new FormatException();
                var title = Regex.Match(html, @"<title>([^<]*)</title>", RegexOptions.IgnoreCase,
                    HtmlMatchTimeout);
                if (!title.Success || WebUtility.HtmlDecode(title.Groups[1].Value).Trim()
                    != keyword + "-哔哩哔哩_bilibili") throw new FormatException();
                var canonical = Regex.Match(html, "<link\\b(?=[^>]*\\brel=\"canonical\")[^>]*\\bhref=\"([^\"]+)\"[^>]*>",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlMatchTimeout);
                Uri address;
                if (!canonical.Success || !Uri.TryCreate(WebUtility.HtmlDecode(canonical.Groups[1].Value),
                    UriKind.Absolute, out address) || address.Scheme != "https"
                    || address.Host != "search.bilibili.com" || address.AbsolutePath != path) throw new FormatException();
                var queryKeywords = address.Query.TrimStart('?').Split('&').Select(pair => pair.Split(new[] { '=' }, 2))
                    .Where(pair => Uri.UnescapeDataString(pair[0]) == "keyword").ToList();
                if (queryKeywords.Count != 1 || queryKeywords[0].Length != 2
                    || Uri.UnescapeDataString(queryKeywords[0][1].Replace("+", " ")) != keyword) throw new FormatException();
                var marker = Regex.Match(html, @"<script\b[^>]*>\s*window\.__pinia\s*=\s*",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, HtmlMatchTimeout);
                if (!marker.Success) throw new FormatException();
                var start = marker.Index + marker.Length;
                var end = html.IndexOf("</script>", start, StringComparison.OrdinalIgnoreCase);
                if (end < start || end - start > 4 * 1024 * 1024) throw new FormatException();
                var reader = new DataReader(html.Substring(start, end - start));
                reader.Need("("); reader.Need("function"); reader.Need("(");
                var names = new List<string>();
                var unique = new HashSet<string>(StringComparer.Ordinal);
                if (!reader.Take(")"))
                {
                    do
                    {
                        var name = reader.Identifier();
                        if (!unique.Add(name) || names.Count >= 5000) throw new FormatException();
                        names.Add(name);
                    } while (reader.Take(","));
                    reader.Need(")");
                }
                reader.Need("{"); reader.Need("return");
                var body = reader.ObjectText();
                reader.Take(";"); reader.Need("}"); reader.Need("(");
                var values = new List<JToken>();
                if (!reader.Take(")"))
                {
                    do
                    {
                        var value = reader.Value(0);
                        if (!(value is JValue) || values.Count >= 5000) throw new FormatException();
                        values.Add(value);
                    } while (reader.Take(","));
                    reader.Need(")");
                }
                reader.Need(")"); reader.Take(";"); reader.Finish();
                if (names.Count != values.Count) throw new FormatException();
                var aliases = new Dictionary<string, JToken>(StringComparer.Ordinal);
                for (var i = 0; i < names.Count; i++) aliases.Add(names[i], values[i]);
                var bodyReader = new DataReader(body, aliases);
                var root = bodyReader.Value(0) as JObject;
                bodyReader.Finish();
                var typeStore = root?["searchTypeResponse"] as JObject;
                var data = typeStore?["searchTypeResponse"] as JObject;
                long actualPage, size, total, pages;
                if (data == null || !(data["result"] is JArray)
                    || !Count(data["page"], out actualPage) || actualPage != page
                    || !Count(data["pagesize"], out size) || size <= 0 || size > 100
                    || !Count(data["numResults"], out total) || !Count(data["numPages"], out pages)
                    || ((JArray)data["result"]).Count > size) throw new FormatException();
                if (((JArray)data["result"]).Any(item => item.Type != JTokenType.Object
                    || item["type"]?.Value<string>() != resultType)) throw new FormatException();
                return data;
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException
                || ex is ArgumentException || ex is OverflowException || ex is RegexMatchTimeoutException)
            {
                throw new BilibiliApiException(-1, "官网搜索结果格式已变化或未返回请求的页面，请重试搜索。");
            }
        }

        private static bool Count(JToken value, out long result)
        {
            result = 0;
            return value != null && long.TryParse(value.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out result) && result >= 0;
        }

        private sealed class DataReader
        {
            private readonly string _text;
            private readonly IDictionary<string, JToken> _aliases;
            private int _position;
            private int _nodes;
            private static readonly Regex Numeric = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?",
                RegexOptions.CultureInvariant);

            internal DataReader(string text, IDictionary<string, JToken> aliases = null)
            { _text = text; _aliases = aliases; }
            private void White() { while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++; }
            internal bool Take(string token)
            {
                White();
                if (_position + token.Length > _text.Length
                    || string.CompareOrdinal(_text, _position, token, 0, token.Length) != 0) return false;
                _position += token.Length; return true;
            }
            internal void Need(string token) { if (!Take(token)) throw new FormatException(); }
            internal void Finish() { White(); if (_position != _text.Length) throw new FormatException(); }
            private static bool Start(char value) => value >= 'a' && value <= 'z'
                || value >= 'A' && value <= 'Z' || value == '_' || value == '$';
            private static bool Continue(char value) => Start(value) || value >= '0' && value <= '9';
            internal string Identifier()
            {
                White(); var start = _position;
                if (_position >= _text.Length || !Start(_text[_position])) throw new FormatException();
                while (_position < _text.Length && Continue(_text[_position])) _position++;
                return _text.Substring(start, _position - start);
            }
            private string Quoted()
            {
                White(); var start = _position;
                if (_position >= _text.Length || _text[_position++] != '"') throw new FormatException();
                while (_position < _text.Length)
                {
                    var ch = _text[_position++];
                    if (ch == '\\') { if (_position >= _text.Length) throw new FormatException(); _position++; }
                    else if (ch == '"') return JsonConvert.DeserializeObject<string>(_text.Substring(start, _position - start));
                }
                throw new FormatException();
            }
            internal string ObjectText()
            {
                White(); var start = _position;
                Need("{");
                var stack = new Stack<char>(); stack.Push('}');
                while (_position < _text.Length && stack.Count > 0)
                {
                    var ch = _text[_position];
                    if (ch == '"') { Quoted(); continue; }
                    _position++;
                    if (ch == '{') stack.Push('}');
                    else if (ch == '[') stack.Push(']');
                    else if (ch == '}' || ch == ']') { if (stack.Pop() != ch) throw new FormatException(); }
                    else if (ch == '(' || ch == ')') throw new FormatException();
                    if (stack.Count > 64) throw new FormatException();
                }
                if (stack.Count != 0) throw new FormatException();
                return _text.Substring(start, _position - start);
            }
            internal JToken Value(int depth)
            {
                White();
                if (depth > 64 || ++_nodes > 100000 || _position >= _text.Length) throw new FormatException();
                if (Take("{"))
                {
                    var obj = new JObject();
                    if (Take("}")) return obj;
                    do
                    {
                        White(); var key = _position < _text.Length && _text[_position] == '"' ? Quoted() : Identifier();
                        Need(":");
                        if (obj.Property(key) != null) throw new FormatException();
                        obj.Add(key, Value(depth + 1));
                    } while (Take(","));
                    Need("}"); return obj;
                }
                if (Take("["))
                {
                    var array = new JArray();
                    if (Take("]")) return array;
                    do { array.Add(Value(depth + 1)); } while (Take(","));
                    Need("]"); return array;
                }
                if (_text[_position] == '"') return new JValue(Quoted());
                var number = Numeric.Match(_text, _position);
                if (number.Success) { _position += number.Length; return JToken.Parse(number.Value); }
                var literal = Identifier();
                if (literal == "true") return new JValue(true);
                if (literal == "false") return new JValue(false);
                if (literal == "null") return JValue.CreateNull();
                if (literal == "void") { Need("0"); return JValue.CreateNull(); }
                JToken alias;
                if (_aliases != null && _aliases.TryGetValue(literal, out alias)) return alias.DeepClone();
                throw new FormatException();
            }
        }
    }
}
