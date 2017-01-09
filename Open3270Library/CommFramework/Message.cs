#region License

/* 
 *
 * Open3270 - A C# implementation of the TN3270/TN3270E protocol
 *
 *   Copyright © 2004-2006 Michael Warriner. All rights reserved
 * 
 * This is free software; you can redistribute it and/or modify it
 * under the terms of the GNU Lesser General Public License as
 * published by the Free Software Foundation; either version 2.1 of
 * the License, or (at your option) any later version.
 *
 * This software is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this software; if not, write to the Free
 * Software Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA
 * 02110-1301 USA, or see the FSF site: http://www.fsf.org.
 */

#endregion

using System;
using System.IO;
using System.Net.Sockets;
using System.Xml.Serialization;

namespace StEn.Open3270.CommFramework
{
    /// <summary>
    ///     Internal - message base class
    /// </summary>
    [Serializable]
    [XmlInclude(typeof(HtmlMessage))]
    internal class Message
    {
        /// <summary>
        ///     Internal - message type
        /// </summary>
        public string MessageType;


        public void Send(Socket socket)
        {
            // SOAP Serialization
            //Thread.CurrentThread.CurrentCulture = new CultureInfo("en-gb");
            //Thread.CurrentThread.CurrentUICulture = Thread.CurrentThread.CurrentCulture;


            //SoapFormatter soap = new SoapFormatter();
            var soap = new XmlSerializer(typeof(Message));
            var ms = new MemoryStream();
            soap.Serialize(ms, this);
            var bMessage = ms.GetBuffer();
            ms.Close();
            //
            var header = new MessageHeader();
            header.uMessageSize = bMessage.Length;
            //
            //
            socket.Send(header.ToByte(), 0, MessageHeader.MessageHeaderSize, SocketFlags.None);
            socket.Send(bMessage, 0, bMessage.Length, SocketFlags.None);
        }

        public static Message CreateFromByteArray(byte[] data)
        {
            //Thread.CurrentThread.CurrentCulture = new CultureInfo("en-gb");
            //Thread.CurrentThread.CurrentUICulture = Thread.CurrentThread.CurrentCulture;

            //
            // SOAP Serializer
            Message msg = null;
            try
            {
                //SoapFormatter soap = new SoapFormatter();
                var soap = new XmlSerializer(typeof(Message));
                var ms = new MemoryStream(data);
                var dso = soap.Deserialize(ms);

                try
                {
                    msg = (Message) dso;
                }
                catch (Exception ef)
                {
                    Audit.WriteLine("type=" + dso.GetType() + " cast to Message threw exception " + ef);
                    return null;
                }
                ms.Close();
            }
            catch (Exception ee)
            {
                Audit.WriteLine("Message serialization failed, error=" + ee);
                return null;
            }
            Audit.WriteLine("Message type= " + msg.GetType());
            return msg;
        }
    }

    //
}