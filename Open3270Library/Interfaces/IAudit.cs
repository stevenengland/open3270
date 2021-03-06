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

namespace StEn.Open3270.Interfaces
{
    /// <summary>
    ///     Summary description for IAudit.
    /// </summary>
    public interface IAudit
    {
        /// <summary>
        ///     Write text to the audit file, no CR/LF
        /// </summary>
        /// <param name="text"></param>
        void Write(string text);

        /// <summary>
        ///     Write text to the audit file, CR/LF
        /// </summary>
        /// <param name="text"></param>
        void WriteLine(string text);
    }
}