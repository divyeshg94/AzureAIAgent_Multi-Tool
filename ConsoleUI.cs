using System.Diagnostics;

namespace AzureAIAgent101;

/// <summary>
/// Console output helpers for colored text, wrapped output, and spinner animation.
/// </summary>
internal static class ConsoleUI
{
    public static void WriteRole(string role, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(role);
        Console.ForegroundColor = old;
    }

    public static void WriteInfo(string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    public static void WriteWarn(string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    public static void WriteError(string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    public static void WriteWrapped(string text, ConsoleColor color)
    {
        var width = Math.Max(40, Console.WindowWidth - 4);
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        
        foreach (var line in (text ?? string.Empty).Replace("\r", "").Split('\n'))
        {
            var remaining = line;
            while (remaining.Length > width)
            {
                var cut = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
                if (cut <= 0) cut = Math.Min(width, remaining.Length);
                Console.WriteLine(remaining[..cut]);
                remaining = remaining[cut..].TrimStart();
            }
            Console.WriteLine(remaining);
        }
        Console.ForegroundColor = old;
    }
}

/// <summary>
/// Animated spinner for console output during long-running operations.
/// </summary>
internal sealed class Spinner : IDisposable
{
    private readonly object _lock = new();
    private string _text;
    private bool _running = true;
    private readonly Thread _thread;
    private readonly string[] _frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    public Spinner(string text)
    {
        _text = text;
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
    }

    public void Update(string text)
    {
        lock (_lock) _text = text;
    }

    public void Done(string? final = null)
    {
        _running = false;
        Thread.Sleep(60);
        Console.Write("\r".PadRight(Console.WindowWidth - 1) + "\r");
        if (!string.IsNullOrWhiteSpace(final))
            ConsoleUI.WriteInfo(final);
    }

    private void Run()
    {
        int i = 0;
        while (_running)
        {
            string t;
            lock (_lock) t = _text;
            Console.Write($"\r{_frames[i % _frames.Length]} {t}".PadRight(Console.WindowWidth - 1));
            i++;
            Thread.Sleep(80);
        }
    }

    public void Dispose() => Done();
}
