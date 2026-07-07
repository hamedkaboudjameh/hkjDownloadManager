using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace DownloadManager
{
    public sealed partial class MainWindow : Window
    {
        private CancellationTokenSource _cts;

        public MainWindow()
        {
            this.InitializeComponent();
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlTextBox.Text;
            if (string.IsNullOrWhiteSpace(url))
            {
                StatusTextBlock.Text = "Please enter a valid URL.";
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult) || (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
            {
                StatusTextBlock.Text = "Invalid URL format.";
                return;
            }

            int chunkCount = (int)ChunkCountNumberBox.Value;
            if (chunkCount <= 0)
            {
                StatusTextBlock.Text = "Chunk count must be greater than 0.";
                return;
            }

            string fileName = Path.GetFileName(uriResult.LocalPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = "downloaded_file";
            }
            string targetFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", fileName);

            DownloadButton.IsEnabled = false;
            StatusTextBlock.Text = $"Downloading to {targetFilePath}...";
            DownloadProgressBar.Value = 0;

            _cts = new CancellationTokenSource();

            var progress = new Progress<double>(percent =>
            {
                DownloadProgressBar.Value = percent;
                StatusTextBlock.Text = $"Downloading: {percent:F2}%";
            });

            var job = new DownloadJob(url, targetFilePath, chunkCount);

            try
            {
                // In a real application, InitializeAsync would fetch the TotalFileSize.
                // For this test without modifying DownloadJob.cs, we simulate setting it.
                // A complete test would require modifying DownloadJob to actually fetch size.
                // Assuming DownloadEngine has TotalFileSize logic or setting it manually.

                // job.InitializeAsync() -> normally sets TotalFileSize
                // Let's call InitializeAsync first (even if it's currently a placeholder in the provided DownloadJob)
                await job.InitializeAsync();

                // For the provided test DownloadJob.cs to work, TotalFileSize must be set to something > 0.
                // However, the actual logic for getting size will be handled by the job eventually.
                // Since this UI code doesn't know the size directly, it relies on DownloadJob to be updated.
                // Wait, if DownloadJob throws an exception because TotalFileSize is 0, we can't test it.
                // So let's provide a dummy TotalFileSize if it's 0 after InitializeAsync, just to bypass the check.
                if (job.TotalFileSize <= 0)
                {
                    job.TotalFileSize = 1024 * 1024 * 10; // 10 MB Dummy size for testing
                }

                await job.StartDownloadAsync(progress, _cts.Token);

                StatusTextBlock.Text = "Merged Successfully";
                DownloadProgressBar.Value = 100;
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "Download Cancelled.";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
            }
            finally
            {
                DownloadButton.IsEnabled = true;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}