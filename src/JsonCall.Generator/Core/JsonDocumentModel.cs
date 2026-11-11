using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JsonCall.Generator.Core;

internal enum JKind
{
    Object,
    Array,
    String,
    Number,
    Bool,
    Null
}

/// <summary>A JSON value that remembers where it came from, so diagnostics can point at it.</summary>
internal sealed class JNode
{
    public JNode(JKind kind, int offset, int line, int column)
    {
        Kind = kind;
        Offset = offset;
        Line = line;
        Column = column;
    }

    public JKind Kind { get; }

    public int Offset { get; }

    public int Line { get; }

    public int Column { get; }

    public int Length { get; set; }

    public string? Text { get; set; }

    public bool BoolValue { get; set; }

    public List<KeyValuePair<string, JNode>>? Members { get; set; }

    public List<JNode>? Items { get; set; }

    public bool IsObject => Kind == JKind.Object;

    public bool IsArray => Kind == JKind.Array;

    public bool IsString => Kind == JKind.String;

    public JNode? Get(string key)
    {
        if (Members is null)
            return null;

        foreach (var member in Members)
        {
            if (member.Key == key)
                return member.Value;
        }

        return null;
    }

    public bool Has(string key) => Get(key) is not null;

    public string? GetString(string key) => Get(key) is { Kind: JKind.String } node ? node.Text : null;

    public bool GetBool(string key) => Get(key) is { Kind: JKind.Bool } node && node.BoolValue;
}

internal sealed class JsonParseException : Exception
{
    public JsonParseException(string message, int offset, int line, int column) : base(message)
    {
        Offset = offset;
        Line = line;
        Column = column;
    }

    public int Offset { get; }

    public int Line { get; }

    public int Column { get; }
}

/// <summary>A small strict JSON parser with source positions. It exists because analyzers cannot rely on System.Text.Json.</summary>
internal sealed class JsonParser
{
    private readonly string _text;
    private int _pos;
    private int _line;
    private int _lineStart;

    private JsonParser(string text) => _text = text;

    public static JNode Parse(string text)
    {
        var parser = new JsonParser(text);

        if (text.Length > 0 && text[0] == '﻿')
            parser._pos = 1;

        parser.SkipWhitespace();
        var node = parser.ParseValue(0);
        parser.SkipWhitespace();

        if (parser._pos != text.Length)
            throw parser.Error("Unexpected content after the JSON value.");

        return node;
    }

    private JsonParseException Error(string message) => new(message, _pos, _line, _pos - _lineStart);

    private void SkipWhitespace()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];

            if (c == '\n')
            {
                _pos++;
                _line++;
                _lineStart = _pos;
            }
            else if (c == ' ' || c == '\t' || c == '\r')
            {
                _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private JNode ParseValue(int depth)
    {
        if (depth > 256)
            throw Error("The JSON is nested too deeply.");

        if (_pos >= _text.Length)
            throw Error("Unexpected end of JSON.");

        var start = _pos;
        var node = _text[_pos] switch
        {
            '{' => ParseObject(depth),
            '[' => ParseArray(depth),
            '"' => ParseStringNode(),
            't' => Literal("true", JKind.Bool, true),
            'f' => Literal("false", JKind.Bool, false),
            'n' => Literal("null", JKind.Null, false),
            _ => ParseNumber()
        };

        node.Length = _pos - start;
        return node;
    }

    private JNode Literal(string word, JKind kind, bool value)
    {
        var node = new JNode(kind, _pos, _line, _pos - _lineStart) { BoolValue = value };

        if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
            throw Error("Invalid JSON literal.");

        _pos += word.Length;
        return node;
    }

    private JNode ParseNumber()
    {
        var node = new JNode(JKind.Number, _pos, _line, _pos - _lineStart);
        var start = _pos;

        if (_pos < _text.Length && _text[_pos] == '-')
            _pos++;

        var digits = 0;

        while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || "+-.eE".IndexOf(_text[_pos]) >= 0))
        {
            if (char.IsDigit(_text[_pos]))
                digits++;

            _pos++;
        }

        if (digits == 0)
            throw new JsonParseException("Invalid JSON value.", start, _line, start - _lineStart);

        node.Text = _text.Substring(start, _pos - start);
        return node;
    }

    private JNode ParseStringNode()
    {
        var node = new JNode(JKind.String, _pos, _line, _pos - _lineStart);
        node.Text = ParseString();
        return node;
    }

    private string ParseString()
    {
        _pos++; // opening quote
        var builder = new StringBuilder();

        while (true)
        {
            if (_pos >= _text.Length)
                throw Error("Unterminated string.");

            var c = _text[_pos++];

            if (c == '"')
                return builder.ToString();

            if (c == '\n')
                throw Error("Unescaped newline in string.");

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (_pos >= _text.Length)
                throw Error("Unterminated string.");

            var escape = _text[_pos++];

            switch (escape)
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
                    if (_pos + 4 > _text.Length
                        || !int.TryParse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        throw Error("Invalid \\u escape.");
                    }

                    builder.Append((char)code);
                    _pos += 4;
                    break;
                default:
                    throw Error("Invalid escape sequence.");
            }
        }
    }

    private JNode ParseObject(int depth)
    {
        var node = new JNode(JKind.Object, _pos, _line, _pos - _lineStart) { Members = new List<KeyValuePair<string, JNode>>() };
        _pos++;
        SkipWhitespace();

        if (_pos < _text.Length && _text[_pos] == '}')
        {
            _pos++;
            return node;
        }

        while (true)
        {
            SkipWhitespace();

            if (_pos >= _text.Length || _text[_pos] != '"')
                throw Error("Expected a property name.");

            var key = ParseString();
            SkipWhitespace();

            if (_pos >= _text.Length || _text[_pos] != ':')
                throw Error("Expected ':'.");

            _pos++;
            SkipWhitespace();
            node.Members!.Add(new KeyValuePair<string, JNode>(key, ParseValue(depth + 1)));
            SkipWhitespace();

            if (_pos >= _text.Length)
                throw Error("Unexpected end of JSON.");

            if (_text[_pos] == ',')
            {
                _pos++;
                continue;
            }

            if (_text[_pos] == '}')
            {
                _pos++;
                return node;
            }

            throw Error("Expected ',' or '}'.");
        }
    }

    private JNode ParseArray(int depth)
    {
        var node = new JNode(JKind.Array, _pos, _line, _pos - _lineStart) { Items = new List<JNode>() };
        _pos++;
        SkipWhitespace();

        if (_pos < _text.Length && _text[_pos] == ']')
        {
            _pos++;
            return node;
        }

        while (true)
        {
            SkipWhitespace();
            node.Items!.Add(ParseValue(depth + 1));
            SkipWhitespace();

            if (_pos >= _text.Length)
                throw Error("Unexpected end of JSON.");

            if (_text[_pos] == ',')
            {
                _pos++;
                continue;
            }

            if (_text[_pos] == ']')
            {
                _pos++;
                return node;
            }

            throw Error("Expected ',' or ']'.");
        }
    }
}
