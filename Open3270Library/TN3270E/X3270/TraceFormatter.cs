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
using System.Text;

namespace StEn.Open3270.TN3270E.X3270
{
    internal class TraceFormatter
    {
        public static string Format(string fmt, params object[] args)
        {
            var builder = new StringBuilder();
            var i = 0;
            var argindex = 0;
            while (i < fmt.Length)
            {
                if (fmt[i] == '%')
                {
                    switch (fmt[i + 1])
                    {
                        case '0':
                            if (fmt.Substring(i).StartsWith("%02x"))
                            {
                                try
                                {
                                    var v = Convert.ToInt32("" + args[argindex]);
                                    builder.Append(v.ToString("X2"));
                                }
                                catch (FormatException)
                                {
                                    builder.Append("??");
                                }
                                catch (OverflowException)
                                {
                                    builder.Append("??");
                                }
                                catch (ArgumentException)
                                {
                                    builder.Append("??");
                                }
                            }
                            else
                                throw new ApplicationException("Format '" + fmt.Substring(i) + "' not known");
                            break;
                        case 'c':
                            builder.Append(Convert.ToChar((char) args[argindex]));
                            break;
                        case 'f':
                            builder.Append((double) args[argindex]);
                            break;
                        case 'd':
                        case 's':
                        case 'u':
                            if (args[argindex] == null)
                                builder.Append("(null)");
                            else
                                builder.Append(args[argindex]);
                            break;
                        case 'x':
                            builder.Append(string.Format("{0:x}", args[argindex]));
                            break;
                        case 'X':
                            builder.Append(string.Format("{0:X}", args[argindex]));
                            break;
                        default:
                            throw new ApplicationException("Format '%" + fmt[i + 1] + "' not known");
                    }
                    i++;
                    argindex++;
                }
                else
                    builder.Append("" + fmt[i]);
                i++;
            }
            return builder.ToString();
        }
    }
}