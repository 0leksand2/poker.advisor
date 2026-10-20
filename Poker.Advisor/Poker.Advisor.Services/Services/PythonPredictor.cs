using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

using Poker.Advisor.Core.Models;

namespace Poker.Advisor.Services;

public sealed class PythonPredictor : IDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private PythonPredictor(Process process)
    {
        _process = process;
    }

    public static async Task<PythonPredictor> StartAsync(CancellationToken cancellationToken = default)
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "model", "predict_img_with_background_stream.py");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Prediction script not found: {scriptPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{scriptPath}\" --imgsz 1280 --conf 0.25",
            WorkingDirectory = Path.Combine(repoRoot, "model"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Python predictor.");
        }

        process.StandardInput.AutoFlush = true;
        _ = DrainStderrAsync(process);

        var readyLine = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Python predictor produced no ready response.");
        }

        using var readyDoc = JsonDocument.Parse(readyLine);
        if (!readyDoc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = readyDoc.RootElement.TryGetProperty("error", out var err)
                ? err.GetString()
                : readyLine;
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"Python predictor failed to start: {error}");
        }

        return new PythonPredictor(process);
    }

    public async Task<IReadOnlyList<Detection>> PredictAsync(
        byte[] jpegBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException("Python predictor process exited.");
            }

            var stdin = _process.StandardInput.BaseStream;
            var lengthBytes = BitConverter.GetBytes(jpegBytes.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBytes);
            }

            await stdin.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
            await stdin.WriteAsync(jpegBytes, cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException("Python predictor returned an empty response.");
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var err) ? err.GetString() : line;
                throw new InvalidOperationException(error ?? "Prediction failed.");
            }

            if (!root.TryGetProperty("detections", out var detectionsNode))
            {
                return Array.Empty<Detection>();
            }

            var detections = new List<Detection>(detectionsNode.GetArrayLength());
            foreach (var item in detectionsNode.EnumerateArray())
            {
                detections.Add(
                    new Detection(
                        item.GetProperty("name").GetString() ?? "?",
                        item.GetProperty("conf").GetDouble(),
                        item.GetProperty("x1").GetDouble(),
                        item.GetProperty("y1").GetDouble(),
                        item.GetProperty("x2").GetDouble(),
                        item.GetProperty("y2").GetDouble()));
            }

            return detections;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                var zero = new byte[] { 0, 0, 0, 0 };
                _process.StandardInput.BaseStream.Write(zero);
                _process.StandardInput.BaseStream.Flush();
                if (!_process.WaitForExit(1500))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored on shutdown
            }
        }
        finally
        {
            _process.Dispose();
            _gate.Dispose();
        }
    }

    private static async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                Debug.WriteLine("[python] " + line);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var script = Path.Combine(dir.FullName, "model", "predict_img_with_background_stream.py");
            if (File.Exists(script))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repo root containing model/predict_img_with_background_stream.py");
    }
}
