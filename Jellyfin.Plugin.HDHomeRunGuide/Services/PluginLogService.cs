using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HDHomeRunGuide.Services;

/// <summary>
/// Keeps recent plugin diagnostics available from the configuration page.
/// </summary>
public sealed class PluginLogService
{
    private const int MaxEntries = 250;
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ILogger<PluginLogService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLogService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public PluginLogService(ILogger<PluginLogService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Adds an informational diagnostic line.
    /// </summary>
    /// <param name="message">Message.</param>
    public void Info(string message)
    {
        Add("INFO", message);
        _logger.LogInformation("{Message}", message);
    }

    /// <summary>
    /// Adds a warning diagnostic line.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="exception">Optional exception.</param>
    public void Warning(string message, Exception? exception = null)
    {
        Add("WARN", exception is null ? message : message + " " + exception.Message);
        _logger.LogWarning(exception, "{Message}", message);
    }

    /// <summary>
    /// Adds an error diagnostic line.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="exception">Optional exception.</param>
    public void Error(string message, Exception? exception = null)
    {
        Add("ERROR", exception is null ? message : message + " " + exception.Message);
        _logger.LogError(exception, "{Message}", message);
    }

    /// <summary>
    /// Gets recent diagnostic lines.
    /// </summary>
    /// <returns>Recent diagnostic lines.</returns>
    public IReadOnlyList<string> GetRecent()
    {
        return _entries.ToArray();
    }

    private void Add(string level, string message)
    {
        _entries.Enqueue($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {RedactSecrets(message)}");
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    private static string RedactSecrets(string value)
    {
        var index = value.IndexOf("DeviceAuth=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return value;
        }

        var end = value.IndexOf('&', index);
        return end < 0
            ? value[..index] + "DeviceAuth=REDACTED"
            : value[..index] + "DeviceAuth=REDACTED" + value[end..];
    }
}
