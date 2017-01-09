using System.Windows;
using System.Windows.Input;
using StEn.Open3270.TN3270E;

namespace TerminalDemo
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            //This odd event handler is needed because the TextBox control eats that spacebar, so we have to intercept an already-handled event.
            Console.AddHandler(KeyDownEvent, new KeyEventHandler(Console_KeyDown), true);
        }

        public Terminal Terminal
        {
            get { return Resources["term"] as Terminal; }
        }


        private void Window_TextInput(object sender, TextCompositionEventArgs e)
        {
            if (Terminal.IsConnected)
            {
                Terminal.SendText(e.Text);
            }
        }


        private void Console_KeyDown(object sender, KeyEventArgs e)
        {
            //The textbox eats several keystrokes, so we can't handle them from keybindings/commands.
            if (Terminal.IsConnected)
            {
                switch (e.Key)
                {
                    case Key.Space:
                    {
                        Terminal.SendText(" ");
                        break;
                    }
                    case Key.Left:
                    {
                        Terminal.SendKey(TnKey.Left);
                        break;
                    }
                    case Key.Right:
                    {
                        Terminal.SendKey(TnKey.Right);
                        break;
                    }
                    case Key.Up:
                    {
                        Terminal.SendKey(TnKey.Up);
                        break;
                    }
                    case Key.Down:
                    {
                        Terminal.SendKey(TnKey.Down);
                        break;
                    }
                    case Key.Back:
                    {
                        Terminal.SendKey(TnKey.Backspace);
                        break;
                    }
                    case Key.Delete:
                    {
                        Terminal.SendKey(TnKey.Delete);
                        break;
                    }
                    default:
                        break;
                }
            }
        }


        //This command isn't used in the demo, but can be used when you want to send some predefined text.

        #region SendText Command

        public static RoutedUICommand SendText = new RoutedUICommand();

        private void CanExecuteSendText(object sender, CanExecuteRoutedEventArgs args)
        {
            if (true)
            {
                args.CanExecute = true;
            }
        }


        private void ExecuteSendText(object sender, ExecutedRoutedEventArgs args)
        {
        }

        #endregion SendText Command

        #region SendCommand Command

        public static RoutedUICommand SendCommand = new RoutedUICommand();

        private void CanExecuteSendCommand(object sender, CanExecuteRoutedEventArgs args)
        {
            args.CanExecute = Terminal.IsConnected;
        }


        private void ExecuteSendCommand(object sender, ExecutedRoutedEventArgs args)
        {
            Terminal.SendKey((TnKey) args.Parameter);
        }

        #endregion SendCommand Command

        #region Connect Command

        public static RoutedUICommand Connect = new RoutedUICommand();

        private void CanExecuteConnect(object sender, CanExecuteRoutedEventArgs args)
        {
            args.CanExecute = !Terminal.IsConnected && !Terminal.IsConnecting;
        }


        private void ExecuteConnect(object sender, ExecutedRoutedEventArgs args)
        {
            Terminal.Connect();

            //The caret won't show up in the textbox until it receives focus.
            Console.Focus();
        }

        #endregion Connect Command

        #region Refresh Command

        public static RoutedUICommand Refresh = new RoutedUICommand();

        private void CanExecuteRefresh(object sender, CanExecuteRoutedEventArgs args)
        {
            args.CanExecute = Terminal.IsConnected;
        }


        private void ExecuteRefresh(object sender, ExecutedRoutedEventArgs args)
        {
            Terminal.Refresh();
        }

        #endregion Refresh Command

        #region DumpFields Command

        public static RoutedUICommand DumpFields = new RoutedUICommand();

        private void CanExecuteDumpFields(object sender, CanExecuteRoutedEventArgs args)
        {
            args.CanExecute = Terminal.IsConnected;
        }


        private void ExecuteDumpFields(object sender, ExecutedRoutedEventArgs args)
        {
            Terminal.DumpFillableFields();
        }

        #endregion DumpFields Command

        #region OpenSettings Command

        public static RoutedUICommand OpenSettings = new RoutedUICommand();

        private void CanExecuteOpenSettings(object sender, CanExecuteRoutedEventArgs args)
        {
            args.CanExecute = true;
        }


        private void ExecuteOpenSettings(object sender, ExecutedRoutedEventArgs args)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.ShowDialog();
        }

        #endregion OpenSettings Command
    }
}