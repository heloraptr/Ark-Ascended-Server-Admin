using System.Text;

namespace ArkAscendedServerAdmin.Install;

/// <summary>
/// Turns the raw UTF-8 byte stream of a Windows pseudo console (ConPTY) into plain text lines. ConPTY
/// renders the child's screen as VT output, so the stream carries cursor, mode, and title sequences
/// around the text; this strips CSI, OSC/DCS-style strings, and single-character escapes, ends a line at
/// <c>\n</c> or <c>\r\n</c>, treats a bare <c>\r</c> as "overwrite the current line", and drops blank
/// lines made only of escape sequences. It also tracks the cursor row, because ConPTY skips blank rows
/// with a cursor move instead of line ends; without that, the lines around a blank line run together.
/// Bytes may arrive in any chunking: a split UTF-8 character, escape sequence, or line carries over to
/// the next <see cref="Append"/>.
/// </summary>
public sealed class TerminalLineAssembler
{
    /// <summary>A line this long is emitted as is, so a stream without line ends cannot grow memory without bound.</summary>
    public const int MaxLineLength = 16 * 1024;

    /// <summary>Upper bound for a <c>CSI n C</c> (cursor forward) rendered as spaces.</summary>
    private const int MaxCursorForward = 512;

    /// <summary>Parameter bytes kept per CSI sequence; anything longer is not a sequence we act on.</summary>
    private const int MaxCsiParameters = 32;

    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    private readonly Action<string> _onLine;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _csiParameters = new();
    private readonly int _screenRows;
    private char[] _chars = new char[1024];
    private State _state = State.Text;
    private bool _pendingCarriageReturn;
    private bool _lineHasEscapes;
    private int _row = 1;
    private bool _emittedAny;
    private bool _lastWasBlank;

    /// <param name="onLine">Receives each completed line.</param>
    /// <param name="screenRows">
    /// The pseudo console's height. Line ends at the bottom row scroll instead of moving the cursor, so the
    /// tracked row never goes past it.
    /// </param>
    public TerminalLineAssembler(Action<string> onLine, int screenRows = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(screenRows, 1);
        _onLine = onLine;
        _screenRows = screenRows;
    }

    private enum State
    {
        Text,
        Escape,
        EscapeIntermediate,
        Csi,
        String,
        StringEscape,
    }

    /// <summary>Feeds the next chunk of output; each line it completes is handed to the callback.</summary>
    public void Append(ReadOnlySpan<byte> utf8)
    {
        var needed = _decoder.GetCharCount(utf8, flush: false);
        if (needed > _chars.Length)
        {
            _chars = new char[Math.Max(needed, _chars.Length * 2)];
        }

        var count = _decoder.GetChars(utf8, _chars, flush: false);
        foreach (var c in _chars.AsSpan(0, count))
        {
            Process(c);
        }
    }

