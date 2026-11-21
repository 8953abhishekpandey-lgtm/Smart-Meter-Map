using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;

namespace SmartMeter.Application.Ingestion;

public sealed record UploadSessionDto(Guid JobId, int ChunkSize, int ChunkCount);

// ------------------------------------------------------------------ chunked upload
public sealed record CreateUploadCommand(IngestionKind Kind, string FileName, long FileSize) : IRequest<UploadSessionDto>;

public sealed class CreateUploadValidator : AbstractValidator<CreateUploadCommand>
{
    public CreateUploadValidator(IOptions<IngestionOptions> options)
    {
        var o = options.Value;
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(260)
            .Must(n => o.AllowedExtensions.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase))
            .WithMessage($"File type not supported. Allowed: {string.Join(", ", o.AllowedExtensions)}");
        RuleFor(x => x.FileSize).InclusiveBetween(1, o.MaxFileSizeBytes);
    }
}

internal sealed class CreateUploadHandler(IIngestionRepository repo, ICurrentUser user, IOptions<IngestionOptions> options)
    : IRequestHandler<CreateUploadCommand, UploadSessionDto>
{
    public async Task<UploadSessionDto> Handle(CreateUploadCommand c, CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        await repo.CreateJobAsync(jobId, c.Kind.ToCode(), Path.GetFileName(c.FileName), c.FileSize, user.Name, ct);
        var chunk = options.Value.ChunkSizeBytes;
        return new UploadSessionDto(jobId, chunk, (int)((c.FileSize + chunk - 1) / chunk));
    }
}

public sealed record UploadChunkCommand(Guid JobId, int Index, Stream Content) : IRequest;

internal sealed class UploadChunkHandler(IIngestionRepository repo, IUploadStore store, IOptions<IngestionOptions> options)
    : IRequestHandler<UploadChunkCommand>
{
    public async Task Handle(UploadChunkCommand c, CancellationToken ct)
    {
        var job = await repo.GetJobAsync(c.JobId, ct) ?? throw new NotFoundException("Upload", c.JobId.ToString());
        if (job.Status != IngestionStatus.Uploading) throw new ConflictException($"Upload is already {job.Status}.");
        var chunk = options.Value.ChunkSizeBytes;
        var chunkCount = (int)((job.FileSize + chunk - 1) / chunk);
        if (c.Index < 0 || c.Index >= chunkCount) throw new BadRequestException($"Chunk index must be between 0 and {chunkCount - 1}.");
        await store.WriteChunkAsync(c.JobId, c.Index, chunk, c.Content, ct);
    }
}

public sealed record CompleteUploadCommand(Guid JobId) : IRequest<IngestionJobDto>;

internal sealed class CompleteUploadHandler(IIngestionRepository repo, IUploadStore store, IBackgroundJobs jobs,
    IIngestionNotifier notifier, IOptions<IngestionOptions> options) : IRequestHandler<CompleteUploadCommand, IngestionJobDto>
{
    public async Task<IngestionJobDto> Handle(CompleteUploadCommand c, CancellationToken ct)
    {
        var job = await repo.GetJobAsync(c.JobId, ct) ?? throw new NotFoundException("Upload", c.JobId.ToString());
        if (job.Status != IngestionStatus.Uploading) throw new ConflictException($"Upload is already {job.Status}.");
        var chunk = options.Value.ChunkSizeBytes;
        var expectedChunks = (int)((job.FileSize + chunk - 1) / chunk);
        var received = await store.CountChunksAsync(c.JobId, ct);
        if (received != expectedChunks) throw new BadRequestException($"Upload incomplete: {received} of {expectedChunks} chunks received.");
        var size = await store.FinalizeAsync(c.JobId, ct);
        if (size != job.FileSize) throw new BadRequestException($"Uploaded size {size} does not match declared size {job.FileSize}.");
        return await UploadQueue.QueueAsync(c.JobId, repo, jobs, notifier, ct);
    }
}

// ------------------------------------------------------------------ single-shot streaming upload (curl / scripts)
public sealed record UploadFileCommand(IngestionKind Kind, string FileName, Stream Content) : IRequest<IngestionJobDto>;

