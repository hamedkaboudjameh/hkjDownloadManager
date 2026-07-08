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
            ProgressBarsPanel.Children.Clear();

            _cts = new CancellationTokenSource();

            var job = new DownloadJob(url, targetFilePath, chunkCount);

            try
            {
                await job.InitializeAsync();

                var chunkProgresses = new List<IProgress<double>>();

                // If the server doesn't support range requests, chunkCount is adjusted in InitializeAsync
                int actualChunkCount = job.ChunkCount;

                for (int i = 0; i < actualChunkCount; i++)
                {
                    var chunkPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
                    var chunkTextBlock = new TextBlock { Text = $"Chunk {i + 1}: 0.00%", Style = (Style)Application.Current.Resources["BodyTextBlockStyle"] };
                    var chunkProgressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

                    chunkPanel.Children.Add(chunkTextBlock);
                    chunkPanel.Children.Add(chunkProgressBar);
                    ProgressBarsPanel.Children.Add(chunkPanel);

                    int chunkIndex = i; // capture loop variable
                    var chunkProgress = new Progress<double>(percent =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            chunkProgressBar.Value = percent;
                            chunkTextBlock.Text = $"Chunk {chunkIndex + 1}: {percent:F2}%";
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


                await job.StartDownloadAsync(globalProgress, chunkProgresses, _cts.Token);

                StatusTextBlock.Text = "Merged Successfully";
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
