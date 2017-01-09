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
using System.Net.Sockets;
using StEn.Open3270.CommFramework;

namespace StEn.Open3270.Server
{
    /// <summary>
    ///     Summary description for TN3270Server.
    /// </summary>
    public class TN3270Server
    {
        private ServerSocket server;
        private TN3270ServerEmulationBase system;
        //TN3270ServerScript _Script;
        //int _port = 23;
        //bool bQuit = false;

        public void Start(TN3270ServerEmulationBase system, int port)
        {
            this.system = system;

            server = new ServerSocket(ServerSocketType.RAW);
            //
            server.OnConnectRAW += server_OnConnectRAW;
            server.Listen(port);
        }

        public void Stop()
        {
            server.Close();
        }

        private void server_OnConnectRAW(Socket sock)
        {
            Console.WriteLine("OnConnectRAW");
            //
            var instance = system.CreateInstance(sock);
            try
            {
                try
                {
                    instance.Run();
                }
                catch (TN3270ServerException tse)
                {
                    Console.WriteLine("tse = " + tse);
                    throw;
                }
            }
            finally
            {
                instance.Disconnect();
            }
        }
    }
}