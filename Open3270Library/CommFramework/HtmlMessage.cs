using System;
using System.Text;

namespace StEn.Open3270.CommFramework
{
    [Serializable]
    internal class HtmlMessage : Message
    {
        /// <summary>
        ///     Internal - message type
        /// </summary>
        public byte[] Bytes;

        public HtmlMessage()
        {
        }

        public HtmlMessage(string text)
        {
            Bytes = Encoding.UTF8.GetBytes(text);
            MessageType = "Html";
        }

        public string GetText()
        {
            if (Bytes == null)
                return null;
            return Encoding.UTF8.GetString(Bytes);
        }
    }
}