using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace JadeCourt.Chess;

internal sealed class StockfishBot(string executable, Action<string>? warn)
{
    private static readonly SemaphoreSlim Slot = new(1, 1);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    private const int MaxOutputCharacters = 32_768;
    private const int MaxLineCharacters = 2_048;

    internal string? Select(string history, IReadOnlySet<string> legalMoves, ChessBotPreset preset)
    {
        if (!Path.IsPathFullyQualified(executable))
            return Fallback("the executable path must be absolute");
        if (!Slot.Wait(0))
            return Fallback("the engine is busy");

        string failure;
        try
        {
            var move = RequestAsync(history, preset).GetAwaiter().GetResult();
            if (legalMoves.Contains(move))
                return move;
            failure = "the engine returned a move that is not legal";
        }
        catch (OperationCanceledException)
        {
            failure = "the UCI request timed out";
        }
        catch (InvalidDataException)
        {
            failure = "the engine exceeded the UCI output limit";
        }
        catch (IOException)
        {
            failure = "the UCI pipe closed before a move was received";
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            failure = "the executable could not be run";
        }
        finally
        {
            Slot.Release();
        }

        return Fallback(failure);
    }

    private string? Fallback(string reason)
    {
        var warning = $"Stockfish unavailable: {reason}; using the built-in chess bot.";
        if (warn is null)
            Trace.TraceWarning(warning);
        else
            warn(warning);
        return null;
    }

    private async Task<string> RequestAsync(string history, ChessBotPreset preset)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Stockfish did not start.");
        using var input = process.StandardInput.BaseStream;
        using var output = process.StandardOutput;
        using var error = process.StandardError;

        var drain = DrainErrorAsync(error, deadline.Token);
        var exchange = ExchangeAsync(input, output, history, preset, deadline.Token);
        var completed = false;
        try
        {
            if (await Task.WhenAny(exchange, drain).WaitAsync(deadline.Token).ConfigureAwait(false) == drain)
                await drain.ConfigureAwait(false);

            var move = await exchange.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (drain.IsFaulted)
                await drain.ConfigureAwait(false);
            completed = true;
            return move;
        }
        finally
        {
            deadline.Cancel();
            Stop(process, completed);
            // A timed-out read may fault once the process closes its pipe.
            _ = Task.WhenAll(exchange, drain).ContinueWith(static faulted => _ = faulted.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task<string> ExchangeAsync(
        Stream input, StreamReader output, string history, ChessBotPreset preset, CancellationToken token)
    {
        var reader = new UciReader(output);
        await SendAsync(input, "uci", token).ConfigureAwait(false);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) != "uciok") { }

        await SendAsync(input, "setoption name Threads value 1", token).ConfigureAwait(false);
        await SendAsync(input, "setoption name Hash value 16", token).ConfigureAwait(false);
        if (preset == ChessBotPreset.Easy)
            await SendAsync(input, "setoption name Skill Level value 2", token).ConfigureAwait(false);
        else if (preset == ChessBotPreset.Hard)
            await SendAsync(input, "setoption name Skill Level value 20", token).ConfigureAwait(false);
        await SendAsync(input, "isready", token).ConfigureAwait(false);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) != "readyok") { }

        // Replay accepts only canonical UCI coordinates, never arbitrary checkpoint text.
        var position = history.Length == 0 ? "position startpos" : "position startpos moves " + history;
        await SendAsync(input, position, token).ConfigureAwait(false);
        var search = preset switch
        {
            ChessBotPreset.Easy => "go movetime 45",
            ChessBotPreset.Hard => "go movetime 400",
            _ => "go movetime 100"
        };
        await SendAsync(input, search, token).ConfigureAwait(false);

        while (true)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (!line.StartsWith("bestmove ", StringComparison.Ordinal))
                continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                await SendAsync(input, "quit", token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A process can exit after writing its result, before receiving quit.
            }
            return tokens.Length > 1 ? tokens[1] : "";
        }
    }

    private static async Task SendAsync(Stream input, string line, CancellationToken token)
    {
        await input.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), token).ConfigureAwait(false);
        await input.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task DrainErrorAsync(StreamReader stream, CancellationToken token)
    {
        var buffer = new char[512];
        var received = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            received += read;
            if (received > MaxOutputCharacters)
                throw new InvalidDataException("Stockfish stderr limit exceeded.");
        }
    }

    private static void Stop(Process process, bool completed)
    {
        try
        {
            if (process.HasExited || (completed && process.WaitForExit(100)))
                return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(250);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The child may exit between the check and the kill.
        }
    }

    private sealed class UciReader(StreamReader stream)
    {
        private readonly char[] buffer = new char[512];
        private int start, end, received;

        internal async Task<string> ReadLineAsync(CancellationToken token)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (start == end)
                {
                    end = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    start = 0;
                    if (end == 0)
                        throw new EndOfStreamException();
                    received += end;
                    if (received > MaxOutputCharacters)
                        throw new InvalidDataException("Stockfish stdout limit exceeded.");
                }

                var next = buffer[start++];
                if (next == '\n')
                    return line.ToString().TrimEnd('\r');
                if (line.Length == MaxLineCharacters)
                    throw new InvalidDataException("Stockfish line limit exceeded.");
                line.Append(next);
            }
        }
    }
}
