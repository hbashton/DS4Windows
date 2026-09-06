using System;
using System.Windows;
using System.Windows.Threading;
using DS4WinWPF.DS4Forms.ViewModels;
using DS4Windows.Switch2;

namespace DS4WinWPF.DS4Forms;

public partial class Switch2StickCalibrationWindow : Window
{
    private readonly Switch2StickCalibrationViewModel viewModel;
    private readonly DispatcherTimer progressTimer;

    internal Switch2StickCalibrationWindow(Switch2RuntimeInputDevice runtime)
    {
        viewModel = new Switch2StickCalibrationViewModel(runtime);
        InitializeComponent();
        DataContext = viewModel;
        progressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        progressTimer.Tick += Progress_Tick;
        Loaded += (_, _) => { viewModel.Poll(); progressTimer.Start(); };
        Closed += (_, _) =>
        {
            progressTimer.Stop();
            progressTimer.Tick -= Progress_Tick;
            viewModel.Close();
        };
    }

    private void Progress_Tick(object sender, EventArgs e) => viewModel.Poll();
    private async void Start_Click(object sender, RoutedEventArgs e) => await viewModel.StartAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await viewModel.SaveAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => viewModel.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.CanStart && MessageBox.Show(this, viewModel.ResetConfirmation,
                "Reset PC stick calibration", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes)
            await viewModel.ResetAsync();
    }
}
