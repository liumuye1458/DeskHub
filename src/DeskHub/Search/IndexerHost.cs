using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DeskHub.Search;

/// <summary>搜索请求（主程序 → 索引程序）。</summary>
public sealed class QueryRequest
{
    public string Q { get; set; } = "";
    public string? Kind { get; set; }
    public int Max { get; set; } = 200;
}

/// <summary>
/// 索引程序：主程序启动它（同一个 exe 带 --indexer 参数，普通权限），主程序退出它也跟着退出。
/// 单独一个进程是为了把索引占的内存和主程序分开，主程序界面始终轻快。
/// 通信用命名管道，只允许当前用户连接；只回答"文件名包含这些字的文件在哪"，不执行命令、不读文件内容、不联网。
/// </summary>
public static class IndexerHost
{
    static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    public static string PipeName => "DeskHub.Search." + Sid;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // ================= 索引程序本体（--indexer 主程序进程号） =================
    public static int Run(int parentPid)
    {
        using var single = new Mutex(true, @"Local\DeskHub.Indexer", out bool first);
        if (!first) return 0;
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        var dataDir = Core.AppConfig.DataDir;
        void Log(string s)
        {
            try { Directory.CreateDirectory(dataDir); File.AppendAllText(Path.Combine(dataDir, "indexer.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}\n"); } catch { }
        }

        var index = new FileIndex(Path.Combine(dataDir, "index"));
        for (int i = 0; i < 3; i++) new Thread(() => Serve(index, Log)) { IsBackground = true, Name = "Pipe" }.Start();
        index.Start(Log);

        // 主程序退出（包括被强制结束）后，存一下索引再退出
        try { using var parent = Process.GetProcessById(parentPid); parent.WaitForExit(); }
        catch { /* 主程序已经不在了 */ }
        index.SaveAll();
        index.Dispose();
        return 0;
    }

    static void Serve(FileIndex index, Action<string> log)
    {
        while (true)
        {
            try
            {
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.None, 1 << 16, 1 << 16, sec);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 4096) break; // 请求不会这么长
                    var req = JsonSerializer.Deserialize<QueryRequest>(line, Json) ?? new QueryRequest();
                    var res = index.Query(req.Q ?? "", req.Kind, Math.Clamp(req.Max, 1, 1000));
                    writer.WriteLine(JsonSerializer.Serialize(res));
                    writer.Flush();
                }
            }
            catch (IOException) { }
            catch (Exception ex) { log("管道出错：" + ex.Message); Thread.Sleep(500); }
        }
    }

    // ================= 主程序这边 =================
    static Process? _child;

    /// <summary>启动索引程序（已在运行就不重复启动）。</summary>
    public static void EnsureStarted()
    {
        if (_child is { HasExited: false }) return;
        try
        {
            _child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--indexer {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { _child = null; }
    }

    public static async Task<QueryResult?> QueryAsync(QueryRequest req, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500, ct).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(req)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            return line == null ? null : JsonSerializer.Deserialize<QueryResult>(line, Json);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
