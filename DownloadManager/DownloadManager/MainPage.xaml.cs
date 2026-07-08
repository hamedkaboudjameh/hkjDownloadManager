using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager
{
    public sealed partial class MainPage : Page
    {
        private CancellationTokenSource? _cts;
        private DownloadJob? _currentJob;
        private bool _isPaused;
        private bool _isStopped;

        private Queue<string> _downloadQueue = new();
        private bool _isProcessingQueue;

        private List<ProgressBar> _chunkProgressBars = new();
        private List<TextBlock> _chunkTextBlocks = new();

        private DispatcherTimer _speedTimer;
        private long _previousBytesDownloaded;

        public MainPage()
        {
            this.InitializeComponent();
            _speedTimer = new DispatcherTimer();
            _speedTimer.Interval = TimeSpan.FromSeconds(1);
            _speedTimer.Tick += SpeedTimer_Tick;
        }

        private void SpeedTimer_Tick(object? sender, object e)
        {
            if (_currentJob != null)
            {
                long currentBytesDownloaded = _currentJob.TotalBytesDownloaded;
                long bytesDifference = currentBytesDownloaded - _previousBytesDownloaded;
                _previousBytesDownloaded = currentBytesDownloaded;

                // Send speed can be simulated or 0 as we are only downloading
                // Receive speed in kbps (1 kilobyte = 1024 bytes)
                double speedKbps = bytesDifference / 1024.0;

                SpeedTextBlock.Text = $"Send: 0 kbps | Receive: {speedKbps:F2} kbps";
            }
        }

        private void UpdateButtonStates(bool isIdle = false, bool isDownloading = false, bool isPaused = false)
        {
            DownloadButton.IsEnabled = isIdle;
            PauseButton.IsEnabled = isDownloading;
            ResumeButton.IsEnabled = isPaused;
            StopButton.IsEnabled = isDownloading || isPaused;
            UrlTextBox.IsEnabled = isIdle;
            ChunkCountNumberBox.IsEnabled = isIdle;
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var urls = UrlTextBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var url in urls)
            {
                string trimmed = url.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    _downloadQueue.Enqueue(trimmed);
                }
            }
            UrlTextBox.Text = string.Empty;

            _isStopped = false;

            if (!_isProcessingQueue && !_isPaused)
            {
                await ProcessQueueAsync();
            }
        }

        private async Task ProcessQueueAsync()
        {
            _isProcessingQueue = true;
            bool encounteredError = false;
            try
            {
                while (_downloadQueue.Count > 0 && !_isStopped && !_isPaused)
                {
                    string url = _downloadQueue.Dequeue();
                    bool success = await StartOrResumeDownload(false, url);
                    if (!success)
                    {
                        encounteredError = true;
                        break; // Stop processing if a download failed or was cancelled
                    }
                }
            }
            finally
            {
                _isProcessingQueue = false;
                if (!_isPaused && !_isStopped)
                {
                    UpdateButtonStates(isIdle: true);
                    if (!encounteredError && _downloadQueue.Count == 0 && _currentJob == null)
                    {
                        StatusTextBlock.Text = "All downloads completed.";
                    }
                }
                else if (_isPaused)
                {
                    UpdateButtonStates(isPaused: true);
                }
                else if (_isStopped)
                {
                    UpdateButtonStates(isIdle: true);
                }
            }
        }

        private async Task<bool> StartOrResumeDownload(bool isResume, string? overrideUrl = null)
        {
            if (!isResume)
            {
                string url = overrideUrl ?? UrlTextBox.Text;
                if (string.IsNullOrWhiteSpace(url))
                {
                    StatusTextBlock.Text = "Please enter a valid URL.";
                    return false;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult) || (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
                {
                    StatusTextBlock.Text = "Invalid URL format.";
                    return false;
                }

                // In Uno, the Value property of NumberBox is double, so we cast to int
                int chunkCount = (int)ChunkCountNumberBox.Value;
                if (chunkCount <= 0)
                {
                    StatusTextBlock.Text = "Chunk count must be greater than 0.";
                    return false;
                }

                string fileName = Path.GetFileName(uriResult.LocalPath) ?? "";
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = "downloaded_file";
                }
                string targetFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", fileName);

                if (File.Exists(targetFilePath))
                {
                    string directory = Path.GetDirectoryName(targetFilePath) ?? "";
                    string nameWithoutExtension = Path.GetFileNameWithoutExtension(targetFilePath);
                    string extension = Path.GetExtension(targetFilePath);
                    int counter = 1;

                    do
                    {
                        string newFileName = $"{nameWithoutExtension}_{counter}{extension}";
                        targetFilePath = Path.Combine(directory, newFileName);
                        counter++;
                    } while (File.Exists(targetFilePath));
                }

                _currentJob = new DownloadJob(url, targetFilePath, chunkCount);
                _isStopped = false;
            }

            _isPaused = false;
            UpdateButtonStates(isDownloading: true);
            StatusTextBlock.Text = isResume ? $"Resuming download to {_currentJob?.TargetFilePath}..." : $"Downloading to {_currentJob?.TargetFilePath}...";

            if (_currentJob != null)
            {
                _previousBytesDownloaded = _currentJob.TotalBytesDownloaded;
            }
            else
            {
                _previousBytesDownloaded = 0;
            }
            _speedTimer.Start();

            _cts = new CancellationTokenSource();

            try
            {
                if (!isResume)
                {
                    if (_currentJob != null)
                    {
                        await _currentJob.InitializeAsync();

                        ProgressBarsPanel.Children.Clear();
                        _chunkProgressBars.Clear();
                        _chunkTextBlocks.Clear();

                        // If the server doesn't support range requests, chunkCount is adjusted in InitializeAsync
                        int actualChunkCount = _currentJob.ChunkCount;

                        for (int i = 0; i < actualChunkCount; i++)
                        {
                            var chunkPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
                            var chunkTextBlock = new TextBlock { Text = $"Chunk {i + 1}: 0.00%", Style = (Style)Application.Current.Resources["BodyTextBlockStyle"] };
                            var chunkProgressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

                            chunkPanel.Children.Add(chunkTextBlock);
                            chunkPanel.Children.Add(chunkProgressBar);
                            ProgressBarsPanel.Children.Add(chunkPanel);

                            _chunkProgressBars.Add(chunkProgressBar);
                            _chunkTextBlocks.Add(chunkTextBlock);
                        }
                    }
                }

                var chunkProgresses = new List<IProgress<double>>();
                for (int i = 0; i < (_currentJob?.ChunkCount ?? 0); i++)
                {
                    int chunkIndex = i; // capture loop variable
                    var chunkProgress = new Progress<double>(percent =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (chunkIndex < _chunkProgressBars.Count)
                            {
                                _chunkProgressBars[chunkIndex].Value = percent;
                                _chunkTextBlocks[chunkIndex].Text = $"Chunk {chunkIndex + 1}: {percent:F2}%";
                            }
                        });
                    });
                    chunkProgresses.Add(chunkProgress);
                }

                var globalProgress = new Progress<double>(percent =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusTextBlock.Text = $"Total Downloading: {percent:F2}%";
                    });
                });

                if (_currentJob != null)
                {
                    await _currentJob.StartDownloadAsync(globalProgress, chunkProgresses, _cts.Token);
                }

                _speedTimer.Stop();
                SpeedTextBlock.Text = "Send: 0 kbps | Receive: 0 kbps";
                StatusTextBlock.Text = "Merged Successfully";
                _currentJob = null;
                return true;
            }
            catch (OperationCanceledException)
            {
                if (_isStopped)
                {
                    CleanupPartFiles();
                    StatusTextBlock.Text = "Download Stopped.";
                    _currentJob = null;
                    return false;
                }
                else if (_isPaused)
                {
                    StatusTextBlock.Text = "Download Paused.";
                    return false;
                }
                else
                {
                    StatusTextBlock.Text = "Download Cancelled.";
                    _currentJob = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
                _currentJob = null;
                return false;
            }
            finally
            {
                _speedTimer.Stop();
                SpeedTextBlock.Text = "Send: 0 kbps | Receive: 0 kbps";
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _isPaused = true;
                _cts.Cancel();
            }
        }

        private async void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentJob != null && _isPaused)
            {
                bool success = await StartOrResumeDownload(true);
                if (!success)
                {
                    if (_isPaused) UpdateButtonStates(isPaused: true);
                    else UpdateButtonStates(isIdle: true);
                }
                else if (!_isPaused && !_isStopped)
                {
                    await ProcessQueueAsync();
                }
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _downloadQueue.Clear();
            _isStopped = true;

            if (_cts != null && !_cts.IsCancellationRequested)
            {
                // Note: The cancellation exception handler will handle the cleanup and UI reset for the stopped download.
                _cts.Cancel();
            }
            else
            {
                // If it was already paused or not actively downloading
                CleanupPartFiles();
                StatusTextBlock.Text = "Download Stopped.";
                _currentJob = null;
                ProgressBarsPanel.Children.Clear();
                _chunkProgressBars.Clear();
                _chunkTextBlocks.Clear();
                UpdateButtonStates(isIdle: true);
            }

            // If actively downloading, we wait for the try/catch block to clear the UI after lock release.
            // But we can eagerly clear UI elements here if we want. Let's just do it here to make it responsive.
            ProgressBarsPanel.Children.Clear();
            _chunkProgressBars.Clear();
            _chunkTextBlocks.Clear();
        }

        private void CleanupPartFiles()
        {
            if (_currentJob == null) return;

            for (int i = 0; i < _currentJob.ChunkCount; i++)
            {
                string partFilePath = $"{_currentJob.TargetFilePath}.part{i}";
                if (File.Exists(partFilePath))
                {
                    try
                    {
                        File.Delete(partFilePath);
                    }
                    catch
                    {
                        // Ignore cleanup errors during stop
                    }
                }
            }
        }
    }
}
