namespace DeskHub;

// 本机 Claude 小服务（编译开关 FeatLocalClaude）：只听 127.0.0.1，转给本机 claude -p
public sealed partial class ToolHub
{
    Core.LocalClaude? _claude;
    public Core.LocalClaude? LocalClaude => _claude;
    /// <summary>没开起来的原因（端口被占、没填口令），设置页显示。</summary>
    public string LocalClaudeError { get; private set; } = "";

    partial void StartLocalClaude() => ApplyLocalClaude();
    partial void StopLocalClaude() => _claude?.Dispose();

    /// <summary>按设置开 / 关 / 换端口口令重开（默认关）。</summary>
    public void ApplyLocalClaude()
    {
        _claude?.Dispose();
        _claude = null;
        LocalClaudeError = "";
        if (!_c.Cfg.LocalClaude) return;
        var s = new Core.LocalClaude(_c.Cfg.LocalClaudePort, Core.Secret.Unprotect(_c.Cfg.LocalClaudeToken));
        if (s.Start()) _claude = s;
        else LocalClaudeError = s.Error;
    }
}
