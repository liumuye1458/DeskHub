using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskHub.Search;

/// <summary>一条搜索结果。</summary>
public sealed class Hit
{
    public string Path { get; set; } = "";
    public bool Dir { get; set; }
    public int Score { get; set; }
}

/// <summary>一次搜索的结果：排好序的前 N 条，加上每个分组一共命中多少。</summary>
public sealed class QueryResult
{
    public List<Hit> Items { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
    public long Indexed { get; set; }
    public bool Ready { get; set; }
    /// <summary>正在重新扫描磁盘（结果可能缺最近的变化）。</summary>
    public bool Scanning { get; set; }
}

/// <summary>一次查询的关键词，预先转好小写和单字节形式。</summary>
sealed class Terms
{
    public readonly string[] Text;
    public readonly byte[]?[] Ascii;   // 纯英文关键词的小写单字节形式；含中文的为 null
    public readonly bool AllAscii;

    public Terms(string[] text)
    {
        Text = text;
        Ascii = text.Select(t => t.All(c => c < 0x80) ? t.ToLowerInvariant().Select(c => (byte)c).ToArray() : null).ToArray();
        AllAscii = Ascii.All(a => a != null);
    }
}

/// <summary>
/// 一个盘的全部条目。数据放在手动申请的内存里（不归 .NET 垃圾回收管），
/// 换新索引时旧的整块立刻还给系统，不会像普通数组那样回收后还占着内存。
/// 名字：纯英文的每个字符存 1 字节，含中文等的存 2 字节。
/// </summary>
unsafe sealed class Store : IDisposable
{
    public int* Off; public ushort* Len; public int* Parent; public byte* Flag;
    public byte* Pool;
    public int N, Cap;
    public long PoolUsed, PoolCap;
    public long Count;
    // 目录的"父编号 + 名字哈希 → 编号"，用来把路径换成编号（文件变化通知只给路径）
    public Dictionary<long, int> Dirs = new();

    public const byte FDir = 1, FGone = 2, FWide = 4;

    public Store(int cap = 1 << 16, long poolCap = 1 << 20) { Grow(cap); GrowPool(poolCap); }

    void Grow(int cap)
    {
        Off = (int*)NativeMemory.Realloc(Off, (nuint)cap * 4);
        Len = (ushort*)NativeMemory.Realloc(Len, (nuint)cap * 2);
        Parent = (int*)NativeMemory.Realloc(Parent, (nuint)cap * 4);
        Flag = (byte*)NativeMemory.Realloc(Flag, (nuint)cap);
        Cap = cap;
    }

    void GrowPool(long cap)
    {
        Pool = (byte*)NativeMemory.Realloc(Pool, (nuint)cap);
        PoolCap = cap;
    }

    /// <summary>扫描结束后把多申请的部分还回去，只留一点余量给之后新建的文件。</summary>
    public void Trim()
    {
        Grow(N + 4096);
        GrowPool(PoolUsed + (1 << 20));
    }

    public long Bytes => (long)Cap * 11 + PoolCap;

    public int Add(int parent, ReadOnlySpan<char> name, bool dir)
    {
        if (N == Cap) Grow(Cap + (Cap >> 1));
        int id = N++;
        Parent[id] = parent;
        Flag[id] = dir ? FDir : (byte)0;
        SetName(id, name);
        if (id > 0) Count++;
        if (dir) Dirs[Key(parent, name)] = id;
        return id;
    }

    public void SetName(int id, ReadOnlySpan<char> name)
    {
        bool wide = false;
        foreach (var c in name) if (c >= 0x80) { wide = true; break; }
        long need = wide ? name.Length * 2L : name.Length;
        if (PoolUsed + need > PoolCap) GrowPool(Math.Max(PoolCap + (PoolCap >> 1), PoolUsed + need + (1 << 20)));
        var dst = Pool + PoolUsed;
        if (wide) name.CopyTo(new Span<char>(dst, name.Length));
        else for (int i = 0; i < name.Length; i++) dst[i] = (byte)name[i];
        Off[id] = (int)PoolUsed;   // 字符池不会超过 2GB（两百万个文件也就几十 MB）
        Len[id] = (ushort)name.Length;
        Flag[id] = (byte)(wide ? Flag[id] | FWide : Flag[id] & ~FWide);
        PoolUsed += need;
    }

