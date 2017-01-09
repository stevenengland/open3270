namespace StEn.Open3270.TN3270E
{
    internal enum SmsState
    {
        /// <summary>
        ///     no command active (scripts only)
        /// </summary>
        Idle,

        /// <summary>
        ///     command(s) buffered and ready to run
        /// </summary>
        Incomplete,

        /// <summary>
        ///     command executing
        /// </summary>
        Running,

        /// <summary>
        ///     command awaiting keyboard unlock
        /// </summary>
        KBWait,

        /// <summary>
        ///     command awaiting connection to complete
        /// </summary>
        ConnectWait,

        /// <summary>
        ///     stopped in PauseScript action
        /// </summary>
        Paused,

        /// <summary>
        ///     awaiting completion of Wait(ansi)
        /// </summary>
        WaitAnsi,

        /// <summary>
        ///     awaiting completion of Wait(3270)
        /// </summary>
        Wait3270,

        /// <summary>
        ///     awaiting completion of Wait(Output)
        /// </summary>
        WaitOutput,

        /// <summary>
        ///     awaiting completion of Snap(Wait)
        /// </summary>
        SnapWaitOutput,

        /// <summary>
        ///     awaiting completion of Wait(Disconnect)
        /// </summary>
        WaitDisconnect,

        /// <summary>
        ///     awaiting completion of Wait()
        /// </summary>
        Wait,

        /// <summary>
        ///     awaiting completion of Expect()
        /// </summary>
        Expecting,

        /// <summary>
        ///     awaiting completion of Close()
        /// </summary>
        Closing
    }
}