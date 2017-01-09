namespace StEn.Open3270.TN3270E.X3270
{
    internal class EventNotification
    {
        private readonly object[] data;
        public string error;

        public EventNotification(string error, object[] data)
        {
            this.error = error;
            this.data = data;
        }

        public override string ToString()
        {
            return TraceFormatter.Format(error, data);
        }
    }
}