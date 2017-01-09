using System.Windows;
using TerminalDemo.Properties;

namespace TerminalDemo
{
    /// <summary>
    ///     Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            (mainGrid.DataContext as TerminalSettings).SaveToSettings(Settings.Default);
            Close();
        }
    }
}