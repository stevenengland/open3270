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
using System.Threading;

namespace StEn.Open3270.TN3270E.X3270
{
    /// <summary>
    ///     Summary description for Idle.
    /// </summary>
    internal class Idle : IDisposable
    {
        // 7 minutes
        private const int IdleMilliseconds = 7*60*1000;


        private Timer idleTimer;

        private bool idleWasIn3270;
        private bool isTicking;

        private int milliseconds;
        private Random rand;
        private bool randomize;
        private readonly Telnet telnet;

        internal Idle(Telnet tn)
        {
            telnet = tn;
        }


        public void Dispose()
        {
            if (telnet != null)
            {
                telnet.Connected3270 -= telnet_Connected3270;
            }
        }

        // Initialization
        private void Initialize()
        {
            // Register for state changes.
            telnet.Connected3270 += telnet_Connected3270;

            // Seed the random number generator (we seem to be the only user).
            rand = new Random();
        }

        private void telnet_Connected3270(object sender, Connected3270EventArgs e)
        {
            IdleIn3270(e.Is3270);
        }

        /// <summary>
        ///     Process a timeout value.
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        private int ProcessTimeoutValue(string t)
        {
            if (t == null || t.Length == 0)
            {
                milliseconds = IdleMilliseconds;
                randomize = true;
                return 0;
            }

            if (t[0] == '~')
            {
                randomize = true;
                t = t.Substring(1);
            }
            throw new ApplicationException("process_timeout_value not implemented");
        }


        /// <summary>
        ///     Called when a host connects or disconnects.
        /// </summary>
        /// <param name="in3270"></param>
        private void IdleIn3270(bool in3270)
        {
            if (in3270 && !idleWasIn3270)
            {
                idleWasIn3270 = true;
            }
            else
            {
                if (isTicking)
                {
                    telnet.Controller.RemoveTimeOut(idleTimer);
                    isTicking = false;
                }
                idleWasIn3270 = false;
            }
        }


        private void TimedOut(object state)
        {
            lock (telnet)
            {
                telnet.Trace.trace_event("Idle timeout\n");
                //Console.WriteLine("PUSH MACRO ignored (BUGBUG)");
                //push_macro(idle_command, false);
                ResetIdleTimer();
            }
        }


        /// <summary>
        ///     Reset (and re-enable) the idle timer.  Called when the user presses an AID key.
        /// </summary>
        public void ResetIdleTimer()
        {
            if (milliseconds != 0)
            {
                int idleMsNow;

                if (isTicking)
                {
                    telnet.Controller.RemoveTimeOut(idleTimer);
                    isTicking = false;
                }

                idleMsNow = milliseconds;

                if (randomize)
                {
                    idleMsNow = milliseconds;
                    if (rand.Next(100)%2 != 0)
                    {
                        idleMsNow += rand.Next(milliseconds/10);
                    }
                    else
                    {
                        idleMsNow -= rand.Next(milliseconds/10);
                    }
                }

                telnet.Trace.trace_event("Setting idle timeout to " + idleMsNow);
                idleTimer = telnet.Controller.AddTimeout(idleMsNow, TimedOut);
                isTicking = true;
            }
        }
    }
}