    /// <summary>Call once at end of stream: flushes a trailing partial character and an unterminated last line.</summary>
    public void Complete()
    {
        var count = _decoder.GetChars([], _chars, flush: true);
        foreach (var c in _chars.AsSpan(0, count))
        {
            Process(c);
        }

        if (_line.Length > 0)
        {
            EmitLine();
        }

        _state = State.Text;
        _pendingCarriageReturn = false;
        _lineHasEscapes = false;
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case State.Text:
                ProcessText(c);
                break;

            case State.Escape:
                ProcessEscape(c);
                break;

            case State.EscapeIntermediate:
                // ESC, intermediates (0x20-0x2F), then one final character, e.g. ESC ( B.
                if (c is < ' ' or > '/')
                {
                    _state = State.Text;
                }

                break;

            case State.Csi:
                ProcessCsi(c);
                break;

            case State.String:
                if (c == Bell)
                {
                    _state = State.Text;
                }
                else if (c == Escape)
                {
                    _state = State.StringEscape;
                }

                break;

            case State.StringEscape:
                // ESC \ is the string terminator; any other ESC starts a new sequence.
                if (c == '\\')
                {
                    _state = State.Text;
                }
                else
                {
                    _state = State.Escape;
                    ProcessEscape(c);
                }

                break;
        }
    }

    private void ProcessText(char c)
    {
        switch (c)
        {
            case Escape:
                _state = State.Escape;
                _lineHasEscapes = true;
                return;

            case '\n':
                _pendingCarriageReturn = false;
                EmitLine();
                _row = Math.Min(_row + 1, _screenRows);
                return;

            case '\r':
                _pendingCarriageReturn = true;
                return;

            case '\b':
                if (_line.Length > 0)
                {
                    _line.Length--;
                }

                return;

            case '\t':
                AppendText(c);
                return;

            default:
                if (!char.IsControl(c))
                {
                    AppendText(c);
                }

                return;
        }
    }

    private void ProcessEscape(char c)
    {
        switch (c)
        {
            case '[':
                _csiParameters.Clear();
                _state = State.Csi;
                break;

            case ']' or 'P' or 'X' or '^' or '_':
                // OSC (window title and friends), DCS, SOS, PM, APC: skipped up to BEL or ESC \.
                _state = State.String;
                break;

            case >= ' ' and <= '/':
                _state = State.EscapeIntermediate;
                break;

            case Escape:
                break;

            default:
                // A single-character escape such as ESC 7 or ESC M.
                _state = State.Text;
                break;
        }
    }

    private void ProcessCsi(char c)
    {
        if (c is >= '0' and <= '?')
        {
            if (_csiParameters.Length < MaxCsiParameters)
            {
                _csiParameters.Append(c);
            }
        }
        else if (c is >= '@' and <= '~')
        {
            _state = State.Text;
            ExecuteCsi(c);
        }
        else if (c == Escape)
        {
            _state = State.Escape;
        }

        // Intermediates (0x20-0x2F) and stray control characters inside a sequence are ignored.
    }

    private void ExecuteCsi(char final)
    {
        // Private sequences (ESC [ ? 25 h and friends) only switch modes.
        if (_csiParameters.Length > 0 && _csiParameters[0] is '<' or '=' or '>' or '?')
        {
            return;
        }

        switch (final)
        {
            case 'C':
                // ConPTY may compress a run of blanks into "cursor forward"; keep the spacing.
                for (var i = 0; i < Math.Min(Parameter(0), MaxCursorForward); i++)
                {
                    AppendText(' ');
                }

                break;

            case 'H' or 'f':
                MoveTo(Parameter(0), Parameter(1));
                break;

            case 'd':
                MoveTo(Parameter(0), column: null);
                break;

            case 'B':
                MoveTo(_row + Parameter(0), column: null);
                break;

            case 'E':
                MoveTo(_row + Parameter(0), column: 1);
                break;
        }
    }

    /// <summary>The <paramref name="index"/>th numeric parameter of the current CSI sequence; missing, empty, or zero means 1.</summary>
    private int Parameter(int index)
    {
        var parts = _csiParameters.ToString().Split(';');
        return index < parts.Length
            && int.TryParse(parts[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value > 0
                ? value
                : 1;
    }

    /// <summary>
    /// A cursor move. ConPTY skips rows that the child left blank by moving the cursor down instead of
    /// sending line ends, so a move to a lower row ends the pending line, and the skipped rows become one
    /// blank line (never more, and never at the very start or right after another blank line). A move up,
    /// or to column one of the current row, ends the pending line only if it has text. A column past one
    /// pads the line with spaces up to that column, which keeps a leading space ConPTY expressed as a
    /// cursor position.
    /// </summary>
    private void MoveTo(int row, int? column)
    {
        row = Math.Clamp(row, 1, _screenRows);
        _pendingCarriageReturn = false;
        if (row > _row)
        {
            var skipped = row - _row - 1;
            EmitLine();
            if (skipped > 0 && _emittedAny && !_lastWasBlank)
            {
                Emit(string.Empty);
            }
        }
        else if ((row < _row || column == 1) && _line.Length > 0)
        {
            EmitLine();
        }

        _row = row;
        if (column is > 1)
        {
            var target = Math.Min(column.Value - 1, MaxCursorForward);
            while (_line.Length < target)
            {
                _line.Append(' ');
            }
        }
    }

    private void AppendText(char c)
    {
        if (_pendingCarriageReturn)
        {
            // A bare carriage return followed by more text rewrites the line from column one.
            _pendingCarriageReturn = false;
            _line.Clear();
        }

        _line.Append(c);
        if (_line.Length >= MaxLineLength)
        {
            EmitLine();
        }
    }

    private void EmitLine()
    {
        var text = _line.ToString();

        // Blank (or padding-only) lines made of escape sequences are screen noise, not output.
        var noise = _lineHasEscapes && string.IsNullOrWhiteSpace(text);
        _line.Clear();
        _lineHasEscapes = false;
        if (!noise)
        {
            Emit(text);
        }
    }

    private void Emit(string text)
    {
        _emittedAny = true;
        _lastWasBlank = text.Length == 0;
        _onLine(text);
    }
}
