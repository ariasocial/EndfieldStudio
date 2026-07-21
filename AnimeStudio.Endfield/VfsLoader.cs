using System.Buffers.Binary;

namespace AnimeStudio.Endfield;

/// <summary>
/// VFS block loader: reads encrypted .blc and .chk files from the
/// primary VFS folder, optionally falling back to a base VFS folder.
/// This matches the game's hot-update layout, where Persistent contains
/// updated metadata and only some of the referenced chunks.
/// </summary>
public sealed class VfsLoader
{
    private const string VfsDir = "VFS";

    private readonly string _vfsPath;
    private readonly string? _fallbackVfsPath;
    private readonly byte[] _chachaKey;

    public VfsLoader(string streamingAssetsPath, byte[] chacha20Key, string? baseStreamingAssetsPath = null)
    {
        if (streamingAssetsPath is null) throw new ArgumentNullException(nameof(streamingAssetsPath));
        if (chacha20Key is null) throw new ArgumentNullException(nameof(chacha20Key));
        if (chacha20Key.Length != 32)
            throw new ArgumentException("ChaCha20 key must be 32 bytes", nameof(chacha20Key));

        _vfsPath = Path.Combine(streamingAssetsPath, VfsDir);
        _fallbackVfsPath = string.IsNullOrWhiteSpace(baseStreamingAssetsPath)
            ? null
            : Path.Combine(baseStreamingAssetsPath, VfsDir);
        _chachaKey = (byte[])chacha20Key.Clone();
    }

    public string VfsPath => _vfsPath;

    public string? FallbackVfsPath => _fallbackVfsPath;

    public BlockMainInfo LoadBlockInfo(BlockType bt)
    {
        string dirName = BlockHashes.GetDirName(bt);
        string blockFilePath = ResolveBlockFilePath(dirName);
        byte[] blockData = File.ReadAllBytes(blockFilePath);

        if (blockData.Length < Keys.BlockHeadLen)
        {
            throw new InvalidDataException("block file too short");
        }

        Span<byte> nonce = stackalloc byte[Keys.BlockHeadLen];
        blockData.AsSpan(0, Keys.BlockHeadLen).CopyTo(nonce);

        int payloadLen = blockData.Length - Keys.BlockHeadLen;
        var decrypted = new byte[payloadLen];
        Buffer.BlockCopy(blockData, Keys.BlockHeadLen, decrypted, 0, payloadLen);

        var cipher = new ChaCha20(_chachaKey, nonce, 1);
        cipher.ApplyKeystream(decrypted);

        return VfsParser.Parse(decrypted, verifyCrc: true);
    }

    private string ResolveBlockFilePath(string dirName)
    {
        string primaryDir = Path.Combine(_vfsPath, dirName);
        string primaryPath = Path.Combine(primaryDir, dirName + ".blc");
        if (File.Exists(primaryPath)) return primaryPath;

        if (_fallbackVfsPath is not null)
        {
            string fallbackPath = Path.Combine(_fallbackVfsPath, dirName, dirName + ".blc");
            if (File.Exists(fallbackPath)) return fallbackPath;
        }

        bool primaryDirExists = Directory.Exists(primaryDir);
        bool fallbackDirExists = _fallbackVfsPath is not null
            && Directory.Exists(Path.Combine(_fallbackVfsPath, dirName));
        if (!primaryDirExists && !fallbackDirExists)
            throw new DirectoryNotFoundException($"block directory not found: {dirName}");

        throw new FileNotFoundException($"block metadata file not found: {dirName}.blc", primaryPath);
    }

    private string ResolveChunkPath(BlockType bt, string chunkFileName)
    {
        string dirName = BlockHashes.GetDirName(bt);
        string primaryPath = Path.Combine(_vfsPath, dirName, chunkFileName);
        if (File.Exists(primaryPath)) return primaryPath;

        if (_fallbackVfsPath is not null)
        {
            string fallbackPath = Path.Combine(_fallbackVfsPath, dirName, chunkFileName);
            if (File.Exists(fallbackPath)) return fallbackPath;
        }

        string searched = _fallbackVfsPath is null
            ? primaryPath
            : $"{primaryPath}; fallback: {Path.Combine(_fallbackVfsPath, dirName, chunkFileName)}";
        throw new FileNotFoundException($"chunk file not found: {chunkFileName} (searched {searched})", primaryPath);
    }

    public long ExtractFile(BlockType bt, ChunkInfo chunk, FileInfo file, Stream writer)
    {
        if (chunk is null) throw new ArgumentNullException(nameof(chunk));
        if (file is null) throw new ArgumentNullException(nameof(file));
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        string chunkPath = ResolveChunkPath(bt, chunk.FileName());

        using var stream = new FileStream(
            chunkPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: false);

        stream.Seek(file.Offset, SeekOrigin.Begin);

        long bytesWritten = 0;
        long fileLen = file.Length;
        var buffer = new byte[64 * 1024];

        if (file.UseEncrypt)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(nonce.Slice(0, 4), Keys.VfsProtoVersion);
            BinaryPrimitives.WriteInt64LittleEndian(nonce.Slice(4, 8), file.IvSeed);

            var cipher = new ChaCha20(_chachaKey, nonce, 1);

            long remaining = fileLen;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = stream.Read(buffer, 0, toRead);
                if (read == 0) break;

                cipher.ApplyKeystream(buffer.AsSpan(0, read));
                writer.Write(buffer, 0, read);
                bytesWritten += read;
                remaining -= read;
            }
        }
        else
        {
            long remaining = fileLen;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = stream.Read(buffer, 0, toRead);
                if (read == 0) break;

                writer.Write(buffer, 0, read);
                bytesWritten += read;
                remaining -= read;
            }
        }

        return bytesWritten;
    }

    public byte[] ExtractFileToBytes(BlockType bt, ChunkInfo chunk, FileInfo file)
    {
        using var ms = new MemoryStream(checked((int)Math.Max(0, file.Length)));
        ExtractFile(bt, chunk, file, ms);
        return ms.ToArray();
    }
}
