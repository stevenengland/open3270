using System;
using System.Xml.Serialization;

namespace StEn.Open3270.Engine
{
    [Serializable]
    public class XMLScreenField
    {
        [XmlElement("Location")] public XMLScreenLocation Location;

        [XmlElement("Attributes")] public XMLScreenAttributes Attributes;


        [XmlText] public string Text;
    }
}