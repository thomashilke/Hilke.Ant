using Terminal.Gui;

namespace Hilke.Ant.Cli;

/// <summary>
/// The full-screen Terminal.Gui shell: a pinned device table, a scrolling log, a command input with
/// Tab completion, and a live status bar. Cross-thread log/telemetry writes marshal onto the UI
/// thread via <see cref="MainLoop.Invoke"/>.
/// </summary>
public static class TuiApp
{
    private const int MaxLogLines = 500;

    private static readonly List<string> _logLines = new();
    private static ListView _deviceList = null!;
    private static ListView _logList = null!;
    private static TextField _input = null!;
    private static StatusItem _status = null!;
    private static DeviceRegistry _registry = null!;
    private static AntSession _session = null!;
    private static volatile bool _uiReady;

    /// <summary>The log sink handed to <see cref="AntSession"/> and <see cref="CommandProcessor"/>.</summary>
    public static void AppendLog(string message)
    {
        lock (_logLines)
        {
            _logLines.Add(message);
            if (_logLines.Count > MaxLogLines)
                _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
        }
        if (_uiReady)
            Application.MainLoop?.Invoke(RenderLog);
    }

    public static void Run(AntSession session, DeviceRegistry registry, CommandProcessor processor)
    {
        _session = session;
        _registry = registry;

        Application.Init();
        var top = Application.Top;

        var win = new Window("ant")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1), // leave the last row for the status bar
        };

        var devicesFrame = new FrameView("Devices")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(45),
        };
        _deviceList = new ListView(Array.Empty<string>())
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        devicesFrame.Add(_deviceList);

        var logFrame = new FrameView("Log")
        {
            X = 0,
            Y = Pos.Bottom(devicesFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(3), // leave 2 rows for prompt + input inside the window
        };
        _logList = new ListView(Array.Empty<string>())
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        logFrame.Add(_logList);

        var prompt = new Label("[ant]# ")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = 7,
            Height = 1,
        };
        _input = new TextField("")
        {
            X = Pos.Right(prompt),
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };
        _input.KeyPress += a => OnInputKey(a, processor);

        win.Add(devicesFrame, logFrame, prompt, _input);

        _status = new StatusItem(Key.Null, "starting…", null);
        var statusBar = new StatusBar(new[]
        {
            _status,
            new StatusItem(Key.CtrlMask | Key.Q, "~^Q~ Quit", () => Application.RequestStop()),
        });

        top.Add(win, statusBar);

        _uiReady = true;
        RenderLog();
        RefreshDevices();
        RefreshStatus();

        Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(500), _ =>
        {
            RefreshDevices();
            RefreshStatus();
            return true;
        });

        _input.SetFocus();
        Application.Run();
        Application.Shutdown();
        _uiReady = false;
    }

    private static void OnInputKey(View.KeyEventEventArgs a, CommandProcessor processor)
    {
        var key = a.KeyEvent.Key;
        if (key == Key.Enter)
        {
            string line = _input.Text?.ToString() ?? "";
            _input.Text = "";
            _input.CursorPosition = 0;
            if (!string.IsNullOrWhiteSpace(line))
            {
                AppendLog($"[ant]# {line}");
                _ = processor.ExecuteAsync(line);
            }
            a.Handled = true;
        }
        else if (key == Key.Tab)
        {
            string current = _input.Text?.ToString() ?? "";
            string completed = Completion.Complete(current, _registry.CompletionTokens(), out var candidates);
            _input.Text = completed;
            _input.CursorPosition = completed.Length;
            if (candidates.Count > 1)
                AppendLog(string.Join("  ", candidates));
            a.Handled = true;
        }
    }

    private static void RenderLog()
    {
        string[] copy;
        lock (_logLines)
            copy = _logLines.ToArray();
        _logList.SetSource(copy);
        if (copy.Length > 0)
        {
            _logList.SelectedItem = copy.Length - 1;
            _logList.EnsureSelectedItemVisible();
        }
    }

    private static void RefreshDevices()
    {
        var snapshot = _registry.Snapshot();
        var rows = new List<string>(snapshot.Count);
        foreach (var e in snapshot)
        {
            string name = e.Alias is { } a ? $"{e.Token}/{a}" : e.Token;
            string state = DeviceDisplay.FormatState(e);
            string batt = e.Battery is { } b
                ? $"{b}{(e.BatteryVolts is { } v ? $" {v:F1}V" : "")}"
                : "--";
            string rssi = e.Rssi is { } r ? $"{r}dBm" : "--";
            rows.Add($"{Pad(name, 12)} {Pad(e.ProfileName, 6)} {Pad(state, 12)} " +
                     $"{Pad(DeviceDisplay.FormatPrimary(e), 18)} batt:{Pad(batt, 10)} {rssi}");
        }
        _deviceList.SetSource(rows);
    }

    private static void RefreshStatus()
    {
        var snapshot = _registry.Snapshot();
        int connected = 0, visible = 0;
        foreach (var e in snapshot)
        {
            if (e.Connected)
                connected++;
            else
                visible++;
        }
        _status.Title = $"mode:{_session.ModeLabel} · {connected} connected · {visible} visible";
        Application.Top.SetNeedsDisplay();
    }

    private static string Pad(string s, int width)
        => s.Length >= width ? s.Substring(0, width) : s.PadRight(width);
}
