using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocConvert.Core;

namespace DocConvert.Infrastructure.Windows;

public sealed record OfficeWorkerRequest(string InputPath, string OutputPath, string Operation);
public sealed record OfficeWorkerResponse(bool Success, string? Error = null, int? OfficeProcessId = null, bool IsStarted = false);

public static class OfficeAvailability
{
    public static bool IsInstalledForRoute(string inputPath, string outputExtension, out string requirement)
    {
        var input = Path.GetExtension(inputPath).ToLowerInvariant();
        var output = outputExtension.ToLowerInvariant();
        var program = (input, output) switch
        {
            (".docx", ".pdf") or (".pdf", ".docx") => "Word",
            (".xlsx", ".pdf") => "Excel",
            (".pptx", ".pdf") => "PowerPoint",
            _ => string.Empty
        };
        requirement = program;
        return string.IsNullOrEmpty(program) || Type.GetTypeFromProgID(program + ".Application") is not null;
    }
}

public sealed class OfficeConversionEngine : IConversionEngine
{
    public string Name => "Microsoft Office 转换引擎";

    public bool CanHandle(DocumentJobRequest request)
    {
        if (request.Kind != JobKind.Convert) return false;
        var input = Path.GetExtension(request.InputPath).ToLowerInvariant();
        var output = Path.GetExtension(request.OutputPath).ToLowerInvariant();
        return (input == ".docx" && output == ".pdf")
            || (input == ".xlsx" && output == ".pdf")
            || (input == ".pptx" && output == ".pdf")
            || (input == ".pdf" && output == ".docx");
    }

    public async Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (!OfficeAvailability.IsInstalledForRoute(request.InputPath, Path.GetExtension(request.OutputPath), out var requirement))
            return JobResult.Fail(request.OutputPath, $"该转换需要安装 Microsoft {requirement}。");
        using var workspace = new JobWorkspace(request.JobId);
        var temporary = workspace.PathFor("office" + Path.GetExtension(request.OutputPath));
        var operation = $"{Path.GetExtension(request.InputPath).TrimStart('.').ToLowerInvariant()}-to-{Path.GetExtension(request.OutputPath).TrimStart('.').ToLowerInvariant()}";
        var response = await OfficeWorkerClient.ExecuteAsync(new OfficeWorkerRequest(request.InputPath, temporary, operation), progress, cancellationToken);
        if (!response.Success) return JobResult.Fail(request.OutputPath, response.Error ?? "Office 转换失败。");
        workspace.Commit(temporary, request.OutputPath);
        progress?.Report(new JobProgress(100, "Office 转换完成"));
        return JobResult.Ok(request.OutputPath);
    }
}

public static class OfficeWorkerClient
{
    public static async Task<OfficeWorkerResponse> ExecuteAsync(OfficeWorkerRequest request, IProgress<JobProgress>? progress, CancellationToken token)
    {
        var pipeName = "DocConvert-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 DocConvert.exe。");
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, $"--office-worker {pipeName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("无法启动 Office Worker。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        int? officeProcessId = null;
        try
        {
            progress?.Report(new JobProgress(5, "正在连接 Office Worker"));
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            OfficeWorkerResponse? response = null;
            while (response is null)
            {
                var responseJson = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrWhiteSpace(responseJson)) break;
                var message = JsonSerializer.Deserialize<OfficeWorkerResponse>(responseJson);
                if (message?.IsStarted == true)
                {
                    officeProcessId = message.OfficeProcessId;
                    progress?.Report(new JobProgress(12, "Microsoft Office 已启动，正在转换"));
                    continue;
                }
                response = message;
            }
            await process.WaitForExitAsync(timeout.Token);
            return response ?? new OfficeWorkerResponse(false, "Office Worker 未返回结果。");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            TerminateTaskOfficeProcess(officeProcessId);
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TerminateTaskOfficeProcess(int? processId)
    {
        if (processId is null) return;
        try
        {
            using var officeProcess = System.Diagnostics.Process.GetProcessById(processId.Value);
            if (!officeProcess.HasExited) officeProcess.Kill(true);
        }
        catch { }
    }
}