internal sealed class UploadFileHandler(IIngestionRepository repo, IUploadStore store, IBackgroundJobs jobs, IIngestionNotifier notifier,
    ICurrentUser user, IOptions<IngestionOptions> options) : IRequestHandler<UploadFileCommand, IngestionJobDto>
{
    public async Task<IngestionJobDto> Handle(UploadFileCommand c, CancellationToken ct)
    {
        var o = options.Value;
        var name = Path.GetFileName(c.FileName);
        if (string.IsNullOrWhiteSpace(name) || !o.AllowedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"File type not supported. Allowed: {string.Join(", ", o.AllowedExtensions)}");
        var jobId = Guid.NewGuid();
        await repo.CreateJobAsync(jobId, c.Kind.ToCode(), name, 0, user.Name, ct);
        await store.WriteWholeAsync(jobId, c.Content, o.MaxFileSizeBytes, ct);
        await store.FinalizeAsync(jobId, ct);
        return await UploadQueue.QueueAsync(jobId, repo, jobs, notifier, ct);
    }
}

internal static class UploadQueue
{
    public static async Task<IngestionJobDto> QueueAsync(Guid jobId, IIngestionRepository repo, IBackgroundJobs jobs,
        IIngestionNotifier notifier, CancellationToken ct)
    {
        await repo.SetStatusAsync(jobId, IngestionStatus.Queued, "Waiting for an ingestion worker.", ct);
        jobs.EnqueueIngestion(jobId);
        var dto = IngestionJobDto.From((await repo.GetJobAsync(jobId, ct))!);
        await notifier.JobUpdatedAsync(dto, ct);
        return dto;
    }
}

// ------------------------------------------------------------------ queries
public sealed record GetIngestionJobQuery(Guid JobId) : IRequest<IngestionJobDto>;

internal sealed class GetIngestionJobHandler(IIngestionRepository repo) : IRequestHandler<GetIngestionJobQuery, IngestionJobDto>
{
    public async Task<IngestionJobDto> Handle(GetIngestionJobQuery q, CancellationToken ct) =>
        IngestionJobDto.From(await repo.GetJobAsync(q.JobId, ct) ?? throw new NotFoundException("Ingestion job", q.JobId.ToString()));
}

public sealed record ListIngestionJobsQuery(int Take = 50) : IRequest<IReadOnlyList<IngestionJobDto>>;

internal sealed class ListIngestionJobsHandler(IIngestionRepository repo) : IRequestHandler<ListIngestionJobsQuery, IReadOnlyList<IngestionJobDto>>
{
    public async Task<IReadOnlyList<IngestionJobDto>> Handle(ListIngestionJobsQuery q, CancellationToken ct) =>
        (await repo.ListJobsAsync(Math.Clamp(q.Take, 1, 500), ct)).Select(IngestionJobDto.From).ToList();
}

public sealed record IngestionErrorDto(long RowNo, string? Column, string? RawValue, string Message);

public sealed record GetIngestionErrorsQuery(Guid JobId) : IStreamRequest<IngestionErrorDto>;

internal sealed class GetIngestionErrorsHandler(IIngestionRepository repo) : IStreamRequestHandler<GetIngestionErrorsQuery, IngestionErrorDto>
{
    public async IAsyncEnumerable<IngestionErrorDto> Handle(GetIngestionErrorsQuery q,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in repo.StreamErrorsAsync(q.JobId, ct))
            yield return new IngestionErrorDto(e.RowNo, e.ColumnName, e.RawValue, e.Message);
    }
}

public sealed record RequestAnalyticsRefreshCommand : IRequest<string>;

internal sealed class RequestAnalyticsRefreshHandler(IBackgroundJobs jobs) : IRequestHandler<RequestAnalyticsRefreshCommand, string>
{
    public Task<string> Handle(RequestAnalyticsRefreshCommand request, CancellationToken ct) =>
        Task.FromResult(jobs.EnqueueAnalyticsRefresh(null));
}
