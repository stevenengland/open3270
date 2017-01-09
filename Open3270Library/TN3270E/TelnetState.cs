namespace StEn.Open3270.TN3270E
{
    internal enum TelnetState
    {
        /// <summary>
        ///     receiving data
        /// </summary>
        Data,

        /// <summary>
        ///     got an IAC
        /// </summary>
        IAC,

        /// <summary>
        ///     got an IAC WILL
        /// </summary>
        Will,

        /// <summary>
        ///     got an IAC WONT
        /// </summary>
        Wont,

        /// <summary>
        ///     got an IAC DO
        /// </summary>
        Do,

        /// <summary>
        ///     got an IAC DONT
        /// </summary>
        Dont,

        /// <summary>
        ///     got an IAC SB
        /// </summary>
        SB,

        /// <summary>
        ///     got an IAC after an IAC SB
        /// </summary>
        SbIac
    }
}