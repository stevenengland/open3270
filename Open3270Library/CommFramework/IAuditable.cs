using System.Text;

namespace StEn.Open3270.CommFramework
{
    public interface IAuditable
    {
        void DumpTo(StringBuilder message, bool admin);
    }
}