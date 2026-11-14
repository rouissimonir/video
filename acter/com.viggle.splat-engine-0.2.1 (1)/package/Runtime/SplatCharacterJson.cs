// SPDX-License-Identifier: MIT
// A minimal JSON reader, for the VIGGCHAR block-table header and nothing else.
//
// WHY THIS EXISTS
//
// UnityEngine.JsonUtility - which SplatCharacterBin uses for the VFCHARB1 header - deserialises
// into [Serializable] classes with fixed field names. The VIGGCHAR header's `blocks` value is a
// DICTIONARY keyed by block name, and its `palette` is a variable-length int array inside one of
// those entries. JsonUtility has no representation for either: it cannot bind a JSON object whose
// keys are data, and it silently produces an empty object rather than failing, which would turn
// "the manifest names a block I did not hardcode" into "the file has no blocks".
//
// The alternatives were a package dependency (Newtonsoft, com.unity.nuget.newtonsoft-json) or
// this. A dependency is the worse trade here: this package is embedded in host projects that
// already carry their own JSON stack, and a version conflict on a shared dependency is a problem
// the host cannot fix. ~200 lines of parser that only ever sees a 2.5 KB header we produced
// ourselves has no such failure mode.
//
// SCOPE. This is deliberately not a general JSON library:
//   * it parses a complete document from a string and returns a tree; there is no writer,
//     no streaming, no reflection binding and no LINQ surface;
//   * numbers are doubles, which covers every value the header carries (offsets and lengths are
//     well under 2^53, bounds are floats);
//   * nesting is capped, so a hostile header cannot overflow the stack;
//   * it is `internal`, so nothing outside this package can start depending on it.
//
// It is strict: anything malformed throws, and the caller turns that into the reader's own error.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Viggle.SplatEngine
{
    /// <summary>Thrown for any malformed JSON. Callers translate it into a reader error.</summary>
    internal class SplatJsonException : Exception
    {
        public SplatJsonException(string message) : base(message) { }
    }

    internal enum SplatJsonKind { Object, Array, Number, String, Bool, Null }

    /// <summary>One node of a parsed JSON document.</summary>
    internal sealed class SplatJsonValue
    {
        public readonly SplatJsonKind Kind;

        readonly Dictionary<string, SplatJsonValue> m_Members;
        readonly List<SplatJsonValue> m_Items;
        readonly double m_Number;
        readonly string m_String;
        readonly bool m_Bool;

        SplatJsonValue(SplatJsonKind kind, Dictionary<string, SplatJsonValue> members,
                       List<SplatJsonValue> items, double number, string str, bool b)
        {
            Kind = kind;
            m_Members = members;
            m_Items = items;
            m_Number = number;
            m_String = str;
            m_Bool = b;
        }

        internal static SplatJsonValue Object(Dictionary<string, SplatJsonValue> members) =>
            new SplatJsonValue(SplatJsonKind.Object, members, null, 0, null, false);
        internal static SplatJsonValue Array(List<SplatJsonValue> items) =>
            new SplatJsonValue(SplatJsonKind.Array, null, items, 0, null, false);
        internal static SplatJsonValue Number(double v) =>
            new SplatJsonValue(SplatJsonKind.Number, null, null, v, null, false);
        internal static SplatJsonValue String(string v) =>
            new SplatJsonValue(SplatJsonKind.String, null, null, 0, v, false);
        internal static SplatJsonValue Bool(bool v) =>
            new SplatJsonValue(SplatJsonKind.Bool, null, null, 0, null, v);
        internal static SplatJsonValue Null() =>
            new SplatJsonValue(SplatJsonKind.Null, null, null, 0, null, false);

        public bool IsObject => Kind == SplatJsonKind.Object;
        public bool IsArray => Kind == SplatJsonKind.Array;
        public bool IsNumber => Kind == SplatJsonKind.Number;

        /// <summary>Member lookup. False when this is not an object, or has no such key.</summary>
        public bool TryGet(string key, out SplatJsonValue value)
        {
            if (m_Members != null) return m_Members.TryGetValue(key, out value);
            value = null;
            return false;
        }

        static readonly Dictionary<string, SplatJsonValue> s_NoMembers =
            new Dictionary<string, SplatJsonValue>(StringComparer.Ordinal);

        /// <summary>The members of an object. Empty for anything else.</summary>
        public Dictionary<string, SplatJsonValue> Members => m_Members ?? s_NoMembers;

        /// <summary>Element count of an array. 0 for anything else.</summary>
        public int Count => m_Items?.Count ?? 0;

        public SplatJsonValue this[int i] => m_Items[i];

        public double AsDouble => m_Number;
        public string AsString => m_String;
        public bool AsBool => m_Bool;

        /// <summary>
        /// A JSON summary of this node, truncated, for error messages. Enough to identify what was
        /// found without pasting a 391-entry palette into a log line.
        /// </summary>
        public string Describe(int maxChars = 60)
        {
            string s;
            switch (Kind)
            {
                case SplatJsonKind.Object: s = "{ " + m_Members.Count + " keys }"; break;
                case SplatJsonKind.Array: s = "[ " + m_Items.Count + " items ]"; break;
                case SplatJsonKind.Number: s = m_Number.ToString("R", CultureInfo.InvariantCulture); break;
                case SplatJsonKind.String: s = "\"" + m_String + "\""; break;
                case SplatJsonKind.Bool: s = m_Bool ? "true" : "false"; break;
                default: s = "null"; break;
            }
            return s.Length <= maxChars ? s : s.Substring(0, maxChars) + "...";
        }
    }

    internal static class SplatJson
    {
        // A header is a flat table of block entries; the deepest legal path is
        // root -> blocks -> entry -> min -> element, which is 5. The cap is here so a hostile or
        // corrupt header cannot recurse the parser into a stack overflow, which is a crash no
        // catch block in this package would see.
        const int kMaxDepth = 32;

        /// <summary>Parses a complete JSON document. Throws <see cref="SplatJsonException"/>.</summary>
        public static SplatJsonValue Parse(string text)
        {
            if (text == null) throw new SplatJsonException("empty document");
            int i = 0;
            var v = ParseValue(text, ref i, 0);
            SkipWhitespace(text, ref i);
            if (i != text.Length)
                throw new SplatJsonException($"trailing content at offset {i}");
            return v;
        }

        static SplatJsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > kMaxDepth)
                throw new SplatJsonException($"nested deeper than {kMaxDepth} levels at offset {i}");
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new SplatJsonException("unexpected end of document");

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i, depth);
                case '[': return ParseArray(s, ref i, depth);
                case '"': return SplatJsonValue.String(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return SplatJsonValue.Bool(true);
                case 'f': Expect(s, ref i, "false"); return SplatJsonValue.Bool(false);
                case 'n': Expect(s, ref i, "null"); return SplatJsonValue.Null();
                default: return SplatJsonValue.Number(ParseNumber(s, ref i));
            }
        }

        static SplatJsonValue ParseObject(string s, ref int i, int depth)
        {
            i++;   // '{'
            var members = new Dictionary<string, SplatJsonValue>(StringComparer.Ordinal);
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == '}') { i++; return SplatJsonValue.Object(members); }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != '"')
                    throw new SplatJsonException($"expected a key string at offset {i}");
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != ':')
                    throw new SplatJsonException($"expected ':' after key '{key}' at offset {i}");
                i++;
                // A duplicate key has no agreed meaning across parsers, so it is rejected rather
                // than resolved: whichever one this reader picked, another implementation reading
                // the same file could pick the other.
                var value = ParseValue(s, ref i, depth + 1);
                if (members.ContainsKey(key))
                    throw new SplatJsonException($"duplicate key '{key}' at offset {i}");
                members.Add(key, value);
                SkipWhitespace(s, ref i);
                char d = Peek(s, i);
                if (d == ',') { i++; continue; }
                if (d == '}') { i++; return SplatJsonValue.Object(members); }
                throw new SplatJsonException($"expected ',' or '}}' at offset {i}");
            }
        }

        static SplatJsonValue ParseArray(string s, ref int i, int depth)
        {
            i++;   // '['
            var items = new List<SplatJsonValue>();
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == ']') { i++; return SplatJsonValue.Array(items); }
            while (true)
            {
                items.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                char d = Peek(s, i);
                if (d == ',') { i++; continue; }
                if (d == ']') { i++; return SplatJsonValue.Array(items); }
                throw new SplatJsonException($"expected ',' or ']' at offset {i}");
            }
        }

        static string ParseString(string s, ref int i)
        {
            i++;   // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new SplatJsonException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    // Control characters are not legal raw inside a JSON string.
                    if (c < ' ') throw new SplatJsonException($"raw control character in string at offset {i - 1}");
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length) throw new SplatJsonException("unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length)
                            throw new SplatJsonException("truncated \\u escape");
                        if (!ushort.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out ushort code))
                            throw new SplatJsonException($"bad \\u escape at offset {i}");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new SplatJsonException($"unknown escape '\\{e}' at offset {i - 1}");
                }
            }
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (Peek(s, i) == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                    ((s[i] == '+' || s[i] == '-') && i > start &&
                                     (s[i - 1] == 'e' || s[i - 1] == 'E'))))
                i++;
            if (i == start) throw new SplatJsonException($"expected a value at offset {start}");
            string token = s.Substring(start, i - start);
            // Invariant culture, always: a machine format does not follow the machine's locale, and
            // a decimal comma would otherwise silently reinterpret every bound in the header.
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new SplatJsonException($"'{token}' at offset {start} is not a number");
            return v;
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new SplatJsonException($"expected '{literal}' at offset {i}");
            i += literal.Length;
        }

        static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                // Space, tab, CR, LF - the only whitespace JSON allows. The VIGGCHAR writer pads
                // the header with spaces to the 4-byte boundary, and that padding lands here.
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }
    }
}