    public bool Alive(int id) => (Flag[id] & FGone) == 0;
    public bool IsDir(int id) => (Flag[id] & FDir) != 0;

    /// <summary>取名字（单字节的名字先展开到 buf 里）。</summary>
    public ReadOnlySpan<char> Name(int id, Span<char> buf)
    {
        var p = Pool + Off[id];
        int len = Len[id];
        if ((Flag[id] & FWide) != 0) return new ReadOnlySpan<char>(p, len);
        var b = buf.Length >= len ? buf : new char[len];
        for (int i = 0; i < len; i++) b[i] = (char)p[i];
        return b[..len];
    }

    public bool NameIs(int id, ReadOnlySpan<char> s)
    {
        Span<char> buf = stackalloc char[300];
        return Len[id] == s.Length && Name(id, buf).Equals(s, StringComparison.OrdinalIgnoreCase);
    }

    public static long Key(int parent, ReadOnlySpan<char> name) =>
        ((long)parent << 32) | (uint)string.GetHashCode(name, StringComparison.OrdinalIgnoreCase); // 文件名不区分大小写

    // ---------------- 匹配 ----------------
    static readonly byte[] Lower = Enumerable.Range(0, 256).Select(i => (byte)(i is >= 'A' and <= 'Z' ? i + 32 : i)).ToArray();

