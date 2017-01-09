namespace TerminalDemo
{
    public class ScreenFieldLocation
    {
        public ScreenFieldLocation(int row, int column, int length)
        {
            Row = row;
            Column = column;
            Length = length;
        }

        public int Index { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int Length { get; set; }
    }
}