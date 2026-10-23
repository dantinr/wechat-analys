// SPDX-License-Identifier: GPL-3.0-or-later
// WeChat 4.x layout research: chenanhan230-del/WeChatMsg-4x (GPL-3.0).
// Only PROCESS_VM_READ / QUERY_INFORMATION are requested. No process or source DB writes.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal static class RawKeys
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Base, AllocationBase;
        public uint AllocationProtect, Pad;
        public ulong Size;
        public uint State, Protect, Type, Pad2;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(nint handle, ulong address, out MemoryInfo info, nuint size);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(nint handle, ulong address, byte[] buffer, nuint length, out nuint read);

    private static readonly byte[] Name = Encoding.ASCII.GetBytes("com.Tencent.WCDB.Config.Cipher");
    private static readonly byte[] Mask = Convert.FromHexString("d2c7442458020000004889442450488b450048844c2448488944254048584c24");
    private static readonly Regex Literal = new("[xX]'([0-9a-fA-F]{64,192})'", RegexOptions.CultureInvariant);

    internal static Dictionary<string, byte[]> Find(IEnumerable<string> files, Action<string>? progress, CancellationToken token)
    {
        var pages = files.ToDictionary(x => x, x => SharedRead(x, 4096));
        var keys = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int processes = 0, namesFound = 0, references = 0, blobs = 0;
        void TryLiteral(byte[] data)
        {
            foreach (Match match in Literal.Matches(Encoding.ASCII.GetString(data)))
            {
                string hex = match.Groups[1].Value;
                for (int i = 0; i + 64 <= hex.Length; i += 32)
                {
                    byte[] candidate = Convert.FromHexString(hex.Substring(i, 64));
                    candidates.TryAdd(Convert.ToHexString(SHA256.HashData(candidate)), candidate);
                    foreach (var page in pages.Where(p => !keys.ContainsKey(p.Key)))
                        if (CipherPages.VerifyKey(candidate, page.Value)) keys[page.Key] = candidate.ToArray();
                }
            }
        }
        foreach (var process in Process.GetProcessesByName("Weixin").OrderByDescending(p => p.WorkingSet64))
        {
            token.ThrowIfCancellationRequested();
            if (keys.Count == pages.Count) break;
            nint handle = OpenProcess(0x410, false, process.Id);
            if (handle == 0) continue;
            processes++;
            try
            {
                var regions = new List<(ulong Base, ulong Size)>();
                for (ulong address = 0; address < 0x800000000000;)
                {
                    if (VirtualQueryEx(handle, address, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0) break;
                    if (info.State == 0x1000 && (info.Protect & 0x100) == 0 && (info.Protect & 0xEE) != 0)
                        regions.Add((info.Base, info.Size));
                    ulong next = info.Base + info.Size;
                    if (next <= address) break;
                    address = next;
                }
                byte[] Read(ulong address, int length)
                {
                    var buffer = new byte[length];
                    ReadProcessMemory(handle, address, buffer, (nuint)length, out var count);
                    return count == (nuint)length ? buffer : buffer[..(int)count];
                }
                IEnumerable<(ulong Address, byte[] Data)> Chunks()
                {
                    const int block = 2 * 1024 * 1024;
                    foreach (var region in regions)
                        for (ulong offset = 0; offset < region.Size; offset += block)
                        {
                            token.ThrowIfCancellationRequested();
                            int length = (int)Math.Min((ulong)(block + 128), region.Size - offset);
                            var buffer = Read(region.Base + offset, length);
                            if (buffer.Length > 0) yield return (region.Base + offset, buffer);
                        }
                }
                progress?.Invoke("正在只读验证微信数据库连接…");
                var addresses = new HashSet<ulong>();
                foreach (var chunk in Chunks())
                {
                    int offset = 0, found;
                    while (offset < chunk.Data.Length && (found = chunk.Data.AsSpan(offset).IndexOf(Name)) >= 0)
                    {
                        addresses.Add(chunk.Address + (ulong)(offset + found));
                        offset += found + Name.Length;
                    }
                    // 4.0 stores raw SQLCipher literals directly; never log their contents.
                    if (chunk.Data.AsSpan().IndexOf("x'"u8) >= 0) TryLiteral(chunk.Data);
                }
                namesFound += addresses.Count;
                var patterns = addresses.Select(a =>
                {
                    var result = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(result, a);
                    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), (ulong)Name.Length); return result;
                }).ToArray();
                var seen = new HashSet<ulong>();
                foreach (var chunk in Chunks())
                {
                    if (keys.Count == pages.Count) break;
                    foreach (var pattern in patterns)
                    {
                        int offset = 0, found;
                        while (offset < chunk.Data.Length && (found = chunk.Data.AsSpan(offset).IndexOf(pattern)) >= 0)
                        {
                            ulong hit = chunk.Address + (ulong)(offset + found);
                            offset += found + 1; references++;
                            var node = Read(hit + 0x18, 8);
                            if (node.Length != 8) continue;
                            ulong config = BinaryPrimitives.ReadUInt64LittleEndian(node);
                            if (config < 0x10000 || config >= 0x800000000000 || !seen.Add(config)) continue;
                            var descriptor = Read(config + 0x90, 16);
                            if (descriptor.Length != 16) continue;
                            ulong ptr = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
                            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(8));
                            if (ptr < 0x10000 || ptr >= 0x800000000000 || size is 0 or > 1024) continue;
                            var blob = Read(ptr, (int)size);
                            if (blob.Length != (int)size) continue;
                            blobs++;
                            for (int i = 0; i < blob.Length; i++) blob[i] ^= Mask[i % Mask.Length];
                            TryLiteral(blob); CryptographicOperations.ZeroMemory(blob);
                        }
                    }
                }
            }
            finally { CloseHandle(handle); process.Dispose(); }
        }
        // Some 4.1 builds retain a passphrase instead of an already-derived AES key.
        if (keys.Count < pages.Count && candidates.Count <= 64)
            foreach (var candidate in candidates.Values)
                foreach (var page in pages.Where(p => !keys.ContainsKey(p.Key)))
                {
                    token.ThrowIfCancellationRequested();
                    byte[] derived = Rfc2898DeriveBytes.Pbkdf2(candidate, page.Value.AsSpan(0, 16), 256000, HashAlgorithmName.SHA512, 32);
                    if (CipherPages.VerifyKey(derived, page.Value)) keys[page.Key] = derived;
                    else CryptographicOperations.ZeroMemory(derived);
                }
        foreach (var candidate in candidates.Values) CryptographicOperations.ZeroMemory(candidate);
        progress?.Invoke($"只读连接：已验证 {keys.Count}/{pages.Count} 个数据库");
        if (keys.Count != pages.Count)
            throw new InvalidOperationException($"当前微信版本的数据库连接尚未通过验证（{keys.Count}/{pages.Count}）。请保持微信登录后重试。诊断：可读进程 {processes}，配置标记 {namesFound}，引用 {references}，配置块 {blobs}。没有使用 OCR。 ");
        return keys;
    }

    internal static byte[] SharedRead(string path, int? maximum = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > int.MaxValue && maximum == null) throw new IOException("数据库过大，无法创建快照。");
        int length = maximum.HasValue ? (int)Math.Min(maximum.Value, file.Length) : (int)file.Length;
        byte[] data = new byte[length]; file.ReadExactly(data); return data;
    }
}

