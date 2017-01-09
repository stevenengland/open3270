using System;
using System.Xml.Serialization;

namespace StEn.Open3270.Engine
{
    [Serializable]
    public class XMLUnformattedScreen
    {
        [XmlElement("Text")] public string[] Text;
    }
}