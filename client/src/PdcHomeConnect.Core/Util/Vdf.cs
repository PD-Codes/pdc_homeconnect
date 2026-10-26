using System.Text;

namespace PdcHomeConnect.Core.Util;

/// <summary>A node of a Valve KeyValues (VDF) document. Keys are case-insensitive.</summary>
public sealed class VdfNode
{
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Value { get; set; }

    public VdfNode? this[string key] => Children.GetValueOrDefault(key);

    /// <summary>Follows a path of keys, e.g. ("UserLocalConfigStore", "Software", "Valve").</summary>
    public VdfNode? Path(params string[] keys)
    {
        var node = this;
        foreach (var key in keys)
        {
            node = node?[key];
            if (node is null)
            {
                return null;
            }
        }
        return node;
    }

    public string? GetString(string key) => this[key]?.Value;

    public long? GetLong(string key) => long.TryParse(GetString(key), out var v) ? v : null;
}

/// <summary>Parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf, localconfig.vdf).</summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var position = 0;
        ParseObject(text, ref position, root, isRoot: true);
        return root;
    }

    public static VdfNode? TryParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseObject(string text, ref int position, VdfNode node, bool isRoot)
    {
        while (true)
        {
            var key = ReadToken(text, ref position, out var keyKind);
            if (keyKind == TokenKind.End)
            {
                return;
            }

            if (keyKind == TokenKind.CloseBrace)
            {
                if (isRoot)
                {
                    continue;
                }
                return;
            }

            if (keyKind == TokenKind.OpenBrace)
            {
                // Malformed: brace without key. Parse and discard.
                ParseObject(text, ref position, new VdfNode(), isRoot: false);
                continue;
            }

            var value = ReadToken(text, ref position, out var valueKind);
            switch (valueKind)
            {
                case TokenKind.OpenBrace:
                    var child = new VdfNode();
                    ParseObject(text, ref position, child, isRoot: false);
                    node.Children[key!] = child;
                    break;
                case TokenKind.String:
                    node.Children[key!] = new VdfNode { Value = value };
                    break;
                case TokenKind.CloseBrace:
                    return;
                default:
                    return;
            }
        }
    }

    private enum TokenKind
    {
        String,
        OpenBrace,
        CloseBrace,
        End,
    }

    private static string? ReadToken(string text, ref int position, out TokenKind kind)
    {
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }
                continue;
            }

            if (c == '[')
            {
                // Conditional like [$WIN32]: skip.
                while (position < text.Length && text[position] != ']')
                {
                    position++;
                }
                position++;
                continue;
            }

            break;
        }

        if (position >= text.Length)
        {
            kind = TokenKind.End;
            return null;
        }

        var ch = text[position];
        if (ch == '{')
        {
            position++;
            kind = TokenKind.OpenBrace;
            return null;
        }

        if (ch == '}')
        {
            position++;
            kind = TokenKind.CloseBrace;
            return null;
        }

        var sb = new StringBuilder();
        if (ch == '"')
        {
            position++;
            while (position < text.Length)
            {
                var c = text[position++];
                if (c == '\\' && position < text.Length)
                {
                    var next = text[position++];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => next,
                    });
                    continue;
                }

                if (c == '"')
                {
                    break;
                }

                sb.Append(c);
            }
        }
        else
        {
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not '{' and not '}' and not '"')
            {
                sb.Append(text[position++]);
            }
        }

        kind = TokenKind.String;
        return sb.ToString();
    }
}
