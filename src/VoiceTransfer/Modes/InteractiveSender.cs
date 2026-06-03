using System.Text;
using Serilog;
using VoiceTransfer.Audio;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Interactive sender: reads lines from the console and transmits each one
/// as a separate FSK frame through the audio output in real-time.
/// Each line is encoded, modulated, stealth-shaped, and played immediately.
/// Supports up/down arrow keys to recall previous messages.
/// </summary>
public static class InteractiveSender
{
    public static void Run(IAudioBackend audio, int deviceIndex, TransmissionProfile profile, string? password = null)
    {
        profile.LogSettings();

        var effectiveBaud = (double)profile.BaudRate / profile.FecRepeat;
        var bytesPerSec = effectiveBaud / 8 / 1.37;
        Log.Information("Interactive sender ready (~{Rate:F1} bytes/sec effective)", bytesPerSec);
        Log.Information("Type text and press Enter to transmit. Up/Down for history. Ctrl+C to quit");
        Log.Information("");

        using var player = audio.CreatePlayer(deviceIndex);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = false;
            Log.Information("Shutting down sender...");
        };

        var history = new List<string>();
        var historyIndex = -1;

        while (true)
        {
            Console.Write("> ");
            var line = ReadLineWithHistory(history, ref historyIndex);
            if (line == null)
            {
                break; // EOF / Ctrl+C
            }

            if (line.Length == 0)
            {
                continue;
            }

            history.Add(line);
            historyIndex = history.Count;

            var data = Encoding.UTF8.GetBytes(line);

            // Encode -> modulate -> stealth shape
            var bits = FrameCodec.Encode(data, profile, password);
            var modulator = new FskModulator(profile);
            var fskSamples = modulator.ModulateBits(bits);
            var shaped = StealthShaper.Apply(fskSamples, profile);

            // Small comfort noise gap between messages (silence would be conspicuous)
            var noiseLevel = profile.Amplitude * 0.25;
            var gap = StealthShaper.GenerateComfortNoise(0.15, noiseLevel);
            var all = FskModulator.Concat(gap, shaped, gap);

            var duration = (double)all.Length / Constants.SampleRate;
            Log.Information("Sent {Len} bytes ({Bits} bits, {Dur:F1}s audio)",
                data.Length, bits.Length, duration);

            player.Play(all);
        }
    }

    private static string? ReadLineWithHistory(List<string> history, ref int historyIndex)
    {
        var buffer = new StringBuilder();
        var cursorPos = 0;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.UpArrow:
                    if (history.Count > 0 && historyIndex > 0)
                    {
                        historyIndex--;
                        ReplaceBuffer(buffer, ref cursorPos, history[historyIndex]);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (historyIndex < history.Count - 1)
                    {
                        historyIndex++;
                        ReplaceBuffer(buffer, ref cursorPos, history[historyIndex]);
                    }
                    else
                    {
                        historyIndex = history.Count;
                        ReplaceBuffer(buffer, ref cursorPos, "");
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursorPos > 0)
                    {
                        cursorPos--;
                        Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorPos < buffer.Length)
                    {
                        cursorPos++;
                        Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
                    }
                    break;

                case ConsoleKey.Home:
                    Console.SetCursorPosition(Console.CursorLeft - cursorPos, Console.CursorTop);
                    cursorPos = 0;
                    break;

                case ConsoleKey.End:
                    Console.SetCursorPosition(Console.CursorLeft + (buffer.Length - cursorPos), Console.CursorTop);
                    cursorPos = buffer.Length;
                    break;

                case ConsoleKey.Backspace:
                    if (cursorPos > 0)
                    {
                        buffer.Remove(cursorPos - 1, 1);
                        cursorPos--;
                        RedrawFromCursor(buffer, cursorPos);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursorPos < buffer.Length)
                    {
                        buffer.Remove(cursorPos, 1);
                        RedrawFromCursor(buffer, cursorPos);
                    }
                    break;

                default:
                    if (key.KeyChar >= ' ')
                    {
                        buffer.Insert(cursorPos, key.KeyChar);
                        cursorPos++;
                        RedrawFromCursor(buffer, cursorPos);
                    }
                    else if (key.KeyChar == '\0' || key.KeyChar == 27)
                    {
                        // Ignore control/escape sequences
                    }
                    else if (key.KeyChar == 3) // Ctrl+C
                    {
                        Console.WriteLine();
                        return null;
                    }
                    break;
            }
        }
    }

    private static void ReplaceBuffer(StringBuilder buffer, ref int cursorPos, string newText)
    {
        // Move to start of input, clear, write new text
        var promptCol = Console.CursorLeft - cursorPos;
        Console.SetCursorPosition(promptCol, Console.CursorTop);
        Console.Write(new string(' ', buffer.Length));
        Console.SetCursorPosition(promptCol, Console.CursorTop);
        Console.Write(newText);

        buffer.Clear();
        buffer.Append(newText);
        cursorPos = newText.Length;
    }

    private static void RedrawFromCursor(StringBuilder buffer, int cursorPos)
    {
        // Calculate the start position of the entire input (after "> ")
        var startCol = Console.CursorLeft - cursorPos;
        Console.SetCursorPosition(startCol, Console.CursorTop);
        Console.Write(buffer);
        Console.Write(' '); // clear trailing char
        Console.SetCursorPosition(startCol + cursorPos, Console.CursorTop);
    }
}