public static class OfficeWorkerHost
{
    public static async Task<int> RunAsync(string pipeName)
    {
        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15_000).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var requestJson = await reader.ReadLineAsync().ConfigureAwait(false);
            var request = string.IsNullOrWhiteSpace(requestJson)
                ? null
                : JsonSerializer.Deserialize<OfficeWorkerRequest>(requestJson);
            if (request is null) throw new InvalidOperationException("Office Worker 请求为空。");
            ExecuteSta(request, processId => WriteMessage(writer, new OfficeWorkerResponse(true, OfficeProcessId: processId, IsStarted: true)));
            await writer.WriteLineAsync(JsonSerializer.Serialize(new OfficeWorkerResponse(true))).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            if (writer is not null)
            {
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new OfficeWorkerResponse(false, exception.GetBaseException().Message))).ConfigureAwait(false);
                }
                catch { }
            }
            return 1;
        }
        finally
        {
            writer?.Dispose();
            if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void WriteMessage(StreamWriter writer, OfficeWorkerResponse message) =>
        writer.WriteLine(JsonSerializer.Serialize(message));

    private static void ExecuteSta(OfficeWorkerRequest request, Action<int> officeStarted)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ExecuteOffice(request, officeStarted); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static void ExecuteOffice(OfficeWorkerRequest request, Action<int> officeStarted)
    {
        switch (request.Operation)
        {
            case "docx-to-pdf": ConvertWord(request, false, officeStarted); break;
            case "pdf-to-docx": ConvertWord(request, true, officeStarted); break;
            case "xlsx-to-pdf": ConvertExcel(request, officeStarted); break;
            case "pptx-to-pdf": ConvertPowerPoint(request, officeStarted); break;
            default: throw new NotSupportedException($"不支持的 Office 操作：{request.Operation}");
        }
    }

    private static void ConvertWord(OfficeWorkerRequest request, bool toDocx, Action<int> officeStarted)
    {
        var type = Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("未检测到 Microsoft Word。");
        dynamic? app = null;
        dynamic? document = null;
        try
        {
            var previousProcesses = SnapshotProcessIds("WINWORD");
            app = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法启动 Microsoft Word。 ");
            app.Visible = false;
            app.DisplayAlerts = 0;
            officeStarted(FindNewOfficeProcess("WINWORD", previousProcesses));
            dynamic documents = app.Documents ?? throw new InvalidOperationException("Word 文档集合不可用。");
            document = documents.Open(request.InputPath, false, true, false, "", "", false, "", "", 0, 0, false, false, 0, true, "");
            if (document is null) throw new InvalidOperationException("Word 未能打开输入文件。");
            if (toDocx) document.SaveAs2(request.OutputPath, 16, false);
            else document.ExportAsFixedFormat(request.OutputPath, 17, false, 0, 0, 1, 1, 0, true, true, 0, true, true, false);
            Release(documents);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Word 转换失败：{exception.GetBaseException().Message}", exception);
        }
        finally
        {
            try { document?.Close(false); } catch { }
            try { app?.Quit(false); } catch { }
            Release(document); Release(app);
        }
    }

    private static void ConvertExcel(OfficeWorkerRequest request, Action<int> officeStarted)
    {
        var type = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException("未检测到 Microsoft Excel。");
        dynamic? app = null;
        dynamic? workbook = null;
        try
        {
            var previousProcesses = SnapshotProcessIds("EXCEL");
            app = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法启动 Microsoft Excel。");
            app.Visible = false;
            app.DisplayAlerts = false;
            officeStarted(FindNewOfficeProcess("EXCEL", previousProcesses));
            dynamic workbooks = app.Workbooks ?? throw new InvalidOperationException("Excel 工作簿集合不可用。");
            workbook = workbooks.Open(request.InputPath, 0, true);
            if (workbook is null) throw new InvalidOperationException("Excel 未能打开输入文件。");
            workbook.ExportAsFixedFormat(0, request.OutputPath);
            Release(workbooks);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Excel 转换失败：{exception.GetBaseException().Message}", exception);
        }
        finally
        {
            try { workbook?.Close(false); } catch { }
            try { app?.Quit(); } catch { }
            Release(workbook); Release(app);
        }
    }

    private static void ConvertPowerPoint(OfficeWorkerRequest request, Action<int> officeStarted)
    {
        var type = Type.GetTypeFromProgID("PowerPoint.Application") ?? throw new InvalidOperationException("未检测到 Microsoft PowerPoint。");
        dynamic? app = null;
        dynamic? presentation = null;
        try
        {
            var previousProcesses = SnapshotProcessIds("POWERPNT");
            app = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法启动 Microsoft PowerPoint。");
            officeStarted(FindNewOfficeProcess("POWERPNT", previousProcesses));
            dynamic presentations = app.Presentations ?? throw new InvalidOperationException("PowerPoint 演示文稿集合不可用。");
            presentation = presentations.Open(request.InputPath, -1, 0, 0);
            if (presentation is null) throw new InvalidOperationException("PowerPoint 未能打开输入文件。");
            presentation.SaveAs(request.OutputPath, 32);
            Release(presentations);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"PowerPoint 转换失败：{exception.GetBaseException().Message}", exception);
        }
        finally
        {
            try { presentation?.Close(); } catch { }
            try { app?.Quit(); } catch { }
            Release(presentation); Release(app);
        }
    }

    private static void Release(object? value)
    {
        try { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        catch { }
    }

    private static HashSet<int> SnapshotProcessIds(string processName) =>
        Process.GetProcessesByName(processName).Select(process => process.Id).ToHashSet();

    private static int FindNewOfficeProcess(string processName, HashSet<int> previousProcessIds)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = Process.GetProcessesByName(processName)
                .Where(process => !previousProcessIds.Contains(process.Id))
                .OrderByDescending(process => process.StartTime)
                .FirstOrDefault();
            if (candidate is not null) return candidate.Id;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("无法识别任务创建的 Office 进程，已停止转换以保护用户当前打开的 Office 文档。");
    }
}
