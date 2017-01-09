using System;
using System.Drawing;
using System.Windows.Forms;
using StEn.Open3270.Engine;
using StEn.Open3270.TN3270E;

namespace SampleForm
{
    public class OpenEmulator : RichTextBox
    {
        private readonly TNEmulator _tn3270 = new TNEmulator();
        private bool _isRedrawing;

        public void Connect(string server, int port, string type, bool useSsl)
        {
            _tn3270.Config.UseSSL = useSsl;
            _tn3270.Config.TermType = type;
            _tn3270.Connect(server, port, string.Empty);

            Redraw();
        }

        public void Redraw()
        {
            var render = new RichTextBox {Text = _tn3270.CurrentScreenXML.Dump()};

            Clear();
            var fnt = new Font("Consolas", 10);
            render.Font = new Font(fnt, FontStyle.Regular);

            _isRedrawing = true;
            render.SelectAll();

            if (_tn3270.CurrentScreenXML.Fields == null)
            {
                var clr = Color.Lime;
                render.SelectionProtected = false;
                render.SelectionColor = clr;
                render.DeselectAll();

                for (var i = 0; i < render.Text.Length; i++)
                {
                    render.Select(i, 1);
                    if (render.SelectedText != " " && render.SelectedText != "\n")
                        render.SelectionColor = Color.Lime;
                }
                return;
            }

            render.SelectionProtected = true;
            foreach (var field in _tn3270.CurrentScreenXML.Fields)
            {
                //if (string.IsNullOrEmpty(field.Text))
                //    continue;

                Application.DoEvents();
                var clr = Color.Lime;
                if (field.Attributes.FieldType == "High" && field.Attributes.Protected)
                    clr = Color.White;
                else if (field.Attributes.FieldType == "High")
                    clr = Color.Red;
                else if (field.Attributes.Protected)
                    clr = Color.RoyalBlue;

                render.Select(field.Location.position + field.Location.top, field.Location.length);
                render.SelectionProtected = false;
                render.SelectionColor = clr;
                if (clr == Color.White || clr == Color.RoyalBlue)
                    render.SelectionProtected = true;
            }

            for (var i = 0; i < render.Text.Length; i++)
            {
                render.Select(i, 1);
                if (render.SelectedText != " " && render.SelectedText != "\n" && render.SelectionColor == Color.Black)
                {
                    render.SelectionProtected = false;
                    render.SelectionColor = Color.Lime;
                }
            }

            Rtf = render.Rtf;

            _isRedrawing = false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Back)
            {
                SelectionStart--;
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Tab)
            {
                //TN3270.SetCursor();
                e.Handled = true;
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {
                _tn3270.SendKey(true, TnKey.Enter, 1000);
                Redraw();
                e.Handled = true;
                return;
            }
            if (e.KeyChar == '\b')
                return;
            if (e.KeyChar == '\t')
                return;

            _tn3270.SetText(e.KeyChar.ToString());
            base.OnKeyPress(e);
        }

        protected override void OnSelectionChanged(EventArgs e)
        {
            if (_tn3270.IsConnected)
            {
                base.OnSelectionChanged(e);
                if (!_isRedrawing)
                {
                    int i = SelectionStart, y = 0;
                    while (i >= 81)
                    {
                        y++;
                        i -= 81;
                    }
                    var x = i;

                    _tn3270.SetCursor(x, y);
                }
            }
        }
    }
}