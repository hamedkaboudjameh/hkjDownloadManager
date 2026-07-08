using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

public class DownloadJob
{
    public long TotalFileSize { get; set; }
    public int ChunkCount { get; set; }
    public string TargetFilePath { get; set; }
    public string DownloadUrl { get; set; }
    private readonly HttpClient _httpClient;

    private long _totalBytesDownloaded;

    public DownloadJob(string downloadUrl, string targetFilePath, int chunkCount, HttpClient httpClient = null)
    {
        DownloadUrl = downloadUrl;
        TargetFilePath = targetFilePath;
        ChunkCount = chunkCount;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task InitializeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, DownloadUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength.HasValue)
        {
            TotalFileSize = response.Content.Headers.ContentLength.Value;
        }
        else
        {
            throw new InvalidOperationException("The server did not provide a Content-Length header. Cannot determine file size.");
        }

        if (response.Headers.AcceptRanges == null || !response.Headers.AcceptRanges.Contains("bytes"))
        {
            // If the server doesn't support range requests, we must download in a single chunk
            ChunkCount = 1;
        }
    }

    public async Task StartDownloadAsync(IProgress<double> globalProgress, IList<IProgress<double>> chunkProgresses, CancellationToken cancellationToken)
    {
        if (ChunkCount <= 0) throw new ArgumentOutOfRangeException(nameof(ChunkCount), "Chunk count must be greater than 0.");
        if (TotalFileSize <= 0) throw new InvalidOperationException("TotalFileSize must be set before starting download.");

        _totalBytesDownloaded = 0;

        long chunkSize = TotalFileSize / ChunkCount;
        var tasks = new List<Task>();

        for (int i = 0; i < ChunkCount; i++)
        {
            long startByte = i * chunkSize;
            long endByte = (i == ChunkCount - 1) ? TotalFileSize - 1 : (startByte + chunkSize - 1);

            var chunkProgress = chunkProgresses != null && i < chunkProgresses.Count ? chunkProgresses[i] : null;

            tasks.Add(DownloadChunkAsync(i, startByte, endByte, globalProgress, chunkProgress, cancellationToken));
        }

        await Task.WhenAll(tasks);

        await MergeChunksAsync();
    }

    private async Task DownloadChunkAsync(int chunkIndex, long originalStartByte, long endByte, IProgress<double> globalProgress, IProgress<double> chunkProgress, CancellationToken cancellationToken)
    {
        string partFilePath = $"{TargetFilePath}.part{chunkIndex}";
        long currentStartByte = originalStartByte;
        long existingFileSize = 0;

        long totalChunkBytes = endByte - originalStartByte + 1;
        long chunkBytesDownloaded = 0;

        if (File.Exists(partFilePath))
        {
            var fileInfo = new FileInfo(partFilePath);
            existingFileSize = fileInfo.Length;
            currentStartByte += existingFileSize;
            chunkBytesDownloaded = existingFileSize;
            Interlocked.Add(ref _totalBytesDownloaded, existingFileSize);
            ReportProgress(globalProgress);
            ReportChunkProgress(chunkProgress, chunkBytesDownloaded, totalChunkBytes);
        }

        if (currentStartByte > endByte)
        {
            // Already fully downloaded this chunk
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
            request.Headers.Range = new RangeHeaderValue(currentStartByte, endByte);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(partFilePath, FileMode.Append, FileAccess.Write, FileShare.None, 8192, true);

            byte[] buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                chunkBytesDownloaded += bytesRead;
                Interlocked.Add(ref _totalBytesDownloaded, bytesRead);
                ReportProgress(globalProgress);
                ReportChunkProgress(chunkProgress, chunkBytesDownloaded, totalChunkBytes);
            }
        }
        catch (OperationCanceledException)
        {
            // Rethrow cancellation exception
            throw;
        }
        catch (Exception ex)
        {
            // Handle or wrap the error appropriately
            throw new Exception($"Failed to download chunk {chunkIndex}", ex);
        }
    }

    private void ReportProgress(IProgress<double> progress)
    {
        if (progress != null && TotalFileSize > 0)
        {
            double percentage = (double)Interlocked.Read(ref _totalBytesDownloaded) / TotalFileSize * 100.0;
            // Ensure we do not exceed 100%
            if (percentage > 100.0) percentage = 100.0;
            progress.Report(percentage);
        }
    }

    private void ReportChunkProgress(IProgress<double> progress, long chunkBytesDownloaded, long totalChunkBytes)
    {
        if (progress != null && totalChunkBytes > 0)
        {
            double percentage = (double)chunkBytesDownloaded / totalChunkBytes * 100.0;
            if (percentage > 100.0) percentage = 100.0;
            progress.Report(percentage);
        }
    }

    private async Task MergeChunksAsync()
    {
        using (var finalStream = new FileStream(TargetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        {
            for (int i = 0; i < ChunkCount; i++)
            {
                string partFilePath = $"{TargetFilePath}.part{i}";
                if (File.Exists(partFilePath))
                {
                    using (var partStream = new FileStream(partFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
                    {
                        await partStream.CopyToAsync(finalStream);
                    }
                }
                else
                {
                    throw new FileNotFoundException($"Missing part file for chunk {i}: {partFilePath}");
                }
            }
        }

        // Clean up part files
        for (int i = 0; i < ChunkCount; i++)
        {
            string partFilePath = $"{TargetFilePath}.part{i}";
            if (File.Exists(partFilePath))
            {
                File.Delete(partFilePath);
            }
        }
    }
}
