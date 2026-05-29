using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WinWPF.DS4Forms;

public partial class LightbarMacroCreator : Window
{
    private OutBinding OutBinding { get; }

    public LightbarMacroCreator(OutBinding outBinding)
    {
        InitializeComponent();
        DataContext = outBinding;
        OutBinding = outBinding;

        TriggerCombo.ItemsSource = Enum.GetValues<LightbarMacroTrigger>();
    }

    private void SelectColor_Click(object sender, RoutedEventArgs e)
    {
        ColorPickerWindow dialog = new()
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
        OutBinding.CurrentColor = dialog.colorPicker.SelectedColor.GetValueOrDefault();
    }

    private void AddColor_OnClick(object sender, RoutedEventArgs e)
    {
        var color = new DS4Color
        {
            red = OutBinding.CurrentColor.R, green = OutBinding.CurrentColor.G,
            blue = OutBinding.CurrentColor.B
        };
        OutBinding.LightbarMacro.Macro.Add(new LightbarMacroElement(color, OutBinding.CurrentInterval));
    }

    private void DeleteColor_OnClick(object sender, RoutedEventArgs e)
    {
        // TODO show a popup window saying you must select an item to delete
        if (MacroListBox.SelectedItems.Count == 0) return;
        OutBinding.LightbarMacro.Macro.Remove((LightbarMacroElement)MacroListBox.SelectedItems[0]);
    }

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        OutBinding.LightbarMacro.Macro.Clear();
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}