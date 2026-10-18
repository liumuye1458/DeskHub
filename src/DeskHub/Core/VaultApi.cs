using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskHub.Core;

/// <summary>其他程序发来的请求（一行 JSON）。</summary>
public sealed class ApiRequest
{
    /// <summary>"status"：密码箱在不在、锁没锁；"get"：要某个网站 / 服务的登录信息。</summary>
    public string Op { get; set; } = "";
    /// <summary>要登录的网址（按网站域名匹配，比如 https://seller.tiktok.com/login 匹配记录里的 tiktok.com）。</summary>
    public string? Url { get; set; }
    /// <summary>或者按服务名找（包含即可）。</summary>
    public string? Name { get; set; }
    /// <summary>可选：说明用途，会显示在确认框里（比如 "自动登录 TikTok 后台"）。</summary>
    public string? Reason { get; set; }
}

public sealed class ApiCredential
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Account { get; set; } = "";
    public string Password { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new();
}

public sealed class ApiResponse
{
    public bool Ok { get; set; }
    /// <summary>出错时：disabled / bad_request / locked / denied / timeout / not_found / busy / unknown_client</summary>
    public string? Error { get; set; }
    public string? App { get; set; }
    public int? Api { get; set; }
    public bool? Locked { get; set; }
    public ApiCredential? Credential { get; set; }

    public static ApiResponse Fail(string e) => new() { Ok = false, Error = e };
}

/// <summary>是谁在要（从管道另一头的进程号查出来的，对方没法冒充）。</summary>
public sealed record ApiClient(int Pid, string Exe, string Hash)
{
    public string Name => Path.GetFileName(Exe);
}

/// <summary>
/// 密码箱对外接口（本机命名管道）。
/// - 只在本机：命名管道，只允许当前 Windows 用户连接，拒绝网络来的连接；不开端口、不联网。
/// - 默认关闭，在密码箱「更多 → 对外接口」里打开。
/// - 每次取密码都要你确认（允许一次 / 总是允许这个程序取这条 / 拒绝），密码箱锁着就先让你解锁。
/// - "总是允许"绑定程序文件的指纹：程序被替换或升级后要重新确认。
/// - 一次只处理一个请求（防止弹窗轰炸），每次取用都记录。
/// 协议见 发布目录里的《密码箱接口说明.md》。
/// </summary>
public sealed class VaultApiServer : IDisposable
{
    public const int Version = 1;
    static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    public static string PipeNameFor(bool sandbox) => "DeskHub.Vault." + Sid + (sandbox ? ".sandbox" : "");

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly string _pipeName;
    readonly Func<ApiRequest, ApiClient, Task<ApiResponse>> _handle;
    readonly Action<string> _log;
    readonly CancellationTokenSource _cts = new();
    Task? _loop;

    /// <param name="handle">在界面线程上处理（要弹确认框）。</param>
    public VaultApiServer(string pipeName, Func<ApiRequest, ApiClient, Task<ApiResponse>> handle, Action<string> log)
    {
        _pipeName = pipeName; _handle = handle; _log = log;
    }

    public void Start() => _loop ??= Task.Run(Loop);

    async Task Loop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                // 只开一个实例：一次只处理一个请求
                using var pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, sec);
                await pipe.WaitForConnectionAsync(ct);
                await Serve(pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (Exception ex) { _log("密码箱接口出错：" + ex.Message); try { await Task.Delay(500, ct); } catch { break; } }
        }
    }

    async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        string? line;
        try { line = await reader.ReadLineAsync(readTimeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
        ApiResponse res;
        if (line == null) return;
        if (line.Length > 4096) res = ApiResponse.Fail("bad_request");
        else
        {
            ApiRequest? req = null;
            try { req = JsonSerializer.Deserialize<ApiRequest>(line, Json); } catch { }
            var client = Identify(pipe);
            if (req == null || string.IsNullOrEmpty(req.Op)) res = ApiResponse.Fail("bad_request");
            else if (client == null) res = ApiResponse.Fail("unknown_client");
            else
            {
                try { res = await _handle(req, client).WaitAsync(TimeSpan.FromSeconds(150), ct); }
                catch (TimeoutException) { res = ApiResponse.Fail("timeout"); }
            }
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(res, Json));
        try { pipe.WaitForPipeDrain(); } catch { }
    }

    // ---------------- 认出是哪个程序 ----------------
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    static ApiClient? Identify(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return null;
            using var p = Process.GetProcessById((int)pid);
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            if (!QueryFullProcessImageName(p.Handle, 0, sb, ref n)) return null;
            var exe = sb.ToString();
            return new ApiClient((int)pid, exe, FileHash(exe));
        }
        catch { return null; }
    }

    static readonly Dictionary<string, (DateTime, long, string)> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public static string FileHash(string exe)
    {
        var fi = new FileInfo(exe);
        lock (_hashes)
        {
            if (_hashes.TryGetValue(exe, out var h) && h.Item1 == fi.LastWriteTimeUtc && h.Item2 == fi.Length) return h.Item3;
            using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            _hashes[exe] = (fi.LastWriteTimeUtc, fi.Length, hash);
            return hash;
        }
    }

    // ---------------- 匹配记录 ----------------
    /// <summary>网址的主域名（seller.tiktok.com → tiktok.com；www.bca.co.id → bca.co.id）。</summary>
    public static string? Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (!u.Contains("://")) u = "https://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        return uri.Host.ToLowerInvariant();
    }

    static string MainDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2 || System.Net.IPAddress.TryParse(host, out _)) return host;
        // co.id / com.cn / co.uk 这类两段后缀
        bool two = parts[^2].Length <= 3 && parts[^1].Length == 2;
        return string.Join('.', parts.TakeLast(two ? 3 : 2));
    }

    /// <summary>找出符合请求的记录：网址同一个主域名，或者服务名包含。</summary>
    public static List<VaultEntry> Match(IEnumerable<VaultEntry> entries, ApiRequest req)
    {
        var host = Host(req.Url);
        var name = req.Name?.Trim();
        var list = new List<VaultEntry>();
        foreach (var e in entries)
        {
            bool hit = false;
            if (host != null && Host(e.Url) is { } eh)
                hit = eh == host || MainDomain(eh) == MainDomain(host);
            if (!hit && !string.IsNullOrEmpty(name))
                hit = e.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
            if (hit) list.Add(e);
        }
        // 网址完全一样的排前面，其次最近用过的
        return list.OrderByDescending(e => host != null && Host(e.Url) == host).ThenByDescending(e => e.LastUsed ?? DateTime.MinValue).ToList();
    }

    public static ApiCredential ToCredential(VaultEntry e) => new()
    {
        Id = e.Id, Name = e.Name, Url = e.Url, Account = e.Account, Password = e.Password,
        Fields = e.Fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Value),
    };

    public void Dispose()
    {
        _cts.Cancel();
        // 唤醒还在等连接的管道
        try { using var c = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut); c.Connect(100); } catch { }
    }
}
