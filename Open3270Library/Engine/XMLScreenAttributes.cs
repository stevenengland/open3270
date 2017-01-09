using System;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace StEn.Open3270.Engine
{
    [Serializable]
    public class XMLScreenAttributes
    {
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public string Background;
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public int Base;
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public string FieldType;
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public string Foreground;
        [XmlAttribute(Form = XmlSchemaForm.Unqualified)] public bool Protected;
    }
}