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

using System.IO;
using StEn.Open3270.Interfaces;

namespace StEn.Open3270.Engine
{
    /// <summary>
    ///     Connection configuration class holds the configuration options for a connection
    /// </summary>
    public class ConnectionConfig
    {
        public ConnectionConfig()
        {
            HostName = null;
            HostPort = 23;
            HostLU = null;
            TermType = null;
        }

        /// <summary>
        ///     The host name to connect to
        /// </summary>
        public string HostName { get; set; }

        /// <summary>
        ///     Host Port
        /// </summary>
        public int HostPort { get; set; }

        /// <summary>
        ///     Host LU, null for none
        /// </summary>
        public string HostLU { get; set; }

        /// <summary>
        ///     Terminal type for host
        /// </summary>
        public string TermType { get; set; }

        public bool UseSSL { get; set; } = false;

        /// <summary>
        ///     Is the internal screen identification engine turned on? Default false.
        /// </summary>
        public bool IdentificationEngineOn { get; set; } = false;

        /// <summary>
        ///     Should we skip to the next unprotected field if SendText is called
        ///     on an protected field? Default true.
        /// </summary>
        public bool AlwaysSkipToUnprotected { get; set; } = true;

        /// <summary>
        ///     Lock the screen if user tries to write to a protected field. Default false.
        /// </summary>
        public bool LockScreenOnWriteToUnprotected { get; set; } = false;

        /// <summary>
        ///     Default timeout for operations such as SendKeyFromText. Default value is 40000 (40 seconds).
        /// </summary>
        public int DefaultTimeout { get; set; } = 40000;

        /// <summary>
        ///     Flag to set whether an exception should be thrown if a screen write met
        ///     a locked screen. Default is now true.
        /// </summary>
        public bool ThrowExceptionOnLockedScreen { get; set; } = true;

        /// <summary>
        ///     Whether to ignore host request for sequence counting
        /// </summary>
        public bool IgnoreSequenceCount { get; set; } = false;

        /// <summary>
        ///     Allows connection to be connected to a proxy log file rather than directly to a host
        ///     for debugging.
        /// </summary>
        public StreamReader LogFile { get; set; } = null;

        /// <summary>
        ///     Whether to ignore keyboard inhibit when moving between screens. Significantly speeds up operations,
        ///     but can result in locked screens and data loss if you try to key data onto a screen that is still locked.
        /// </summary>
        public bool FastScreenMode { get; set; } = false;


        /// <summary>
        ///     Whether the screen should always be refreshed when waiting for an update. Default is false.
        /// </summary>
        public bool AlwaysRefreshWhenWaiting { get; set; } = false;


        /// <summary>
        ///     Whether to refresh the screen for keys like TAB, BACKSPACE etc should refresh the host. Default is now false.
        /// </summary>
        public bool SubmitAllKeyboardCommands { get; set; } = false;

        /// <summary>
        ///     Whether to refuse a TN3270E request from the host, despite the terminal type
        /// </summary>
        public bool RefuseTN3270E { get; set; } = false;


        internal void Dump(IAudit sout)
        {
            if (sout == null) return;
            sout.WriteLine("Config.FastScreenMode " + FastScreenMode);
            sout.WriteLine("Config.IgnoreSequenceCount " + IgnoreSequenceCount);
            sout.WriteLine("Config.IdentificationEngineOn " + IdentificationEngineOn);
            sout.WriteLine("Config.AlwaysSkipToUnprotected " + AlwaysSkipToUnprotected);
            sout.WriteLine("Config.LockScreenOnWriteToUnprotected " + LockScreenOnWriteToUnprotected);
            sout.WriteLine("Config.ThrowExceptionOnLockedScreen " + ThrowExceptionOnLockedScreen);
            sout.WriteLine("Config.DefaultTimeout " + DefaultTimeout);
            sout.WriteLine("Config.hostName " + HostName);
            sout.WriteLine("Config.hostPort " + HostPort);
            sout.WriteLine("Config.hostLU " + HostLU);
            sout.WriteLine("Config.termType " + TermType);
            sout.WriteLine("Config.AlwaysRefreshWhenWaiting " + AlwaysRefreshWhenWaiting);
            sout.WriteLine("Config.SubmitAllKeyboardCommands " + SubmitAllKeyboardCommands);
            sout.WriteLine("Config.RefuseTN3270E " + RefuseTN3270E);
        }
    }
}