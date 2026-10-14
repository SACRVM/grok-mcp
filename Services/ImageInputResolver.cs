using System.IO;

namespace GrokMcp.Services;

public class ImageInputResolver
{
    public object Resolve(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Image input must not be empty", nameof(input));

        var trimmed = input.Trim();

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new { url = trimmed };
        }

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new { url = trimmed };
        }

        if (Path.IsPathFullyQualified(trimmed) || File.Exists(trimmed))
        {
            if (!File.Exists(trimmed))
            {
                // On Unix a leading '/' makes a path fully qualified, and raw JPEG base64 always
                // starts with "/9j/". Only bytes that decode to a known image format count here,
                // so a mistyped path still gets the file-not-found error.
                if (TryDecodeImageBase64(trimmed) is { } knownMime)
                    return new { url = $"data:{knownMime};base64,{trimmed}" };
                throw new FileNotFoundException($"Image file not found: {trimmed}", trimmed);
            }

            var bytes = File.ReadAllBytes(trimmed);
            var mime = SniffMime(bytes);
            var b64 = Convert.ToBase64String(bytes);
            return new { url = $"data:{mime};base64,{b64}" };
        }

        // Treat anything else as raw base64; PNG mime unless the bytes say otherwise.
        return new { url = $"data:{TryDecodeImageBase64(trimmed) ?? "image/png"};base64,{trimmed}" };
    }

    public string ResolveDataUri(string input)
    {
        var obj = Resolve(input);
        return (string)obj.GetType().GetProperty("url")!.GetValue(obj)!;
    }

    private static string SniffMime(byte[] bytes) => SniffKnownMime(bytes) ?? "image/png";

    private static string? SniffKnownMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";
        return null;
    }

    private static string? TryDecodeImageBase64(string s)
    {
        var buffer = new byte[s.Length * 3 / 4];
        return Convert.TryFromBase64String(s, buffer, out var written)
            ? SniffKnownMime(buffer.AsSpan(0, written))
            : null;
    }
}
