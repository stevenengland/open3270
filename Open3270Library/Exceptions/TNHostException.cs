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

namespace StEn.Open3270.Exceptions
{
    /// <summary>
    ///     An object to return an exception from the 3270 host.
    /// </summary>
    public class TNHostException : Exception
    {
        private readonly string mMessage;

        /// <summary>
        ///     Constructor - used internally.
        /// </summary>
        /// <param name="message">The message text</param>
        /// <param name="auditlog">The audit log up to this exception</param>
        public TNHostException(string message, string reason, string auditlog)
        {
            Reason = reason;
            mMessage = message;
            AuditLog = auditlog;
        }

        /// <summary>
        ///     Returns the audit log from the start to this exception. Useful for tracing an exception
        /// </summary>
        /// <value>The formatted audit log</value>
        public string AuditLog { get; set; }

        public override string Message
        {
            get { return mMessage; }
        }


        public string Reason { get; set; }

        /// <summary>
        ///     Returns a textual version of the error
        /// </summary>
        /// <returns>The error text.</returns>
        public override string ToString()
        {
            return "HostException '" + mMessage + "' " + Reason;
        }
    }
}