internal static class CipherPages
{
    internal const int PageSize = 4096, Reserve = 80;
    internal static byte[] MacKey(byte[] key, ReadOnlySpan<byte> salt)
    {
        byte[] altered = salt.ToArray(); for (int i = 0; i < altered.Length; i++) altered[i] ^= 0x3a;
        return Rfc2898DeriveBytes.Pbkdf2(key, altered, 2, HashAlgorithmName.SHA512, 32);
    }
    internal static bool VerifyKey(byte[] key, byte[] page) => page.Length == PageSize && VerifyPage(MacKey(key, page.AsSpan(0, 16)), page, 1);
    internal static bool VerifyPage(byte[] macKey, ReadOnlySpan<byte> page, uint number)
    {
        int start = number == 1 ? 16 : 0;
        byte[] input = new byte[PageSize - 64 - start + 4];
        page.Slice(start, PageSize - 64 - start).CopyTo(input);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(input.Length - 4), number);
        return CryptographicOperations.FixedTimeEquals(HMACSHA512.HashData(macKey, input), page[^64..]);
    }
    internal static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> page, uint number)
    {
        using var aes = Aes.Create(); aes.Key = key; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
        byte[] result = new byte[PageSize]; int start = number == 1 ? 16 : 0;
        if (number == 1) "SQLite format 3\0"u8.CopyTo(result);
        aes.DecryptCbc(page.Slice(start, PageSize - Reserve - start), page.Slice(PageSize - Reserve, 16), result.AsSpan(start), PaddingMode.None);
        return result;
    }
    internal static byte[] Snapshot(string path, byte[] key, CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string walPath = path + "-wal";
                var walBefore = File.Exists(walPath) ? RawKeys.SharedRead(walPath) : [];
                var source = RawKeys.SharedRead(path);
                var wal = File.Exists(walPath) ? RawKeys.SharedRead(walPath) : [];
                if (!walBefore.AsSpan().SequenceEqual(wal)) throw new IOException("微信数据库在读取过程中发生变化。");
                return Decode(source, wal, key, token);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException)
            {
                if (attempt == 2) throw new IOException($"读取 {Path.GetFileName(path)} 连续 3 次未成功：{ex.Message}", ex);
            }
        }
        throw new IOException("无法获取一致的微信数据库快照。");
    }
    internal static byte[] Decode(byte[] source, byte[] wal, byte[] key, CancellationToken token)
    {
        if (source.Length < PageSize || source.Length % PageSize != 0 || !VerifyKey(key, source[..PageSize]))
            throw new CryptographicException("数据库第一页校验失败，请重新连接微信。");
        byte[] mac = MacKey(key, source.AsSpan(0, 16));
        byte[] header = Decrypt(key, source.AsSpan(0, PageSize), 1);
        byte[]? output = null;
        try
        {
            // https://www.sqlite.org/fileformat2.html#in_header_database_size
            // SQLite file format 1.3.7: the authenticated header size is valid only
            // when nonzero and the change counter matches the version-valid-for field.
            // WeChat may preallocate zero-filled physical pages beyond that size.
            uint physicalPages = (uint)(source.Length / PageSize);
            uint headerPages = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
            bool validSize = headerPages > 0 && header.AsSpan(24, 4).SequenceEqual(header.AsSpan(92, 4));
            var committed = ReadWal(wal, token);
            uint pages = committed.Size ?? (validSize ? headerPages : physicalPages);
            if ((long)pages * PageSize > int.MaxValue || pages > (long)physicalPages + committed.Pages.Count)
                throw new IOException("数据库有效页数量超限或快照缺少数据页。");
            output = new byte[checked((int)pages * PageSize)];
            for (uint number = 1; number <= pages; number++)
            {
                token.ThrowIfCancellationRequested();
                int destination = checked((int)(number - 1) * PageSize);
                bool fromWal = committed.Pages.TryGetValue(number, out int offset);
                if (!fromWal && number > physicalPages) throw new IOException($"数据库快照缺少第 {number} 页。");
                var page = fromWal ? wal.AsSpan(offset, PageSize) : source.AsSpan(destination, PageSize);
                if (!VerifyPage(mac, page, number))
                    throw new CryptographicException($"{(fromWal ? "WAL 消息" : "数据库")}第 {number} 页校验失败。");
                byte[] plain = Decrypt(key, page, number);
                try { plain.CopyTo(output, destination); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            return output;
        }
        catch { if (output != null) CryptographicOperations.ZeroMemory(output); throw; }
        finally { CryptographicOperations.ZeroMemory(header); CryptographicOperations.ZeroMemory(mac); }
    }
    // SQLite WAL checksums cover headers and encrypted page bytes, and only committed frames are used.
    private static (uint? Size, Dictionary<uint, int> Pages) ReadWal(byte[] wal, CancellationToken token)
    {
        if (wal.Length < 32) return (null, []);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(wal);
        if (magic is not (0x377f0682 or 0x377f0683) || BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8)) != PageSize)
            throw new IOException("不支持的微信 WAL 格式。");
        bool little = magic == 0x377f0682;
        uint a = 0, b = 0;
        void Checksum(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i += 8)
            {
                uint x = little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]);
                uint y = little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[(i + 4)..]);
                a = unchecked(a + x + b); b = unchecked(b + y + a);
            }
        }
        Checksum(wal.AsSpan(0, 24));
        if (a != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(24)) || b != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(28)))
            throw new IOException("WAL 文件头校验失败。");
        int lastCommit = -1; uint size = 0;
        var frames = new List<(uint Page, int Offset)>();
        for (int offset = 32; offset <= wal.Length - 24 - PageSize; offset += 24 + PageSize)
        {
            token.ThrowIfCancellationRequested();
            var header = wal.AsSpan(offset, 24);
            uint page = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (page == 0 || !header.Slice(8, 8).SequenceEqual(wal.AsSpan(16, 8))) break;
            Checksum(header[..8]); Checksum(wal.AsSpan(offset + 24, PageSize));
            if (a != BinaryPrimitives.ReadUInt32BigEndian(header[16..]) || b != BinaryPrimitives.ReadUInt32BigEndian(header[20..])) break;
            frames.Add((page, offset + 24));
            uint commit = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (commit != 0) { lastCommit = frames.Count - 1; size = commit; }
        }
        var pages = new Dictionary<uint, int>();
        for (int index = 0; index <= lastCommit; index++)
        {
            var frame = frames[index];
            if (frame.Page <= size) pages[frame.Page] = frame.Offset;
        }
        return (lastCommit >= 0 ? size : null, pages);
    }
}
