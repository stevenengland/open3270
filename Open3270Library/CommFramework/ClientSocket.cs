#region License

/* 
 *
 * Open3270 - A C# implementation of the TN3270/TN3270E protocol
 *
 *   Copyright � 2004-2006 Michael Warriner. All rights reserved
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
using System.Net;
using System.Net.Sockets;
using System.Security.Permissions;
using System.Text;

namespace StEn.Open3270.CommFramework
{
    /// <summary>
    ///     Summary description for ClientSocket.
    /// </summary>
    internal class ClientSocket
    {
        private AsyncCallback callbackProc;

        // Code for message builder
        private byte[] currentBuffer;
        private int currentBufferIndex;
        private MessageHeader currentMessageHeader;
        private IPEndPoint iep;
        private readonly byte[] m_byBuff = new byte[32767];
        private Socket mSocket;
        private State mState;

        public ClientSocket()
        {
            Audit.WriteLine("Client socket created.");
            info();
        }

        public ClientSocket(Socket sock)
        {
            mSocket = sock;
            info();
        }

        public ServerSocketType FXSocketType { get; set; } = ServerSocketType.ClientServer;

        public bool IsConnected
        {
            get
            {
                if (mSocket == null)
                    return false;
                if (!mSocket.Connected)
                    return false;
                return true;
            }
        }

        public event ClientSocketNotify OnNotify;
        public event ClientDataNotify OnData;

        public void info()
        {
            Audit.WriteLine("CLRVersion = " + Environment.Version);
            Audit.WriteLine("UserName   = " + Environment.UserName);
            Audit.WriteLine("Assembly version = " + typeof(ClientSocket).Assembly.FullName);
        }

        public void Start()
        {
            Audit.WriteLine("sock.start");
            if (mSocket.Connected)
            {
                mSocket.Blocking = false;
                mState = State.Waiting;
                AsyncCallback recieveData = OnRecievedData;
                mSocket.BeginReceive(m_byBuff, 0, m_byBuff.Length, SocketFlags.None, recieveData, mSocket);
                Audit.WriteLine("called begin receive");
            }
            else
            {
                Audit.WriteLine("bugbug-- not connected");
                throw new ApplicationException("Socket passed to us, but not connected to anything");
            }
        }

        public void Connect(string address, int port)
        {
            Audit.WriteLine("Connect " + address + " -- " + port);
            Disconnect();
            mState = State.Waiting;
            //
            // permissions
            var mySocketPermission1 = new SocketPermission(PermissionState.None);
            mySocketPermission1.AddPermission(NetworkAccess.Connect, TransportType.All, "localhost", 8800);
            mySocketPermission1.Demand();

            //
            // Actually connect
            //
            // count .s, numeric and 3 .s =ipaddress
            var ipaddress = false;
            var text = false;
            var count = 0;
            int i;
            for (i = 0; i < address.Length; i++)
            {
                if (address[i] == '.')
                    count++;
                else
                {
                    if (address[i] < '0' || address[i] > '9')
                        text = true;
                }
            }
            if (count == 3 && text == false)
                ipaddress = true;

            if (!ipaddress)
            {
                Audit.WriteLine("Dns.Resolve " + address);
                var IPHost = Dns.GetHostEntry(address);
                var aliases = IPHost.Aliases;
                var addr = IPHost.AddressList;
                iep = new IPEndPoint(addr[0], port);
            }
            else
            {
                Audit.WriteLine("Use address " + address + " as ip");
                iep = new IPEndPoint(IPAddress.Parse(address), port);
            }


            try
            {
                // Create New Socket 
                mSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                // Create New EndPoint
                // This is a non blocking IO
                mSocket.Blocking = false;
                // set some random options
                //
                //mSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);

                //
                // Assign Callback function to read from Asyncronous Socket
                callbackProc = ConnectCallback;
                // Begin Asyncronous Connection
                mSocket.BeginConnect(iep, callbackProc, mSocket);
            }
            catch (Exception eeeee)
            {
                Audit.WriteLine("e=" + eeeee);
                throw;
                //				st_changed(STCALLBACK.ST_CONNECT, false);
            }
        }

        public void Disconnect()
        {
            if (mSocket != null)
            {
                var mTemp = mSocket;
                mSocket = null;
                Audit.WriteLine("start close");


                try
                {
                    mTemp.Blocking = true;

                    if (mTemp.Connected)
                        mTemp.Close();
                }
                catch (Exception)
                {
                }
                Audit.WriteLine("stop close - all async handlers should be disconnected by now");
                if (OnNotify != null)
                    OnNotify("Disconnect", null);
            }
            mSocket = null;
        }

        private void ConnectCallback(IAsyncResult ar)
        {
            try
            {
                Audit.WriteLine("connect async notifier");
                // Get The connection socket from the callback
                var sock1 = (Socket) ar.AsyncState;
                if (sock1.Connected)
                {
                    // notify parent here
                    if (OnNotify != null)
                        OnNotify("Connect", null);
                    //
                    // Define a new Callback to read the data 
                    AsyncCallback recieveData = OnRecievedData;
                    // Begin reading data asyncronously
                    sock1.BeginReceive(m_byBuff, 0, m_byBuff.Length, SocketFlags.None, recieveData, sock1);
                    Audit.WriteLine("setup data receiver");
                }
                else
                {
                    // notify parent - connect failed
                    if (OnNotify != null)
                        OnNotify("ConnectFailed", null);
                    Audit.WriteLine("Connect failed");
                }
            }
            catch (Exception ex)
            {
                Audit.WriteLine("Setup Receive callback failed " + ex);
                throw;
            }
        }

        private void OnRecievedData(IAsyncResult ar)
        {
            //Audit.WriteLine("OnReceivedData");
            // Get The connection socket from the callback
            var sock = (Socket) ar.AsyncState;
            // is socket closing
            if (!sock.Connected)
                return;
            // Get The data , if any
            var nBytesRec = 0;
            try
            {
                nBytesRec = sock.EndReceive(ar);
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("Client socket OnReceived data received socket disconnect (object disposed)");
                nBytesRec = 0;
                OnData(null, 0); // notify close
                return;
            }
            catch (SocketException se)
            {
                Console.WriteLine("Client socket OnReceived data received socket disconnect (" + se.Message + ")");
                nBytesRec = 0;
                OnData(null, 0); // notify close
                return;
            }
            //Audit.WriteLine("OnReceivedData bytes="+nBytesRec);
            if (OnData != null && nBytesRec > 0)
            {
                OnData(m_byBuff, nBytesRec);
            }
            if (nBytesRec > 0)
            {
                // process it if we're a client server socket
                if (FXSocketType == ServerSocketType.ClientServer)
                {
                    for (var i = 0; i < nBytesRec; i++)
                    {
                        switch (mState)
                        {
                            case State.Waiting:
                                currentBuffer = new byte[MessageHeader.MessageHeaderSize];
                                currentBufferIndex = 0;
                                currentBuffer[currentBufferIndex++] = m_byBuff[i];
                                mState = State.ReadingHeader;
                                break;

                            case State.ReadingHeader:
                                currentBuffer[currentBufferIndex++] = m_byBuff[i];
                                if (currentBufferIndex >= MessageHeader.MessageHeaderSize)
                                {
                                    currentMessageHeader = new MessageHeader(currentBuffer);
                                    currentBuffer = new byte[currentMessageHeader.uMessageSize];
                                    currentBufferIndex = 0;
                                    mState = State.ReadingBuffer;
                                }
                                break;

                            case State.ReadingBuffer:
                                currentBuffer[currentBufferIndex++] = m_byBuff[i];
                                if (currentBufferIndex >= currentMessageHeader.uMessageSize)
                                {
                                    var dump = Encoding.ASCII.GetString(currentBuffer);
                                    Audit.WriteLine("RCLRVersion = " + Environment.Version);
                                    Audit.WriteLine("Writeline " + dump);
                                    try
                                    {
                                        var msg = Message.CreateFromByteArray(currentBuffer);
                                        if (msg != null && OnNotify != null)
                                            OnNotify("Data", msg);
                                    }
                                    catch (Exception e)
                                    {
                                        Audit.WriteLine("Exception handling message " + e);
                                    }
                                    mState = State.Waiting;
                                }
                                break;

                            default:
                                throw new ApplicationException("sorry, state '" + mState + "' not known");
                        }
                    }
                }
                // 
                //
                try
                {
                    // Define a new Callback to read the data 
                    AsyncCallback recieveData = OnRecievedData;
                    // Begin reading data asyncronously
                    mSocket.BeginReceive(m_byBuff, 0, m_byBuff.Length, SocketFlags.None, recieveData, mSocket);
                    //
                }
                catch (Exception e)
                {
                    // assume socket was disconnected somewhere else if an exception occurs here
                    Audit.WriteLine("Socket BeginReceived failed with error " + e.Message);
                    Disconnect();
                }
            }
            else
            {
                // If no data was recieved then the connection is probably dead
                Audit.WriteLine("Socket was disconnected disconnected"); //+ sock.RemoteEndPoint );
                Disconnect();
            }
        }

        public void Send(Message msg)
        {
            if (mSocket.Connected == false)
                throw new ApplicationException("Sorry, socket is not connected");
            //
            msg.Send(mSocket);
        }

        public void Send(byte[] data)
        {
            if (mSocket.Connected == false)
                throw new ApplicationException("Sorry, socket is not connected");
            //
            mSocket.Send(data);
        }

        public void Send(byte[] data, int length)
        {
            if (mSocket.Connected == false)
                throw new ApplicationException("Sorry, socket is not connected");
            //
            mSocket.Send(data, 0, length, SocketFlags.None);
        }

        private enum State
        {
            Waiting,
            ReadingHeader,
            ReadingBuffer
        }
    }
}