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
    internal class Controller : IDisposable
    {
        public int CursorX
        {
            get { return AddressToColumn(CursorAddress); }
        }

        public int CursorY
        {
            get { return AddresstoRow(CursorAddress); }
        }


        private void OnAllChanged()
        {
            ScreenChanged = true;
            if (telnet.IsAnsi)
            {
                firstChanged = 0;
                lastChanged = RowCount*ColumnCount;
            }
        }


        private void OnRegionChanged(int f, int l)
        {
            ScreenChanged = true;
            if (telnet.IsAnsi)
            {
                if (firstChanged == -1 || f < firstChanged) firstChanged = f;
                if (lastChanged == -1 || l > lastChanged) lastChanged = l;
            }
        }


        private void OnOneChanged(int n)
        {
            OnRegionChanged(n, n + 1);
        }


        /// <summary>
        ///     Initialize the emulated 3270 hardware.
        /// </summary>
        /// <param name="cmask"></param>
        public void Initialize(int cmask)
        {
            //Register callback routines.
            telnet.ConnectionPending += telnet_ConnectionPending;
            telnet.PrimaryConnectionChanged += telnet_PrimaryConnectionChanged;
            telnet.Connected3270 += telnet_Connected3270;
        }


        /// <summary>
        ///     Handles connection state changes, e.g. initial connection, disconnection (or does it?), and connecting with 3270
        /// </summary>
        /// <param name="success">Parameter is currently ignored</param>
        private void ReactToConnectionChange(bool success)
        {
            if (Is3270)
            {
                //Console.WriteLine("--ever_3270 is true, set fake_fa to 0xe0 - unprotected");
                FakeFA = 0xE0;
            }
            else
            {
                //Console.WriteLine("--ever_3270 is false, set fake_fa to 0xc4 - protected");
                FakeFA = 0xC4;
            }
            if (!telnet.Is3270 || (telnet.IsSscp && ((telnet.Keyboard.keyboardLock & KeyboardConstants.OiaTWait) != 0)))
            {
                telnet.Keyboard.KeyboardLockClear(KeyboardConstants.OiaTWait, "ctlr_connect");
                //status_reset();
            }

            defaultFg = 0x00;
            defaultGr = 0x00;
            defaultCs = 0x00;
            replyMode = ControllerConstant.SF_SRM_FIELD;
            CrmnAttribute = 0;
        }


        /// <summary>
        ///     Reinitialize the emulated 3270 hardware.
        /// </summary>
        /// <param name="cmask"></param>
        public void Reinitialize(int cmask)
        {
            if ((cmask & ControllerConstant.MODEL_CHANGE) != 0)
            {
                //Allocate buffers
                ScreenBuffer = new byte[MaxRows*MaxColumns];
                extendedAttributes = new ExtendedAttribute[MaxRows*MaxColumns];
                aScreenBuffer = new byte[MaxRows*MaxColumns];
                aExtendedAttributeBuffer = new ExtendedAttribute[MaxRows*MaxColumns];
                extendedAttributesZeroBuffer = new ExtendedAttribute[MaxRows*MaxColumns];
                int i;
                for (i = 0; i < MaxRows*MaxColumns; i++)
                {
                    extendedAttributes[i] = new ExtendedAttribute();
                    aExtendedAttributeBuffer[i] = new ExtendedAttribute();
                    extendedAttributesZeroBuffer[i] = new ExtendedAttribute();
                }

                CursorAddress = 0;
                bufferAddress = 0;
            }
        }


        /// <summary>
        ///     Deal with the relationships between model numbers and rows/cols.
        /// </summary>
        /// <param name="mn"></param>
        /// <param name="ovc"></param>
        /// <param name="ovr"></param>
        private void SetRowsAndColumns(int mn, int ovc, int ovr)
        {
            int defmod;

            switch (mn)
            {
                case 2:
                {
                    MaxColumns = ColumnCount = 80;
                    MaxRows = RowCount = 24;
                    modelNumber = 2;
                    break;
                }
                case 3:
                {
                    MaxColumns = ColumnCount = 80;
                    MaxRows = RowCount = 32;
                    modelNumber = 3;
                    break;
                }
                case 4:
                {
                    MaxColumns = ColumnCount = 80;
                    MaxRows = RowCount = 43;
                    modelNumber = 4;
                    break;
                }
                case 5:
                {
                    MaxColumns = ColumnCount = 132;
                    MaxRows = RowCount = 27;
                    modelNumber = 5;
                    break;
                }
                default:
                {
                    defmod = 4;
                    telnet.Events.ShowError("Unknown model: %d\nDefaulting to %d", mn, defmod);
                    SetRowsAndColumns(defmod, ovc, ovr);
                    return;
                }
            }


            if (ovc != 0 || ovr != 0)
            {
                throw new ApplicationException("oops - oversize");
            }

            /* Update the model name. */
            modelName = "327" + (appres.m3279 ? "9" : "8") + "-" + modelNumber + (appres.extended ? "-E" : "");
        }


        //Set the formatted screen flag.  
        //A formatted screen is a screen thathas at least one field somewhere on it.
        private void SetFormattedFlag()
        {
            int baddr;

            Formatted = false;
            baddr = 0;
            do
            {
                if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                {
                    Formatted = true;
                    break;
                }
                IncrementAddress(ref baddr);
            } while (baddr != 0);
        }


        /// <summary>
        ///     Find the field attribute for the given buffer address.  Return its address rather than its value.
        /// </summary>
        /// <param name="baddr"></param>
        /// <returns></returns>
        public int GetFieldAttribute(int baddr)
        {
            int sbaddr;

            if (!Formatted)
            {
                //Console.WriteLine("get_field_attribute on unformatted screen returns -1");
                return -1; // **BUG** //&fake_fa;
            }

            sbaddr = baddr;
            do
            {
                if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                    return baddr; //&(screen_buf[baddr]);
                DecrementAddress(ref baddr);
            } while (baddr != sbaddr);
            return -1; // **BUG** &fake_fa;
        }


        /// <summary>
        ///     Find the field attribute for the given buffer address, bounded by another buffer address.
        ///     Return the attribute in a parameter.
        /// </summary>
        /// <param name="bAddr"></param>
        /// <param name="bound"></param>
        /// <param name="faOutIndex"></param>
        /// <returns>Returns true if an attribute is found, false if boundary hit.</returns>
        private bool GetBoundedFieldAttribute(int bAddr, int bound, ref int faOutIndex)
        {
            int sbaddr;

            if (!Formatted)
            {
                faOutIndex = -1;
                return true;
            }

            sbaddr = bAddr;
            do
            {
                if (FieldAttribute.IsFA(ScreenBuffer[bAddr]))
                {
                    faOutIndex = bAddr;
                    return true;
                }
                DecrementAddress(ref bAddr);
            } while (bAddr != sbaddr && bAddr != bound);

            //Screen is unformatted (and 'formatted' is inaccurate).
            if (bAddr == sbaddr)
            {
                faOutIndex = -1;
                return true;
            }

            // Wrapped to boundary
            return false;
        }


        /// <summary>
        ///     Find the next unprotected field.  Returns the address following the unprotected attribute byte,
        ///     or 0 if no nonzero-width unprotected field can be found.
        /// </summary>
        /// <param name="fromAddress"></param>
        /// <returns></returns>
        public int GetNextUnprotectedField(int fromAddress)
        {
            int baddr, nbaddr;

            nbaddr = fromAddress;
            do
            {
                baddr = nbaddr;
                IncrementAddress(ref nbaddr);
                if (FieldAttribute.IsFA(ScreenBuffer[baddr]) &&
                    !FieldAttribute.IsProtected(ScreenBuffer[baddr]) &&
                    !FieldAttribute.IsFA(ScreenBuffer[nbaddr]))
                {
                    return nbaddr;
                }
            } while (nbaddr != fromAddress);
            return 0;
        }


        /// <summary>
        ///     Perform an erase command, which may include changing the (virtual) screen size.
        /// </summary>
        /// <param name="alt"></param>
        public void Erase(bool alt)
        {
            telnet.Keyboard.ToggleEnterInhibitMode(false);

            Clear(true);

            if (alt == ScreenAlt)
            {
                return;
            }

            if (alt)
            {
                // Going from 24x80 to maximum. 
                // screen_disp(false);
                RowCount = MaxRows;
                ColumnCount = MaxColumns;
            }
            else
            {
                // Going from maximum to 24x80. 
                if (MaxRows > 24 || MaxColumns > 80)
                {
                    if (debuggingFont)
                    {
                        BlankOutScreen();
                        //	screen_disp(false);
                    }
                    RowCount = 24;
                    ColumnCount = 80;
                }
            }

            ScreenAlt = alt;
        }


        /// <summary>
        ///     Interpret an incoming 3270 command.
        /// </summary>
        /// <param name="buf"></param>
        /// <param name="start"></param>
        /// <param name="bufferLength"></param>
        /// <returns></returns>
        public PDS ProcessDS(byte[] buf, int start, int bufferLength)
        {
            if (buf.Length == 0 || bufferLength == 0)
            {
                return PDS.OkayNoOutput;
            }

            trace.trace_ds("< ");


            // Handle 3270 command
            if (buf[start] == ControllerConstant.CMD_EAU || buf[start] == ControllerConstant.SNA_CMD_EAU)
            {
                //Erase all unprotected
                trace.trace_ds("EraseAllUnprotected\n");
                ProcessEraseAllUnprotectedCommand();
                return PDS.OkayNoOutput;
            }
            if (buf[start] == ControllerConstant.CMD_EWA || buf[start] == ControllerConstant.SNA_CMD_EWA)
            {
                //Erase/write alternate
                trace.trace_ds("EraseWriteAlternate\n");
                Erase(true);
                ProcessWriteCommand(buf, start, bufferLength, true);
                return PDS.OkayNoOutput;
            }
            if (buf[start] == ControllerConstant.CMD_EW || buf[start] == ControllerConstant.SNA_CMD_EW)
            {
                //Erase/write
                trace.trace_ds("EraseWrite\n");
                Erase(false);
                ProcessWriteCommand(buf, start, bufferLength, true);
                return PDS.OkayNoOutput;
            }
            if (buf[start] == ControllerConstant.CMD_W || buf[start] == ControllerConstant.SNA_CMD_W)
            {
                //Write
                trace.trace_ds("Write\n");
                ProcessWriteCommand(buf, start, bufferLength, false);
                return PDS.OkayNoOutput;
            }
            if (buf[start] == ControllerConstant.CMD_RB || buf[start] == ControllerConstant.SNA_CMD_RB)
            {
                //Read buffer
                trace.trace_ds("ReadBuffer\n");
                ProcessReadBufferCommand(AttentionID);
                return PDS.OkayOutput;
            }
            if (buf[start] == ControllerConstant.CMD_RM || buf[start] == ControllerConstant.SNA_CMD_RM)
            {
                //Read modifed
                trace.trace_ds("ReadModified\n");
                ProcessReadModifiedCommand(AttentionID, false);
                return PDS.OkayOutput;
            }
            if (buf[start] == ControllerConstant.CMD_RMA || buf[start] == ControllerConstant.SNA_CMD_RMA)
            {
                //Read modifed all
                trace.trace_ds("ReadModifiedAll\n");
                ProcessReadModifiedCommand(AttentionID, true);
                return PDS.OkayOutput;
            }
            if (buf[start] == ControllerConstant.CMD_WSF || buf[start] == ControllerConstant.SNA_CMD_WSF)
            {
                //Write structured field
                trace.trace_ds("WriteStructuredField");
                return sf.WriteStructuredField(buf, start, bufferLength /*buflen*/);
            }
            if (buf[start] == ControllerConstant.CMD_EWA)
            {
                //No-op
                trace.trace_ds("NoOp\n");
                return PDS.OkayNoOutput;
            }
            //Unknown 3270 command
            telnet.Events.ShowError("Unknown 3270 Data Stream command: 0x%X\n", buf[start]);
            return PDS.BadCommand;
        }


        /// <summary>
        ///     Functions to insert SA attributes into the inbound data stream.
        /// </summary>
        /// <param name="obptr"></param>
        /// <param name="attr"></param>
        /// <param name="vValue"></param>
        /// <param name="currentp"></param>
        /// <param name="anyp"></param>
        private void InsertSaAttribtutes(NetBuffer obptr, byte attr, byte vValue, ref byte currentp, ref bool anyp)
        {
            if (vValue != currentp)
            {
                currentp = vValue;
                obptr.Add(ControllerConstant.ORDER_SA);
                obptr.Add(attr);
                obptr.Add(vValue);
                if (anyp)
                {
                    trace.trace_ds("'");
                }
                trace.trace_ds(" SetAttribute(%s)", See.GetEfa(attr, vValue));
                anyp = false;
            }
        }

        private void InsertSaAttribtutes(NetBuffer obptr, int baddr, ref byte current_fgp, ref byte current_grp,
            ref byte current_csp, ref bool anyp)
        {
            if (replyMode == ControllerConstant.SF_SRM_CHAR)
            {
                int i;
                var foundXAForeground = false;
                var foundXAHighlighting = false;
                var foundXACharset = false;
                for (i = 0; i < CrmnAttribute; i++)
                {
                    if (CrmAttributes[i] == See.XA_FOREGROUND)
                    {
                        foundXAForeground = true;
                    }
                    if (CrmAttributes[i] == See.XA_HIGHLIGHTING)
                    {
                        foundXAHighlighting = true;
                    }
                    if (CrmAttributes[i] == See.XA_CHARSET)
                    {
                        foundXACharset = true;
                    }
                }

                if (foundXAForeground)
                {
                    InsertSaAttribtutes(obptr, See.XA_FOREGROUND, extendedAttributes[baddr].fg, ref current_fgp,
                        ref anyp);
                }

                if (foundXAHighlighting)
                {
                    byte gr;

                    gr = extendedAttributes[baddr].gr;
                    if (gr != 0)
                    {
                        gr |= 0xf0;
                    }
                    InsertSaAttribtutes(obptr, See.XA_HIGHLIGHTING, gr, ref current_grp, ref anyp);
                }

                if (foundXACharset)
                {
                    byte cs;

                    cs = (byte) (extendedAttributes[baddr].cs & ExtendedAttribute.CS_MASK);
                    if (cs != 0)
                    {
                        cs |= 0xf0;
                    }
                    InsertSaAttribtutes(obptr, See.XA_CHARSET, cs, ref current_csp, ref anyp);
                }
            }
        }


        /// <summary>
        ///     Process a 3270 Read-Modified command and transmit the data back to the host.
        /// </summary>
        /// <param name="attentionIDbyte"></param>
        /// <param name="all"></param>
        public void ProcessReadModifiedCommand(byte attentionIDbyte, bool all)
        {
            int baddr, sbaddr;
            var sendData = true;
            var shortRead = false;
            byte currentFG = 0x00;
            byte currentGR = 0x00;
            byte currentCS = 0x00;

            if (telnet.IsSscp && attentionIDbyte != AID.Enter)
            {
                return;
            }

            trace.trace_ds("> ");
            var obptr = new NetBuffer();

            switch (attentionIDbyte)
            {
                //Test request 
                case AID.SysReq:
                {
                    //Soh
                    obptr.Add(0x01);
                    //%
                    obptr.Add(0x5b);
                    // /
                    obptr.Add(0x61);
                    //stx
                    obptr.Add(0x02);
                    trace.trace_ds("SYSREQ");
                    break;
                }
                //Short-read AIDs
                case AID.PA1:
                case AID.PA2:
                case AID.PA3:
                case AID.Clear:
                {
                    if (!all)
                    {
                        shortRead = true;
                        sendData = false;
                    }

                    if (!telnet.IsSscp)
                    {
                        obptr.Add(attentionIDbyte);
                        trace.trace_ds(See.GetAidFromCode(attentionIDbyte));
                        if (shortRead)
                        {
                            goto rm_done;
                        }
                        Util.EncodeBAddress(obptr, CursorAddress);
                        trace.trace_ds(trace.rcba(CursorAddress));
                    }
                    break;
                }
                //No data on READ MODIFIED
                case AID.SELECT:
                {
                    if (!all)
                    {
                        sendData = false;
                    }

                    if (!telnet.IsSscp)
                    {
                        obptr.Add(attentionIDbyte);
                        trace.trace_ds(See.GetAidFromCode(attentionIDbyte));
                        if (shortRead)
                        {
                            goto rm_done;
                        }
                        Util.EncodeBAddress(obptr, CursorAddress);
                        trace.trace_ds(trace.rcba(CursorAddress));
                    }
                    break;
                }
                default: /* ordinary AID */
                    if (!telnet.IsSscp)
                    {
                        obptr.Add(attentionIDbyte);
                        trace.trace_ds(See.GetAidFromCode(attentionIDbyte));
                        if (shortRead)
                            goto rm_done;
                        Util.EncodeBAddress(obptr, CursorAddress);
                        trace.trace_ds(trace.rcba(CursorAddress));
                    }
                    break;
            }

            baddr = 0;
            if (Formatted)
            {
                //Find first field attribute
                do
                {
                    if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                    {
                        break;
                    }
                    IncrementAddress(ref baddr);
                } while (baddr != 0);

                sbaddr = baddr;
                do
                {
                    if (FieldAttribute.IsModified(ScreenBuffer[baddr]))
                    {
                        var any = false;

                        IncrementAddress(ref baddr);
                        obptr.Add(ControllerConstant.ORDER_SBA);
                        Util.EncodeBAddress(obptr, baddr);
                        trace.trace_ds(" SetBufferAddress%s", trace.rcba(baddr));
                        while (!FieldAttribute.IsFA(ScreenBuffer[baddr]))
                        {
                            if (sendData && ScreenBuffer[baddr] != 0)
                            {
                                InsertSaAttribtutes(obptr, baddr,
                                    ref currentFG,
                                    ref currentGR,
                                    ref currentCS,
                                    ref any);

                                if ((extendedAttributes[baddr].cs & ControllerConstant.CS_GE) != 0)
                                {
                                    obptr.Add(ControllerConstant.ORDER_GE);
                                    if (any)
                                    {
                                        trace.trace_ds("'");
                                    }
                                    trace.trace_ds(" GraphicEscape");
                                    any = false;
                                }
                                obptr.Add(Tables.Cg2Ebc[ScreenBuffer[baddr]]);
                                if (!any)
                                {
                                    trace.trace_ds(" '");
                                }
                                trace.trace_ds("%s", See.GetEbc(Tables.Cg2Ebc[ScreenBuffer[baddr]]));
                                any = true;
                            }
                            IncrementAddress(ref baddr);
                        }
                        if (any)
                        {
                            trace.trace_ds("'");
                        }
                    }
                    else
                    {
                        //Not modified - skip 
                        do
                        {
                            IncrementAddress(ref baddr);
                        } while (!FieldAttribute.IsFA(ScreenBuffer[baddr]));
                    }
                } while (baddr != sbaddr);
            }
            else
            {
                var any = false;
                var nBytes = 0;

                //If we're in SSCP-LU mode, the starting point is where the host left the cursor.
                if (telnet.IsSscp)
                {
                    baddr = sscpStart;
                }

                do
                {
                    if (ScreenBuffer[baddr] != 0)
                    {
                        InsertSaAttribtutes(obptr, baddr,
                            ref currentFG,
                            ref currentGR,
                            ref currentCS,
                            ref any);
                        if ((extendedAttributes[baddr].cs & ControllerConstant.CS_GE) != 0)
                        {
                            obptr.Add(ControllerConstant.ORDER_GE);
                            if (any)
                            {
                                trace.trace_ds("' ");
                            }
                            trace.trace_ds(" GraphicEscape ");
                            any = false;
                        }
                        obptr.Add(Tables.Cg2Ebc[ScreenBuffer[baddr]]);
                        if (!any)
                        {
                            trace.trace_ds("'");
                        }
                        trace.trace_ds(See.GetEbc(Tables.Cg2Ebc[ScreenBuffer[baddr]]));
                        any = true;
                        nBytes++;
                    }
                    IncrementAddress(ref baddr);

                    //If we're in SSCP-LU mode, end the return value at255 bytes, or where the screen wraps.
                    if (telnet.IsSscp && (nBytes >= 255 || baddr == 0))
                    {
                        break;
                    }
                } while (baddr != 0);

                if (any)
                {
                    trace.trace_ds("'");
                }
            }

            rm_done:
            trace.trace_ds("\n");
            telnet.Output(obptr);
        }


        /// <summary>
        ///     Calculate the proper 3270 DS value for an internal field attribute.
        /// </summary>
        /// <param name="fa"></param>
        /// <returns></returns>
        private byte CalculateFA(byte fa)
        {
            byte r = 0x00;

            if (FieldAttribute.IsProtected(fa))
            {
                r |= 0x20;
            }
            if (FieldAttribute.IsNumeric(fa))
            {
                r |= 0x10;
            }
            if (FieldAttribute.IsModified(fa))
            {
                r |= 0x01;
            }

            r |= (byte) ((fa & ControllerConstant.FA_INTENSITY) << 2);
            return r;
        }


        /// <summary>
        ///     Process a 3270 Read-Buffer command and transmit the data back to the host.
        /// </summary>
        /// <param name="attentionIDbyte"></param>
        public void ProcessReadBufferCommand(byte attentionIDbyte)
        {
            int baddr;
            byte fa;
            var any = false;
            var attr_count = 0;
            byte currentFG = 0x00;
            byte currentGR = 0x00;
            byte currentCS = 0x00;

            trace.trace_ds("> ");
            var obptr = new NetBuffer();
            obptr.Add(attentionIDbyte);
            Util.EncodeBAddress(obptr, CursorAddress);
            trace.trace_ds("%s%s", See.GetAidFromCode(attentionIDbyte), trace.rcba(CursorAddress));

            baddr = 0;
            do
            {
                if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                {
                    if (replyMode == ControllerConstant.SF_SRM_FIELD)
                    {
                        obptr.Add(ControllerConstant.ORDER_SF);
                    }
                    else
                    {
                        obptr.Add(ControllerConstant.ORDER_SFE);
                        attr_count = obptr.Index;
                        obptr.Add(1); /* for now */
                        obptr.Add(See.XA_3270);
                    }

                    fa = CalculateFA(ScreenBuffer[baddr]);
                    obptr.Add(ControllerConstant.CodeTable[fa]);

                    if (any)
                    {
                        trace.trace_ds("'");
                    }

                    trace.trace_ds(" StartField%s%s%s",
                        replyMode == ControllerConstant.SF_SRM_FIELD ? "" : "Extended",
                        trace.rcba(baddr), See.GetSeeAttribute(fa));

                    if (replyMode != ControllerConstant.SF_SRM_FIELD)
                    {
                        if (extendedAttributes[baddr].fg != 0)
                        {
                            obptr.Add(See.XA_FOREGROUND);
                            obptr.Add(extendedAttributes[baddr].fg);
                            trace.trace_ds("%s", See.GetEfa(See.XA_FOREGROUND, extendedAttributes[baddr].fg));
                            obptr.IncrementAt(attr_count, 1);
                        }
                        if (extendedAttributes[baddr].gr != 0)
                        {
                            obptr.Add(See.XA_HIGHLIGHTING);
                            obptr.Add(extendedAttributes[baddr].gr | 0xf0);
                            trace.trace_ds("%s",
                                See.GetEfa(See.XA_HIGHLIGHTING, (byte) (extendedAttributes[baddr].gr | 0xf0)));
                            obptr.IncrementAt(attr_count, 1);
                        }
                        if ((extendedAttributes[baddr].cs & ExtendedAttribute.CS_MASK) != 0)
                        {
                            obptr.Add(See.XA_CHARSET);
                            obptr.Add((extendedAttributes[baddr].cs & ExtendedAttribute.CS_MASK) | 0xf0);
                            trace.trace_ds("%s",
                                See.GetEfa(See.XA_CHARSET,
                                    (byte) ((extendedAttributes[baddr].cs & ExtendedAttribute.CS_MASK) | 0xf0)));
                            obptr.IncrementAt(attr_count, 1);
                        }
                    }
                    any = false;
                }
                else
                {
                    InsertSaAttribtutes(obptr, baddr,
                        ref currentFG,
                        ref currentGR,
                        ref currentCS,
                        ref any);
                    if ((extendedAttributes[baddr].cs & ControllerConstant.CS_GE) != 0)
                    {
                        obptr.Add(ControllerConstant.ORDER_GE);
                        if (any)
                        {
                            trace.trace_ds("'");
                        }
                        trace.trace_ds(" GraphicEscape");
                        any = false;
                    }
                    obptr.Add(Tables.Cg2Ebc[ScreenBuffer[baddr]]);
                    if (Tables.Cg2Ebc[ScreenBuffer[baddr]] <= 0x3f || Tables.Cg2Ebc[ScreenBuffer[baddr]] == 0xff)
                    {
                        if (any)
                        {
                            trace.trace_ds("'");
                        }

                        trace.trace_ds(" %s", See.GetEbc(Tables.Cg2Ebc[ScreenBuffer[baddr]]));
                        any = false;
                    }
                    else
                    {
                        if (!any)
                            trace.trace_ds(" '");
                        trace.trace_ds("%s", See.GetEbc(Tables.Cg2Ebc[ScreenBuffer[baddr]]));
                        any = true;
                    }
                }
                IncrementAddress(ref baddr);
            } while (baddr != 0);
            if (any)
                trace.trace_ds("'");

            trace.trace_ds("\n");
            telnet.Output(obptr);
        }


        /// <summary>
        ///     Construct a 3270 command to reproduce the current state of the display.
        /// </summary>
        /// <param name="obptr"></param>
        public void TakeBufferSnapshot(NetBuffer obptr)
        {
            var baddr = 0;
            int attr_count;
            byte current_fg = 0x00;
            byte current_gr = 0x00;
            byte current_cs = 0x00;
            byte av;

            obptr.Add(ScreenAlt ? ControllerConstant.CMD_EWA : ControllerConstant.CMD_EW);
            obptr.Add(ControllerConstant.CodeTable[0]);

            do
            {
                if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                {
                    obptr.Add(ControllerConstant.ORDER_SFE);
                    attr_count = obptr.Index; //obptr - obuf;
                    obptr.Add(1); /* for now */
                    obptr.Add(See.XA_3270);
                    obptr.Add(ControllerConstant.CodeTable[CalculateFA(ScreenBuffer[baddr])]);
                    if (extendedAttributes[baddr].fg != 0)
                    {
                        //space3270out(2);
                        obptr.Add(See.XA_FOREGROUND);
                        obptr.Add(extendedAttributes[baddr].fg);
                        obptr.IncrementAt(attr_count, 1);
                    }
                    if (extendedAttributes[baddr].gr != 0)
                    {
                        obptr.Add(See.XA_HIGHLIGHTING);
                        obptr.Add(extendedAttributes[baddr].gr | 0xf0);
                        obptr.IncrementAt(attr_count, 1);
                    }
                    if ((extendedAttributes[baddr].cs & ExtendedAttribute.CS_MASK) != 0)
                    {
                        obptr.Add(See.XA_CHARSET);
                        obptr.Add((extendedAttributes[baddr].cs & ExtendedAttribute.CS_MASK) | 0xf0);
                        obptr.IncrementAt(attr_count, 1);
                    }
                }
                else
                {
                    av = extendedAttributes[baddr].fg;
                    if (current_fg != av)
                    {
                        current_fg = av;
                        obptr.Add(ControllerConstant.ORDER_SA);
                        obptr.Add(See.XA_FOREGROUND);
                        obptr.Add(av);
                    }
                    av = extendedAttributes[baddr].gr;
                    if (av != 0)
                        av |= 0xf0;
                    if (current_gr != av)
                    {
                        current_gr = av;
                        obptr.Add(ControllerConstant.ORDER_SA);
                        obptr.Add(See.XA_HIGHLIGHTING);
                        obptr.Add(av);
                    }
                    av = (byte) (extendedAttributes[baddr].cs & ExtendedAttribute.CS_MASK);
                    if (av != 0)
                        av |= 0xf0;
                    if (current_cs != av)
                    {
                        current_cs = av;
                        obptr.Add(ControllerConstant.ORDER_SA);
                        obptr.Add(See.XA_CHARSET);
                        obptr.Add(av);
                    }
                    if ((extendedAttributes[baddr].cs & ControllerConstant.CS_GE) != 0)
                    {
                        obptr.Add(ControllerConstant.ORDER_GE);
                    }
                    obptr.Add(Tables.Cg2Ebc[ScreenBuffer[baddr]]);
                }
                IncrementAddress(ref baddr);
            } while (baddr != 0);

            obptr.Add(ControllerConstant.ORDER_SBA);
            Util.EncodeBAddress(obptr, CursorAddress);
            obptr.Add(ControllerConstant.ORDER_IC);
        }


        /// <summary>
        ///     Construct a 3270 command to reproduce the reply mode.
        ///     Returns a bool indicating if one is necessary.
        /// </summary>
        /// <param name="obptr"></param>
        /// <returns></returns>
        private bool TakeReplyModeSnapshot(NetBuffer obptr)
        {
            var success = false;

            if (telnet.Is3270 && replyMode != ControllerConstant.SF_SRM_FIELD)
            {
                obptr.Add(ControllerConstant.CMD_WSF);
                obptr.Add(0x00); //Implicit length
                obptr.Add(0x00);
                obptr.Add(ControllerConstant.SF_SET_REPLY_MODE);
                obptr.Add(0x00); //Partition 0
                obptr.Add(replyMode);
                if (replyMode == ControllerConstant.SF_SRM_CHAR)
                {
                    for (var i = 0; i < CrmnAttribute; i++)
                    {
                        obptr.Add(CrmAttributes[i]);
                    }
                }
                success = true;
            }

            return success;
        }

        /// <summary>
        ///     Process a 3270 Erase All Unprotected command.
        /// </summary>
        public void ProcessEraseAllUnprotectedCommand()
        {
            int baddr, sbaddr;
            byte fa;
            bool f;

            telnet.Keyboard.ToggleEnterInhibitMode(false);

            OnAllChanged();

            if (Formatted)
            {
                //Find first field attribute
                baddr = 0;
                do
                {
                    if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                    {
                        break;
                    }
                    IncrementAddress(ref baddr);
                } while (baddr != 0);
                sbaddr = baddr;
                f = false;
                do
                {
                    fa = ScreenBuffer[baddr];
                    if (!FieldAttribute.IsProtected(fa))
                    {
                        MDTClear(ScreenBuffer, baddr);
                        do
                        {
                            IncrementAddress(ref baddr);
                            if (!f)
                            {
                                SetCursorAddress(baddr);
                                f = true;
                            }
                            if (!FieldAttribute.IsFA(ScreenBuffer[baddr]))
                            {
                                AddCharacter(baddr, CharacterGenerator.Null, 0);
                            }
                        } while (!FieldAttribute.IsFA(ScreenBuffer[baddr]));
                    }
                    else
                    {
                        do
                        {
                            IncrementAddress(ref baddr);
                        } while (!FieldAttribute.IsFA(ScreenBuffer[baddr]));
                    }
                } while (baddr != sbaddr);
                if (!f)
                {
                    SetCursorAddress(0);
                }
            }
            else
            {
                Clear(true);
            }
            AttentionID = AID.None;
            telnet.Keyboard.ResetKeyboardLock(false);
        }


        private void EndText()
        {
            if (previous == PreviousEnum.Text)
            {
                trace.trace_ds("'");
            }
        }


        private void EndText(string cmd)
        {
            EndText();
            trace.trace_ds(" " + cmd);
        }


        private byte AttributeToFA(byte attr)
        {
            return (byte) (ControllerConstant.FA_BASE |
                           ((attr & 0x20) != 0 ? ControllerConstant.FA_PROTECT : 0) |
                           ((attr & 0x10) != 0 ? ControllerConstant.FA_NUMERIC : 0) |
                           ((attr & 0x01) != 0 ? ControllerConstant.FA_MODIFY : 0) |
                           ((attr >> 2) & ControllerConstant.FA_INTENSITY));
        }


        private void StartFieldWithFA(byte fa)
        {
            //current_fa = screen_buf[buffer_addr]; 
            currentFaIndex = bufferAddress;
            AddCharacter(bufferAddress, fa, 0);
            SetForegroundColor(bufferAddress, 0);
            ctlr_add_gr(bufferAddress, 0);
            trace.trace_ds(See.GetSeeAttribute(fa));
            Formatted = true;
        }


        private void StartField()
        {
            StartFieldWithFA(ControllerConstant.FA_BASE);
        }


        private void StartFieldWithAttribute(byte attr)
        {
            var new_attr = AttributeToFA(attr);
            StartFieldWithFA(new_attr);
        }

        /// <summary>
        ///     Process a 3270 Write command.
        /// </summary>
        /// <param name="buf"></param>
        /// <param name="start"></param>
        /// <param name="length"></param>
        /// <param name="erase"></param>
        /// <returns></returns>
        public PDS ProcessWriteCommand(byte[] buf, int start, int length, bool erase)
        {
            var packetwasjustresetrewrite = false;
            int baddr;
            byte newAttr;
            bool lastCommand;
            bool lastZpt;
            bool wccKeyboardRestore;
            bool wccSoundAlarm;
            bool raGe;
            byte na;
            int anyFA;
            byte efaFG;
            byte efaGR;
            byte efaCS;
            var paren = "(";
            defaultFg = 0;
            defaultGr = 0;
            defaultCs = 0;
            tracePrimed = true;


            trace.WriteLine("::ctlr_write::" + (DateTime.Now.Ticks - startTime)/10000 + " " + length + " bytes");

            // ResetRewrite is just : 00 00 00 00 00 f1 c2 ff ef
            if (length == 4 &&
                buf[start + 0] == 0xf1 &&
                buf[start + 1] == 0xc2 &&
                buf[start + 2] == 0xff &&
                buf[start + 3] == 0xef)
            {
                trace.WriteLine(
                    "****Identified packet as a reset/rewrite combination. patch 29/Mar/2005 assumes more data will follow so does not notify user yet");
                packetwasjustresetrewrite = true;
            }

            var rv = PDS.OkayNoOutput;

            telnet.Keyboard.ToggleEnterInhibitMode(false);

            if (buf.Length < 2)
            {
                return PDS.BadCommand;
            }

            bufferAddress = CursorAddress;
            if (See.WCC_RESET(buf[start + 1]))
            {
                if (erase)
                {
                    replyMode = ControllerConstant.SF_SRM_FIELD;
                }
                trace.trace_ds("%sreset", paren);
                paren = ",";
            }
            wccSoundAlarm = See.WCC_SOUND_ALARM(buf[start + 1]);

            if (wccSoundAlarm)
            {
                trace.trace_ds("%salarm", paren);
                paren = ",";
            }
            wccKeyboardRestore = See.WCC_KEYBOARD_RESTORE(buf[start + 1]);

            if (wccKeyboardRestore)
            {
                //Console.WriteLine("2218::ticking_stop");
                //ticking_stop();
            }

            if (wccKeyboardRestore)
            {
                trace.trace_ds("%srestore", paren);
                paren = ",";
            }

            if (See.WCC_RESET_MDT(buf[start + 1]))
            {
                trace.trace_ds("%sresetMDT", paren);
                paren = ",";
                baddr = 0;
                if (appres.modified_sel)
                {
                    OnAllChanged();
                }

                do
                {
                    if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                    {
                        MDTClear(ScreenBuffer, baddr);
                    }
                    IncrementAddress(ref baddr);
                } while (baddr != 0);
            }
            if (paren != "(")
            {
                trace.trace_ds(")");
            }

            lastCommand = true;
            lastZpt = false;
            currentFaIndex = GetFieldAttribute(bufferAddress);

            for (var cp = 2; cp < length; cp++)
            {
                switch (buf[cp + start])
                {
                    //Start field
                    case ControllerConstant.ORDER_SF:
                    {
                        EndText("StartField");
                        if (previous != PreviousEnum.SBA)
                        {
                            trace.trace_ds(trace.rcba(bufferAddress));
                        }
                        previous = PreviousEnum.Order;

                        //Skip field attribute
                        cp++;
                        StartFieldWithAttribute(buf[cp + start]);
                        SetForegroundColor(bufferAddress, 0);
                        IncrementAddress(ref bufferAddress);
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }

                    //Set buffer address
                    case ControllerConstant.ORDER_SBA:
                    {
                        //Skip buffer address
                        cp += 2;
                        bufferAddress = Util.DecodeBAddress(buf[cp + start - 1], buf[cp + start]);
                        EndText("SetBufferAddress");
                        previous = PreviousEnum.SBA;
                        trace.trace_ds(trace.rcba(bufferAddress));
                        if (bufferAddress >= ColumnCount*RowCount)
                        {
                            trace.trace_ds(" [invalid address, write command terminated]\n");
                            // Let a script go.
                            telnet.Events.RunScript("ctlr_write SBA_ERROR");
                            return PDS.BadAddress;
                        }
                        currentFaIndex = GetFieldAttribute(bufferAddress);
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    //Insert cursor
                    case ControllerConstant.ORDER_IC:
                    {
                        EndText("InsertCursor");
                        if (previous != PreviousEnum.SBA)
                        {
                            trace.trace_ds(trace.rcba(bufferAddress));
                        }
                        previous = PreviousEnum.Order;
                        SetCursorAddress(bufferAddress);
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    //Program tab
                    case ControllerConstant.ORDER_PT:
                    {
                        EndText("ProgramTab");
                        previous = PreviousEnum.Order;
                        //If the buffer address is the field attribute of of an unprotected field, simply advance one position.
                        if (FieldAttribute.IsFA(ScreenBuffer[bufferAddress]) &&
                            !FieldAttribute.IsProtected(ScreenBuffer[bufferAddress]))
                        {
                            IncrementAddress(ref bufferAddress);
                            lastZpt = false;
                            lastCommand = true;
                            break;
                        }

                        //Otherwise, advance to the first position of the next unprotected field.
                        baddr = GetNextUnprotectedField(bufferAddress);
                        if (baddr < bufferAddress)
                        {
                            baddr = 0;
                        }

                        //Null out the remainder of the current field -- even if protected -- if the PT doesn't follow a command
                        //or order, or (honestly) if the last order we saw was a null-filling PT that left the buffer address at 0.
                        if (!lastCommand || lastZpt)
                        {
                            trace.trace_ds("(nulling)");
                            while ((bufferAddress != baddr) && !FieldAttribute.IsFA(ScreenBuffer[bufferAddress]))
                            {
                                AddCharacter(bufferAddress, CharacterGenerator.Null, 0);
                                IncrementAddress(ref bufferAddress);
                            }
                            if (baddr == 0)
                            {
                                lastZpt = true;
                            }
                        }
                        else
                        {
                            lastZpt = false;
                        }
                        bufferAddress = baddr;
                        lastCommand = true;
                        break;
                    }
                    //Repeat to address
                    case ControllerConstant.ORDER_RA:
                    {
                        EndText("RepeatToAddress");
                        //Skip buffer address
                        cp += 2;
                        baddr = Util.DecodeBAddress(buf[cp + start - 1], buf[cp + start]);
                        trace.trace_ds(trace.rcba(baddr));
                        //Skip char to repeat
                        cp++;
                        if (buf[cp + start] == ControllerConstant.ORDER_GE)
                        {
                            raGe = true;
                            trace.trace_ds("GraphicEscape");
                            cp++;
                        }
                        else
                        {
                            raGe = false;
                        }
                        previous = PreviousEnum.Order;
                        if (buf[cp + start] != 0)
                        {
                            trace.trace_ds("'");
                        }
                        trace.trace_ds("%s", See.GetEbc(buf[cp + start]));
                        if (buf[cp + start] != 0)
                        {
                            trace.trace_ds("'");
                        }
                        if (baddr >= ColumnCount*RowCount)
                        {
                            trace.trace_ds(" [invalid address, write command terminated]\n");
                            // Let a script go.
                            telnet.Events.RunScript("ctlr_write baddr>COLS*ROWS");
                            return PDS.BadAddress;
                        }
                        do
                        {
                            if (raGe)
                            {
                                AddCharacter(bufferAddress, Tables.Ebc2Cg0[buf[cp + start]], ControllerConstant.CS_GE);
                            }
                            else if (defaultCs != 0)
                            {
                                AddCharacter(bufferAddress, Tables.Ebc2Cg0[buf[cp + start]], 1);
                            }
                            else
                            {
                                AddCharacter(bufferAddress, Tables.Ebc2Cg[buf[cp + start]], 0);
                            }

                            SetForegroundColor(bufferAddress, defaultFg);
                            ctlr_add_gr(bufferAddress, defaultGr);
                            IncrementAddress(ref bufferAddress);
                        } while (bufferAddress != baddr);

                        currentFaIndex = GetFieldAttribute(bufferAddress);
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    //Erase unprotected to address
                    case ControllerConstant.ORDER_EUA:
                    {
                        //Skip buffer address
                        cp += 2;
                        baddr = Util.DecodeBAddress(buf[cp + start - 1], buf[cp + start]);
                        EndText("EraseUnprotectedAll");
                        if (previous != PreviousEnum.SBA)
                        {
                            trace.trace_ds(trace.rcba(baddr));
                        }
                        previous = PreviousEnum.Order;
                        if (baddr >= ColumnCount*RowCount)
                        {
                            trace.trace_ds(" [invalid address, write command terminated]\n");
                            //Let a script go.
                            telnet.Events.RunScript("ctlr_write baddr>COLS*ROWS#2");
                            return PDS.BadAddress;
                        }
                        do
                        {
                            if (FieldAttribute.IsFA(ScreenBuffer[bufferAddress]))
                            {
                                currentFaIndex = bufferAddress;
                            }
                            else if (!FieldAttribute.IsProtected(ScreenBuffer[currentFaIndex]))
                            {
                                AddCharacter(bufferAddress, CharacterGenerator.Null, 0);
                            }

                            IncrementAddress(ref bufferAddress);
                        } while (bufferAddress != baddr);

                        currentFaIndex = GetFieldAttribute(bufferAddress);

                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    //Graphic escape 
                    case ControllerConstant.ORDER_GE:
                    {
                        EndText("GraphicEscape ");
                        cp++;
                        previous = PreviousEnum.Order;
                        if (buf[cp + start] != 0)
                        {
                            trace.trace_ds("'");
                        }
                        trace.trace_ds("%s", See.GetEbc(buf[cp + start]));
                        if (buf[cp + start] != 0)
                        {
                            trace.trace_ds("'");
                        }

                        AddCharacter(bufferAddress, Tables.Ebc2Cg0[buf[cp + start]], ControllerConstant.CS_GE);
                        SetForegroundColor(bufferAddress, defaultFg);
                        ctlr_add_gr(bufferAddress, defaultGr);
                        IncrementAddress(ref bufferAddress);

                        currentFaIndex = GetFieldAttribute(bufferAddress);
                        lastCommand = false;
                        lastZpt = false;
                        break;
                    }
                    //Modify field
                    case ControllerConstant.ORDER_MF:
                    {
                        EndText("ModifyField");
                        if (previous != PreviousEnum.SBA)
                        {
                            trace.trace_ds(trace.rcba(bufferAddress));
                        }
                        previous = PreviousEnum.Order;
                        cp++;
                        na = buf[cp + start];
                        if (FieldAttribute.IsFA(ScreenBuffer[bufferAddress]))
                        {
                            for (var i = 0; i < (int) na; i++)
                            {
                                cp++;
                                if (buf[cp + start] == See.XA_3270)
                                {
                                    trace.trace_ds(" 3270");
                                    cp++;
                                    newAttr = AttributeToFA(buf[cp + start]);
                                    AddCharacter(bufferAddress, newAttr, 0);
                                    trace.trace_ds(See.GetSeeAttribute(newAttr));
                                }
                                else if (buf[cp + start] == See.XA_FOREGROUND)
                                {
                                    trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                    cp++;
                                    if (appres.m3279)
                                    {
                                        SetForegroundColor(bufferAddress, buf[cp + start]);
                                    }
                                }
                                else if (buf[cp + start] == See.XA_HIGHLIGHTING)
                                {
                                    trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                    cp++;
                                    ctlr_add_gr(bufferAddress, (byte) (buf[cp + start] & 0x07));
                                }
                                else if (buf[cp + start] == See.XA_CHARSET)
                                {
                                    var cs = 0;

                                    trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                    cp++;
                                    if (buf[cp + start] == 0xf1)
                                    {
                                        cs = 1;
                                    }
                                    AddCharacter(bufferAddress, ScreenBuffer[bufferAddress], (byte) cs);
                                }
                                else if (buf[cp + start] == See.XA_ALL)
                                {
                                    trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                    cp++;
                                }
                                else
                                {
                                    trace.trace_ds("%s[unsupported]", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                    cp++;
                                }
                            }
                            IncrementAddress(ref bufferAddress);
                        }
                        else
                            cp += na*2;
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    //Start field extended
                    case ControllerConstant.ORDER_SFE:
                    {
                        EndText("StartFieldExtended");
                        if (previous != PreviousEnum.SBA)
                        {
                            trace.trace_ds(trace.rcba(bufferAddress));
                        }
                        previous = PreviousEnum.Order;
                        //Skip order
                        cp++;
                        na = buf[cp + start];
                        anyFA = 0;
                        efaFG = 0;
                        efaGR = 0;
                        efaCS = 0;
                        for (var i = 0; i < (int) na; i++)
                        {
                            cp++;
                            if (buf[cp + start] == See.XA_3270)
                            {
                                trace.trace_ds(" 3270");
                                cp++;
                                StartFieldWithAttribute(buf[cp + start]);
                                anyFA++;
                            }
                            else if (buf[cp + start] == See.XA_FOREGROUND)
                            {
                                trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                cp++;
                                if (appres.m3279)
                                {
                                    efaFG = buf[cp + start];
                                }
                            }
                            else if (buf[cp + start] == See.XA_HIGHLIGHTING)
                            {
                                trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                cp++;
                                efaGR = (byte) (buf[cp + start] & 0x07);
                            }
                            else if (buf[cp + start] == See.XA_CHARSET)
                            {
                                trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                cp++;
                                if (buf[cp + start] == 0xf1)
                                {
                                    efaCS = 1;
                                }
                            }
                            else if (buf[cp + start] == See.XA_ALL)
                            {
                                trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                cp++;
                            }
                            else
                            {
                                trace.trace_ds("%s[unsupported]", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                                cp++;
                            }
                        }
                        if (anyFA == 0)
                        {
                            StartFieldWithFA(ControllerConstant.FA_BASE);
                        }
                        AddCharacter(bufferAddress, ScreenBuffer[bufferAddress], efaCS);
                        SetForegroundColor(bufferAddress, efaFG);
                        ctlr_add_gr(bufferAddress, efaGR);
                        IncrementAddress(ref bufferAddress);
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    //Set attribute
                    case ControllerConstant.ORDER_SA:
                    {
                        EndText("SetAttribtue");
                        previous = PreviousEnum.Order;
                        cp++;
                        if (buf[cp + start] == See.XA_FOREGROUND)
                        {
                            trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                            if (appres.m3279)
                            {
                                defaultFg = buf[cp + start + 1];
                            }
                        }
                        else if (buf[cp + start] == See.XA_HIGHLIGHTING)
                        {
                            trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                            defaultGr = (byte) (buf[cp + start + 1] & 0x07);
                        }
                        else if (buf[cp + start] == See.XA_ALL)
                        {
                            trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                            defaultFg = 0;
                            defaultGr = 0;
                            defaultCs = 0;
                        }
                        else if (buf[cp + start] == See.XA_CHARSET)
                        {
                            trace.trace_ds("%s", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                            defaultCs = buf[cp + start + 1] == 0xf1 ? (byte) 1 : (byte) 0;
                        }
                        else
                            trace.trace_ds("%s[unsupported]", See.GetEfa(buf[cp + start], buf[cp + start + 1]));
                        cp++;
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    //Format control orders
                    case ControllerConstant.FCORDER_SUB:
                    case ControllerConstant.FCORDER_DUP:
                    case ControllerConstant.FCORDER_FM:
                    case ControllerConstant.FCORDER_FF:
                    case ControllerConstant.FCORDER_CR:
                    case ControllerConstant.FCORDER_NL:
                    case ControllerConstant.FCORDER_EM:
                    case ControllerConstant.FCORDER_EO:
                    {
                        EndText(See.GetEbc(buf[cp + start]));
                        previous = PreviousEnum.Order;
                        AddCharacter(bufferAddress, Tables.Ebc2Cg[buf[cp + start]], defaultCs);
                        SetForegroundColor(bufferAddress, defaultFg);
                        ctlr_add_gr(bufferAddress, defaultGr);
                        IncrementAddress(ref bufferAddress);
                        lastCommand = true;
                        lastZpt = false;
                        break;
                    }
                    case ControllerConstant.FCORDER_NULL:
                    {
                        EndText("NULL");
                        previous = PreviousEnum.NullCharacter;
                        AddCharacter(bufferAddress, Tables.Ebc2Cg[buf[cp + start]], defaultCs);
                        SetForegroundColor(bufferAddress, defaultFg);
                        ctlr_add_gr(bufferAddress, defaultGr);
                        IncrementAddress(ref bufferAddress);
                        lastCommand = false;
                        lastZpt = false;
                        break;
                    }
                    //Enter character
                    default:
                    {
                        if (buf[cp + start] <= 0x3F)
                        {
                            EndText("ILLEGAL_ORDER");
                            trace.trace_ds("%s", See.GetEbc(buf[cp + start]));
                            lastCommand = true;
                            lastZpt = false;
                            break;
                        }
                        if (previous != PreviousEnum.Text)
                            trace.trace_ds(" '");
                        previous = PreviousEnum.Text;
                        trace.trace_ds("%s", See.GetEbc(buf[cp + start]));
                        AddCharacter(bufferAddress, Tables.Ebc2Cg[buf[cp + start]], defaultCs);
                        SetForegroundColor(bufferAddress, defaultFg);
                        ctlr_add_gr(bufferAddress, defaultGr);
                        IncrementAddress(ref bufferAddress);
                        lastCommand = false;
                        lastZpt = false;
                        break;
                    }
                }
            }

            SetFormattedFlag();

            if (previous == PreviousEnum.Text)
            {
                trace.trace_ds("'");
            }

            trace.trace_ds("\n");
            if (wccKeyboardRestore)
            {
                AttentionID = AID.None;
                telnet.Keyboard.ResetKeyboardLock(false);
            }
            else if ((telnet.Keyboard.keyboardLock & KeyboardConstants.OiaTWait) != 0)
            {
                telnet.Keyboard.KeyboardLockClear(KeyboardConstants.OiaTWait, "ctlr_write");
                //status_syswait();
            }
            if (wccSoundAlarm)
            {
                //	ring_bell();
            }

            tracePrimed = false;

            ProcessPendingInput();

            //Let a script go.
            if (!packetwasjustresetrewrite)
            {
                telnet.Events.RunScript("ctlr_write - end");
                try
                {
                    NotifyDataAvailable();
                }
                catch
                {
                }
            }

            return rv;
        }


        private void NotifyDataAvailable()
        {
            lock (dataAvailablePadlock)
            {
                if (telnet != null)
                {
                    dataAvailableCount = telnet.StartedReceivingCount;
                }
                else
                {
                    dataAvailableCount++;
                }
            }
            var rcvCnt = 0;
            if (telnet != null)
            {
                rcvCnt = telnet.StartedReceivingCount;
            }
            trace.trace_dsn("NotifyDataAvailable : dataReceivedCount = " + rcvCnt + "  dataAvailableCount = " +
                            DataAvailableCount + Environment.NewLine);
        }


        /// <summary>
        ///     Write SSCP-LU data, which is quite a bit dumber than regular 3270 output.
        /// </summary>
        /// <param name="buf"></param>
        /// <param name="start"></param>
        /// <param name="buflen"></param>
        public void WriteSspcLuData(byte[] buf, int start, int buflen)
        {
            int i;
            var cp = start;
            int sRow;
            byte c;
            int baddr;
            byte fa;


            //The 3174 Functionl Description says that anything but NL, NULL, FM, or DUP is to be displayed as a graphic.  However, to deal with
            //badly-behaved hosts, we filter out SF, IC and SBA sequences, andwe display other control codes as spaces.
            trace.trace_ds("SSCP-LU data\n");
            cp = start;
            for (i = 0; i < buflen; cp++, i++)
            {
                switch (buf[cp])
                {
                    case ControllerConstant.FCORDER_NL:

                        //Insert NULLs to the end of the line and advance to the beginning of the next line.
                        sRow = bufferAddress/ColumnCount;
                        while (bufferAddress/ColumnCount == sRow)
                        {
                            AddCharacter(bufferAddress, Tables.Ebc2Cg[0], defaultCs);
                            SetForegroundColor(bufferAddress, defaultFg);
                            ctlr_add_gr(bufferAddress, defaultGr);
                            IncrementAddress(ref bufferAddress);
                        }
                        break;

                    case ControllerConstant.ORDER_SF: /* some hosts forget their talking SSCP-LU */
                        cp++;
                        i++;
                        fa = AttributeToFA(buf[cp]);
                        trace.trace_ds(" StartField" + trace.rcba(bufferAddress) + " " + See.GetSeeAttribute(fa) +
                                       " [translated to space]\n");
                        AddCharacter(bufferAddress, CharacterGenerator.Space, defaultCs);
                        SetForegroundColor(bufferAddress, defaultFg);
                        ctlr_add_gr(bufferAddress, defaultGr);
                        IncrementAddress(ref bufferAddress);
                        break;
                    case ControllerConstant.ORDER_IC:
                        trace.trace_ds(" InsertCursor%s [ignored]\n", trace.rcba(bufferAddress));
                        break;
                    case ControllerConstant.ORDER_SBA:
                        baddr = Util.DecodeBAddress(buf[cp + 1], buf[cp + 2]);
                        trace.trace_ds(" SetBufferAddress%s [ignored]\n", trace.rcba(baddr));
                        cp += 2;
                        i += 2;
                        break;

                    case ControllerConstant.ORDER_GE:
                        cp++;
                        if (++i >= buflen)
                            break;
                        if (buf[cp] <= 0x40)
                            c = CharacterGenerator.Space;
                        else
                            c = Tables.Ebc2Cg0[buf[cp]];
                        AddCharacter(bufferAddress, c, ControllerConstant.CS_GE);
                        SetForegroundColor(bufferAddress, defaultFg);
                        ctlr_add_gr(bufferAddress, defaultGr);
                        IncrementAddress(ref bufferAddress);
                        break;

                    default:
                        if (buf[cp] == ControllerConstant.FCORDER_NULL)
                            c = CharacterGenerator.Space;
                        else if (buf[cp] == ControllerConstant.FCORDER_FM)
                            c = CharacterGenerator.Asterisk;
                        else if (buf[cp] == ControllerConstant.FCORDER_DUP)
                            c = CharacterGenerator.Semicolon;
                        else if (buf[cp] < 0x40)
                        {
                            trace.trace_ds(" X'" + buf[cp] + "') [translated to space]\n");
                            c = CharacterGenerator.Space; /* technically not necessary */
                        }
                        else
                            c = Tables.Ebc2Cg[buf[cp]];
                        AddCharacter(bufferAddress, c, defaultCs);
                        SetForegroundColor(bufferAddress, defaultFg);
                        ctlr_add_gr(bufferAddress, defaultGr);
                        IncrementAddress(ref bufferAddress);
                        break;
                }
            }
            SetCursorAddress(bufferAddress);
            sscpStart = bufferAddress;

            /* Unlock the keyboard. */
            AttentionID = AID.None;
            telnet.Keyboard.ResetKeyboardLock(false);

            /* Let a script go. */
            telnet.Events.RunScript("ctlr_write_sscp_lu done");
            //sms_host_output();
        }


        public void ProcessPendingInput()
        {
            //Process type ahead queue
            while (telnet.Keyboard.RunTypeAhead()) ;
            //Notify script we're ok
            //Console.WriteLine("--sms_continue");

            Continue();
        }


        public void Continue()
        {
            lock (telnet)
            {
                switch (telnet.WaitState)
                {
                    case SmsState.Idle:
                        break;
                    case SmsState.KBWait:
                        if (telnet.IsKeyboardInWait)
                        {
                            telnet.WaitEvent1.Set();
                        }
                        break;
                    case SmsState.WaitAnsi:
                        if (telnet.IsAnsi)
                        {
                            telnet.WaitEvent1.Set();
                        }
                        break;
                    case SmsState.Wait3270:
                        if (telnet.Is3270 | telnet.IsSscp)
                        {
                            telnet.WaitEvent1.Set();
                        }
                        break;
                    case SmsState.Wait:
                        if (!telnet.CanProceed)
                            break;
                        if (telnet.IsPending ||
                            (telnet.IsConnected && (telnet.Keyboard.keyboardLock & KeyboardConstants.AwaitingFirst) != 0))
                            break;
                        // do stuff
                        telnet.WaitEvent1.Set();

                        break;
                    case SmsState.ConnectWait:
                        if (telnet.IsPending ||
                            (telnet.IsConnected && (telnet.Keyboard.keyboardLock & KeyboardConstants.AwaitingFirst) != 0))
                            break;
                        // do stuff
                        telnet.WaitEvent1.Set();
                        break;
                    default:
                        Console.WriteLine("**BUGBUG**IGNORED STATE " + telnet.WaitState);
                        break;
                }
            }
        }


        /// <summary>
        ///     Clear the text (non-status) portion of the display.  Also resets the cursor and buffer addresses and extended
        ///     attributes.
        /// </summary>
        /// <param name="can_snap"></param>
        public void Clear(bool can_snap)
        {
            /* Snap any data that is about to be lost into the trace file. */
            if (StreamHasData)
            {
                if (can_snap && !traceSkipping && appres.Toggled(Appres.ScreenTrace))
                {
                    trace.trace_screen();
                }
                //		scroll_save(maxROWS, ever_3270 ? false : true);
            }
            traceSkipping = false;

            /* Clear the screen. */
            int i;
            for (i = 0; i < RowCount*ColumnCount; i++)
            {
                ScreenBuffer[i] = 0;
                // CFC,Jr. 8/23/2008
                // Clear the ExtendedAttributes instead of creating new ones
                //ea_buf[i] = new ExtendedAttribute();
                extendedAttributes[i].Clear();
            }
            //memset((char *)screen_buf, 0, ROWS*COLS);
            //memset((char *)ea_buf, 0, ROWS*COLS*sizeof(struct ea));
            OnAllChanged();
            SetCursorAddress(0);
            bufferAddress = 0;
            //	unselect(0, ROWS*COLS);
            Formatted = false;
            defaultFg = 0;
            defaultGr = 0;
            sscpStart = 0;
        }


        /// <summary>
        ///     Fill the screen buffer with blanks.
        /// </summary>
        private void BlankOutScreen()
        {
            int i;
            for (i = 0; i < RowCount*ColumnCount; i++)
            {
                ScreenBuffer[i] = CharacterGenerator.Space;
            }
            OnAllChanged();
            SetCursorAddress(0);
            bufferAddress = 0;
            //	unselect(0, ROWS*COLS);
            Formatted = false;
        }


        /// <summary>
        ///     Change a character in the 3270 buffer.
        /// </summary>
        /// <param name="baddr"></param>
        /// <param name="c"></param>
        /// <param name="cs"></param>
        public void AddCharacter(int baddr, byte c, byte cs)
        {
            byte oc;
            var ch = Convert.ToChar(Tables.Cg2Ascii[c]);

            if ((oc = ScreenBuffer[baddr]) != c || extendedAttributes[baddr].cs != cs)
            {
                if (tracePrimed && !IsBlank(oc))
                {
                    if (appres.Toggled(Appres.ScreenTrace))
                    {
                        trace.trace_screen();
                    }
                    //			scroll_save(maxROWS, false);
                    tracePrimed = false;
                }
                //		if (SELECTED(baddr))
                //			unselect(baddr, 1);
                OnOneChanged(baddr);
                ScreenBuffer[baddr] = c;
                extendedAttributes[baddr].cs = cs;
            }
            //			Dump();
        }

        /*
		 * Change the graphic rendition of a character in the 3270 buffer.
		 */

        public void ctlr_add_gr(int baddr, byte gr)
        {
            if (extendedAttributes[baddr].gr != gr)
            {
                //		if (SELECTED(baddr))
                //			unselect(baddr, 1);
                OnOneChanged(baddr);
                extendedAttributes[baddr].gr = gr;
                //		if (gr & GR_BLINK)
                //			blink_start();
            }
        }


        /// <summary>
        ///     Change the foreground color for a character in the 3270 buffer.
        /// </summary>
        /// <param name="baddr"></param>
        /// <param name="color"></param>
        public void SetForegroundColor(int baddr, byte color)
        {
            if (appres.m3279)
            {
                if ((color & 0xf0) != 0xf0)
                {
                    color = 0;
                }
                if (extendedAttributes[baddr].fg != color)
                {
                    //		if (SELECTED(baddr))
                    //			unselect(baddr, 1);
                    OnOneChanged(baddr);
                    extendedAttributes[baddr].fg = color;
                }
            }
        }


        /// <summary>
        ///     Change the background color for a character in the 3270 buffer.
        /// </summary>
        /// <param name="baddr"></param>
        /// <param name="color"></param>
        public void SetBackgroundColor(int baddr, byte color)
        {
            if (appres.m3279)
            {
                if ((color & 0xf0) != 0xf0)
                {
                    color = 0;
                }
                if (extendedAttributes[baddr].bg != color)
                {
                    //		if (SELECTED(baddr))
                    //			unselect(baddr, 1);
                    OnOneChanged(baddr);
                    extendedAttributes[baddr].bg = color;
                }
            }
        }


        /// <summary>
        ///     Copy a block of characters in the 3270 buffer, optionally including all of the extended attributes.
        ///     (The character set, which is actually kept in the extended attributes, is considered part of the characters here.)
        /// </summary>
        /// <param name="fromAddress"></param>
        /// <param name="toAddress"></param>
        /// <param name="count"></param>
        /// <param name="moveExtendedAttributes"></param>
        public void CopyBlock(int fromAddress, int toAddress, int count, bool moveExtendedAttributes)
        {
            var changed = false;

            var any = 0;
            int start, end, inc;

            if (toAddress < fromAddress || fromAddress + count < toAddress)
            {
                // Scan forward
                start = 0;
                end = count + 1;
                inc = 1;
            }
            else
            {
                // Scan backward
                start = count - 1;
                end = -1;
                inc = -1;
            }

            for (var i = start; i != end; i += inc)
            {
                if (ScreenBuffer[fromAddress + i] != ScreenBuffer[toAddress + i])
                {
                    ScreenBuffer[toAddress + i] = ScreenBuffer[fromAddress + i];
                    changed = true;
                }
            }

            if (changed)
            {
                OnRegionChanged(toAddress, toAddress + count);
                /*
				 * For the time being, if any selected text shifts around on
				 * the screen, unhighlight it.  Eventually there should be
				 * logic for preserving the highlight if the *all* of the
				 * selected text moves.
				 */
                //if (area_is_selected(baddr_to, count))
                //	unselect(baddr_to, count);
            }


            //If we aren't supposed to move all the extended attributes, move the character sets separately.

            if (!moveExtendedAttributes)
            {
                for (var i = start; i != end; i += inc)
                {
                    if (extendedAttributes[toAddress + i].cs != extendedAttributes[fromAddress + i].cs)
                    {
                        extendedAttributes[toAddress + i].cs = extendedAttributes[fromAddress + i].cs;
                        OnRegionChanged(toAddress + i, toAddress + i + 1);
                        any++;
                    }
                }
                //if (any && area_is_selected(baddr_to, count))
                //	unselect(baddr_to, count);
            }

            //Move extended attributes.
            if (moveExtendedAttributes)
            {
                changed = false;
                for (var i = 0; i < count; i++)
                {
                    if (extendedAttributes[fromAddress + i] != extendedAttributes[toAddress + i])
                    {
                        extendedAttributes[fromAddress + i] = extendedAttributes[toAddress + i];
                        changed = true;
                    }
                }
                if (changed)
                {
                    OnRegionChanged(toAddress, toAddress + count);
                }
            }
        }


        /// <summary>
        ///     Erase a region of the 3270 buffer, optionally clearing extended attributes as well.
        /// </summary>
        /// <param name="baddr"></param>
        /// <param name="count"></param>
        /// <param name="clear_ea"></param>
        public void EraseRegion(int baddr, int count, bool clear_ea)
        {
            //Console.WriteLine("ctlr_aclear - bugbug - compare to c code");
            int i;
            var changed = false;
            for (i = 0; i < count; i++)
            {
                if (ScreenBuffer[baddr] != 0)
                {
                    ScreenBuffer[baddr] = 0;
                    changed = true;
                }
            }
            if (changed)
            {
                OnRegionChanged(baddr, baddr + count);
                //		if (area_is_selected(baddr, count))
                //			unselect(baddr, count);
            }
            if (clear_ea)
            {
                changed = false;
                for (i = 0; i < count; i++)
                {
                    if (!extendedAttributes[baddr + i].IsZero)
                    {
                        // CFC,Jr. 8/23/2008
                        // Clear the ExtendedAttributes instead of creating new ones
                        //ea_buf[baddr + i] = new ExtendedAttribute();
                        extendedAttributes[baddr + i].Clear();
                        changed = true;
                    }
                }
                if (changed)
                {
                    OnRegionChanged(baddr, baddr + count);
                }
            }
        }

        /*
		 * Scroll the screen 1 row.
		 *
		 * This could be accomplished with ctlr_bcopy() and ctlr_aclear(), but this
		 * operation is common enough to warrant a separate path.
		 */

        public void ScrollOne()
        {
            throw new ApplicationException("ctlr_scroll not implemented");
        }


        /// <summary>
        ///     Note that a particular region of the screen has changed.
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        private void ScreenRegionChanged(int start, int end)
        {
            OnRegionChanged(start, end);
        }


        /// <summary>
        ///     Swap the regular and alternate screen buffers
        /// </summary>
        /// <param name="alt"></param>
        public void SwapAltBuffers(bool alt)
        {
            byte[] stmp;
            ExtendedAttribute[] etmp;


            if (alt != IsAltBuffer)
            {
                stmp = ScreenBuffer;
                ScreenBuffer = aScreenBuffer;
                aScreenBuffer = stmp;

                etmp = extendedAttributes;
                extendedAttributes = aExtendedAttributeBuffer;
                aExtendedAttributeBuffer = etmp;

                IsAltBuffer = alt;
                OnAllChanged();
                //		unselect(0, ROWS*COLS);

                /*
				 * There may be blinkers on the alternate screen; schedule one
				 * iteration just in case.
				 */
                //		blink_start();
            }
        }


        /// <summary>
        ///     Set or clear the MDT on an attribute
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        public void SetMDT(byte[] data, int offset)
        {
            // mfw
            if (offset != -1)
            {
                if ((data[offset] & ControllerConstant.FA_MODIFY) != 0)
                {
                    return;
                }

                data[offset] |= ControllerConstant.FA_MODIFY;
                if (appres.modified_sel)
                {
                    OnAllChanged();
                }
            }
        }

        public void MDTClear(byte[] data, int offset)
        {
            if ((data[offset] & ControllerConstant.FA_MODIFY) == 0)
                return;
            data[offset] &= ControllerConstant.FA_MODIFY_MASK; //(byte)~FA_MODIFY;
            if (appres.modified_sel)
                OnAllChanged();
        }


        /// <summary>
        ///     Support for screen-size swapping for scrolling
        /// </summary>
        private void Shrink()
        {
            int i;
            for (i = 0; i < RowCount*ColumnCount; i++)
            {
                ScreenBuffer[i] = debuggingFont ? CharacterGenerator.Space : CharacterGenerator.Null;
            }
            OnAllChanged();
            //	screen_disp(false);
        }

        public event EventHandler CursorLocationChanged;

        protected virtual void OnCursorLocationChanged()
        {
            if (CursorLocationChanged != null)
            {
                CursorLocationChanged(this, EventArgs.Empty);
            }
        }


        public void SetCursorAddress(int address)
        {
            if (address != CursorAddress)
            {
                CursorAddress = address;
                OnCursorLocationChanged();
            }
        }

        public int AddresstoRow(int address)
        {
            return address/ColumnCount;
        }

        public int AddressToColumn(int address)
        {
            return address%ColumnCount;
        }

        public int RowColumnToByteAddress(int row, int column)
        {
            return row*ColumnCount + column;
        }

        public void IncrementAddress(ref int address)
        {
            address = (address + 1)%(ColumnCount*RowCount);
        }

        public void DecrementAddress(ref int address)
        {
            address = address != 0 ? address - 1 : ColumnCount*RowCount - 1;
        }

        public void RemoveTimeOut(Timer timer)
        {
            //Console.WriteLine("remove timeout");
            if (timer != null)
            {
                timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        public Timer AddTimeout(int milliseconds, TimerCallback callback)
        {
            //Console.WriteLine("add timeout");
            var timer = new Timer(callback, this, milliseconds, 0);
            return timer;
        }


        public bool MoveCursor(CursorOp op, int x, int y)
        {
            int bAddress;
            int sbAddress;
            int nbAddress;
            var success = false;

            switch (op)
            {
                case CursorOp.Exact:
                case CursorOp.NearestUnprotectedField:
                {
                    if (!telnet.Is3270)
                    {
                        x--;
                        y--;
                    }
                    if (x < 0)
                        x = 0;
                    if (y < 0)
                        y = 0;

                    bAddress = (y*ColumnCount + x)%(RowCount*ColumnCount);

                    if (op == CursorOp.Exact)
                    {
                        SetCursorAddress(bAddress);
                    }
                    else
                    {
                        SetCursorAddress(GetNextUnprotectedField(CursorAddress));
                    }

                    success = true;
                    break;
                }
                case CursorOp.Tab:
                {
                    if (telnet.IsAnsi)
                    {
                        telnet.SendChar('\t');
                        return true;
                    }
                    SetCursorAddress(GetNextUnprotectedField(CursorAddress));
                    success = true;
                    break;
                }
                case CursorOp.BackTab:
                {
                    if (telnet.Is3270)
                    {
                        bAddress = CursorAddress;
                        DecrementAddress(ref bAddress);
                        if (FieldAttribute.IsFA(ScreenBuffer[bAddress]))
                        {
                            //At beginning of field
                            DecrementAddress(ref bAddress);
                        }
                        sbAddress = bAddress;
                        while (true)
                        {
                            nbAddress = bAddress;
                            IncrementAddress(ref nbAddress);
                            if (FieldAttribute.IsFA(ScreenBuffer[bAddress])
                                && !FieldAttribute.IsProtected(ScreenBuffer[bAddress])
                                && !FieldAttribute.IsFA(ScreenBuffer[nbAddress]))
                            {
                                break;
                            }

                            DecrementAddress(ref bAddress);

                            if (bAddress == sbAddress)
                            {
                                SetCursorAddress(0);
                                success = true;
                            }
                        }

                        IncrementAddress(ref bAddress);
                        SetCursorAddress(bAddress);
                        success = true;
                    }
                    break;
                }

                default:
                    throw new ApplicationException("Sorry, cursor op '" + op + "' not implemented");
            }

            return success;
        }


        public void DumpRange(int first, int len, bool in_ascii, byte[] buf, int rel_rows, int rel_cols)
        {
            var any = false;
            var lineBuffer = new byte[MaxColumns*3 + 1];
            var s = 0;
            var debug = "";

            /*
			 * If the client has looked at the live screen, then if they later
			 * execute 'Wait(output)', they will need to wait for output from the
			 * host.  output_wait_needed is cleared by sms_host_output,
			 * which is called from the write logic in ctlr.c.
			 */
            //	if (sms != SN && buf == screen_buf)
            //		sms->output_wait_needed = True;

            for (var i = 0; i < len; i++)
            {
                byte c;

                if (i != 0 && 0 == (first + i)%rel_cols)
                {
                    lineBuffer[s] = 0;
                    telnet.Action.action_output(lineBuffer, s);
                    s = 0;
                    debug = "";
                    any = false;
                }
                if (!any)
                {
                    any = true;
                }
                if (in_ascii)
                {
                    c = Tables.Cg2Ascii[buf[first + i]];
                    lineBuffer[s++] = c == 0 ? (byte) ' ' : c;
                    if (c == 0)
                    {
                        debug += " ";
                    }
                    else
                    {
                        debug += Convert.ToChar(c);
                    }
                }
                else
                {
                    var temp = string.Format("{0}{1:x2}", i != 0 ? " " : "", Tables.Cg2Ebc[buf[first + i]]);
                    int tt;
                    for (tt = 0; tt < temp.Length; tt++)
                    {
                        lineBuffer[s++] = (byte) temp[tt];
                    }
                }
            }
            if (any)
            {
                lineBuffer[s] = 0;
                telnet.Action.action_output(lineBuffer, s);
            }
        }

        private void DumpRangeXML(int first, int length, bool inAscii, byte[] buffer, int relRows, int relCols)
        {
            var any = false;
            var linebuf = new byte[MaxColumns*3*5 + 1];
            var s = 0;
            if (!inAscii)
            {
                throw new ApplicationException("sorry, dump_rangeXML only valid for ascii buffer");
            }

            /*
			 * If the client has looked at the live screen, then if they later
			 * execute 'Wait(output)', they will need to wait for output from the
			 * host.  output_wait_needed is cleared by sms_host_output,
			 * which is called from the write logic in ctlr.c.
			 */
            //if (sms != SN && buf == screen_buf)
            //	sms->output_wait_needed = True;

            for (var i = 0; i < length; i++)
            {
                byte c;

                if (i != 0 && 0 == (first + i)%relCols)
                {
                    linebuf[s] = 0;
                    telnet.Action.action_output(linebuf, s);
                    s = 0;
                    any = false;
                }
                if (!any)
                {
                    any = true;
                }

                c = Tables.Cg2Ascii[buffer[first + i]];
                if (c == 0) c = (byte) ' ';
                var temp = "";

                temp = "" + Convert.ToChar(c);
                int tt;
                for (tt = 0; tt < temp.Length; tt++)
                {
                    linebuf[s++] = (byte) temp[tt];
                }
            }
            if (any)
            {
                linebuf[s] = 0;
                telnet.Action.action_output(linebuf, s, true);
            }
        }


        private bool DumpFixed(object[] args, string name, bool inAscii, byte[] buffer, int relRows, int relColumns,
            int cAddress)
        {
            int row, col, len, rows = 0, cols = 0;

            switch (args.Length)
            {
                //Everything
                case 0:
                {
                    row = 0;
                    col = 0;
                    len = relRows*relColumns;
                    break;
                }
                //From cursor, for n
                case 1:
                {
                    row = cAddress/relColumns;
                    col = cAddress%relColumns;
                    len = (int) args[0];
                    break;
                }
                //From (row,col), for n
                case 3:
                {
                    row = (int) args[0];
                    col = (int) args[1];
                    len = (int) args[2];
                    break;
                }
                //From (row,col), for rows x cols
                case 4:
                {
                    row = (int) args[0];
                    col = (int) args[1];
                    rows = (int) args[2];
                    cols = (int) args[3];
                    len = 0;
                    break;
                }
                default:
                {
                    telnet.Events.ShowError(name + " requires 0, 1, 3 or 4 arguments");
                    return false;
                }
            }

            if (
                row < 0 || row > relRows || col < 0 || col > relColumns || len < 0 ||
                ((args.Length < 4) && (row*relColumns + col + len > relRows*relColumns)) ||
                ((args.Length == 4) && (cols < 0 || rows < 0 ||
                                        col + cols > relColumns || row + rows > relRows))
                )
            {
                telnet.Events.ShowError(name + ": Invalid argument", name);
                return false;
            }


            if (args.Length < 4)
            {
                DumpRange(row*relColumns + col, len, inAscii, buffer, relRows, relColumns);
            }
            else
            {
                int i;

                for (i = 0; i < rows; i++)
                {
                    DumpRange((row + i)*relColumns + col, cols, inAscii, buffer, relRows, relColumns);
                }
            }

            return true;
        }

        private bool DumpField(string name, bool in_ascii)
        {
            int faIndex;
            var fa = FakeFA;
            int start, baddr;
            var length = 0;

            if (!Formatted)
            {
                telnet.Events.ShowError(name + ": Screen is not formatted");
                return false;
            }
            faIndex = GetFieldAttribute(CursorAddress);
            start = faIndex;
            IncrementAddress(ref start);
            baddr = start;
            do
            {
                if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                {
                    break;
                }
                length++;
                IncrementAddress(ref baddr);
            } while (baddr != start);

            DumpRange(start, length, in_ascii, ScreenBuffer, RowCount, ColumnCount);
            return true;
        }

        private int DumpFieldAsXML(int address, ExtendedAttribute ea)
        {
            var fa = FakeFA;
            int faIndex;
            int start, baddr;
            var length = 0;


            faIndex = GetFieldAttribute(address);
            if (faIndex != -1)
            {
                fa = ScreenBuffer[faIndex];
            }
            start = faIndex;
            IncrementAddress(ref start);
            baddr = start;

            do
            {
                if (FieldAttribute.IsFA(ScreenBuffer[baddr]))
                {
                    if (extendedAttributes[baddr].fg != 0)
                        ea.fg = extendedAttributes[baddr].fg;
                    if (extendedAttributes[baddr].bg != 0)
                        ea.bg = extendedAttributes[baddr].bg;
                    if (extendedAttributes[baddr].cs != 0)
                        ea.cs = extendedAttributes[baddr].cs;
                    if (extendedAttributes[baddr].gr != 0)
                        ea.gr = extendedAttributes[baddr].gr;

                    break;
                }
                length++;
                IncrementAddress(ref baddr);
            } while (baddr != start);

            var columnStart = AddressToColumn(start);
            var rowStart = AddresstoRow(start);
            var rowEnd = AddresstoRow(baddr) + 1;
            var remainingLength = length;

            int rowCount;

            for (rowCount = rowStart; rowCount < rowEnd; rowCount++)
            {
                if (rowCount == rowStart)
                {
                    if (length > ColumnCount - columnStart)
                    {
                        length = ColumnCount - columnStart;
                    }
                    remainingLength -= length;
                }
                else
                {
                    start = RowColumnToByteAddress(rowCount, 0);
                    length = Math.Min(ColumnCount, remainingLength);
                    remainingLength -= length;
                }


                telnet.Action.action_output("<Field>");
                telnet.Action.action_output("<Location position=\"" + start + "\" left=\"" + AddressToColumn(start) +
                                            "\" top=\"" + AddresstoRow(start) + "\" length=\"" + length + "\"/>");

                var temp = "";
                temp += "<Attributes Base=\"" + fa + "\"";

                if (FieldAttribute.IsProtected(fa))
                {
                    temp += " Protected=\"true\"";
                }
                else
                    temp += " Protected=\"false\"";
                if (FieldAttribute.IsZero(fa))
                {
                    temp += " FieldType=\"Hidden\"";
                }
                else if (FieldAttribute.IsHigh(fa))
                {
                    temp += " FieldType=\"High\"";
                }
                else if (FieldAttribute.IsIntense(fa))
                {
                    temp += " FieldType=\"Intense\"";
                }
                else
                {
                    if (ea.fg != 0)
                    {
                        temp += " Foreground=\"" + See.GetEfaUnformatted(See.XA_FOREGROUND, ea.fg) + "\"";
                    }
                    if (ea.bg != 0)
                    {
                        temp += " Background=\"" + See.GetEfaUnformatted(See.XA_BACKGROUND, ea.bg) + "\"";
                    }
                    if (ea.gr != 0)
                    {
                        temp += " Highlighting=\"" + See.GetEfaUnformatted(See.XA_HIGHLIGHTING, (byte) (ea.bg | 0xf0)) +
                                "\"";
                    }
                    if ((ea.cs & ExtendedAttribute.CS_MASK) != 0)
                    {
                        temp += " Mask=\"" +
                                See.GetEfaUnformatted(See.XA_CHARSET,
                                    (byte) ((ea.cs & ExtendedAttribute.CS_MASK) | 0xf0)) + "\"";
                    }
                }

                temp += "/>";
                telnet.Action.action_output(temp);
                DumpRangeXML(start, length, true, ScreenBuffer, rowCount, ColumnCount);
                telnet.Action.action_output("</Field>");
            }

            if (baddr <= address)
            {
                return -1;
            }
            return baddr;
        }


        //endif

        public void Dump()
        {
            int x, y;
            Console.WriteLine("dump starting.... Cursor@" + CursorAddress);
            for (y = 0; y < 24; y++)
            {
                var temp = "";
                for (x = 0; x < 80; x++)
                {
                    var ch = Tables.Cg2Ascii[ScreenBuffer[x + y*80]];
                    if (ch == 0)
                    {
                        temp += " ";
                    }
                    else
                    {
                        temp += "" + Convert.ToChar(ch);
                    }
                }
                Console.WriteLine("{0:d2} {1}", y, temp);
            }
        }

        public bool AsciiAction(params object[] args)
        {
            return DumpFixed(args, "Ascii_action", true, ScreenBuffer, RowCount, ColumnCount, CursorAddress);
        }

        public bool AsciiFieldAction(params object[] args)
        {
            return DumpField("AsciiField_action", true);
        }

        public bool DumpXMLAction(params object[] args)
        {
            var pos = 0;
            //string name = "DumpXML_action";
            telnet.Action.action_output("<?xml version=\"1.0\"?>"); // encoding=\"utf-16\"?>");
            telnet.Action.action_output(
                "<XMLScreen xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
            telnet.Action.action_output("<CX>" + ColumnCount + "</CX>");
            telnet.Action.action_output("<CY>" + RowCount + "</CY>");
            if (Formatted)
            {
                telnet.Action.action_output("<Formatted>true</Formatted>");
                var ea = new ExtendedAttribute();
                // CFCJR Mar 4,2008 : user tmcquire in post on www.open3270.net
                // says this do loop can hang up (pos never changes) in certain cases.
                // Added lastPos check to prevent this.
                var lastPos = -1;
                var cnt = 0;
                do
                {
                    lastPos = pos;
                    pos = DumpFieldAsXML(pos, ea);
                    if (lastPos == pos)
                    {
                        cnt++;
                    }
                    else
                    {
                        cnt = 0;
                    }
                } while (pos != -1 && cnt < 999);
            }
            else
            {
                telnet.Action.action_output("<Formatted>false</Formatted>");
            }

            //Output unformatted image anyway
            int i;
            telnet.Action.action_output("<Unformatted>");
            for (i = 0; i < RowCount; i++)
            {
                var start = RowColumnToByteAddress(i, 0);

                var length = ColumnCount;
                telnet.Action.action_output("<Text>");
                DumpRangeXML(start, length, true, ScreenBuffer, RowCount, ColumnCount);
                telnet.Action.action_output("</Text>");
            }
            telnet.Action.action_output("</Unformatted>");
            telnet.Action.action_output("</XMLScreen>");
            return true;
        }

        #region Fields

        private byte[] aScreenBuffer;


        private byte defaultCs;
        private byte defaultFg;
        private byte defaultGr;
        private byte replyMode;


        private bool tracePrimed;
        private bool traceSkipping;
        private readonly bool debuggingFont = false;


        private int bufferAddress;
        private int currentFaIndex;
        private int modelNumber;
        private int sscpStart;
        private int firstChanged;
        private int lastChanged;
        private int dataAvailableCount;
        private readonly long startTime;


        private string modelName;


        private ExtendedAttribute[] extendedAttributes;
        private ExtendedAttribute[] aExtendedAttributeBuffer;
        private ExtendedAttribute[] extendedAttributesZeroBuffer;


        private readonly Telnet telnet;
        private readonly TNTrace trace;
        private readonly Appres appres;
        private readonly StructuredField sf;

        private PreviousEnum previous = PreviousEnum.None;

        //For synchonization only
        private readonly object dataAvailablePadlock = new object();

        #endregion Fields

        #region Properties

        public bool Formatted { get; set; }


        public byte[] CrmAttributes { get; set; }

        public int CrmnAttribute { get; set; }

        public byte FakeFA { get; set; }

        public int RowCount { get; set; } = 25;

        public int ColumnCount { get; set; } = 80;

        public bool Is3270 { get; set; } = false;

        public int MaxColumns { get; set; } = 132;

        public int MaxRows { get; set; } = 43;

        public byte AttentionID { get; set; } = AID.None;

        public int BufferAddress
        {
            get { return bufferAddress; }
            set { bufferAddress = value; }
        }

        public int CursorAddress { get; set; }

        public byte[] ScreenBuffer { get; set; }

        public bool IsAltBuffer { get; set; }

        public bool ScreenAlt { get; set; } = true;

        public bool ScreenChanged { get; private set; }

        public int DataAvailableCount
        {
            get
            {
                lock (dataAvailablePadlock)
                {
                    return dataAvailableCount;
                }
            }
        }

        #endregion Properties

        #region Calculated properties

        public bool IsBlank(byte c)
        {
            return (c == CharacterGenerator.Null) || (c == CharacterGenerator.Space);
        }


        /// <summary>
        ///     Tell me if there is any data on the screen.
        /// </summary>
        private bool StreamHasData
        {
            get
            {
                var c = 0;
                int i;
                byte oc;

                for (i = 0; i < RowCount*ColumnCount; i++)
                {
                    oc = ScreenBuffer[c++];
                    if (!FieldAttribute.IsFA(oc) && !IsBlank(oc))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        #endregion Calculated properties

        #region Ctors, dtors, and clean-up

        internal Controller(Telnet tn, Appres appres)
        {
            sf = new StructuredField(tn);
            CrmAttributes = new byte[16];
            CrmnAttribute = 0;
            telnet = tn;
            trace = tn.Trace;
            this.appres = appres;
            startTime = DateTime.Now.Ticks;
        }

        #region IDisposable Members

        ~Controller()
        {
            Dispose(false);
        }

        private bool isDisposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
            {
                return;
            }
            isDisposed = true;

            if (disposing)
            {
                for (var i = 0; i < extendedAttributes.Length; i++)
                    extendedAttributes[i] = null;
                for (var i = 0; i < aExtendedAttributeBuffer.Length; i++)
                    aExtendedAttributeBuffer[i] = null;
                for (var i = 0; i < extendedAttributesZeroBuffer.Length; i++)
                    extendedAttributesZeroBuffer[i] = null;

                telnet.ConnectionPending -= telnet_ConnectionPending;
                telnet.PrimaryConnectionChanged -= telnet_PrimaryConnectionChanged;
                telnet.Connected3270 -= telnet_Connected3270;
            }
        }

        #endregion

        #endregion Ctors, dtors, and clean-up

        #region Eventhandlers

        private void telnet_PrimaryConnectionChanged(object sender, PrimaryConnectionChangedArgs e)
        {
            ReactToConnectionChange(e.Success);
        }


        private void telnet_ConnectionPending(object sender, EventArgs e)
        {
            //Not doing anything here, yet.
        }


        private void telnet_Connected3270(object sender, Connected3270EventArgs e)
        {
            ReactToConnectionChange(e.Is3270);
        }

        #endregion Eventhandlers
    }
}