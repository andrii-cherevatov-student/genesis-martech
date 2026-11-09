using System.Net;
using System.Net.Sockets;

namespace MarTechSolution.Services;

public record DownloadedVideo(string Path, long Length, string MimeType, string Name);

/// <summary>Handles "paste a link" input: YouTube links go to Gemini as-is; direct video links are downloaded.</summary>
public sealed class VideoLinks(HttpClient http)
{
    const long MaxBytes = 2L * 1024 * 1024 * 1024;

    public static Uri Parse(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new AppException(400, "That doesn't look like a valid link.", "Paste a full http(s) link to a video file or a YouTube video.");
        return uri;
    }

    /// <summary>The video id for youtube.com/watch?v=, /shorts/, /embed/, /live/ and youtu.be links; null for anything else.</summary>
    public static string? YouTubeId(Uri u)
    {
        var host = u.Host.ToLowerInvariant();
        string? id = null;
        if (host == "youtu.be") id = u.AbsolutePath.Trim('/');
        else if (host == "youtube.com" || host.EndsWith(".youtube.com"))
        {
            id = System.Web.HttpUtility.ParseQueryString(u.Query)["v"];
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (id is null && parts.Length >= 2 && parts[0] is "shorts" or "embed" or "live") id = parts[1];
        }
        return id is { Length: >= 6 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    public async Task<DownloadedVideo> DownloadAsync(Uri uri, CancellationToken ct)
    {
        // Follow redirects manually so every hop is checked against private/local addresses.
        HttpResponseMessage? res = null;
        for (var hop = 0; ; hop++)
        {
            await EnsurePublicHostAsync(uri, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("CreativeRemixEngine/1.0");
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } next && hop < 5)
            {
                uri = new Uri(uri, next);
                res.Dispose();
                continue;
            }
            break;
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new AppException(422, $"Couldn't download the video (HTTP {(int)res.StatusCode}).",
                    "Make sure the link is public and points directly to a video file.");

            var mime = res.Content.Headers.ContentType?.MediaType ?? "";
            if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                throw new AppException(422, "That link opens a web page, not a video file.",
                    "Ad Library and TikTok pages don't expose the video file. Paste a direct .mp4 link or a YouTube link, or download the ad and drop the file here.");
            if (!mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                if (mime is not ("" or "application/octet-stream" or "binary/octet-stream"))
                    throw new AppException(415, $"The link returned “{mime}”, not a video.", "Paste a direct link to an MP4 file.");
                mime = "video/mp4";
            }
            if (res.Content.Headers.ContentLength > MaxBytes)
                throw new AppException(413, "That video is larger than 2 GB.", "Use a shorter or compressed export.");

            var path = System.IO.Path.GetTempFileName();
            try
            {
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(path);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new AppException(413, "That video is larger than 2 GB.", "Use a shorter or compressed export.");
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total == 0) throw new AppException(422, "The link returned an empty file.");
                var name = System.IO.Path.GetFileName(uri.LocalPath);
                return new DownloadedVideo(path, total, mime, string.IsNullOrWhiteSpace(name) ? "video.mp4" : name);
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }
    }

    static async Task EnsurePublicHostAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); }
        catch (SocketException) { throw new AppException(422, $"Couldn't resolve “{uri.Host}”.", "Check the link for typos."); }

        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new AppException(400, "Links to local or private-network addresses aren't allowed.", "Use a public video link.");
    }

    static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] is 10 or 127 or 0
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
