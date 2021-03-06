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
using System.Collections;
using System.Net.Sockets;
using StEn.Open3270.CommFramework;

namespace StEn.Open3270.Server
{
    /// <summary>
    ///     Summary description for TN3270ServerEmulationBase.
    /// </summary>
    public abstract class TN3270ServerEmulationBase
    {
        private readonly Queue mData = new Queue();
        private readonly MySemaphore mDataSemaphore = new MySemaphore(0, 9999);
        private ClientSocket mSocket;
        //

        public bool TN3270E { get; set; } = false;

        public void Init(Socket sock)
        {
            mSocket = new ClientSocket(sock);
            mSocket.FXSocketType = ServerSocketType.RAW;
            mSocket.OnData += cs_OnData;
            mSocket.OnNotify += mSocket_OnNotify;
            mSocket.Start();
        }

        public virtual void Run()
        {
        }

        public abstract TN3270ServerEmulationBase CreateInstance(Socket sock);

        private void cs_OnData(byte[] data, int Length)
        {
            if (data == null)
            {
                Console.WriteLine("cs_OnData received disconnect, close down this instance");
                Disconnect();
            }
            else
            {
                //Console.WriteLine("\n\n--on data");
                AddData(data, Length);
            }
        }

        public void Send(string dataStream)
        {
            //Console.WriteLine("send "+dataStream);
            var bytedata = ByteFromData(dataStream);
            mSocket.Send(bytedata);
        }

        public void Send(TNServerScreen s)
        {
            //Console.WriteLine("send screen");
            var data = s.AsTN3270Buffer(true, true, TN3270E);
            //Console.WriteLine("data.length="+data.Length);
            mSocket.Send(data);
        }

        private byte[] ByteFromData(string text)
        {
            var data = text.Split(' ');
            var bytedata = new byte[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                bytedata[i] = (byte) Convert.ToInt32(data[i], 16);
            }
            return bytedata;
        }

        public string WaitForKey(TNServerScreen currentScreen)
        {
            //Console.WriteLine("--wait for key");
            byte[] data = null;
            var screen = false;
            do
            {
                do
                {
                    while (!mDataSemaphore.Acquire(1000))
                    {
                        if (mSocket == null)
                            throw new TN3270ServerException("Connection dropped");
                    }
                    data = (byte[]) mData.Dequeue();
                } while (data == null);
                if (data[0] == 255 && data[1] == 253)
                {
                    // assume do/will string
                    for (var i = 0; i < data.Length; i++)
                    {
                        if (data[i] == 253)
                        {
                            data[i] = 251; // swap DO to WILL
                            Console.WriteLine("DO " + data[i + 1]);
                        }
                        else if (data[i] == 251)
                        {
                            data[i] = 253; // swap WILL to DO
                            Console.WriteLine("WILL " + data[i + 1]);
                        }
                    }
                    mSocket.Send(data);
                    screen = false;
                }
                else
                    screen = true;
            } while (!screen);
            return currentScreen.HandleTN3270Data(data, data.Length);
        }

        public int Wait(params string[] dataStream)
        {
            //Console.WriteLine("--wait for "+dataStream);
            while (!mDataSemaphore.Acquire(1000))
            {
                if (mSocket == null)
                    throw new TN3270ServerException("Connection dropped");
            }
            var data = (byte[]) mData.Dequeue();

            for (var count = 0; count < dataStream.Length; count++)
            {
                var bytedata = ByteFromData(dataStream[count]);

                if (bytedata.Length == data.Length)
                {
                    var ok = true;
                    for (var i = 0; i < bytedata.Length; i++)
                    {
                        if (bytedata[i] != data[i])
                        {
                            ok = false;
                        }
                    }
                    if (ok)
                        return count;
                }
            }
            Console.WriteLine("\n\ndata match error");
            for (var count = 0; count < dataStream.Length; count++)
            {
                Console.WriteLine("--expected " + dataStream);
            }
            Console.Write("--received ");
            for (var i = 0; i < data.Length; i++)
            {
                Console.Write("{0:x2} ", data[i]);
            }
            Console.WriteLine();
            throw new TN3270ServerException(
                "Error reading incoming data stream. Expected data missing. Check console log for details");
        }

        public void AddData(byte[] data, int length)
        {
            var copy = new byte[length];
            for (var i = 0; i < length; i++)
            {
                copy[i] = data[i];
            }
            mData.Enqueue(copy);
            mDataSemaphore.Release(1);
        }

        public void Disconnect()
        {
            if (mSocket != null)
            {
                mSocket.Disconnect();
                mSocket = null;
            }
        }

        private void mSocket_OnNotify(string eventName, Message message)
        {
            if (eventName == "Disconnect")
            {
                Disconnect();
            }
        }
    }
}