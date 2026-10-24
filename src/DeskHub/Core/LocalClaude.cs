using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskHub.Core;

/// <summary>
/// 本机 Claude 小服务：只听 127.0.0.1，收到请求转给这台电脑上已登录的 claude 命令行（claude -p），把回答原样还回去。
/// 日历的“智能识别日程”用它，自家别的程序也能用（传提示词、拿文字）。接口说明见 发布\DeskHub\本机Claude服务说明.md。
/// 不落盘请求内容：日志只记时间、接口、模型、字数、耗时、结果。
/// </summary>
public sealed class LocalClaude : IDisposable
{
    public const int DefaultPort = 17860;
    /// <summary>单次调用最多等多久（日历那边等 2 分钟，这里要比它短，才能把“超时”说清楚）。</summary>
    public const int TimeoutSec = 100;
    public static readonly string[] Models = { "sonnet", "opus", "haiku", "fable" };
    public static string LogPath => Path.Combine(AppConfig.DataDir, "local-claude.log");
    public static string DocPath => Path.Combine(AppContext.BaseDirectory, "本机Claude服务说明.md");

    readonly int _port;
    readonly byte[] _token;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _gate = new(2); // 同时最多跑两个 claude，多的排队
    TcpListener? _listener;

    public int Port => _port;
    public string Error { get; private set; } = "";

    public LocalClaude(int port, string token)
    {
        _port = port;
        _token = Encoding.UTF8.GetBytes(token);
    }

