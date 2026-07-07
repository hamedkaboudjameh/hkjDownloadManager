using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager
{
    public sealed partial class MainPage : Page
    {
        private CancellationTokenSource _cts;

        public MainPage()
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

            // In Uno, the Value property of NumberBox is double, so we cast to int
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
                // UI updates must be on the UI thread.
                // In WinUI/Uno, they usually marshal back, but to be safe:
                DispatcherQueue.TryEnqueue(() => 
                {
                    DownloadProgressBar.Value = percent;
                    StatusTextBlock.Text = $"Downloading: {percent:F2}%";
                });
            });

            var job = new DownloadJob(url, targetFilePath, chunkCount);

            try
            {
                await job.InitializeAsync();


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
