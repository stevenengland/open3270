namespace StEn.Open3270.TN3270E
{
    internal enum ControllerState
    {
        Data = 0,
        Esc = 1,
        CSDES = 2,
        N1 = 3,
        DECP = 4,
        Text = 5,
        Text2 = 6
    }
}