    public bool Matches(int id, Terms t)
    {
        var p = Pool + Off[id];
        int len = Len[id];
        if ((Flag[id] & FWide) != 0)
        {
            var name = new ReadOnlySpan<char>(p, len);
            foreach (var s in t.Text) if (!name.Contains(s, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        if (!t.AllAscii) return false; // 纯英文的名字不可能包含中文关键词
        var hay = new ReadOnlySpan<byte>(p, len);
        foreach (var needle in t.Ascii) if (!ContainsAscii(hay, needle!)) return false;
        return true;
    }

    /// <summary>不区分大小写的单字节查找：先用向量化的 IndexOfAny 找首字母，再逐字比较。</summary>
    static bool ContainsAscii(ReadOnlySpan<byte> hay, byte[] needle)
    {
        if (needle.Length == 0) return true;
        byte f = needle[0], fu = f is >= (byte)'a' and <= (byte)'z' ? (byte)(f - 32) : f;
        int start = 0;
        while (hay.Length - start >= needle.Length)
        {
            int i = hay[start..^(needle.Length - 1)].IndexOfAny(f, fu);
            if (i < 0) return false;
            i += start;
            int k = 1;
            while (k < needle.Length && Lower[hay[i + k]] == needle[k]) k++;
            if (k == needle.Length) return true;
            start = i + 1;
        }
        return false;
    }

    // ---------------- 存盘 / 读盘 ----------------
    public void Write(BinaryWriter w)
    {
        w.Write(N); w.Write(PoolUsed); w.Write(Count);
        w.Write(new ReadOnlySpan<byte>(Off, N * 4)); w.Write(new ReadOnlySpan<byte>(Len, N * 2));
        w.Write(new ReadOnlySpan<byte>(Parent, N * 4)); w.Write(new ReadOnlySpan<byte>(Flag, N));
        for (long o = 0; o < PoolUsed; o += 1 << 30) w.Write(new ReadOnlySpan<byte>(Pool + o, (int)Math.Min(1 << 30, PoolUsed - o)));
    }

    public static Store Read(BinaryReader r)
    {
        int n = r.ReadInt32(); long used = r.ReadInt64(); long count = r.ReadInt64();
        var s = new Store(n + 4096, used + (1 << 20)) { N = n, PoolUsed = used, Count = count };
        try
        {
            Fill(r, s.Off, n * 4L); Fill(r, s.Len, n * 2L); Fill(r, s.Parent, n * 4L); Fill(r, s.Flag, n); Fill(r, s.Pool, used);
            Span<char> buf = stackalloc char[300];
            for (int i = 1; i < n; i++)
                if ((s.Flag[i] & (FDir | FGone)) == FDir) s.Dirs[Key(s.Parent[i], s.Name(i, buf))] = i;
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    static void Fill(BinaryReader r, void* dst, long bytes)
    {
        var p = (byte*)dst;
        while (bytes > 0)
        {
            int k = r.Read(new Span<byte>(p, (int)Math.Min(bytes, 1 << 30)));
            if (k <= 0) throw new EndOfStreamException();
            p += k; bytes -= k;
        }
    }

    public void Dispose()
    {
        NativeMemory.Free(Off); NativeMemory.Free(Len); NativeMemory.Free(Parent); NativeMemory.Free(Flag); NativeMemory.Free(Pool);
        Off = null; Len = null; Parent = null; Flag = null; Pool = null;
        N = Cap = 0;
    }
}

/// <summary>
/// 一个盘的文件名索引。不需要管理员权限：逐个目录扫描，之后靠文件变化通知增量更新；索引存盘，下次启动直接读。
/// </summary>
sealed class VolumeIndex : IDisposable
{
    public string Root { get; }   // 例如 "C:\"
    readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    Store _s = new();

    FileSystemWatcher? _fsw;
    readonly ConcurrentQueue<(WatcherChangeTypes Type, string Path, string? Old)> _changes = new();
    Timer? _apply;
    public bool NeedRescan { get; private set; }
    volatile bool _hold; // 扫描期间先攒着变化，扫完换上新索引后再应用

    public VolumeIndex(string root) { Root = root; }

    public long Count => _s.Count;
    public long Bytes => _s.Bytes + _s.Dirs.Count * 24L;

    // 不进去扫的目录：回收站、系统卷信息、Windows 系统目录、开发工具的依赖和缓存（几十万个文件，没人会搜，只会把结果搅乱）
    static readonly string[] SkipDirs = { "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi", "node_modules", ".git", "__pycache__", ".pnpm-store" };
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');

    static bool Skip(ReadOnlySpan<char> parentDir, ReadOnlySpan<char> name)
    {
        foreach (var s in SkipDirs) if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
        // C:\Windows 本身留着，里面不扫
        return name.Equals("Windows", StringComparison.OrdinalIgnoreCase) && parentDir.Length <= 3
            && WinDir.AsSpan().Equals(Path.Join(parentDir, name), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- 全盘扫描 ----------------
    /// <summary>从头扫一遍，扫完一次性换上新索引（扫描期间旧索引照常可用）。</summary>
    public void Scan(CancellationToken ct)
    {
        _hold = true;
        var fresh = new Store(1 << 20, 16 << 20);
        try
        {
            fresh.Add(-1, "", true); // 0 号 = 盘根目录
            var stack = new Stack<(int Id, string Path)>();
            stack.Push((0, Root));
            var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 64 << 10 };
            var subdirs = new List<(int, string)>();
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (pid, dir) = stack.Pop();
                subdirs.Clear();
                try
                {
                    var e = new FileSystemEnumerable<int>(dir, (ref FileSystemEntry en) =>
                    {
                        bool isDir = en.IsDirectory;
                        int id = fresh.Add(pid, en.FileName, isDir);
                        // 不跟进链接目录（避免绕圈），也不进跳过名单里的目录
                        if (isDir && (en.Attributes & FileAttributes.ReparsePoint) == 0 && !Skip(en.Directory, en.FileName))
                            subdirs.Add((id, Path.Join(en.Directory, en.FileName)));
                        return id;
                    }, opts);
                    foreach (var _ in e) { }
                }
                catch { /* 没权限的目录跳过 */ }
                for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
            }
            fresh.Trim();
        }
        catch { _hold = false; fresh.Dispose(); throw; }

        Store old;
        _lock.EnterWriteLock();
        try
        {
            old = _s;
            _s = fresh;
            NeedRescan = false;
            _hold = false;
        }
        finally { _lock.ExitWriteLock(); }
        old.Dispose();     // 旧索引的内存立刻还给系统
        FlushChanges();    // 扫描期间攒下的变化补上
    }

    // ---------------- 增量更新 ----------------
    public void Watch()
    {
        _fsw = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 << 10,
        };
        _fsw.Created += (_, e) => Queue(WatcherChangeTypes.Created, e.FullPath, null);
        _fsw.Deleted += (_, e) => Queue(WatcherChangeTypes.Deleted, e.FullPath, null);
        _fsw.Renamed += (_, e) => Queue(WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath);
        _fsw.Error += (_, _) => NeedRescan = true; // 变化太多、通知丢了：之后整盘重扫
        _apply = new Timer(_ => FlushChanges());
        _fsw.EnableRaisingEvents = true;
    }

    void Queue(WatcherChangeTypes t, string path, string? old)
    {
        if (InSkipped(path)) return;
        _changes.Enqueue((t, path, old));
        _apply?.Change(500, Timeout.Infinite); // 攒半秒一起处理
    }

    static bool InSkipped(string path)
    {
        if (path.StartsWith(WinDir + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var s in SkipDirs) if (path.Contains("\\" + s + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    void FlushChanges()
    {
        if (_changes.IsEmpty || _hold) return;
        _lock.EnterWriteLock();
        try
        {
            while (_changes.TryDequeue(out var c))
            {
                try
                {
                    switch (c.Type)
                    {
                        case WatcherChangeTypes.Created: Created(c.Path); break;
                        case WatcherChangeTypes.Deleted: Remove(Find(c.Path)); break;
                        case WatcherChangeTypes.Renamed:
                            int id = Find(c.Old!);
                            if (id <= 0) { Created(c.Path); break; }
                            int np = FindDir(Path.GetDirectoryName(c.Path));
                            if (np < 0) { Remove(id); break; }
                            Rename(id, np, Path.GetFileName(c.Path));
                            break;
                    }
                }
                catch { }
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    void Created(string path)
    {
        if (Find(path) > 0) return;
        int pid = FindDir(Path.GetDirectoryName(path));
        if (pid < 0) return;
        bool dir = Directory.Exists(path);
        _s.Add(pid, Path.GetFileName(path), dir);
        if (!dir) return;
        // 新出现的文件夹（例如从别处移进来的）：把里面的东西也收进来
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var sub in Directory.EnumerateFileSystemEntries(path, "*", opts)) if (!InSkipped(sub + "\\")) Created(sub);
        }
        catch { }
    }

    unsafe void Remove(int id)
    {
        var s = _s;
        if (id <= 0 || !s.Alive(id)) return;
        if (s.IsDir(id)) { Span<char> buf = stackalloc char[300]; s.Dirs.Remove(Store.Key(s.Parent[id], s.Name(id, buf))); }
        s.Flag[id] |= Store.FGone;
        s.Count--;
        // 文件夹里的东西不逐个删：父目录没了，拼路径时自然找不到，搜索结果里不会出现
    }

    unsafe void Rename(int id, int newParent, string newName)
    {
        var s = _s;
        Span<char> buf = stackalloc char[300];
        if (s.IsDir(id)) s.Dirs.Remove(Store.Key(s.Parent[id], s.Name(id, buf)));
        s.SetName(id, newName);
        s.Parent[id] = newParent;
        if (s.IsDir(id)) s.Dirs[Store.Key(newParent, newName)] = id;
    }

    /// <summary>目录路径 → 编号；不在索引里返回 -1。</summary>
    int FindDir(string? dir)
    {
        if (dir == null) return -1;
        if (dir.Length <= Root.Length) return string.Equals(dir.TrimEnd('\\') + "\\", Root, StringComparison.OrdinalIgnoreCase) ? 0 : -1;
        if (!dir.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return -1;
        int cur = 0;
        foreach (var part in dir[Root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_s.Dirs.TryGetValue(Store.Key(cur, part), out var next) || !_s.Alive(next) || !_s.NameIs(next, part))
            {
                next = ScanChild(cur, part, wantDir: true);
                if (next < 0) return -1;
            }
            cur = next;
        }
        return cur;
    }

    /// <summary>路径 → 编号（文件或目录）；找不到返回 -1。</summary>
    int Find(string path)
    {
        int pid = FindDir(Path.GetDirectoryName(path));
        if (pid < 0) return -1;
        var name = Path.GetFileName(path);
        if (_s.Dirs.TryGetValue(Store.Key(pid, name), out var d) && _s.Alive(d) && _s.NameIs(d, name)) return d;
        return ScanChild(pid, name, wantDir: false);
    }

    /// <summary>在某个目录下按名字找（文件没有建反查表，就顺序找一遍，一百多万条约几毫秒）。</summary>
    unsafe int ScanChild(int parent, string name, bool wantDir)
    {
        var s = _s;
        for (int i = s.N - 1; i > 0; i--)
            if (s.Parent[i] == parent && s.Alive(i) && (!wantDir || s.IsDir(i)) && s.NameIs(i, name))
                return i;
        return -1;
    }

    // ---------------- 查询 ----------------
    /// <summary>找出名字里包含全部关键词的条目，按名字打分，每个分组计数，留下分数最高的 keep 条。</summary>
    public unsafe void Match(Terms terms, string? kind, int keep, List<(VolumeIndex Vol, int Id, int Score, bool Dir)> top, Dictionary<string, int> counts)
    {
        _lock.EnterReadLock();
        try
        {
            var s = _s;
            int n = s.N;
            int parts = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
            var partTop = new List<(VolumeIndex, int, int, bool)>[parts];
            var partCount = new Dictionary<string, int>[parts];
            Parallel.For(0, parts, p =>
            {
                var mine = new List<(VolumeIndex, int, int, bool)>();
                var cnt = new Dictionary<string, int>();
                int min = int.MinValue;
                Span<char> buf = stackalloc char[300];
                for (int i = Math.Max(1, (int)((long)n * p / parts)); i < (long)n * (p + 1) / parts; i++)
                {
                    if ((s.Flag[i] & Store.FGone) != 0 || !s.Matches(i, terms)) continue;
                    bool dir = s.IsDir(i);
                    var name = s.Name(i, buf);
                    var k = FileKinds.Of(name, dir);
                    cnt[k] = cnt.GetValueOrDefault(k) + 1;
                    if (kind != null && kind != k) continue;
                    int sc = NameScore(name, terms.Text, dir);
                    if (mine.Count >= keep && sc <= min) continue;
                    mine.Add((this, i, sc, dir));
                    if (mine.Count >= keep * 2)
                    {
                        mine.Sort((a, b) => b.Item3.CompareTo(a.Item3));
                        mine.RemoveRange(keep, mine.Count - keep);
                        min = mine[^1].Item3;
                    }
                }
                partTop[p] = mine; partCount[p] = cnt;
            });
            foreach (var m in partTop) top.AddRange(m);
            foreach (var c in partCount) foreach (var (k, v) in c) counts[k] = counts.GetValueOrDefault(k) + v;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>名字打分：完全一样、开头命中、整词命中的靠前，名字越短越靠前。</summary>
    static int NameScore(ReadOnlySpan<char> name, string[] terms, bool dir)
    {
        int s = 0;
        int dot = dir ? -1 : name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        if (terms.Length == 1 && stem.Equals(terms[0], StringComparison.OrdinalIgnoreCase)) s += 1000;
        if (name.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase)) s += 200;
        foreach (var t in terms)
        {
            int i = name.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && !char.IsLetterOrDigit(name[i - 1])) s += 60;
        }
        return s - Math.Min(name.Length, 80);
    }

    /// <summary>由编号拼出完整路径；中间的目录已删除则返回 null。</summary>
    public unsafe string? PathOf(int id)
    {
        _lock.EnterReadLock();
        try
        {
            var s = _s;
            Span<int> chain = stackalloc int[160];
            int depth = 0;
            for (int cur = id; cur != 0; cur = s.Parent[cur])
            {
                if (cur < 0 || cur >= s.N || !s.Alive(cur) || depth == chain.Length) return null;
                chain[depth++] = cur;
            }
            var sb = new StringBuilder(160).Append(Root);
            Span<char> buf = stackalloc char[300];
            for (int i = depth - 1; i >= 0; i--)
            {
                sb.Append(s.Name(chain[i], buf));
                if (i > 0) sb.Append('\\');
            }
            return sb.ToString();
        }
        finally { _lock.ExitReadLock(); }
    }

    // ---------------- 存盘 / 读盘 ----------------
    const int CacheVersion = 2;

    public void Save(string file)
    {
        _lock.EnterReadLock();
        try
        {
            var tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(CacheVersion); w.Write(Root);
                _s.Write(w);
            }
            File.Move(tmp, file, overwrite: true);
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool Load(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != CacheVersion || r.ReadString() != Root) return false;
            var s = Store.Read(r);
            Store old;
            _lock.EnterWriteLock();
            try { old = _s; _s = s; }
            finally { _lock.ExitWriteLock(); }
            old.Dispose();
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _apply?.Dispose();
    }
}

/// <summary>所有本地硬盘的索引合在一起。</summary>
public sealed class FileIndex : IDisposable
{
    readonly List<VolumeIndex> _vols = new();
    volatile bool _ready, _scanning;
    readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
    readonly string _usersRoot;
    readonly string _cacheDir;
    readonly CancellationTokenSource _cts = new();

    public FileIndex(string cacheDir)
    {
        _cacheDir = cacheDir;
        _usersRoot = Path.GetDirectoryName(_profile.TrimEnd('\\'))!.TrimEnd('\\') + "\\";
    }

    public bool Ready => _ready;
    public long Count { get { lock (_vols) return _vols.Sum(v => v.Count); } }

    string CacheFile(VolumeIndex v) => Path.Combine(_cacheDir, $"index_{v.Root[0]}.bin");

    /// <summary>
    /// 先读上次存下的索引（一两秒就能搜），再在后台低优先级重扫一遍磁盘，把关机期间的变化补上。
    /// 只索引本机硬盘；U 盘、网络盘不索引。
    /// </summary>
    public void Start(Action<string>? log = null)
    {
        var roots = DriveInfo.GetDrives().Where(d =>
        {
            try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; }
        }).Select(d => d.RootDirectory.FullName).ToList();

        foreach (var root in roots)
        {
            var v = new VolumeIndex(root);
            if (v.Load(CacheFile(v))) log?.Invoke($"{root} 读取上次的索引：{v.Count:N0} 个");
            lock (_vols) _vols.Add(v);
            try { v.Watch(); } catch (Exception ex) { log?.Invoke($"{root} 无法监视变化：{ex.Message}"); }
        }
        _ready = _vols.Any(v => v.Count > 0);

        var t = new Thread(() =>
        {
            Rescan(log);
            _ready = true;
            // 之后每隔一段时间看看有没有"通知丢失"的盘，有就重扫
            while (!_cts.IsCancellationRequested)
            {
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromMinutes(10));
                VolumeIndex[] vols;
                lock (_vols) vols = _vols.ToArray();
                foreach (var v in vols.Where(v => v.NeedRescan)) RescanOne(v, log);
            }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Rescan" };
        t.Start();
    }

    void Rescan(Action<string>? log)
    {
        VolumeIndex[] vols;
        lock (_vols) vols = _vols.ToArray();
        _scanning = true;
        try { Parallel.ForEach(vols, new ParallelOptions { MaxDegreeOfParallelism = 2 }, v => RescanOne(v, log)); }
        finally { _scanning = false; }
    }

    void RescanOne(VolumeIndex v, Action<string>? log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            v.Scan(_cts.Token);
            log?.Invoke($"{v.Root} 扫描完成：{v.Count:N0} 个，用时 {sw.Elapsed.TotalSeconds:F0} 秒，占内存 {v.Bytes >> 20} MB");
            Directory.CreateDirectory(_cacheDir);
            v.Save(CacheFile(v));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log?.Invoke($"{v.Root} 扫描失败：{ex.Message}"); }
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    public void SaveAll()
    {
        VolumeIndex[] vols;
        lock (_vols) vols = _vols.ToArray();
        foreach (var v in vols) { try { v.Save(CacheFile(v)); } catch { } }
    }

    public QueryResult Query(string query, string? kind, int max)
    {
        var res = new QueryResult { Ready = _ready, Indexed = Count, Scanning = _scanning };
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(8).ToArray();
        if (words.Length == 0) return res;
        if (kind == FileKinds.All) kind = null;
        var terms = new Terms(words);

        // 第一轮：按文件名粗排，留 2000 条；第二轮：拼出路径，再按位置调整分数
        var top = new List<(VolumeIndex Vol, int Id, int Score, bool Dir)>();
        VolumeIndex[] vols;
        lock (_vols) vols = _vols.ToArray();
        foreach (var v in vols) v.Match(terms, kind, 2000, top, res.Counts);
        res.Counts[FileKinds.All] = res.Counts.Values.Sum();
        top.Sort((a, b) => b.Score.CompareTo(a.Score));

        var hits = new List<Hit>();
        foreach (var t in top.Take(2000))
        {
            var path = t.Vol.PathOf(t.Id);
            if (path == null || Hidden(path)) continue;
            hits.Add(new Hit { Path = path, Dir = t.Dir, Score = t.Score + PathScore(path) });
        }
        res.Items = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Path.Length).Take(max).ToList();
        return res;
    }

    /// <summary>不显示其他用户的个人文件夹。</summary>
    bool Hidden(string path)
    {
        if (!path.StartsWith(_usersRoot, StringComparison.OrdinalIgnoreCase) || path.StartsWith(_profile, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path.AsSpan(_usersRoot.Length);
        int slash = rest.IndexOf('\\');
        return slash >= 0 && !rest[..slash].Equals("Public", StringComparison.OrdinalIgnoreCase);
    }

    static readonly string[] Noise =
    {
        "\\.", "\\Program Files", "\\ProgramData\\", "\\AppData\\", "\\$", "\\Microsoft\\", "\\cache", "\\Temp\\",
        "\\.nuget\\", "\\site-packages\\", "\\obj\\", "\\bin\\", "\\.vs\\", "\\.vscode\\", "\\dist\\", "\\build\\",
    };

    /// <summary>位置打分：个人文件夹里的靠前，系统和程序目录里的靠后。</summary>
    int PathScore(string path)
    {
        int s = 0;
        if (path.StartsWith(_profile, StringComparison.OrdinalIgnoreCase)) s += 150;
        foreach (var n in Noise) if (path.Contains(n, StringComparison.OrdinalIgnoreCase)) { s -= 500; break; }
        s -= path.Count(c => c == '\\') * 4; // 目录越深越靠后
        return s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_vols) foreach (var v in _vols) v.Dispose();
    }
}
