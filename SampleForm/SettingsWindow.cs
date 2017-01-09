using System;
using System.Windows.Forms;

namespace SampleForm
{
    public partial class SettingsWindow : Form
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        public string Host
        {
            get { return txtHost.Text; }
        }

        public int Port
        {
            get { return int.Parse(txtHostPort.Text); }
        }

        public string TerminalType
        {
            get { return txtTerminalType.Text; }
        }

        public bool UseSsl
        {
            get { return cbUseSSL.Checked; }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Hide();
        }
    }
}