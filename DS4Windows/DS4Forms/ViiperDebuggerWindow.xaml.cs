/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using DS4Windows;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace DS4WinWPF.DS4Forms
{
    public partial class ViiperDebuggerWindow : Window
    {
        private readonly ViiperBackendDebugger debugger;
        private bool running;

        public ViiperDebuggerWindow()
        {
            InitializeComponent();
            debugger = new ViiperBackendDebugger(AppendLogLine);
            AppendLogLine("VIIPER debugger ready. Turn on Settings > Verbose logging before running probes.");
        }

        private void RunAllBtn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.All);

        private void PrereqBtn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.Prerequisites);

        private void XboxBtn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.Xbox360);

        private void Ds4Btn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.DualShock4);

        private void DualSenseBtn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.DualSense);

        private void DualSenseEdgeBtn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.DualSenseEdge);

        private void Switch2Btn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.Switch2Pro);

        private void AdaptiveBtn_Click(object sender, RoutedEventArgs e) => RunProbe(ViiperDebugTest.AdaptiveTriggers);

        private async void RunProbe(ViiperDebugTest test)
        {
            if (running)
            {
                AppendLogLine("A debugger probe is already running. Wait for it to finish before starting another.");
                return;
            }

            SetRunning(true, test);
            try
            {
                await debugger.RunAsync(test);
            }
            catch (Exception ex)
            {
                AppendLogLine($"VIIPER DEBUG WINDOW EXCEPTION: {ex}");
            }
            finally
            {
                SetRunning(false, test);
            }
        }

        private void SetRunning(bool value, ViiperDebugTest test)
        {
            running = value;
            runAllBtn.IsEnabled = !value;
            prereqBtn.IsEnabled = !value;
            xboxBtn.IsEnabled = !value;
            ds4Btn.IsEnabled = !value;
            dualSenseBtn.IsEnabled = !value;
            dualSenseEdgeBtn.IsEnabled = !value;
            switch2Btn.IsEnabled = !value;
            adaptiveBtn.IsEnabled = !value;
            closeBtn.IsEnabled = !value;
            statusText.Text = value ? $"Running {test}..." : "Ready";
        }

        private void AppendLogLine(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                outputText.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
                outputText.ScrollToEnd();
            }));
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
