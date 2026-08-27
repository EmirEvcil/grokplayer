using System.Net;
using System.Text.Json;

static class Dump
{
    public static void Player(string videoId)
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("https://www.youtube.com"), new Cookie("SOCS", "CAI"));
        cookies.Add(new Uri("https://www.youtube.com"), new Cookie("CONSENT", "YES+"));
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var page = Get(http, "https://www.youtube.com/watch?v=" + videoId + "&hl=en",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        var visitor = Extract(page, "\"VISITOR_DATA\":\"") ?? Extract(page, "\"visitorData\":\"");
        Console.WriteLine("visitor=" + (visitor is null ? "none" : visitor[..Math.Min(24, visitor.Length)]));
        Summarize("watch", page is null ? null : Slice(page, "ytInitialPlayerResponse"));

        var safari = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.5 Safari/605.1.15,gzip(gfe)";
        var clients = new (string name, int id, string ver, string ua, string body)[]
        {
            ("ANDROID_VR", 28, "1.65.10",
                "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
                Client("ANDROID_VR", "1.65.10", videoId, visitor, extra: ",\"deviceMake\":\"Oculus\",\"deviceModel\":\"Quest 3\",\"androidSdkVersion\":32,\"osName\":\"Android\",\"osVersion\":\"12L\"")),
            ("IOS", 5, "21.26.4",
                "com.google.ios.youtube/21.26.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
                Client("IOS", "21.26.4", videoId, visitor, extra: ",\"deviceMake\":\"Apple\",\"deviceModel\":\"iPhone16,2\",\"osName\":\"iPhone\",\"osVersion\":\"18.3.2.22D82\"")),
            ("WEB_SAFARI", 1, "2.20260817.01.00", safari,
                Client("WEB", "2.20260817.01.00", videoId, visitor, extra: ",\"userAgent\":\"" + safari.Replace("\"", "") + "\"")),
            ("WEB_EMBED", 56, "2.20260817.00.00",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                Client("WEB_EMBEDDED_PLAYER", "2.20260817.00.00", videoId, visitor)),
            ("TV_EMBED", 85, "2.0",
                "Mozilla/5.0 (ChromiumStylePlatform) Cobalt/Version",
                "{\"context\":{\"client\":{\"clientName\":\"TVHTML5_SIMPLY_EMBEDDED_PLAYER\",\"clientVersion\":\"2.0\",\"hl\":\"en\",\"gl\":\"US\"" + (string.IsNullOrWhiteSpace(visitor) ? "" : ",\"visitorData\":\"" + visitor + "\"") + "},\"thirdParty\":{\"embedUrl\":\"https://www.youtube.com\"}},\"videoId\":\"" + videoId + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}"),
            ("ANDROID", 3, "19.17.34",
                "com.google.android.youtube/19.17.34 (Linux; U; Android 11) gzip",
                Client("ANDROID", "19.17.34", videoId, visitor, extra: ",\"androidSdkVersion\":30,\"osName\":\"Android\",\"osVersion\":\"11\"")),
            ("VISIONOS", 101, "1.02",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
                Client("VISIONOS", "1.02", videoId, visitor, extra: ",\"deviceMake\":\"Apple\",\"deviceModel\":\"RealityDevice17,1\",\"osName\":\"visionOS\",\"osVersion\":\"26.5.23O471\"")),
        };

        foreach (var c in clients)
        {
            var json = Post(http, c.ua, c.id, c.ver, visitor, c.body);
            Summarize(c.name, json);
        }
    }

    private static string Client(string name, string ver, string id, string? visitor, string extra = "")
    {
        var visit = string.IsNullOrWhiteSpace(visitor) ? "" : ",\"visitorData\":\"" + visitor + "\"";
        return "{\"context\":{\"client\":{\"clientName\":\"" + name + "\",\"clientVersion\":\"" + ver + "\",\"hl\":\"en\",\"gl\":\"US\"" + extra + visit + "}},\"videoId\":\"" + id + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}";
    }

    private static void Summarize(string name, string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length < 2 || json[0] != '{')
        {
            Console.WriteLine(name + ": no-json len=" + (json?.Length ?? 0));
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.TryGetProperty("playabilityStatus", out var ps) && ps.TryGetProperty("status", out var st)
            ? st.GetString()
            : "?";
        var reason = ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("reason", out var rs) ? rs.GetString() : "";
        if (!root.TryGetProperty("streamingData", out var sd))
        {
            Console.WriteLine(name + ": status=" + status + " reason=" + reason + " no-streaming");
            return;
        }

        var hls = sd.TryGetProperty("hlsManifestUrl", out var h) ? h.GetString() : null;
        var dash = sd.TryGetProperty("dashManifestUrl", out var d) ? d.GetString() : null;
        var formats = Count(sd, "formats", out var prog);
        var adaptive = Count(sd, "adaptiveFormats", out var adp);
        Console.WriteLine(name + ": status=" + status + " hls=" + (hls is not null) + " dash=" + (dash is not null) + " formats=" + formats + " adaptive=" + adaptive + " urls=" + (prog + adp) + " " + Sample(sd));
        if (hls is not null)
        {
            Console.WriteLine("  hls=" + hls[..Math.Min(100, hls.Length)]);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "yt-itags.txt"), "hls\t" + hls + Environment.NewLine);
        }

        PrintItags(name, sd);
    }

    private static void PrintItags(string name, JsonElement sd)
    {
        foreach (var bucket in new[] { "formats", "adaptiveFormats" })
        {
            if (!sd.TryGetProperty(bucket, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("url", out var href) || href.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = href.GetString() ?? "";
                var itag = item.TryGetProperty("itag", out var t) ? t.ToString() : "?";
                var mime = item.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
                var w = item.TryGetProperty("width", out var ww) && ww.TryGetInt32(out var px) ? px : 0;
                if (itag is "18" or "22" or "136" or "137" or "140" or "160" or "133" or "134" or "135")
                {
                    Console.WriteLine("  " + name + " itag=" + itag + " w=" + w + " mime=" + mime);
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "yt-itags.txt"), itag + "\t" + url + Environment.NewLine);
                }
            }
        }
    }

    private static int Count(JsonElement sd, string name, out int urls)
    {
        urls = 0;
        if (!sd.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var n = 0;
        foreach (var item in list.EnumerateArray())
        {
            n++;
            if (item.TryGetProperty("url", out _))
            {
                urls++;
            }
        }

        return n;
    }

    private static string Sample(JsonElement sd)
    {
        foreach (var name in new[] { "formats", "adaptiveFormats" })
        {
            if (!sd.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in list.EnumerateArray())
            {
                var itag = item.TryGetProperty("itag", out var t) ? t.ToString() : "?";
                var mime = item.TryGetProperty("mimeType", out var m) ? m.GetString() : "";
                var hasUrl = item.TryGetProperty("url", out _);
                var cipher = item.TryGetProperty("signatureCipher", out _);
                return "sample itag=" + itag + " url=" + hasUrl + " cipher=" + cipher + " mime=" + mime;
            }
        }

        return "";
    }

    private static string? Get(HttpClient http, string url, string ua)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using var resp = http.Send(req);
        return resp.IsSuccessStatusCode ? resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;
    }

    private static string? Post(HttpClient http, string ua, int clientName, string ver, string? visitor, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientName.ToString());
        req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", ver);
        if (!string.IsNullOrWhiteSpace(visitor))
        {
            req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        }

        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = http.Send(req);
        return resp.IsSuccessStatusCode ? resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;
    }

    private static string? Extract(string? text, string key)
    {
        if (text is null)
        {
            return null;
        }

        var at = text.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + key.Length;
        var end = text.IndexOf('"', start);
        return end > start ? text[start..end] : null;
    }

    private static string? Slice(string html, string name)
    {
        var marker = name + " = ";
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var brace = html.IndexOf('{', at + marker.Length);
        if (brace < 0)
        {
            return null;
        }

        var depth = 0;
        var inStr = false;
        var esc = false;
        for (var i = brace; i < html.Length; i++)
        {
            var ch = html[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }

            if (ch == '"') { inStr = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return html[brace..(i + 1)];
                }
            }
        }

        return null;
    }
}
