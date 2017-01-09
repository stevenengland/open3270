using System;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace StEn.Open3270.Engine
{
    [Serializable]
    public class XMLScreenLocation
    {
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public int left;
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public int length;
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public int position;
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public int top;
    }
}