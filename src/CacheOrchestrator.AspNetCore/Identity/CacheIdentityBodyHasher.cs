using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Globalization;
using System.IO.Hashing;

namespace CacheOrchestrator.Identity;

/// <summary>
/// Bounded request-body XxHash3 helper for content-hash cache identity.
/// </summary>
internal static class CacheIdentityBodyHasher
{
    private const string BodyHashValueKey = "body-hash";

    /// <summary>
    /// Hashes up to <paramref name="maxBodyBytes"/> of the request body.
    /// Returns <see langword="null"/> when the body exceeds the limit (no silent truncation).
    /// Oversized bodies are logged at <see cref="LogLevel.Warning"/>.
    /// The limit applies only to identity hashing: buffering remains usable so the endpoint
    /// can read the full request body after a bypass.
    /// </summary>
    public static async ValueTask<CacheIdentityMaterial?> HashAsync(
        HttpRequest request,
        int maxBodyBytes,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maxBodyBytes <= 0)
            return null;

        if (request.ContentLength is long contentLength && contentLength > maxBodyBytes)
        {
            LogOversize(logger, request, maxBodyBytes, contentLength);
            return null;
        }

        // Enable buffering so the endpoint can re-read the body after hashing.
        // Do not set bufferLimit to maxBodyBytes: that limit is only for cache identity.
        // Oversized bodies still bypass identity, but the request body must remain readable
        // (host MaxRequestBodySize remains the absolute request size guard).
        if (!request.Body.CanSeek)
            request.EnableBuffering(bufferThreshold: Math.Min(30_720, maxBodyBytes));

        Stream body = request.Body;
        long originalPosition = 0;
        if (body.CanSeek)
        {
            originalPosition = body.Position;
            body.Position = 0;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            XxHash3 hasher = new();
            long total = 0;
            while (true)
            {
                int toRead = (int)Math.Min(buffer.Length, maxBodyBytes + 1L - total);
                if (toRead <= 0)
                    break;

                int read = await body.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (total > maxBodyBytes)
                {
                    LogOversize(logger, request, maxBodyBytes, measuredLength: total);
                    return null;
                }

                hasher.Append(buffer.AsSpan(0, read));
            }

            ulong hash = hasher.GetCurrentHashAsUInt64();
            string hex = hash.ToString("x16", CultureInfo.InvariantCulture);
            return new CacheIdentityMaterial(
            [
                new KeyValuePair<string, string>(BodyHashValueKey, hex),
            ]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (body.CanSeek)
                body.Position = originalPosition;
        }
    }

    private static void LogOversize(
        ILogger? logger,
        HttpRequest request,
        int maxBodyBytes,
        long? contentLength = null,
        long? measuredLength = null)
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Warning))
            return;

        if (contentLength is long cl)
        {
            logger.LogWarning(
                "Content-hash cache identity skipped: Content-Length {ContentLength} exceeds MaxBodyBytes {MaxBodyBytes} for {Method} {Path}",
                cl,
                maxBodyBytes,
                request.Method,
                request.Path.Value);
            return;
        }

        logger.LogWarning(
            "Content-hash cache identity skipped: request body exceeds MaxBodyBytes {MaxBodyBytes} (read at least {BytesRead} bytes) for {Method} {Path}",
            maxBodyBytes,
            measuredLength,
            request.Method,
            request.Path.Value);
    }
}
