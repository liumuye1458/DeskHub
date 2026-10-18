using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskHub.Core;

/// <summary>自定义字段，例如支付密码、安全问题、客服电话。Secret = 显示成圆点、复制后自动清除。</summary>
public sealed class VaultField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Secret { get; set; }
}

/// <summary>一条密码记录：固定字段 + 自定义字段。</summary>
public sealed class VaultEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Url { get; set; } = "";
    public string Account { get; set; } = "";
    public string Password { get; set; } = "";
    public string Note { get; set; } = "";
    public List<VaultField> Fields { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public DateTime? LastUsed { get; set; }

    public VaultEntry Clone()
    {
        var c = (VaultEntry)MemberwiseClone();
        c.Fields = Fields.Select(f => new VaultField { Name = f.Name, Value = f.Value, Secret = f.Secret }).ToList();
        return c;
    }
}

public sealed class VaultData
{
    public List<VaultEntry> Entries { get; set; } = new();
    public List<string> Groups { get; set; } = new() { L.T("公司后台"), L.T("直播账号"), L.T("银行支付"), L.T("服务器"), L.T("邮箱"), L.T("其他") };
    /// <summary>对外接口：你点过"总是允许"的（哪个程序可以直接取哪条记录）。和密码一起加密保存。</summary>
    public List<ApiGrant> Grants { get; set; } = new();
    /// <summary>对外接口的取用记录（最近 200 条）。</summary>
    public List<ApiLogItem> ApiLog { get; set; } = new();
}

public sealed class ApiGrant
{
    public string Exe { get; set; } = "";
    /// <summary>程序文件的指纹（SHA-256）：程序被换掉或升级后要重新确认。</summary>
    public string Hash { get; set; } = "";
    public string EntryId { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastUsed { get; set; }
}

public sealed class ApiLogItem
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Exe { get; set; } = "";
    public string Target { get; set; } = "";
    public string Entry { get; set; } = "";
    /// <summary>允许一次 / 总是允许 / 已授权 / 拒绝 / 超时 / 没找到 / 未解锁</summary>
    public string Result { get; set; } = "";
}

/// <summary>
/// 密码箱文件：整份数据用 AES-256-GCM 加密，密钥由主密码经 PBKDF2-SHA256（60 万次）推导。
/// 主密码不保存在任何地方；忘了就打不开，所以建议导出一份加密备份（备份同样要主密码才能打开）。
/// 不联网。
/// </summary>
public sealed class Vault : IDisposable
{
    const int Iterations = 600_000;

    sealed class FileModel
    {
        public int V { get; set; } = 1;
        public string Salt { get; set; } = "";
        public int Iter { get; set; }
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    public static string FilePath => Path.Combine(AppConfig.DataDir, "vault.dat");
    public static bool Exists => File.Exists(FilePath);

    byte[] _key;
    byte[] _salt;
    readonly string _path;
    public VaultData Data { get; private set; }

    Vault(string path, byte[] key, byte[] salt, VaultData data) { _path = path; _key = key; _salt = salt; Data = data; }

    static byte[] Derive(string password, byte[] salt, int iter) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, 32);

