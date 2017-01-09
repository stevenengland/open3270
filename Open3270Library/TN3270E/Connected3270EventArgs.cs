using System;

namespace StEn.Open3270.TN3270E
{
    public class Connected3270EventArgs : EventArgs
    {
        public Connected3270EventArgs(bool is3270)
        {
            Is3270 = is3270;
        }

        public bool Is3270 { get; }
    }
}