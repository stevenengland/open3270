namespace StEn.Open3270.TN3270E.X3270
{
    internal enum AnsiState
    {
        DATA = 0,
        ESC = 1,
        CSDES = 2,
        N1 = 3,
        DECP = 4,
        TEXT = 5,
        TEXT2 = 6
    }
}