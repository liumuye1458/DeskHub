using System.Windows;
using DeskHub.Core;
using DeskHub.UI;

namespace DeskHub;

// 密码箱对外接口（编译开关 FeatVaultApi）：本机其他程序经你确认后取账号密码
public sealed partial class ToolHub
{
    VaultApiServer? _api;
    public static string ApiDocPath => System.IO.Path.Combine(AppContext.BaseDirectory, "密码箱接口说明.md");

    partial void StartVaultApi() => ApplyVaultApi();
    partial void StopVaultApi() => _api?.Dispose();

    partial void SetupVault(VaultWindow w)
    {
        w.ApiOn = () => _c.Cfg.VaultApi;
        w.SetApiOn = on => { _c.Cfg.VaultApi = on; _c.Cfg.Save(); ApplyVaultApi(); };
        w.ApiDoc = ApiDocPath;
    }

    /// <summary>按设置开 / 关接口（默认关）。</summary>
    public void ApplyVaultApi()
    {
        if (_c.Cfg.VaultApi && _api == null)
        {
            _api = new VaultApiServer(VaultApiServer.PipeNameFor(_sandbox),
                (req, cl) => Application.Current.Dispatcher.InvokeAsync(() => HandleApi(req, cl)).Task.Unwrap(),
                msg => { try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppConfig.DataDir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { } });
            _api.Start();
        }
        else if (!_c.Cfg.VaultApi && _api != null) { _api.Dispose(); _api = null; }
    }

    async Task<ApiResponse> HandleApi(ApiRequest req, ApiClient cl)
    {
        if (!_c.Cfg.VaultApi) return ApiResponse.Fail("disabled");
        switch (req.Op.Trim().ToLowerInvariant())
        {
            case "status":
                return new ApiResponse { Ok = true, App = Brand.Id, Api = VaultApiServer.Version, Locked = _vault?.Current == null };
            case "get": break;
            default: return ApiResponse.Fail("bad_request");
        }
        if (VaultApiServer.Host(req.Url) == null && string.IsNullOrWhiteSpace(req.Name)) return ApiResponse.Fail("bad_request");
        var target = req.Url is { Length: > 0 } ? VaultApiServer.Host(req.Url)! : req.Name!.Trim();

        var v = await UnlockForApi(cl, target);
        if (v == null) return ApiResponse.Fail("locked");

        var cands = VaultApiServer.Match(v.Data.Entries, req);
        void Log(string entry, string result)
        {
            v.Data.ApiLog.Add(new ApiLogItem { Exe = cl.Exe, Target = target, Entry = entry, Result = result });
            if (v.Data.ApiLog.Count > 200) v.Data.ApiLog.RemoveRange(0, v.Data.ApiLog.Count - 200);
        }
        ApiResponse Give(VaultEntry e, string result)
        {
            e.LastUsed = DateTime.Now;
            Log(e.Name + "（" + e.Account + "）", result);
            v.Save();
            _vault?.Refresh();
            return new ApiResponse { Ok = true, Credential = VaultApiServer.ToCredential(e) };
        }

        // 点过"总是允许"的：同一个程序（文件指纹也一样）取同一条，直接给
        foreach (var e in cands)
        {
            var g = v.Data.Grants.FirstOrDefault(g => g.EntryId == e.Id && g.Hash == cl.Hash && string.Equals(g.Exe, cl.Exe, StringComparison.OrdinalIgnoreCase));
            if (g != null) { g.LastUsed = DateTime.Now; return Give(e, L.T("已授权")); }
        }

        var (ans, chosen) = await VaultConsent.Ask(cl, req, cands);
        if (_vault?.Current != v) return ApiResponse.Fail("locked"); // 确认期间被锁上了
        switch (ans)
        {
            case VaultConsent.Answer.Once when chosen != null:
                return Give(chosen, L.T("允许一次"));
            case VaultConsent.Answer.Always when chosen != null:
                v.Data.Grants.RemoveAll(g => g.EntryId == chosen.Id && string.Equals(g.Exe, cl.Exe, StringComparison.OrdinalIgnoreCase));
                v.Data.Grants.Add(new ApiGrant { Exe = cl.Exe, Hash = cl.Hash, EntryId = chosen.Id, LastUsed = DateTime.Now });
                return Give(chosen, L.T("总是允许"));
            case VaultConsent.Answer.NotFound:
                Log("", "没找到"); v.Save();
                return ApiResponse.Fail("not_found");
            case VaultConsent.Answer.Timeout:
                Log("", "超时"); v.Save();
                return ApiResponse.Fail("timeout");
            default:
                Log("", "拒绝"); v.Save();
                return ApiResponse.Fail("denied");
        }
    }

    /// <summary>密码箱锁着：打开解锁界面并说明是谁在要，等你解锁（最多 2 分钟）。</summary>
    async Task<Vault?> UnlockForApi(ApiClient cl, string target)
    {
        if (_vault?.Current is { } v) return v;
        if (!Core.Vault.Exists || _sandbox) return null; // 沙盒测试不弹解锁界面
        Vault();
        var w = _vault!;
        w.SetLockNotice(L.F("「{0}」想取用 {1} 的登录信息。\n先输入主密码解锁，之后会再问你是否允许。", cl.Name, target));
        var tcs = new TaskCompletionSource<bool>();
        void OnUnlock() => tcs.TrySetResult(true);
        void OnClose(object? s, EventArgs e) => tcs.TrySetResult(false);
        w.Unlocked += OnUnlock;
        w.Closed += OnClose;
        try { await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2))); }
        finally { w.Unlocked -= OnUnlock; w.Closed -= OnClose; }
        return w.Current;
    }
}
