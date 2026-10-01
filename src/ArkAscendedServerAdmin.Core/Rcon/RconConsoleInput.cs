namespace ArkAscendedServerAdmin.Rcon;

/// <summary>What the console panel does after <see cref="RconConsoleInput.Key"/>.</summary>
public enum RconKeyOutcome
{
    /// <summary>Not a key the input handles; the browser's default stays.</summary>
    Ignored,
    /// <summary>The state changed (or the key was absorbed); re-render.</summary>
    Handled,
    /// <summary>Send <see cref="RconConsoleInput.Text"/>.</summary>
    Send,
}

/// <summary>
/// The console input's text, history walk, and command suggestions as one state machine, kept out of the
/// component so the key rules are unit-tested:
/// <list type="bullet">
/// <item>The suggestion list opens while the first word is being typed and has matches, or when the full list is
/// opened from its button.</item>
/// <item>ArrowUp walks history back, or moves the highlight up once the list is being navigated (from the first
/// item it returns to the input with nothing highlighted).</item>
/// <item>ArrowDown walks history forward while an entry is recalled (past the newest it restores the draft); at the
/// draft with the list open it highlights the first item; once navigating it moves the highlight down.</item>
/// <item>Tab accepts the highlighted item, or the top match, only while the list is open.</item>
/// <item>Escape closes the list. Enter accepts an item highlighted with the arrows without sending; otherwise it
/// sends what is typed.</item>
/// </list>
/// </summary>
public sealed class RconConsoleInput(IReadOnlyList<RconCommandInfo> catalog)
{
    private IReadOnlyList<string> _history = [];
    private bool _typing;
    private bool _browsing;

    public RconConsoleInput()
        : this(RconCommandCatalog.All)
    {
    }

    public string Text { get; private set; } = string.Empty;

    /// <summary>Position in the history while an entry is recalled; -1 at the draft.</summary>
    public int HistoryIndex { get; private set; } = -1;

    /// <summary>What was typed before the history walk began; restored when ArrowDown passes the newest entry.</summary>
    public string Draft { get; private set; } = string.Empty;

    /// <summary>Index into <see cref="Items"/> of the highlighted suggestion; -1 when none is.</summary>
    public int Highlight { get; private set; } = -1;

    public IReadOnlyList<string> History => _history;

    /// <summary>
    /// Bumped by <see cref="Reset"/>. A send captures it before it awaits, so a send that finishes after the panel
    /// moved to another instance cannot touch that instance's input or history.
    /// </summary>
    public int Session { get; private set; }

    /// <summary>True while the full list opened from the button is showing.</summary>
    public bool IsBrowsing => _browsing;

    /// <summary>The suggestions on show: the whole catalog while browsing, the matches while typing, else none.</summary>
    public IReadOnlyList<RconCommandInfo> Items =>
        _browsing ? catalog : _typing ? RconCommandSuggester.Suggest(Text, catalog) : [];

    public bool IsListOpen => Items.Count > 0;

    /// <summary>The syntax of the command whose arguments are being typed, shown in place of the list.</summary>
    public RconCommandInfo? Hint => IsListOpen ? null : RconCommandSuggester.ArgumentHint(Text, catalog);

    /// <summary>Replaces the history (another instance, or the store's answer after a send) and resets the walk.</summary>
    public void SetHistory(IReadOnlyList<string> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
        HistoryIndex = -1;
        Draft = string.Empty;
    }

    /// <summary>Back to an empty input: another instance's console now owns the panel.</summary>
    public void Reset(IReadOnlyList<string> history)
    {
        SetHistory(history);
        Text = string.Empty;
        Session++;
        Close();
    }

    /// <summary>The user typed: the text becomes the draft and the list follows it.</summary>
    public void TextChanged(string? text)
    {
        Text = text ?? string.Empty;
        HistoryIndex = -1;
        _typing = true;
        _browsing = false;
        Highlight = -1;
    }

    /// <summary>The button beside the input: shows or hides the full list.</summary>
    public void ToggleBrowse()
    {
        _browsing = !_browsing;
        _typing = false;
        Highlight = -1;
    }

    public void Close()
    {
        _typing = false;
        _browsing = false;
        Highlight = -1;
    }

    /// <summary>Puts the command's name in the input (with a space when it takes arguments) and closes the list.</summary>
    public void Accept(RconCommandInfo command)
    {
        Text = RconCommandSuggester.Accept(command);
        HistoryIndex = -1;
        Close();
    }

    /// <summary>
    /// <paramref name="command"/> went out in <paramref name="session"/>: it joins the local history (until the
    /// parent supplies the stored one), the input empties, and the walk starts over. Ignored when the panel has been
    /// reset for another instance since the send began.
    /// </summary>
    public void Sent(int session, string command)
    {
        if (session != Session)
        {
            return;
        }

        var history = _history.ToList();
        if (RconHistory.Append(history, command))
        {
            _history = history;
        }

        Text = string.Empty;
        HistoryIndex = -1;
        Draft = string.Empty;
        Close();
    }

    /// <summary>Applies one key press (DOM <c>KeyboardEvent.key</c> names) and says what the panel does next.</summary>
    public RconKeyOutcome Key(string key)
    {
        var items = Items;
        switch (key)
        {
            case "Enter" when Highlight >= 0 && Highlight < items.Count:
                Accept(items[Highlight]);
                return RconKeyOutcome.Handled;
            case "Enter":
                return RconKeyOutcome.Send;
            case "Tab" when items.Count > 0:
                Accept(items[Highlight >= 0 && Highlight < items.Count ? Highlight : 0]);
                return RconKeyOutcome.Handled;
            case "Escape":
                Close();
                return RconKeyOutcome.Handled;
            case "ArrowUp" when Highlight >= 0:
                Highlight--;
                return RconKeyOutcome.Handled;
            case "ArrowUp":
                RecallOlder();
                return RconKeyOutcome.Handled;
            case "ArrowDown" when Highlight >= 0:
                Highlight = Math.Min(items.Count - 1, Highlight + 1);
                return RconKeyOutcome.Handled;
            case "ArrowDown" when HistoryIndex >= 0:
                RecallNewer();
                return RconKeyOutcome.Handled;
            case "ArrowDown" when items.Count > 0:
                Highlight = 0;
                return RconKeyOutcome.Handled;
            case "ArrowDown":
                return RconKeyOutcome.Handled;
            default:
                return RconKeyOutcome.Ignored;
        }
    }

    private void RecallOlder()
    {
        if (_history.Count == 0)
        {
            return;
        }

        if (HistoryIndex < 0)
        {
            Draft = Text;
            HistoryIndex = _history.Count - 1;
        }
        else
        {
            HistoryIndex = Math.Max(0, HistoryIndex - 1);
        }

        Text = _history[HistoryIndex];
        Close();
    }

    private void RecallNewer()
    {
        HistoryIndex = HistoryIndex + 1 >= _history.Count ? -1 : HistoryIndex + 1;
        Text = HistoryIndex < 0 ? Draft : _history[HistoryIndex];
        Close();
    }
}
