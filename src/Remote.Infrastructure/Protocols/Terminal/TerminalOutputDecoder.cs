using System.Text;

namespace Remote.Infrastructure.Protocols.Terminal;

/// <summary>Incrementally decodes UTF-8 and removes terminal control sequences for the basic text renderer.</summary>
public sealed class TerminalOutputDecoder
{
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private ParserState _state;

    public string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _utf8.GetChars(bytes, chars, flush: false);
        var output = new StringBuilder(count);
        foreach (var character in chars.AsSpan(0, count))
        {
            Process(character, output);
        }

        return output.ToString();
    }

    public void Reset()
    {
        _utf8.Reset();
        _state = ParserState.Text;
    }

    private void Process(char character, StringBuilder output)
    {
        switch (_state)
        {
            case ParserState.Text:
                if (character == '\u001b')
                {
                    _state = ParserState.Escape;
                }
                else if (character == '\r')
                {
                    // Line feeds are preserved; carriage returns used for redraw do not become visible glyphs.
                }
                else if (character == '\b')
                {
                    if (output.Length > 0 && output[^1] != '\n')
                    {
                        output.Length--;
                    }
                }
                else if (character is '\n' or '\t' || !char.IsControl(character))
                {
                    output.Append(character);
                }
                break;
            case ParserState.Escape:
                _state = character switch
                {
                    '[' => ParserState.ControlSequence,
                    ']' => ParserState.OperatingSystemCommand,
                    'P' => ParserState.DeviceControl,
                    _ => ParserState.Text,
                };
                break;
            case ParserState.ControlSequence:
                if (character is >= '@' and <= '~')
                {
                    _state = ParserState.Text;
                }
                break;
            case ParserState.OperatingSystemCommand:
            case ParserState.DeviceControl:
                if (character == '\a')
                {
                    _state = ParserState.Text;
                }
                else if (character == '\u001b')
                {
                    _state = ParserState.StringEscape;
                }
                break;
            case ParserState.StringEscape:
                _state = character == '\\' ? ParserState.Text : ParserState.OperatingSystemCommand;
                break;
            default:
                throw new InvalidOperationException("Unknown terminal parser state.");
        }
    }

    private enum ParserState
    {
        Text,
        Escape,
        ControlSequence,
        OperatingSystemCommand,
        DeviceControl,
        StringEscape,
    }
}
