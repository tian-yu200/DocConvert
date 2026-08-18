namespace DocConvert.Core;

public sealed class DocumentJobRunner(IEnumerable<IDocumentEngine> engines) : IDocumentJobRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private readonly IReadOnlyList<IDocumentEngine> _engines = engines.ToList();
    private readonly SemaphoreSlim _serialGate = new(1, 1);

    public async Task<JobResult> RunAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var engine = _engines.FirstOrDefault(candidate => candidate.CanHandle(request));
        if (engine is null)
        {
            return JobResult.Fail(request.OutputPath, "当前版本没有可处理该输入与输出组合的引擎。");
        }

        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            progress?.Report(new JobProgress(1, $"正在启动 {engine.Name}"));
            return await engine.ExecuteAsync(request, progress, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return JobResult.Fail(request.OutputPath, "单个文件处理超过 5 分钟，任务已停止。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return JobResult.Fail(request.OutputPath, exception.Message);
        }
        finally
        {
            _serialGate.Release();
        }
    }
}
