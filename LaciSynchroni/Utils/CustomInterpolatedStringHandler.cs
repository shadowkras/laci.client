using System.Runtime.CompilerServices;
using System.Text;

namespace LaciSynchroni.Utils;

[InterpolatedStringHandler]
public readonly ref struct CustomInterpolatedStringHandler
{
    readonly StringBuilder _logMessageStringbuilder;

    public CustomInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _logMessageStringbuilder = new StringBuilder(literalLength);
    }

    public void AppendLiteral(string s)
    {
        _logMessageStringbuilder.Append(s);
    }

    public void AppendFormatted<T>(T t)
    {
        _logMessageStringbuilder.Append(t);
    }

    public string BuildMessage() => _logMessageStringbuilder.ToString();
}