    /// <summary>第一次使用：用主密码新建一个空的密码箱。</summary>
    public static Vault Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var v = new Vault(FilePath, Derive(password, salt, Iterations), salt, new VaultData());
        v.Save();
        return v;
    }

    /// <summary>解锁。主密码不对返回 null。</summary>
    public static Vault? Open(string password, string? path = null)
    {
        path ??= FilePath;
        var f = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path)) ?? throw new InvalidDataException(L.T("密码箱文件损坏"));
        var salt = Convert.FromBase64String(f.Salt);
        var key = Derive(password, salt, f.Iter);
        var plain = Decrypt(key, f);
        if (plain == null) { CryptographicOperations.ZeroMemory(key); return null; }
        var data = JsonSerializer.Deserialize<VaultData>(plain) ?? new VaultData();
        CryptographicOperations.ZeroMemory(plain);
        return new Vault(path, key, salt, data);
    }

    static byte[]? Decrypt(byte[] key, FileModel f)
    {
        var ct = Convert.FromBase64String(f.Data);
        var plain = new byte[ct.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(Convert.FromBase64String(f.Nonce), ct, Convert.FromBase64String(f.Tag), plain);
            return plain;
        }
        catch (CryptographicException) { return null; } // 密码不对（或文件被改过）
    }

    public void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(Data);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(_key, 16)) gcm.Encrypt(nonce, plain, ct, tag);
        CryptographicOperations.ZeroMemory(plain);
        var f = new FileModel
        {
            Salt = Convert.ToBase64String(_salt), Iter = Iterations,
            Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(ct),
        };
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(f));
    }

    public void ChangePassword(string newPassword)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Derive(newPassword, salt, Iterations);
        CryptographicOperations.ZeroMemory(_key);
        _key = key; _salt = salt;
        Save();
    }

    /// <summary>导出加密备份：就是把加密文件复制一份，打开时同样要主密码。</summary>
    public void Backup(string dest) { Save(); File.Copy(_path, dest, true); }

    /// <summary>从备份恢复：备份要能用当前输入的主密码打开，成功后替换现在的数据。</summary>
    public static Vault? Restore(string backup, string password)
    {
        var b = Open(password, backup);
        if (b == null) return null;
        var v = new Vault(FilePath, b._key, b._salt, b.Data);
        v.Save();
        return v;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        Data = new VaultData();
    }

    // ---------------- 密码强度 ----------------
    public enum Strength { None, Weak, Medium, Strong }

    public static Strength Rate(string pw)
    {
        if (string.IsNullOrEmpty(pw)) return Strength.None;
        int classes = (pw.Any(char.IsLower) ? 1 : 0) + (pw.Any(char.IsUpper) ? 1 : 0) + (pw.Any(char.IsDigit) ? 1 : 0) + (pw.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (pw.Length < 8 || classes <= 1 || pw.Distinct().Count() <= 3) return Strength.Weak;
        if (pw.Length >= 12 && classes >= 3) return Strength.Strong;
        return Strength.Medium;
    }

    /// <summary>弱密码：太简单，或者和别的记录用的是同一个密码。</summary>
    public bool IsWeak(VaultEntry e) =>
        e.Password.Length > 0 && (Rate(e.Password) == Strength.Weak || Data.Entries.Any(o => o != e && o.Password == e.Password));

    // ---------------- 密码生成器 ----------------
    public static string Generate(int length, bool digits, bool symbols, bool upper = true)
    {
        // 去掉容易看错的字符：0 O o 1 l I
        const string lower = "abcdefghijkmnpqrstuvwxyz", up = "ABCDEFGHJKLMNPQRSTUVWXYZ", dig = "23456789", sym = "!@#$%^&*-_=+?";
        var sets = new List<string> { lower };
        if (upper) sets.Add(up);
        if (digits) sets.Add(dig);
        if (symbols) sets.Add(sym);
        var all = string.Concat(sets);
        var chars = new char[Math.Clamp(length, 4, 64)];
        // 每类至少一个，其余随机，再打乱
        for (int i = 0; i < chars.Length; i++)
            chars[i] = i < sets.Count ? sets[i][RandomNumberGenerator.GetInt32(sets[i].Length)] : all[RandomNumberGenerator.GetInt32(all.Length)];
        for (int i = chars.Length - 1; i > 0; i--) { int j = RandomNumberGenerator.GetInt32(i + 1); (chars[i], chars[j]) = (chars[j], chars[i]); }
        return new string(chars);
    }

    // ---------------- 导入浏览器导出的 CSV（Chrome / Edge / Firefox） ----------------
    public static List<VaultEntry> ParseCsv(string text, string group)
    {
        var rows = Csv(text);
        if (rows.Count < 2) return new();
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) { foreach (var n in names) { var i = head.IndexOf(n); if (i >= 0) return i; } return -1; }
        int cName = Col("name", "title"), cUrl = Col("url", "login_uri", "website"), cUser = Col("username", "login_username", "user", "account"),
            cPw = Col("password", "login_password"), cNote = Col("note", "notes", "extra", "comment");
        if (cPw < 0) throw new InvalidDataException(L.T("没找到“password”这一列，不像是浏览器导出的密码文件"));
        string Get(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
        var list = new List<VaultEntry>();
        foreach (var r in rows.Skip(1))
        {
            var url = Get(r, cUrl);
            var name = Get(r, cName);
            if (name.Length == 0) name = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
            if (name.Length == 0 && Get(r, cPw).Length == 0) continue;
            list.Add(new VaultEntry { Name = name, Url = url, Account = Get(r, cUser), Password = Get(r, cPw), Note = Get(r, cNote), Group = group });
        }
        return list;
    }

    static List<List<string>> Csv(string s)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder();
        bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                else cell.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        return rows;
    }
}

/// <summary>复制密码：不进 Windows 剪贴板历史、不同步到云，30 秒后自动清除（剪贴板没被换掉的话）。</summary>
public static class SecretClipboard
{
    static System.Windows.Threading.DispatcherTimer? _timer;
    static string? _last;

    public static void Copy(string text, bool secret, int clearSeconds = 30)
    {
        var d = new System.Windows.DataObject();
        d.SetText(text);
        if (secret)
        {
            d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
            d.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
            d.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
        }
        System.Windows.Clipboard.SetDataObject(d, false);
        if (!secret) return;
        _last = text;
        _timer?.Stop();
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(clearSeconds) };
        _timer.Tick += (_, _) => { _timer!.Stop(); ClearNow(); };
        _timer.Start();
    }

    /// <summary>剪贴板里还是我们复制的密码就清掉。</summary>
    public static void ClearNow()
    {
        try { if (_last != null && System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == _last) System.Windows.Clipboard.Clear(); }
        catch { }
        _last = null;
    }
}
