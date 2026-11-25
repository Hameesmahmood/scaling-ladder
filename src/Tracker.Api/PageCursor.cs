using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Tracker.Api;

/// <summary>An opaque page cursor: the last row's created_at and id, URL-safe base64.</summary>
public sealed record PageCursor(DateTimeOffset CreatedAt, long Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CreatedAt.ToUnixTimeMilliseconds()}:{Id}"));

    public static bool TryDecode(string? value, out PageCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value)) return true;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                cursor = new PageCursor(DateTimeOffset.FromUnixTimeMilliseconds(ms), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }
        return false;
    }
}
