using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using StEn.Open3270.Engine;
using StEn.Open3270.TN3270E;
using TerminalDemo.Properties;

namespace TerminalDemo
{
    public class Terminal : INotifyPropertyChanged
    {
        private int caretIndex;
        private readonly TNEmulator emu = new TNEmulator();
        private bool isConnected;
        private bool isConnecting;
        private string screenText;

        public Terminal()
        {
            emu = new TNEmulator();
            emu.Disconnected += emu_Disconnected;
            emu.CursorLocationChanged += emu_CursorLocationChanged;
        }

        public bool IsConnecting
        {
            get { return isConnecting; }
            set
            {
                isConnecting = value;
                OnPropertyChanged("IsConnecting");
            }
        }


        /// <summary>
        ///     Indicates when the terminal is connected to the host.
        /// </summary>
        public bool IsConnected
        {
            get { return isConnected; }
            set
            {
                isConnected = value;
                OnPropertyChanged("IsConnected");
            }
        }


        /// <summary>
        ///     This is the text buffer to display.
        /// </summary>
        public string ScreenText
        {
            get { return screenText; }
            set
            {
                screenText = value;
                OnPropertyChanged("ScreenText");
            }
        }

        public int CaretIndex
        {
            get { return caretIndex; }
            set
            {
                caretIndex = value;
                OnPropertyChanged("CaretIndex");
            }
        }


        private void emu_Disconnected(TNEmulator where, string Reason)
        {
            IsConnected = false;
            IsConnecting = false;
            ScreenText = Reason;
        }

        public void Connect()
        {
            emu.Config.FastScreenMode = true;

            //Retrieve host settings
            emu.Config.HostName = Settings.Default.Hostname;
            emu.Config.HostPort = Settings.Default.HostPort;
            emu.Config.TermType = Settings.Default.TerminalType;
            emu.Config.UseSSL = Settings.Default.UseSSL;

            //Begin the connection process asynchomously
            IsConnecting = true;
            Task.Factory.StartNew(ConnectToHost).ContinueWith(t =>
            {
                //Update the display when we are finished connecting
                IsConnecting = false;
                IsConnected = emu.IsConnected;
                ScreenText = emu.CurrentScreenXML.Dump();
            });
        }

        private void ConnectToHost()
        {
            emu.Connect();

            //Account for delays
            emu.Refresh(true, 1000);
        }


        /// <summary>
        ///     Sends text to the terminal.
        ///     This is used for typical alphanumeric text entry.
        /// </summary>
        /// <param name="text">The text to send</param>
        internal void SendText(string text)
        {
            emu.SetText(text);
            ScreenText = emu.CurrentScreenXML.Dump();
        }


        /// <summary>
        ///     Sends a character to the terminal.
        ///     This is used for special characters like F1, Tab, et cetera.
        /// </summary>
        /// <param name="key">The key to send.</param>
        public void SendKey(TnKey key)
        {
            emu.SendKey(true, key, 2000);
            if (key != TnKey.Tab && key != TnKey.BackTab)
            {
                Refresh();
            }
        }

        /// <summary>
        ///     Forces a refresh and updates the screen display
        /// </summary>
        public void Refresh()
        {
            Refresh(100);
        }


        /// <summary>
        ///     Forces a refresh and updates the screen display
        /// </summary>
        /// <param name="screenCheckInterval">
        ///     This is the speed in milliseconds at which the library will poll
        ///     to determine if we have a valid screen of data to display.
        /// </param>
        public void Refresh(int screenCheckInterval)
        {
            //This line keeps checking to see when we've received a valid screen of data from the mainframe.
            //It loops until the TNEmulator.Refresh() method indicates that waiting for the screen did not time out.
            //This helps prevent blank screens, etc.
            while (!emu.Refresh(true, screenCheckInterval))
            {
            }

            ScreenText = emu.CurrentScreenXML.Dump();
            UpdateCaretIndex();
        }


        public void UpdateCaretIndex()
        {
            CaretIndex = emu.CursorY*81 + emu.CursorX;
        }

        private void emu_CursorLocationChanged(object sender, EventArgs e)
        {
            UpdateCaretIndex();
        }

        /// <summary>
        ///     Sends field information to the debug console.
        ///     This can be used to define well-known field positions in your application.
        /// </summary>
        internal void DumpFillableFields()
        {
            var output = string.Empty;

            XMLScreenField field;

            for (var i = 0; i < emu.CurrentScreenXML.Fields.Length; i++)
            {
                field = emu.CurrentScreenXML.Fields[i];
                if (!field.Attributes.Protected)
                {
                    Debug.WriteLine("public static int fieldName = {0};   //{1},{2}  Length:{3}   {4}", i,
                        field.Location.top + 1, field.Location.left + 1, field.Location.length, field.Text);
                }
            }
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        #endregion INotifyPropertyChanged
    }
}