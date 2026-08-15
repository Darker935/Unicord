using System;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Pages.Settings
{
    /// <summary>
    /// Diagnostics for sideloaded builds. Gives a way to get an ETW trace onto the disk without
    /// closing Unicord, which is otherwise the only point at which one is written.
    /// </summary>
    public sealed partial class DebugSettingsPage : Page
    {
        public DebugSettingsPage()
        {
            this.InitializeComponent();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await UpdateLogsInfoAsync();
        }

        private async void SaveLogsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLogsButton.IsEnabled = false;

            try
            {
                var file = await Logger.SaveNowAsync();

                // Show where it landed, not just the name. A saved trace and the session's live
                // in-progress file look alike, and "saved" is not obviously true without the folder.
                ShowStatus(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success, "Trace saved", DescribeLocation(file));
            }
            catch (Exception ex)
            {
                ShowStatus(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, "Could not save the trace", ex.Message);
            }
            finally
            {
                SaveLogsButton.IsEnabled = true;
                await UpdateLogsInfoAsync();
            }
        }

        private async void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = await Logger.GetLogsFolderAsync();
                await Launcher.LaunchFolderAsync(folder);
            }
            catch (Exception ex)
            {
                ShowStatus(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, "Could not open the folder", ex.Message);
            }
        }

        private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog()
            {
                Title = "Clear saved logs?",
                Content = "Every trace already written to disk will be deleted. This cannot be undone.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            ClearLogsButton.IsEnabled = false;

            try
            {
                var deleted = await Logger.ClearLogsAsync();
                ShowStatus(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success, "Logs cleared", $"Deleted {deleted} file(s).");
            }
            catch (Exception ex)
            {
                ShowStatus(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, "Could not clear the logs", ex.Message);
            }
            finally
            {
                ClearLogsButton.IsEnabled = true;
                await UpdateLogsInfoAsync();
            }
        }

        private async Task UpdateLogsInfoAsync()
        {
            try
            {
                var (count, size) = await Logger.GetLogsInfoAsync();
                LogsFolderBlock.Description = $"{count} file(s), {FormatSize(size)} on disk.";
            }
            catch (Exception ex)
            {
                LogsFolderBlock.Description = ex.Message;
            }
        }

        private static string DescribeLocation(Windows.Storage.StorageFile file)
        {
            const string Anchor = @"\LocalState\";

            var path = file.Path ?? file.Name;
            var index = path.IndexOf(Anchor, StringComparison.OrdinalIgnoreCase);

            return index < 0 ? path : path.Substring(index + Anchor.Length);
        }

        private static string FormatSize(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };

            double size = bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.#} {units[unit]}";
        }

        private void ShowStatus(Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string title, string message)
        {
            StatusBar.Severity = severity;
            StatusBar.Title = title;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        }
    }
}
