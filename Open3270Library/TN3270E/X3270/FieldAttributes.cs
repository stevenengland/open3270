namespace StEn.Open3270.TN3270E.X3270
{
    public struct FieldAttributes
    {
        public bool IsModified { get; set; }
        public bool IsNumeric { get; set; }
        public bool IsProtected { get; set; }
        public bool IsSkip { get; set; }
        public bool IsZero { get; set; }
        public bool IsHigh { get; set; }
        public bool IsNormal { get; set; }
        public bool IsSelectable { get; set; }
        public bool IsIntense { get; set; }
    }
}