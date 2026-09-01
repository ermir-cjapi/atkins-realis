using System.Text;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Writes sensitive AI diagnostic payloads to a local, git-ignored file in Development only.
/// </summary>
public sealed class DevelopmentPayloadWriter(IHostEnvironment environment)
{
    private readonly object _writeLock = new();
    private readonly string _logPath = Path.Combine(environment.ContentRootPath, "logs", "ai-payloads.log");

    /// <summary>
    /// Gets the payload log path when development logging is enabled.
    /// </summary>
    public string? LogPath => environment.IsDevelopment() ? _logPath : null;

    /// <summary>
    /// Appends a delimited payload without allowing diagnostics to affect request processing.
    /// </summary>
    public void Write(string category, string payload)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        var entry = new StringBuilder()
            .AppendLine($"===== {category} | {DateTimeOffset.UtcNow:O} =====")
            .AppendLine(payload)
            .AppendLine($"===== END {category} =====")
            .AppendLine()
            .ToString();

        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, entry, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Diagnostic logging must not affect the application request.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostic logging must not affect the application request.
        }
    }
}