    public bool Start()
    {
        if (_token.Length == 0) { Error = L.T("先填口令"); return false; }
        try
        {
            // 写死 127.0.0.1：局域网里别的电脑连不上
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start();
        }
        catch (SocketException e)
        {
            _listener = null;
            Error = e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied ? L.F("端口 {0} 被别的程序占了，换一个", _port) : e.Message;
            return false;
        }
        Log($"start 127.0.0.1:{_port}");
        _ = AcceptLoop();
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_listener != null) Log("stop");
        _listener = null;
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    // ---------------- HTTP（一个连接一个请求，回完就关） ----------------
    sealed record Req(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    sealed class Fail(int status, string type, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string Type { get; } = type;
    }

    async Task Serve(TcpClient c)
    {
        using var _ = c;
        var s = c.GetStream();
        var sw = Stopwatch.StartNew();
        string line = "?";
        string info = "";
        int status = 500;
        try
        {
            using var rt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            rt.CancelAfter(TimeSpan.FromSeconds(30)); // 读请求最多 30 秒
            var req = await Read(s, rt.Token);
            line = $"{req.Method} {req.Path}";
            var (st, body, note) = await Handle(req);
            status = st; info = note;
            await Write(s, st, body);
        }
        catch (Fail f)
        {
            status = f.Status; info = f.Type;
            try { await Write(s, f.Status, Err(f.Type, f.Message)); } catch { }
        }
        catch (Exception e)
        {
            info = e.GetType().Name;
            try { await Write(s, 500, Err("internal", e.Message)); } catch { }
        }
        Log($"{line} {status} {sw.Elapsed.TotalSeconds:0.0}s {info}".TrimEnd());
    }

    static async Task<Req> Read(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        int n = 0, head = -1;
        while (head < 0)
        {
            if (n == buf.Length) throw new Fail(431, "bad_request", "headers too large");
            int r = await s.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) throw new Fail(400, "bad_request", "connection closed");
            n += r;
            head = IndexOf(buf, n, "\r\n\r\n"u8);
        }
        var lines = Encoding.UTF8.GetString(buf, 0, head).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) throw new Fail(400, "bad_request", "bad request line");
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
            if (l.IndexOf(':') is var i and > 0) h[l[..i].Trim()] = l[(i + 1)..].Trim();
        if (h.ContainsKey("Transfer-Encoding")) throw new Fail(411, "bad_request", "send Content-Length, not chunked");
        int len = h.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
        if (len < 0 || len > 30 * 1024 * 1024) throw new Fail(413, "bad_request", "body too large (max 30 MB)");
        var body = new byte[len];
        int have = Math.Min(len, n - head - 4);
        Array.Copy(buf, head + 4, body, 0, have);
        if (have < len && h.TryGetValue("Expect", out var ex) && ex.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            await s.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);
        while (have < len)
        {
            int r = await s.ReadAsync(body.AsMemory(have), ct);
            if (r == 0) throw new Fail(400, "bad_request", "body cut short");
            have += r;
        }
        var path = first[1];
        if (path.IndexOf('?') is var q and >= 0) path = path[..q];
        return new Req(first[0].ToUpperInvariant(), path.TrimEnd('/') is { Length: > 0 } p ? p : "/", h, body);
    }

    static int IndexOf(byte[] b, int n, ReadOnlySpan<byte> pat) => b.AsSpan(0, n).IndexOf(pat);

    static async Task Write(NetworkStream s, int status, JsonNode body)
    {
        var data = Encoding.UTF8.GetBytes(body.ToJsonString(Plain));
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 502 => "Bad Gateway", 503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {data.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(data);
    }

    /// <summary>中文原样输出，不转成 \uXXXX。</summary>
    static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static JsonObject Err(string type, string message) => new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };

    async Task<(int, JsonNode, string)> Handle(Req r)
    {
        // 只认本机地址：挡住“DNS 重绑定”那种借浏览器打进来的
        var host = r.Headers.GetValueOrDefault("Host", "");
        if (host != $"127.0.0.1:{_port}" && host != $"localhost:{_port}") throw new Fail(403, "forbidden", "Host must be 127.0.0.1:" + _port);
        // 浏览器里的网页发请求会带 Origin：一律不接（自家程序不会带）
        if (r.Headers.ContainsKey("Origin") || r.Method == "OPTIONS") throw new Fail(403, "forbidden", "requests from web pages are not accepted");

        if (r.Method == "GET" && r.Path is "/" or "/health")
            return (200, new JsonObject { ["ok"] = true, ["service"] = "New B Desk local Claude", ["engine"] = "claude -p", ["timeout_sec"] = TimeoutSec }, "");

        var given = r.Headers.TryGetValue("x-api-key", out var k) ? k
            : r.Headers.TryGetValue("Authorization", out var a) && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..].Trim() : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token))
            throw new Fail(401, "authentication_error", L.T("口令不对"));

        switch (r.Method, r.Path)
        {
            case ("GET", "/v1/models"):
                var data = new JsonArray();
                foreach (var m in Models) data.Add(new JsonObject { ["id"] = m, ["object"] = "model", ["type"] = "model", ["display_name"] = m });
                return (200, new JsonObject { ["object"] = "list", ["data"] = data }, "");
            case ("POST", "/v1/ask"):
                return await Ask(r);
            case ("POST", "/v1/messages"):
                return await Messages(r);
            case ("POST", "/v1/chat/completions"):
                return await Chat(r);
        }
        throw new Fail(r.Path is "/v1/ask" or "/v1/messages" or "/v1/chat/completions" or "/v1/models" ? 405 : 404, "not_found", $"{r.Method} {r.Path} not supported");
    }

    static JsonNode? ParseJson(byte[] body)
    {
        try { return JsonNode.Parse(RawText(body)); } catch { return null; }
    }

    /// <summary>通用接口：{"prompt": "…", "system": "…", "model": "sonnet", "images": ["base64…" 或 "data:image/png;base64,…"]}；不是 JSON 就把整段当提示词。</summary>
    async Task<(int, JsonNode, string)> Ask(Req r)
    {
        var j = ParseJson(r.Body) as JsonObject;
        string prompt = j != null ? j["prompt"]?.ToString() ?? "" : RawText(r.Body);
        var content = new JsonArray();
        if (j?["images"] is JsonArray imgs)
            foreach (var i in imgs) if (Image(i?.ToString() ?? "") is { } b) content.Add(b);
        if (prompt.Trim().Length == 0 && content.Count == 0) throw new Fail(400, "invalid_request_error", "prompt is empty");
        content.Add(Text(prompt));
        var res = await Run(j?["system"]?.ToString(), content, j?["model"]?.ToString());
        return (200, new JsonObject { ["text"] = res.Text, ["model"] = res.Model }, Note(res, content));
    }

    /// <summary>直接发来的一段文字：按 UTF-8 读；不是 UTF-8 的（Windows 命令行里的 curl 会按系统编码发，比如 GBK）按系统编码读。</summary>
    static string RawText(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(b);
        }
    }

    /// <summary>Claude 自己的格式（/v1/messages）：日历识别默认走这个。</summary>
    async Task<(int, JsonNode, string)> Messages(Req r)
    {
        if (ParseJson(r.Body) is not JsonObject j) throw new Fail(400, "invalid_request_error", "body must be JSON");
        var system = j["system"] switch
        {
            JsonValue v => v.ToString(),
            JsonArray arr => string.Join("\n", arr.Select(b => b?["text"]?.ToString() ?? "")),
            _ => null,
        };
        var content = Flatten(j["messages"] as JsonArray, m => m?["content"], block => block?["type"]?.ToString() switch
        {
            "text" => Text(block?["text"]?.ToString() ?? ""),
            "image" when block?["source"]?["type"]?.ToString() == "base64" => new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = block["source"]!["media_type"]?.ToString() ?? "image/png", ["data"] = block["source"]!["data"]?.ToString() ?? "" },
            },
            _ => null,
        });
        var res = await Run(system, content, j["model"]?.ToString());
        var o = new JsonObject
        {
            ["id"] = "msg_local_" + Guid.NewGuid().ToString("N")[..16],
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = res.Model,
            ["content"] = new JsonArray { Text(res.Text) },
            ["stop_reason"] = "end_turn",
        };
        return (200, o, Note(res, content));
    }

    /// <summary>OpenAI 兼容格式（/v1/chat/completions）：大多数现成工具认这个。</summary>
    async Task<(int, JsonNode, string)> Chat(Req r)
    {
        if (ParseJson(r.Body) is not JsonObject j) throw new Fail(400, "invalid_request_error", "body must be JSON");
        var msgs = j["messages"] as JsonArray ?? new JsonArray();
        var system = string.Join("\n", msgs.Where(m => m?["role"]?.ToString() is "system" or "developer").Select(m => m?["content"] is JsonValue v ? v.ToString() : string.Join("\n", (m?["content"] as JsonArray ?? new()).Select(p => p?["text"]?.ToString() ?? ""))));
        var rest = new JsonArray(msgs.Where(m => m?["role"]?.ToString() is not ("system" or "developer")).Select(m => m?.DeepClone()).ToArray());
        var content = Flatten(rest, m => m?["content"], part => part?["type"]?.ToString() switch
        {
            "text" => Text(part?["text"]?.ToString() ?? ""),
            "image_url" => Image(part?["image_url"]?["url"]?.ToString() ?? ""),
            _ => null,
        });
        var res = await Run(system.Length > 0 ? system : null, content, j["model"]?.ToString());
        var o = new JsonObject
        {
            ["id"] = "chatcmpl-local-" + Guid.NewGuid().ToString("N")[..16],
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = res.Model,
            ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = res.Text }, ["finish_reason"] = "stop" } },
        };
        return (200, o, Note(res, content));
    }

    /// <summary>把一串消息拼成一条：只有一条就原样转（文字 + 图片），多条就把前面的对话写成文字带上。</summary>
    static JsonArray Flatten(JsonArray? msgs, Func<JsonNode?, JsonNode?> contentOf, Func<JsonNode?, JsonObject?> block)
    {
        if (msgs == null || msgs.Count == 0) throw new Fail(400, "invalid_request_error", "messages is empty");
        var outp = new JsonArray();
        bool multi = msgs.Count > 1;
        foreach (var m in msgs)
        {
            var role = m?["role"]?.ToString() ?? "user";
            if (multi) outp.Add(Text(role == "assistant" ? "[Assistant]" : "[User]"));
            switch (contentOf(m))
            {
                case JsonValue v: outp.Add(Text(v.ToString())); break;
                case JsonArray parts:
                    foreach (var p in parts) if (block(p) is { } b) outp.Add(b);
                    break;
            }
        }
        if (!outp.Any(b => b?["type"]?.ToString() == "image" || (b?["text"]?.ToString() ?? "").Trim().Length > 0))
            throw new Fail(400, "invalid_request_error", "messages has no text or image");
        return outp;
    }

    static JsonObject Text(string s) => new() { ["type"] = "text", ["text"] = s };

    static JsonObject? Image(string s)
    {
        s = s.Trim();
        string media = "image/png";
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = s.IndexOf(',');
            if (comma < 0 || !s[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            media = s[5..s.IndexOf(';')];
            s = s[(comma + 1)..];
        }
        else if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) throw new Fail(400, "invalid_request_error", "image URLs are not supported, send base64");
        if (s.Length == 0) return null;
        return new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = media, ["data"] = s } };
    }

    static string Note(Result r, JsonArray content) =>
        $"model={r.Model} chars={content.Sum(b => b?["text"]?.ToString().Length ?? 0)} images={content.Count(b => b?["type"]?.ToString() == "image")}";

    // ---------------- 调本机 claude -p ----------------
    public sealed record Result(string Text, string Model);

    async Task<Result> Run(string? system, JsonArray content, string? model)
    {
        var exe = FindClaude() ?? throw new Fail(503, "claude_not_found", L.T("这台电脑上没找到 claude 命令行。装好后在命令行里运行一次 claude 登录，再试。"));
        model = model?.Trim() is { Length: > 0 } m ? m : Models[0]; // 没指定就用 sonnet，免得跟着命令行的默认设置变
        if (model.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(model, @"^[A-Za-z0-9._\-\[\]]{1,64}$"))
            throw new Fail(400, "invalid_request_error", "bad model name");

        using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        t.CancelAfter(TimeSpan.FromSeconds(TimeoutSec));
        try { await _gate.WaitAsync(t.Token); }
        catch (OperationCanceledException) { throw new Fail(504, "timeout", L.F("本机 Claude 正忙，{0} 秒内没排上", TimeoutSec)); }
        try { return await RunOnce(exe, system, content, model, t.Token); }
        finally { _gate.Release(); }
    }

    static async Task<Result> RunOnce(string exe, string? system, JsonArray content, string model, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkDir(),
        };
        // 用本机登录的账号（不加 --bare）；不给它任何工具，不留会话记录，只当“问一句答一句”
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                                  "--tools", "", "--no-session-persistence", "--strict-mcp-config", "--disable-slash-commands" })
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--system-prompt");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(system) ? "You are Claude, a helpful assistant." : system);
        // 本工具要是从某个 Claude 会话里启动的，会继承那个会话的环境变量（子会话标记、它的连接方式、模型设置）：全去掉，用本机登录的账号
        if (psi.Environment.ContainsKey("CLAUDECODE"))
            foreach (var key in psi.Environment.Keys.ToList())
                if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase))
                    psi.Environment.Remove(key);

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception e) { throw new Fail(503, "claude_not_found", L.F("启动 claude 失败：{0}", e.Message)); }
        try
        {
            var msg = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = content.DeepClone() } };
            await p.StandardInput.WriteLineAsync(msg.ToJsonString().AsMemory(), ct);
            p.StandardInput.Close();
            var errTask = p.StandardError.ReadToEndAsync(ct);
            JsonNode? result = null;
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (!line.Contains("\"result\"")) continue;
                try { if (JsonNode.Parse(line) is { } n && n["type"]?.ToString() == "result") result = n; } catch { }
            }
            await p.WaitForExitAsync(ct);
            var err = (await errTask).Trim();
            if (result == null)
                throw new Fail(502, "claude_error", L.F("本机 Claude 没给出结果（退出码 {0}）", p.ExitCode) + (err.Length > 0 ? "：" + Tail(err) : ""));
            var text = result["result"]?.ToString() ?? "";
            if (result["is_error"]?.GetValue<bool>() == true)
                throw new Fail(502, "claude_error", L.T("本机 Claude 出错") + "：" + Tail(text.Length > 0 ? text : err));
            // 回答主要是哪个模型写的（输出最多的那个；它内部可能顺带用了别的小模型）
            var used = (result["modelUsage"] as JsonObject)?.OrderByDescending(kv => kv.Value?["outputTokens"]?.GetValue<double>() ?? 0).Select(kv => kv.Key).FirstOrDefault() ?? model;
            return new Result(text, used);
        }
        catch (OperationCanceledException)
        {
            throw new Fail(504, "timeout", L.F("本机 Claude {0} 秒没回完，这次已停掉", TimeoutSec));
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    static string Tail(string s) => s.Length > 300 ? "…" + s[^300..] : s;

    /// <summary>给 claude 一个空文件夹当工作目录，免得它去读别处的项目说明。</summary>
    static string WorkDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "DeskHub-claude");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>找 claude.exe：PATH 里的、官方安装的 ~/.local/bin、npm 装的（claude.cmd 旁边 node_modules 里的 exe）。</summary>
    public static string? FindClaude()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var d in dirs)
        {
            try
            {
                var exe = Path.Combine(d, "claude.exe");
                if (File.Exists(exe)) return exe;
                var npm = Path.Combine(d, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                if (File.Exists(Path.Combine(d, "claude.cmd")) && File.Exists(npm)) return npm;
            }
            catch { }
        }
        return null;
    }

    static readonly object LogLock = new();
    static void Log(string s)
    {
        try
        {
            lock (LogLock)
            {
                var f = new FileInfo(LogPath);
                if (f.Exists && f.Length > 1024 * 1024) f.Delete(); // 只留最近一点
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}\n");
            }
        }
        catch { }
    }
}
