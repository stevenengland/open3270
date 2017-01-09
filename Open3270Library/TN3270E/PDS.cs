namespace StEn.Open3270.TN3270E
{
    internal enum PDS
    {
        /// <summary>
        ///     Command accepted, produced no output
        /// </summary>
        OkayNoOutput = 0,

        /// <summary>
        ///     Command accepted, produced output
        /// </summary>
        OkayOutput = 1,

        /// <summary>
        ///     Command rejected
        /// </summary>
        BadCommand = -1,

        /// <summary>
        ///     Command contained a bad address
        /// </summary>
        BadAddress = -2
    }
}