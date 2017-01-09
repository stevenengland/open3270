namespace StEn.Open3270.LogParser
{
    internal enum CS
    {
        Waiting,
        R_IAC,
        R_SB,
        R_DATA,
        R_IAC_END,
        R_WILL,
        R_WONT,
        R_HEADER,
        R_HEADERDATA
    }
}