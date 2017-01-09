namespace StEn.Open3270.TN3270E
{
    internal enum ConnectionState
    {
        /// <summary>
        ///     no socket, unknown mode
        /// </summary>
        NotConnected = 0,

        /// <summary>
        ///     resolving hostname
        /// </summary>
        Resolving,

        /// <summary>
        ///     connection pending
        /// </summary>
        Pending,

        /// <summary>
        ///     connected, no mode yet
        /// </summary>
        ConnectedInitial,

        /// <summary>
        ///     connected in NVT ANSI mode
        /// </summary>
        ConnectedANSI,

        /// <summary>
        ///     connected in old-style 3270 mode
        /// </summary>
        Connected3270,

        /// <summary>
        ///     connected in TN3270E mode, unnegotiated
        /// </summary>
        ConnectedInitial3270E,

        /// <summary>
        ///     connected in TN3270E mode, NVT mode
        /// </summary>
        ConnectedNVT,

        /// <summary>
        ///     connected in TN3270E mode, SSCP-LU mode
        /// </summary>
        ConnectedSSCP,

        /// <summary>
        ///     connected in TN3270E mode, 3270 mode
        /// </summary>
        Connected3270E
    }
}