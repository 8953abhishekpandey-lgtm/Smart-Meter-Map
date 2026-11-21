using Microsoft.Extensions.Options;
using SmartMeter.Application;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;

namespace SmartMeter.Infrastructure.Ingestion;

/// <summary>
/// Disk-backed upload store (mount a shared volume when running several API instances).
/// Chunks are written with positional I/O into one pre-allocated file so they can arrive in any
/// order / in parallel; a marker file per chunk proves completeness before processing.
/// </summary>
internal sealed class LocalUploadStore : IUploadStore
{
    private readonly string _root;

    public LocalUploadStore(IOptions<IngestionOptions> options)
    {
        _root = Path.GetFullPath(options.Value.StoragePath);
        Directory.CreateDirectory(_root);
    }

    public string GetFilePath(Guid jobId) => Path.Combine(_root, $"{jobId:N}.data");
    private string PartPath(Guid jobId) => Path.Combine(_root, $"{jobId:N}.part");
    private string ChunkDir(Guid jobId) => Path.Combine(_root, $"{jobId:N}.chunks");

    public async Task WriteChunkAsync(Guid jobId, int index, long chunkSize, Stream content, CancellationToken ct)
    {
        Directory.CreateDirectory(ChunkDir(jobId));
        using var handle = File.OpenHandle(PartPath(jobId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long offset = index * chunkSize, written = 0;
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                written += read;
                if (written > chunkSize) throw new BadRequestException($"Chunk {index} exceeds the chunk size of {chunkSize} bytes.");
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct);
                offset += read;
            }
            if (written == 0) throw new BadRequestException($"Chunk {index} is empty.");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        await File.WriteAllTextAsync(Path.Combine(ChunkDir(jobId), index.ToString("D6")), "", ct);
    }

    public Task<int> CountChunksAsync(Guid jobId, CancellationToken ct) =>
        Task.FromResult(Directory.Exists(ChunkDir(jobId)) ? Directory.GetFiles(ChunkDir(jobId)).Length : 0);

    public async Task WriteWholeAsync(Guid jobId, Stream content, long maxBytes, CancellationToken ct)
    {
        await using var file = new FileStream(PartPath(jobId), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new BadRequestException($"File exceeds the maximum size of {maxBytes} bytes.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (total == 0) throw new BadRequestException("The uploaded file is empty.");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task<long> FinalizeAsync(Guid jobId, CancellationToken ct)
    {
        var part = PartPath(jobId);
        if (!File.Exists(part)) throw new BadRequestException("No data has been uploaded.");
        var final = GetFilePath(jobId);
        File.Move(part, final, overwrite: true);
        if (Directory.Exists(ChunkDir(jobId))) Directory.Delete(ChunkDir(jobId), recursive: true);
        return Task.FromResult(new FileInfo(final).Length);
    }

    public void Delete(Guid jobId)
    {
        foreach (var path in new[] { GetFilePath(jobId), PartPath(jobId) })
            if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(ChunkDir(jobId))) Directory.Delete(ChunkDir(jobId), recursive: true);
    }
}
