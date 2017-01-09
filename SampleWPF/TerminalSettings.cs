using System.ComponentModel;
using TerminalDemo.Properties;

namespace TerminalDemo
{
    public class TerminalSettings : INotifyPropertyChanged
    {
        private string host;


        private int hostPort;


        private string terminalType;


        private bool useSSL;


        public TerminalSettings()
        {
            LoadFromSettings(Settings.Default);
        }

        public string Host
        {
            get { return host; }
            set
            {
                host = value;
                OnPropertyChanged("Host");
            }
        }

        public int HostPort
        {
            get { return hostPort; }
            set
            {
                hostPort = value;
                OnPropertyChanged("HostPort");
            }
        }

        public bool UseSSL
        {
            get { return useSSL; }
            set
            {
                useSSL = value;
                OnPropertyChanged("UseSSL");
            }
        }

        public string TerminalType
        {
            get { return terminalType; }
            set
            {
                terminalType = value;
                OnPropertyChanged("TerminalType");
            }
        }


        internal void LoadFromSettings(Settings settings)
        {
            Host = Settings.Default.Hostname;
            HostPort = Settings.Default.HostPort;
            TerminalType = Settings.Default.TerminalType;
            UseSSL = Settings.Default.UseSSL;
        }

        internal void SaveToSettings(Settings settings)
        {
            Settings.Default.Hostname = Host;
            Settings.Default.HostPort = HostPort;
            Settings.Default.TerminalType = TerminalType;
            Settings.Default.UseSSL = UseSSL;

            Settings.Default.Save();
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