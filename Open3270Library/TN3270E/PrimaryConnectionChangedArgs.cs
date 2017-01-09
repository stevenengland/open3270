using System;

namespace StEn.Open3270.TN3270E
{
    public class PrimaryConnectionChangedArgs : EventArgs
    {
        public PrimaryConnectionChangedArgs(bool success)
        {
            Success = success;
        }

        public bool Success { get; }
    }
}