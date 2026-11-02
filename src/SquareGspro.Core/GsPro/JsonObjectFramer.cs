using System.Text;

namespace SquareGspro.Core.GsPro;

/// <summary>
/// GSPro sends bare JSON objects over TCP with no delimiter, so one read may hold a partial
/// object or several. This splits a character stream into complete top-level objects,
/// ignoring braces inside strings.
/// </summary>
public sealed class JsonObjectFramer
{
    private readonly StringBuilder _sb = new();
    private int _depth;
    private bool _inString, _escape;

    public IReadOnlyList<string> Feed(string chunk)
    {
        var result = new List<string>();
        foreach (char c in chunk)
        {
            if (_depth == 0 && c != '{') continue; // skip whitespace / noise between objects
            _sb.Append(c);

            if (_inString)
            {
                if (_escape) _escape = false;
                else if (c == '\\') _escape = true;
                else if (c == '"') _inString = false;
                continue;
            }

            if (c == '"') _inString = true;
            else if (c == '{') _depth++;
            else if (c == '}' && --_depth == 0)
            {
                result.Add(_sb.ToString());
                _sb.Clear();
            }
        }
        return result;
    }
}
