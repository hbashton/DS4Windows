using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace DS4WinWPF.DS4Forms
{
    public partial class ViiperSetupProgress : Window
    {
        private static readonly Regex LogPrefix = new Regex(
            @"^\[[^\]]+\]\s*", RegexOptions.Compiled);

        private readonly string logPath;
        private readonly DispatcherTimer logTimer;
        private long logOffset;
        private bool allowClose;

        public ViiperSetupProgress(string logPath)
        {
            this.logPath = logPath;
            InitializeComponent();
            logOffset = GetLogLength();
            logTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background, ReadNewLogMessages,
                Dispatcher);
        }

        public void ShowPreparing()
        {
            phaseText.Text = "Verifying the DS4Windows package...";
            Show();
            logTimer.Start();
            // Render the window before the protected package snapshot begins.
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        public void SetPhase(string message)
        {
            if (Dispatcher.CheckAccess())
            {
                phaseText.Text = message;
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => phaseText.Text = message));
        }

        public int WaitForProcess(Process process)
        {
            if (!process.HasExited)
            {
                DispatcherFrame frame = new DispatcherFrame();
                EventHandler exited = null;
                exited = (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    frame.Continue = false;
                }));
                process.EnableRaisingEvents = true;
                process.Exited += exited;
                try
                {
                    if (!process.HasExited)
                    {
                        Dispatcher.PushFrame(frame);
                    }
                }
                finally
                {
                    process.Exited -= exited;
                }
            }

            process.WaitForExit();
            return process.ExitCode;
        }

        public void Finish(bool success)
        {
            ReadNewLogMessages(null, EventArgs.Empty);
            logTimer.Stop();
            setupProgress.IsIndeterminate = false;
            setupProgress.Value = success ? 100 : 0;
            statusGlyph.Text = success ? "\u2713" : "!";
            headingText.Text = success
                ? "VIIPER is ready"
                : "VIIPER setup needs attention";
            phaseText.Text = success
                ? "Setup completed successfully."
                : "Setup stopped safely before verification completed.";
            detailText.Text = success
                ? "DS4Windows will continue automatically."
                : "A detailed error and the diagnostic log will appear next.";
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!allowClose)
            {
                e.Cancel = true;
                return;
            }

            logTimer.Stop();
            base.OnClosing(e);
        }

        private void ReadNewLogMessages(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    return;
                }

                using FileStream stream = new FileStream(logPath,
                    FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < logOffset)
                {
                    logOffset = 0;
                }
                if (stream.Length == logOffset)
                {
                    return;
                }

                stream.Position = logOffset;
                using StreamReader reader = new StreamReader(stream,
                    Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096, leaveOpen: true);
                string text = reader.ReadToEnd();
                logOffset = stream.Length;
                string[] lines = text.Split(new[] { "\r\n", "\n" },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int index = lines.Length - 1; index >= 0; index--)
                {
                    if (!lines[index].StartsWith("[",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string message = LogPrefix.Replace(lines[index], "").Trim();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        phaseText.Text = message;
                        break;
                    }
                }
            }
            catch
            {
                // Progress text is best-effort; the persistent setup log and
                // transaction result remain authoritative.
            }
        }

        private long GetLogLength()
        {
            try
            {
                return File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
