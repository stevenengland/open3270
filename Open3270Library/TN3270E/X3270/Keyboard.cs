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
using System.Collections;
using System.Text;
using System.Threading;

namespace StEn.Open3270.TN3270E.X3270
{
    internal class Keyboard : IDisposable
    {
        private readonly Telnet telnet;
        private readonly TNTrace trace;
        private Actions action;


        public int keyboardLock = KeyboardConstants.NotConnected;


        private bool insertMode;
        private bool reverseMode;
        private readonly bool flipped = false;

        private readonly int PF_SZ;
        private readonly int PA_SZ;

        private Composing composing = Composing.None;

        private readonly Queue taQueue = new Queue();

        private Timer unlock_id;


#if N_COMPOSITES
		internal class composite 
		{
			public akeysym k1, k2;
			public  akeysym translation;
		};
		composite[] composites = null;
		int n_composites = 0;
#endif

        #region Ctors, dtors, and clean-up

        internal Keyboard(Telnet telnetObject)
        {
            telnet = telnetObject;
            action = telnet.Action;

            trace = telnet.Trace;
            PF_SZ = KeyboardConstants.PfTranslation.Length;
            PA_SZ = KeyboardConstants.PaTranslation.Length;
        }

        public void Dispose()
        {
            telnet.PrimaryConnectionChanged -= telnet_PrimaryConnectionChanged;
            telnet.Connected3270 -= telnet_Connected3270;
        }

        #endregion Ctors, dtors, and clean-up

        public Actions Actions
        {
            set { action = value; }
        }


        public bool AkEq(AKeySym k1, AKeySym k2)
        {
            return (k1.keysym == k2.keysym) && (k1.keytype == k2.keytype);
        }


        private byte FromHex(char c)
        {
            const string dx1 = "0123456789abcdef";
            const string dx2 = "0123456789ABCDEF";

            var index = dx1.IndexOf(c);
            if (index == -1)
            {
                index = dx2.IndexOf(c);
            }
            if (index == -1)
            {
                throw new ApplicationException("sorry, '" + c + "' isn't a valid hex digit");
            }
            return (byte) index;
        }

        private bool IsXDigit(char ch)
        {
            var ok = "0123456789ABCDEFabcdef";
            if (ok.IndexOf(ch) != -1)
            {
                return true;
            }
            return false;
        }

        private bool IsDigit(char ch)
        {
            if (ch >= '0' && ch <= '9')
            {
                return true;
            }
            return false;
        }


        /// <summary>
        ///     Put an action on the typeahead queue.
        /// </summary>
        /// <param name="fn"></param>
        /// <param name="args"></param>
        private void EnqueueTypeAheadAction(ActionDelegate fn, params object[] args)
        {
            // If no connection, forget it.
            if (!telnet.IsConnected)
            {
                telnet.Trace.trace_event("  dropped (not connected)\n");
                return;
            }

            // If operator error, complain and drop it.
            if ((keyboardLock & KeyboardConstants.ErrorMask) != 0)
            {
                //ring_bell();
                telnet.Trace.trace_event("  dropped (operator error)\n");
                return;
            }

            // If scroll lock, complain and drop it.
            if ((keyboardLock & KeyboardConstants.Scrolled) != 0)
            {
                //ring_bell();
                telnet.Trace.trace_event("  dropped (scrolled)\n");
                return;
            }

            // If typeahead disabled, complain and drop it.
            if (!telnet.Appres.typeahead)
            {
                telnet.Trace.trace_event("  dropped (no typeahead)\n");
                return;
            }

            taQueue.Enqueue(new TAItem(fn, args));
            //	status_typeahead(true);

            telnet.Trace.trace_event("  action queued (kybdlock 0x" + keyboardLock + ")\n");
        }


        /// <summary>
        ///     Execute an action from the typeahead queue.
        /// </summary>
        /// <returns></returns>
        public bool RunTypeAhead()
        {
            var success = false;
            if (keyboardLock == 0 && taQueue.Count != 0)
            {
                var item = (TAItem) taQueue.Dequeue();

                if (taQueue.Count == 0)
                {
                    //status_typeahead(false);
                }
                item.fn(item.args);
                success = true;
            }
            return success;
        }


        /// <summary>
        ///     Flush the typeahead queue.  Returns whether or not anything was flushed.
        /// </summary>
        /// <returns></returns>
        private bool FlushTypeAheadQueue()
        {
            var any = false;
            if (taQueue.Count > 0)
            {
                any = true;
            }
            taQueue.Clear();
            //			status_typeahead(false);
            return any;
        }

        private void PsSet(string text, bool is_hex)
        {
            // Move forwards to first non protected
            // Hack for mfw/FDI USA
            var skiptounprotected = telnet.Config.AlwaysSkipToUnprotected;

            var address = telnet.Controller.CursorAddress;
            if (skiptounprotected)
            {
                // Move cursor forwards to next unprotected field
                var ok = true;
                int fa;
                do
                {
                    ok = true;
                    fa = telnet.Controller.GetFieldAttribute(address);
                    if (fa == -1)
                    {
                        break;
                    }
                    if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]) ||
                        (fa >= 0 && FieldAttribute.IsProtected(telnet.Controller.ScreenBuffer[fa])))
                    {
                        ok = false;
                        telnet.Controller.IncrementAddress(ref address);
                        if (address == telnet.Controller.CursorAddress)
                        {
                            Console.WriteLine("**BUGBUG** Screen has no unprotected field!");
                            return;
                        }
                    }
                } while (!ok);

                if (address != telnet.Controller.CursorAddress)
                {
                    Console.WriteLine("Moved cursor to " + address + " to skip protected fields");
                    telnet.Controller.SetCursorAddress(address);
                    Console.WriteLine("cursor position " + telnet.Controller.AddressToColumn(address) + ", " +
                                      telnet.Controller.AddresstoRow(address));
                    Console.WriteLine("text : " + text);
                }
            }

            //push_string(text, false, is_hex);
            EmulateInput(text, false);
        }

        /// <summary>
        ///     Set bits in the keyboard lock.
        /// </summary>
        /// <param name="bits"></param>
        /// <param name="cause"></param>
        public void KeyboardLockSet(int bits, string cause)
        {
            int n;

            n = keyboardLock | bits;
            if (n != keyboardLock)
            {
                keyboardLock = n;
            }
            //Console.WriteLine("kybdlock_set "+bits+" "+cause);
        }

        /// <summary>
        ///     Clear bits in the keyboard lock.
        /// </summary>
        /// <param name="bits"></param>
        /// <param name="debug"></param>
        public void KeyboardLockClear(int bits, string debug)
        {
            int n;
            //Console.WriteLine("kybdlock_clr "+bits+" "+debug);
            if (bits == -1)
            {
                bits = 0xFFFF;
            }

            n = keyboardLock & ~bits;
            if (n != keyboardLock)
            {
                keyboardLock = n;
            }
        }


        /// <summary>
        ///     Set or clear enter-inhibit mode.
        /// </summary>
        /// <param name="inhibit"></param>
        public void ToggleEnterInhibitMode(bool inhibit)
        {
            if (inhibit)
            {
                KeyboardLockSet(KeyboardConstants.EnterInhibit, "kybd_inhibit");
            }
            else
            {
                KeyboardLockClear(KeyboardConstants.EnterInhibit, "kybd_inhibit");
            }
        }


        /// <summary>
        ///     Called when a host connects or disconnects.
        /// </summary>
        /// <param name="connected"></param>
        public void ConnectedStateChanged(bool connected)
        {
            if ((keyboardLock & KeyboardConstants.DeferredUnlock) != 0)
            {
                telnet.Controller.RemoveTimeOut(unlock_id);
            }

            KeyboardLockClear(-1, "kybd_connect");

            if (connected)
            {
                // Wait for any output or a WCC(restore) from the host
                KeyboardLockSet(KeyboardConstants.AwaitingFirst, "kybd_connect");
            }
            else
            {
                KeyboardLockSet(KeyboardConstants.NotConnected, "kybd_connect");
                FlushTypeAheadQueue();
            }
        }


        /// <summary>
        ///     Called when we switch between 3270 and ANSI modes.
        /// </summary>
        /// <param name="in3270"></param>
        public void SwitchMode3270(bool in3270)
        {
            if ((keyboardLock & KeyboardConstants.DeferredUnlock) != 0)
            {
                telnet.Controller.RemoveTimeOut(unlock_id);
            }
            KeyboardLockClear(-1, "kybd_connect");
        }


        /// <summary>
        ///     Called to initialize the keyboard logic.
        /// </summary>
        public void Initialize()
        {
            telnet.PrimaryConnectionChanged += telnet_PrimaryConnectionChanged;
            telnet.Connected3270 += telnet_Connected3270;
        }


        private void telnet_Connected3270(object sender, Connected3270EventArgs e)
        {
            SwitchMode3270(e.Is3270);
        }


        private void telnet_PrimaryConnectionChanged(object sender, PrimaryConnectionChangedArgs e)
        {
            ConnectedStateChanged(e.Success);
        }


        /// <summary>
        ///     Lock the keyboard because of an operator error.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="errorType"></param>
        public void HandleOperatorError(int address, int errorType)
        {
            Console.WriteLine("cursor@" + address + " - ROW=" + telnet.Controller.AddresstoRow(address) + " COL=" +
                              telnet.Controller.AddressToColumn(address));
            telnet.Events.ShowError("Keyboard locked");
            Console.WriteLine("WARNING--operator_error error_type=" + errorType);

            if (telnet.Config.LockScreenOnWriteToUnprotected)
            {
                KeyboardLockSet(errorType, "operator_error");
                FlushTypeAheadQueue();
            }
        }


        /// <summary>
        ///     Handle an AID (Attention IDentifier) key.  This is the common stuff that gets executed for all AID keys (PFs, PAs,
        ///     Clear and etc).
        /// </summary>
        /// <param name="aidCode"></param>
        public void HandleAttentionIdentifierKey(byte aidCode)
        {
            if (telnet.IsAnsi)
            {
                int i;

                if (aidCode == AID.Enter)
                {
                    telnet.SendChar('\r');
                    return;
                }
                for (i = 0; i < PF_SZ; i++)
                {
                    if (aidCode == KeyboardConstants.PfTranslation[i])
                    {
                        telnet.Ansi.ansi_send_pf(i + 1);
                        return;
                    }
                }
                for (i = 0; i < PA_SZ; i++)
                {
                    if (aidCode == KeyboardConstants.PaTranslation[i])
                    {
                        telnet.Ansi.ansi_send_pa(i + 1);
                        return;
                    }
                }
                return;
            }
            if (telnet.IsSscp)
            {
                if ((keyboardLock & KeyboardConstants.OiaMinus) != 0)
                    return;
                if (aidCode != AID.Enter && aidCode != AID.Clear)
                {
                    KeyboardLockSet(KeyboardConstants.OiaMinus, "key_AID");
                    return;
                }
            }
            if (telnet.IsSscp && aidCode == AID.Enter)
            {
                //Act as if the host had written our input.
                telnet.Controller.BufferAddress = telnet.Controller.CursorAddress;
            }
            if (!telnet.IsSscp || aidCode != AID.Clear)
            {
                insertMode = false;
                //Console.WriteLine("**BUGBUG** KL_OIA_LOCKED REMOVED");
                KeyboardLockSet(KeyboardConstants.OiaTWait | KeyboardConstants.OiaLocked, "key_AID");
            }
            telnet.Idle.ResetIdleTimer();
            telnet.Controller.AttentionID = aidCode;
            telnet.Controller.ProcessReadModifiedCommand(telnet.Controller.AttentionID, false);
        }


        public bool PFAction(params object[] args)
        {
            int k;

            k = (int) args[0];
            if (k < 1 || k > PF_SZ)
            {
                telnet.Events.ShowError("PF_action: Invalid argument '" + args[0] + "'");
                return false;
            }
            if ((keyboardLock & KeyboardConstants.OiaMinus) != 0)
            {
                return false;
            }
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(PFAction, args);
            }
            else
            {
                HandleAttentionIdentifierKey(KeyboardConstants.PfTranslation[k - 1]);
            }
            return true;
        }

        public bool PAAction(params object[] args)
        {
            int k;

            k = (int) args[0];
            if (k < 1 || k > PA_SZ)
            {
                telnet.Events.ShowError("PA_action: Invalid argument '" + args[0] + "'");
                return false;
            }
            if ((keyboardLock & KeyboardConstants.OiaMinus) != 0)
            {
                return false;
            }
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(PAAction, args);
            }
            else
            {
                HandleAttentionIdentifierKey(KeyboardConstants.PaTranslation[k - 1]);
            }
            return true;
        }


        /// <summary>
        ///     ATTN key, per RFC 2355.  Sends IP, regardless.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool AttnAction(params object[] args)
        {
            if (telnet.Is3270)
            {
                telnet.Interrupt();
                return true;
            }
            return false;
        }


        /// <summary>
        ///     IAC IP, which works for 5250 System Request and interrupts the program on an AS/400, even when the keyboard is
        ///     locked.
        ///     This is now the same as the Attn action.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool InterruptAction(params object[] args)
        {
            if (telnet.Is3270)
            {
                telnet.Interrupt();
                return true;
            }
            return false;
        }


        private bool WrapCharacter(int cgcode)
        {
            var with_ge = false;
            var pasting = false;

            if ((cgcode & KeyboardConstants.WFlag) != 0)
            {
                with_ge = true;
                cgcode &= ~KeyboardConstants.WFlag;
            }
            if ((cgcode & KeyboardConstants.PasteWFlag) != 0)
            {
                pasting = true;
                cgcode &= ~KeyboardConstants.PasteWFlag;
            }
            telnet.Trace.trace_event(" %s -> Key(%s\"%s\")\n",
                "nop", /*ia_name[(int) ia_cause],*/
                with_ge ? "GE " : "",
                Util.ControlSee(Tables.Cg2Ascii[cgcode]));
            return HandleOrdinaryCharacter(cgcode, with_ge, pasting);
        }


        /// <summary>
        ///     Handle an ordinary displayable character key.  Lots of stuff to handle: insert-mode, protected fields and etc.
        /// </summary>
        /// <param name="cgCode"></param>
        /// <param name="withGE"></param>
        /// <param name="pasting"></param>
        /// <returns></returns>
        public bool HandleOrdinaryCharacter(int cgCode, bool withGE, bool pasting)
        {
            int address;
            int endAddress;
            int fa;
            var noRoom = false;

            if (keyboardLock != 0)
            {
                Console.WriteLine(
                    "--bugbug--should enq_ta key, but since keylock is !=0, dropping it instead (not implemented properly)");
                return false;
                /*
				char code[64];

				(void) sprintf(code, "%d", cgcode |
					(with_ge ? GE_WFLAG : 0) |
					(pasting ? PASTE_WFLAG : 0));
				enq_ta(new ActionDelegate(key_Character_wrapper, code, CN);
				return false;*/
            }

            address = telnet.Controller.CursorAddress;
            fa = telnet.Controller.GetFieldAttribute(address);
            var favalue = telnet.Controller.FakeFA;
            if (fa != -1)
            {
                favalue = telnet.Controller.ScreenBuffer[fa];
            }
            if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]) || FieldAttribute.IsProtected(favalue))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorProtected);
                return false;
            }
            if (telnet.Appres.numeric_lock && FieldAttribute.IsNumeric(favalue) &&
                !((cgCode >= CharacterGenerator.Numeral0 && cgCode <= CharacterGenerator.Numeral9) ||
                  cgCode == CharacterGenerator.Minus || cgCode == CharacterGenerator.Period))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorNumeric);
                return false;
            }
            if (reverseMode || (insertMode && telnet.Controller.ScreenBuffer[address] != 0))
            {
                var last_blank = -1;

                //Find next null, next fa, or last blank
                endAddress = address;
                if (telnet.Controller.ScreenBuffer[endAddress] == CharacterGenerator.Space)
                {
                    last_blank = endAddress;
                }
                do
                {
                    telnet.Controller.IncrementAddress(ref endAddress);
                    if (telnet.Controller.ScreenBuffer[endAddress] == CharacterGenerator.Space)
                    {
                        last_blank = endAddress;
                    }
                    if (telnet.Controller.ScreenBuffer[endAddress] == CharacterGenerator.Null ||
                        FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[endAddress]))
                    {
                        break;
                    }
                } while (endAddress != address);

                //Pretend a trailing blank is a null, if desired.
                if (telnet.Appres.Toggled(Appres.BlankFill) && last_blank != -1)
                {
                    telnet.Controller.IncrementAddress(ref last_blank);
                    if (last_blank == endAddress)
                    {
                        telnet.Controller.DecrementAddress(ref endAddress);
                        telnet.Controller.AddCharacter(endAddress, CharacterGenerator.Null, 0);
                    }
                }

                //Check for field overflow.
                if (telnet.Controller.ScreenBuffer[endAddress] != CharacterGenerator.Null)
                {
                    if (insertMode)
                    {
                        HandleOperatorError(endAddress, KeyboardConstants.ErrorOverflow);
                        return false;
                    }
                    //Reverse
                    noRoom = true;
                }
                else
                {
                    // Shift data over.
                    if (endAddress > address)
                    {
                        // At least one byte to copy, no wrap.
                        telnet.Controller.CopyBlock(address, address + 1, endAddress - address, false);
                    }
                    else if (endAddress < address)
                    {
                        // At least one byte to copy, wraps to top.
                        telnet.Controller.CopyBlock(0, 1, endAddress, false);
                        telnet.Controller.AddCharacter(0,
                            telnet.Controller.ScreenBuffer[telnet.Controller.RowCount*telnet.Controller.ColumnCount - 1],
                            0);
                        telnet.Controller.CopyBlock(address, address + 1,
                            telnet.Controller.RowCount*telnet.Controller.ColumnCount - 1 - address, false);
                    }
                }
            }

            // Replace leading nulls with blanks, if desired.
            if (telnet.Controller.Formatted && telnet.Appres.Toggled(Appres.BlankFill))
            {
                var addresSof = fa; //fa - this.telnet.tnctlr.screen_buf;
                var addressFill = address;

                telnet.Controller.DecrementAddress(ref addressFill);
                while (addressFill != addresSof)
                {
                    // Check for backward line wrap.
                    if (addressFill%telnet.Controller.ColumnCount == telnet.Controller.ColumnCount - 1)
                    {
                        var aborted = true;
                        var addressScan = addressFill;

                        // Check the field within the preceeding line for NULLs.
                        while (addressScan != addresSof)
                        {
                            if (telnet.Controller.ScreenBuffer[addressScan] != CharacterGenerator.Null)
                            {
                                aborted = false;
                                break;
                            }
                            if (0 == addressScan%telnet.Controller.ColumnCount)
                            {
                                break;
                            }
                            telnet.Controller.DecrementAddress(ref addressScan);
                        }
                        if (aborted)
                        {
                            break;
                        }
                    }

                    if (telnet.Controller.ScreenBuffer[addressFill] == CharacterGenerator.Null)
                    {
                        telnet.Controller.AddCharacter(addressFill, CharacterGenerator.Space, 0);
                    }
                    telnet.Controller.DecrementAddress(ref addressFill);
                }
            }

            // Add the character.
            if (noRoom)
            {
                do
                {
                    telnet.Controller.IncrementAddress(ref address);
                } while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]));
            }
            else
            {
                telnet.Controller.AddCharacter(address, (byte) cgCode, withGE ? ExtendedAttribute.CS_GE : (byte) 0);
                telnet.Controller.SetForegroundColor(address, 0);
                telnet.Controller.ctlr_add_gr(address, 0);
                if (!reverseMode)
                {
                    telnet.Controller.IncrementAddress(ref address);
                }
            }

            //Implement auto-skip, and don't land on attribute bytes.
            //This happens for all pasted data (even DUP), and for all keyboard-generated data except DUP.
            if (pasting || (cgCode != CharacterGenerator.dup))
            {
                while (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
                {
                    if (FieldAttribute.IsSkip(telnet.Controller.ScreenBuffer[address]))
                    {
                        address = telnet.Controller.GetNextUnprotectedField(address);
                    }
                    else
                    {
                        telnet.Controller.IncrementAddress(ref address);
                    }
                }
                telnet.Controller.SetCursorAddress(address);
            }

            telnet.Controller.SetMDT(telnet.Controller.ScreenBuffer, fa);
            return true;
        }


        /// <summary>
        ///     Handle an ordinary character key, given an ASCII code.
        /// </summary>
        /// <param name="character"></param>
        /// <param name="keytype"></param>
        /// <param name="cause"></param>
        private void HandleAsciiCharacter(byte character, KeyType keytype, EIAction cause)
        {
            var keySymbol = new AKeySym();

            keySymbol.keysym = character;
            keySymbol.keytype = keytype;

            switch (composing)
            {
                case Composing.None:
                {
                    break;
                }
                case Composing.Compose:
                {
#if N_COMPOSITES
					for (i = 0; i < n_composites; i++)
						if (ak_eq(composites[i].k1, ak) ||
							ak_eq(composites[i].k2, ak))
							break;
					if (i < n_composites) 
					{
						cc_first.keysym = c;
						cc_first.keytype = keytype;
						composing = enum_composing.FIRST;
//						status_compose(true, c, keytype);
					} 
					else 
#endif
                    {
                        composing = Composing.None;
                    }
                    return;
                }
                case Composing.First:
                {
                    composing = Composing.None;
                    //					status_compose(false, 0, enum_keytype.KT_STD);
#if N_COMPOSITES
					for (i = 0; i < n_composites; i++)
					{
						if ((ak_eq(composites[i].k1, cc_first) &&
							ak_eq(composites[i].k2, ak)) ||
							(ak_eq(composites[i].k1, ak) &&
							ak_eq(composites[i].k2, cc_first)))
							break;
					}
					if (i < n_composites) 
					{
						c = composites[i].translation.keysym;
						keytype = composites[i].translation.keytype;
					} 
					else 
#endif
                    {
                        return;
                    }
                }
            }

            trace.trace_event(" %s -> Key(\"%s\")\n", telnet.Action.ia_name[(int) cause], Util.ControlSee(character));
            if (telnet.Is3270)
            {
                if (character < ' ')
                {
                    trace.trace_event("  dropped (control char)\n");
                    return;
                }
                HandleOrdinaryCharacter(Tables.Ascii2Cg[character], keytype == KeyType.GE, false);
            }
            else if (telnet.IsAnsi)
            {
                telnet.SendChar((char) character);
            }
            else
            {
                trace.trace_event("  dropped (not connected)\n");
            }
        }


        /// <summary>
        ///     Simple toggles.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MonoCaseAction(params object[] args)
        {
            telnet.Appres.ToggleTheValue(Appres.MonoCase);
            return true;
        }


        /// <summary>
        ///     Flip the display left-to-right
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool FlipAction(params object[] args)
        {
            //			screen_flip();
            return true;
        }


        public bool TabForwardAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(TabForwardAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                telnet.SendChar('\t');
                return true;
            }
            telnet.Controller.SetCursorAddress(telnet.Controller.GetNextUnprotectedField(telnet.Controller.CursorAddress));
            return true;
        }


        /// <summary>
        ///     Tab backward to previous field.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool BackTab_action(params object[] args)
        {
            int baddr, nbaddr;
            int sbaddr;

            if (!telnet.Is3270)
            {
                return false;
            }

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(BackTab_action, args);
                return true;
            }
            baddr = telnet.Controller.CursorAddress;
            telnet.Controller.DecrementAddress(ref baddr);
            if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[baddr])) /* at bof */
            {
                telnet.Controller.DecrementAddress(ref baddr);
            }
            sbaddr = baddr;
            while (true)
            {
                nbaddr = baddr;
                telnet.Controller.IncrementAddress(ref nbaddr);
                if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[baddr])
                    && !FieldAttribute.IsProtected(telnet.Controller.ScreenBuffer[baddr])
                    && !FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[nbaddr]))
                {
                    break;
                }
                telnet.Controller.DecrementAddress(ref baddr);
                if (baddr == sbaddr)
                {
                    telnet.Controller.SetCursorAddress(0);
                    return true;
                }
            }
            telnet.Controller.IncrementAddress(ref baddr);
            telnet.Controller.SetCursorAddress(baddr);
            return true;
        }


        /// <summary>
        ///     Deferred keyboard unlock.
        /// </summary>
        /// <param name="state"></param>
        private void DeferUnlock(object state)
        {
            lock (telnet)
            {
                // Only actually process the event if the keyboard is currently unlocked...
                if ((telnet.Keyboard.keyboardLock | KeyboardConstants.DeferredUnlock) ==
                    KeyboardConstants.DeferredUnlock)
                {
                    telnet.Trace.WriteLine("--debug--defer_unlock");
                    KeyboardLockClear(KeyboardConstants.DeferredUnlock, "defer_unlock");
                    //status_reset();
                    if (telnet.IsConnected)
                    {
                        telnet.Controller.ProcessPendingInput();
                    }
                }
                else
                {
                    telnet.Trace.WriteLine("--debug--defer_unlock ignored");
                }
            }
        }


        public void ResetKeyboardLock(bool explicitvalue)
        {
            //If explicit (from the keyboard) and there is typeahead or a half-composed key, simply flush it.

            if (explicitvalue)
            {
                var halfReset = false;

                if (FlushTypeAheadQueue())
                {
                    halfReset = true;
                }
                if (composing != Composing.None)
                {
                    composing = Composing.None;
                    //	status_compose(false, 0, KT_STD);
                    halfReset = true;
                }
                if (halfReset)
                {
                    return;
                }
            }


            //Always clear insert mode.
            insertMode = false;

            // Otherwise, if not connect, reset is a no-op.
            if (!telnet.IsConnected)
            {
                return;
            }

            //Remove any deferred keyboard unlock.  We will either unlock the keyboard now, or want to defer further into the future.

            if ((keyboardLock & KeyboardConstants.DeferredUnlock) != 0)
            {
                telnet.Controller.RemoveTimeOut(unlock_id);
            }


            //If explicit (from the keyboard), unlock the keyboard now.
            //Otherwise (from the host), schedule a deferred keyboard unlock.
            if (explicitvalue)
            {
                KeyboardLockClear(-1, "ResetKeyboardLock");
            }
            else if ((keyboardLock &
                      (KeyboardConstants.DeferredUnlock | KeyboardConstants.OiaTWait | KeyboardConstants.OiaLocked |
                       KeyboardConstants.AwaitingFirst)) != 0)
            {
                telnet.Trace.WriteLine("Clear lock in 1010/55");
                KeyboardLockClear(~KeyboardConstants.DeferredUnlock, "ResetKeyboardLock");
                KeyboardLockSet(KeyboardConstants.DeferredUnlock, "ResetKeyboardLock");
                lock (telnet)
                {
                    unlock_id = telnet.Controller.AddTimeout(KeyboardConstants.UnlockMS, DeferUnlock);
                }
            }

            // Clean up other modes.
            composing = Composing.None;
        }


        public bool ResetAction(params object[] args)
        {
            ResetKeyboardLock(true);
            return true;
        }


        /// <summary>
        ///     Move to first unprotected field on screen.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool HomeAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(HomeAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                telnet.Ansi.ansi_send_home();
                return true;
            }
            if (!telnet.Controller.Formatted)
            {
                telnet.Controller.SetCursorAddress(0);
                return true;
            }
            telnet.Controller.SetCursorAddress(
                telnet.Controller.GetNextUnprotectedField(telnet.Controller.RowCount*telnet.Controller.ColumnCount - 1));
            return true;
        }


        /// <summary>
        ///     Cursor left 1 position.
        /// </summary>
        private void MoveLeft()
        {
            var address = telnet.Controller.CursorAddress;
            telnet.Controller.DecrementAddress(ref address);
            telnet.Controller.SetCursorAddress(address);
        }


        public bool LeftAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(LeftAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                telnet.Ansi.ansi_send_left();
                return true;
            }
            if (!flipped)
                MoveLeft();
            else
            {
                var address = telnet.Controller.CursorAddress;
                telnet.Controller.IncrementAddress(ref address);
                telnet.Controller.SetCursorAddress(address);
            }
            return true;
        }


        /// <summary>
        ///     Delete char key.
        /// </summary>
        /// <returns> Returns "true" if succeeds, "false" otherwise.</returns>
        private bool DeleteCharacter()
        {
            int address;
            int endAddress;
            int faIndex;
            var fa = telnet.Controller.FakeFA;

            address = telnet.Controller.CursorAddress;
            faIndex = telnet.Controller.GetFieldAttribute(address);

            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];

                if (!FieldAttribute.IsProtected(fa))
                {
                    //We're in an unprotected field, so it's okay to delete.
                    telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                }
            }


            if (FieldAttribute.IsProtected(fa) || FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorProtected);
                return false;
            }

            //Find next FA
            if (telnet.Controller.Formatted)
            {
                endAddress = address;
                do
                {
                    telnet.Controller.IncrementAddress(ref endAddress);
                    if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[endAddress]))
                        break;
                } while (endAddress != address);

                telnet.Controller.DecrementAddress(ref endAddress);
            }
            else
            {
                if (address%telnet.Controller.ColumnCount == telnet.Controller.ColumnCount - 1)
                {
                    return true;
                }
                endAddress = address + (telnet.Controller.ColumnCount - address%telnet.Controller.ColumnCount) - 1;
            }

            if (endAddress > address)
            {
                telnet.Controller.CopyBlock(address + 1, address, endAddress - address, false);
            }
            else if (endAddress != address)
            {
                telnet.Controller.CopyBlock(address + 1, address,
                    telnet.Controller.RowCount*telnet.Controller.ColumnCount - 1 - address, false);
                telnet.Controller.AddCharacter(telnet.Controller.RowCount*telnet.Controller.ColumnCount - 1,
                    telnet.Controller.ScreenBuffer[0], 0);
                telnet.Controller.CopyBlock(1, 0, endAddress, false);
            }

            telnet.Controller.AddCharacter(endAddress, CharacterGenerator.Null, 0);
            telnet.Controller.SetMDT(telnet.Controller.ScreenBuffer, faIndex);
            return false;
        }


        public bool DeleteAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(DeleteAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                telnet.SendByte(0x7f);
                return true;
            }

            if (!DeleteCharacter())
            {
                return false;
            }

            if (reverseMode)
            {
                var address = telnet.Controller.CursorAddress;

                telnet.Controller.DecrementAddress(ref address);
                if (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
                {
                    telnet.Controller.SetCursorAddress(address);
                }
            }

            return true;
        }


        public bool BackSpaceAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(BackSpaceAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                telnet.SendErase();
                return true;
            }
            if (reverseMode)
            {
                DeleteCharacter();
            }
            else if (!flipped)
            {
                MoveLeft();
            }
            else
            {
                int address;

                address = telnet.Controller.CursorAddress;
                telnet.Controller.DecrementAddress(ref address);
                telnet.Controller.SetCursorAddress(address);
            }
            return true;
        }


        /// <summary>
        ///     Destructive backspace, like Unix "erase".
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool EraseAction(params object[] args)
        {
            int address;
            var fa = telnet.Controller.FakeFA;
            int faIndex;

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(EraseAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                telnet.SendErase();
                return true;
            }
            address = telnet.Controller.CursorAddress;
            faIndex = telnet.Controller.GetFieldAttribute(address);
            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }
            if (faIndex == address || FieldAttribute.IsProtected(fa))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorProtected);
                return false;
            }
            if (address != 0 && faIndex == address - 1)
            {
                return true;
            }
            MoveLeft();
            DeleteCharacter();
            return true;
        }


        /// <summary>
        ///     Move cursor right
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveRight(params object[] args)
        {
            int address;

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(MoveRight, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                telnet.Ansi.ansi_send_right();
                return true;
            }
            if (!flipped)
            {
                address = telnet.Controller.CursorAddress;
                telnet.Controller.IncrementAddress(ref address);
                telnet.Controller.SetCursorAddress(address);
            }
            else
            {
                MoveLeft();
            }
            return true;
        }


        /// <summary>
        ///     Move cursor left 2 positions.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveCursorLeft2Positions(params object[] args)
        {
            int address;

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(MoveCursorLeft2Positions, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }

            address = telnet.Controller.CursorAddress;
            telnet.Controller.DecrementAddress(ref address);
            telnet.Controller.DecrementAddress(ref address);
            telnet.Controller.SetCursorAddress(address);
            return true;
        }


        /// <summary>
        ///     Move cursor to previous word.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool PreviousWordAction(params object[] args)
        {
            int address;
            int address0;
            byte c;
            bool prot;


            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(PreviousWordAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                return false;
            }
            if (!telnet.Controller.Formatted)
            {
                return false;
            }

            address = telnet.Controller.CursorAddress;
            prot = FieldAttribute.IsProtectedAt(telnet.Controller.ScreenBuffer, address);

            //Skip to before this word, if in one now.
            if (!prot)
            {
                c = telnet.Controller.ScreenBuffer[address];
                while (!FieldAttribute.IsFA(c) && c != CharacterGenerator.Space && c != CharacterGenerator.Null)
                {
                    telnet.Controller.DecrementAddress(ref address);
                    if (address == telnet.Controller.CursorAddress)
                        return true;
                    c = telnet.Controller.ScreenBuffer[address];
                }
            }
            address0 = address;

            //Find the end of the preceding word.
            do
            {
                c = telnet.Controller.ScreenBuffer[address];
                if (FieldAttribute.IsFA(c))
                {
                    telnet.Controller.DecrementAddress(ref address);
                    prot = FieldAttribute.IsProtectedAt(telnet.Controller.ScreenBuffer, address);
                    continue;
                }
                if (!prot && c != CharacterGenerator.Space && c != CharacterGenerator.Null)
                    break;
                telnet.Controller.DecrementAddress(ref address);
            } while (address != address0);

            if (address == address0)
            {
                return true;
            }

            // Go to the front.
            do
            {
                telnet.Controller.DecrementAddress(ref address);
                c = telnet.Controller.ScreenBuffer[address];
            } while (!FieldAttribute.IsFA(c) && c != CharacterGenerator.Space && c != CharacterGenerator.Null);

            telnet.Controller.IncrementAddress(ref address);
            telnet.Controller.SetCursorAddress(address);
            return true;
        }


        /// <summary>
        ///     Move cursor right 2 positions.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveCursorRight2Positions(params object[] args)
        {
            int address;

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(MoveCursorRight2Positions, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                return false;
            }
            address = telnet.Controller.CursorAddress;
            telnet.Controller.IncrementAddress(ref address);
            telnet.Controller.IncrementAddress(ref address);
            telnet.Controller.SetCursorAddress(address);
            return true;
        }


        /// <summary>
        ///     Find the next unprotected word
        /// </summary>
        /// <param name="baseAddress"></param>
        /// <returns>-1 if unsuccessful</returns>
        private int FindNextUnprotectedWord(int baseAddress)
        {
            var address0 = baseAddress;
            byte c;
            bool prot;

            prot = FieldAttribute.IsProtectedAt(telnet.Controller.ScreenBuffer, baseAddress);

            do
            {
                c = telnet.Controller.ScreenBuffer[baseAddress];
                if (FieldAttribute.IsFA(c))
                {
                    prot = FieldAttribute.IsProtected(c);
                }
                else if (!prot && c != CharacterGenerator.Space && c != CharacterGenerator.Null)
                {
                    return baseAddress;
                }
                telnet.Controller.IncrementAddress(ref baseAddress);
            } while (baseAddress != address0);

            return -1;
        }


        /// <summary>
        ///     Find the next word in this field
        /// </summary>
        /// <param name="baseAddress"></param>
        /// <returns>-1 when unsuccessful</returns>
        private int FindNextWordInField(int baseAddress)
        {
            var address0 = baseAddress;
            byte c;
            var inWord = true;

            do
            {
                c = telnet.Controller.ScreenBuffer[baseAddress];
                if (FieldAttribute.IsFA(c))
                {
                    return -1;
                }

                if (inWord)
                {
                    if (c == CharacterGenerator.Space || c == CharacterGenerator.Null)
                    {
                        inWord = false;
                    }
                }
                else
                {
                    if (c != CharacterGenerator.Space && c != CharacterGenerator.Null)
                    {
                        return baseAddress;
                    }
                }

                telnet.Controller.IncrementAddress(ref baseAddress);
            } while (baseAddress != address0);

            return -1;
        }


        /// <summary>
        ///     Cursor to next unprotected word.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveCursorToNextUnprotectedWord(params object[] args)
        {
            int address;
            byte c;

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(MoveCursorToNextUnprotectedWord, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }
            if (!telnet.Controller.Formatted)
            {
                return false;
            }

            // If not in an unprotected field, go to the next unprotected word.
            if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[telnet.Controller.CursorAddress]) ||
                FieldAttribute.IsProtectedAt(telnet.Controller.ScreenBuffer, telnet.Controller.CursorAddress))
            {
                address = FindNextUnprotectedWord(telnet.Controller.CursorAddress);
                if (address != -1)
                {
                    telnet.Controller.SetCursorAddress(address);
                }
                return true;
            }

            // If there's another word in this field, go to it.
            address = FindNextWordInField(telnet.Controller.CursorAddress);
            if (address != -1)
            {
                telnet.Controller.SetCursorAddress(address);
                return true;
            }

            /* If in a word, go to just after its end. */
            c = telnet.Controller.ScreenBuffer[telnet.Controller.CursorAddress];
            if (c != CharacterGenerator.Space && c != CharacterGenerator.Null)
            {
                address = telnet.Controller.CursorAddress;
                do
                {
                    c = telnet.Controller.ScreenBuffer[address];
                    if (c == CharacterGenerator.Space || c == CharacterGenerator.Null)
                    {
                        telnet.Controller.SetCursorAddress(address);
                        return true;
                    }
                    if (FieldAttribute.IsFA(c))
                    {
                        address = FindNextUnprotectedWord(address);
                        if (address != -1)
                        {
                            telnet.Controller.SetCursorAddress(address);
                        }
                        return true;
                    }
                    telnet.Controller.IncrementAddress(ref address);
                } while (address != telnet.Controller.CursorAddress);
            }
            //Otherwise, go to the next unprotected word.
            else
            {
                address = FindNextUnprotectedWord(telnet.Controller.CursorAddress);
                if (address != -1)
                {
                    telnet.Controller.SetCursorAddress(address);
                }
            }
            return true;
        }


        /// <summary>
        ///     Cursor up 1 position.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveCursorUp(params object[] args)
        {
            int address;

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(MoveCursorUp, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                telnet.Ansi.ansi_send_up();
                return true;
            }

            address = telnet.Controller.CursorAddress - telnet.Controller.ColumnCount;

            if (address < 0)
            {
                address = telnet.Controller.CursorAddress + telnet.Controller.RowCount*telnet.Controller.ColumnCount -
                          telnet.Controller.ColumnCount;
            }

            telnet.Controller.SetCursorAddress(address);
            return true;
        }


        /// <summary>
        ///     Cursor down 1 position.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveCursorDown(params object[] args)
        {
            int address;

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(MoveCursorDown, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                telnet.Ansi.ansi_send_down();
                return true;
            }

            address = (telnet.Controller.CursorAddress + telnet.Controller.ColumnCount)%
                      (telnet.Controller.ColumnCount*telnet.Controller.RowCount);
            telnet.Controller.SetCursorAddress(address);
            return true;
        }


        /// <summary>
        ///     Cursor to first field on next line or any lines after that.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveCursorToNewLine(params object[] args)
        {
            int address;
            int faIndex;
            var fa = telnet.Controller.FakeFA;


            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(MoveCursorToNewLine, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                telnet.SendChar('\n');
                return false;
            }

            address = (telnet.Controller.CursorAddress + telnet.Controller.ColumnCount)%
                      (telnet.Controller.ColumnCount*telnet.Controller.RowCount); /* down */
            address = address/telnet.Controller.ColumnCount*telnet.Controller.ColumnCount; /* 1st col */
            faIndex = telnet.Controller.GetFieldAttribute(address);

            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }
            if (faIndex != address && !FieldAttribute.IsProtected(fa))
            {
                telnet.Controller.SetCursorAddress(address);
            }
            else
            {
                telnet.Controller.SetCursorAddress(telnet.Controller.GetNextUnprotectedField(address));
            }
            return true;
        }


        /// <summary>
        ///     DUP key
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool DupAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(DupAction, args);
                return true;
            }
            if (telnet.IsAnsi)
                return false;
            if (HandleOrdinaryCharacter(CharacterGenerator.dup, false, false))
                telnet.Controller.SetCursorAddress(
                    telnet.Controller.GetNextUnprotectedField(telnet.Controller.CursorAddress));
            return true;
        }


        /// <summary>
        ///     FM key
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool FieldMarkAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(FieldMarkAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                return false;
            }
            HandleOrdinaryCharacter(CharacterGenerator.fm, false, false);
            return true;
        }


        /// <summary>
        ///     Vanilla AID keys
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool EnterAction(params object[] args)
        {
            if ((keyboardLock & KeyboardConstants.OiaMinus) != 0)
            {
                return false;
            }
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(EnterAction, args);
            }
            else
            {
                HandleAttentionIdentifierKey(AID.Enter);
            }
            return true;
        }


        public bool SystemRequestAction(params object[] args)
        {
            if (telnet.IsAnsi)
            {
                return false;
            }
            if (telnet.IsE)
            {
                telnet.Abort();
            }
            else
            {
                if ((keyboardLock & KeyboardConstants.OiaMinus) != 0)
                {
                    return false;
                }
                if (keyboardLock != 0)
                {
                    EnqueueTypeAheadAction(SystemRequestAction, args);
                }
                else
                {
                    HandleAttentionIdentifierKey(AID.SysReq);
                }
            }
            return true;
        }


        /// <summary>
        ///     Clear AID key
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool ClearAction(params object[] args)
        {
            if ((keyboardLock & KeyboardConstants.OiaMinus) != 0)
            {
                return false;
            }

            if (keyboardLock != 0 && telnet.IsConnected)
            {
                EnqueueTypeAheadAction(ClearAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                telnet.Ansi.ansi_send_clear();
                return true;
            }

            telnet.Controller.BufferAddress = 0;
            telnet.Controller.Clear(true);
            telnet.Controller.SetCursorAddress(0);

            if (telnet.IsConnected)
            {
                HandleAttentionIdentifierKey(AID.Clear);
            }
            return true;
        }


        /// <summary>
        ///     Cursor Select key (light pen simulator).
        /// </summary>
        /// <param name="address"></param>
        private void LightPenSelect(int address)
        {
            int faIndex;
            var fa = telnet.Controller.FakeFA;
            byte sel;
            int designator;

            faIndex = telnet.Controller.GetFieldAttribute(address);

            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }

            if (!FieldAttribute.IsSelectable(fa))
            {
                //ring_bell();
                return;
            }

            sel = telnet.Controller.ScreenBuffer[faIndex + 1];

            designator = faIndex + 1;

            switch (sel)
            {
                case CharacterGenerator.GreaterThan: /* > */
                    telnet.Controller.AddCharacter(designator, CharacterGenerator.QuestionMark, 0); /* change to ? */
                    telnet.Controller.MDTClear(telnet.Controller.ScreenBuffer, faIndex);
                    break;
                case CharacterGenerator.QuestionMark: /* ? */
                    telnet.Controller.AddCharacter(designator, CharacterGenerator.GreaterThan, 0); /* change to > */
                    telnet.Controller.MDTClear(telnet.Controller.ScreenBuffer, faIndex);
                    break;
                case CharacterGenerator.Space: /* space */
                case CharacterGenerator.Null: /* null */
                    HandleAttentionIdentifierKey(AID.SELECT);
                    break;
                case CharacterGenerator.Ampersand: /* & */
                    telnet.Controller.SetMDT(telnet.Controller.ScreenBuffer, faIndex);
                    HandleAttentionIdentifierKey(AID.Enter);
                    break;
                default:
                    //ring_bell();
                    break;
            }
        }


        /// <summary>
        ///     Cursor Select key (light pen simulator) -- at the current cursor location.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool CursorSelectAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(CursorSelectAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }
            LightPenSelect(telnet.Controller.CursorAddress);
            return true;
        }


        /// <summary>
        ///     Erase End Of Field Key.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool EraseEndOfFieldAction(params object[] args)
        {
            int address;
            int faIndex;
            var fa = telnet.Controller.FakeFA;


            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(EraseEndOfFieldAction, args);
                return false;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }

            address = telnet.Controller.CursorAddress;
            faIndex = telnet.Controller.GetFieldAttribute(address);

            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }

            if (FieldAttribute.IsProtected(fa) || FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorProtected);
                return false;
            }

            if (telnet.Controller.Formatted)
            {
                //Erase to next field attribute
                do
                {
                    telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                    telnet.Controller.IncrementAddress(ref address);
                } while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]));

                telnet.Controller.SetMDT(telnet.Controller.ScreenBuffer, faIndex);
            }
            else
            {
                //Erase to end of screen
                do
                {
                    telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                    telnet.Controller.IncrementAddress(ref address);
                } while (address != 0);
            }
            return true;
        }


        /// <summary>
        ///     Erase all Input Key.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool EraseInputAction(params object[] args)
        {
            int address, sbAddress;
            var fa = telnet.Controller.FakeFA;
            bool f;


            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(EraseInputAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }

            if (telnet.Controller.Formatted)
            {
                //Find first field attribute
                address = 0;
                do
                {
                    if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
                    {
                        break;
                    }
                    telnet.Controller.IncrementAddress(ref address);
                } while (address != 0);

                sbAddress = address;
                f = false;

                do
                {
                    fa = telnet.Controller.ScreenBuffer[address];
                    if (!FieldAttribute.IsProtected(fa))
                    {
                        telnet.Controller.MDTClear(telnet.Controller.ScreenBuffer, address);
                        do
                        {
                            telnet.Controller.IncrementAddress(ref address);
                            if (!f)
                            {
                                telnet.Controller.SetCursorAddress(address);
                                f = true;
                            }

                            if (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
                            {
                                telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                            }
                        } while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]));
                    }
                    else
                    {
                        /* skip protected */
                        do
                        {
                            telnet.Controller.IncrementAddress(ref address);
                        } while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]));
                    }
                } while (address != sbAddress);

                if (!f)
                {
                    telnet.Controller.SetCursorAddress(0);
                }
            }
            else
            {
                telnet.Controller.Clear(true);
                telnet.Controller.SetCursorAddress(0);
            }
            return true;
        }


        /// <summary>
        ///     Delete word key.  Backspaces the cursor until it hits the front of a word, deletes characters until it hits a blank
        ///     or null,
        ///     and deletes all of these but the last. Which is to say, does a ^W.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool DeleteWordAction(params object[] args)
        {
            int address, address2, frontAddress, backAddress, endAddress;
            int faIndex;
            var fa = telnet.Controller.FakeFA;


            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(DeleteWordAction, args);
                return false;
            }

            if (telnet.IsAnsi)
            {
                telnet.SendWErase();
                return true;
            }

            if (!telnet.Controller.Formatted)
            {
                return true;
            }

            address = telnet.Controller.CursorAddress;
            faIndex = telnet.Controller.GetFieldAttribute(address);
            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }

            // Make sure we're on a modifiable field.
            if (FieldAttribute.IsProtected(fa) || FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorProtected);
                return false;
            }

            //Search backwards for a non-blank character.
            frontAddress = address;
            while (telnet.Controller.ScreenBuffer[frontAddress] == CharacterGenerator.Space ||
                   telnet.Controller.ScreenBuffer[frontAddress] == CharacterGenerator.Null)
            {
                telnet.Controller.DecrementAddress(ref frontAddress);
            }

            //If we ran into the edge of the field without seeing any non-blanks,
            //there isn't any word to delete; just move the cursor. 
            if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[frontAddress]))
            {
                telnet.Controller.SetCursorAddress(frontAddress + 1);
                return true;
            }

            //FrontAddress is now pointing at a non-blank character.  Now search for the first blank to the left of that
            //(or the edge of the field), leaving frontAddress pointing at the the beginning of the word.
            while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[frontAddress]) &&
                   telnet.Controller.ScreenBuffer[frontAddress] != CharacterGenerator.Space &&
                   telnet.Controller.ScreenBuffer[frontAddress] != CharacterGenerator.Null)
            {
                telnet.Controller.DecrementAddress(ref frontAddress);
            }

            telnet.Controller.IncrementAddress(ref frontAddress);

            //Find the end of the word, searching forward for the edge of the field or a non-blank.
            backAddress = frontAddress;
            while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[backAddress]) &&
                   telnet.Controller.ScreenBuffer[backAddress] != CharacterGenerator.Space &&
                   telnet.Controller.ScreenBuffer[backAddress] != CharacterGenerator.Null)
            {
                telnet.Controller.IncrementAddress(ref backAddress);
            }

            //Find the start of the next word, leaving back_baddr pointing at it or at the end of the field.
            while (telnet.Controller.ScreenBuffer[backAddress] == CharacterGenerator.Space ||
                   telnet.Controller.ScreenBuffer[backAddress] == CharacterGenerator.Null)
            {
                telnet.Controller.IncrementAddress(ref backAddress);
            }

            // Find the end of the field, leaving end_baddr pointing at the field attribute of the start of the next field.
            endAddress = backAddress;
            while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[endAddress]))
            {
                telnet.Controller.IncrementAddress(ref endAddress);
            }

            //Copy any text to the right of the word we are deleting.
            address = frontAddress;
            address2 = backAddress;
            while (address2 != endAddress)
            {
                telnet.Controller.AddCharacter(address, telnet.Controller.ScreenBuffer[address2], 0);
                telnet.Controller.IncrementAddress(ref address);
                telnet.Controller.IncrementAddress(ref address2);
            }

            // Insert nulls to pad out the end of the field.
            while (address != endAddress)
            {
                telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                telnet.Controller.IncrementAddress(ref address);
            }

            // Set the MDT and move the cursor.
            telnet.Controller.SetMDT(telnet.Controller.ScreenBuffer, faIndex);
            telnet.Controller.SetCursorAddress(frontAddress);
            return true;
        }


        /// <summary>
        ///     Delete field key.  Similar to EraseEOF, but it wipes out the entire field rather than just
        ///     to the right of the cursor, and it leaves the cursor at the front of the field.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool DeleteFieldAction(params object[] args)
        {
            int address;
            var fa = telnet.Controller.FakeFA;
            int faIndex;


            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(DeleteFieldAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                telnet.SendKill();
                return true;
            }

            if (!telnet.Controller.Formatted)
            {
                return false;
            }

            address = telnet.Controller.CursorAddress;
            faIndex = telnet.Controller.GetFieldAttribute(address);

            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }

            if (FieldAttribute.IsProtected(fa) || FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorProtected);
                return false;
            }

            while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
            {
                telnet.Controller.DecrementAddress(ref address);
            }

            telnet.Controller.IncrementAddress(ref address);
            telnet.Controller.SetCursorAddress(address);

            while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
            {
                telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                telnet.Controller.IncrementAddress(ref address);
            }

            telnet.Controller.SetMDT(telnet.Controller.ScreenBuffer, faIndex);
            return true;
        }


        /// <summary>
        ///     Set insert mode key.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool InsertAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(InsertAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }

            insertMode = true;
            return true;
        }


        /// <summary>
        ///     Toggle insert mode key.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool ToggleInsertAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(ToggleInsertAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }

            if (insertMode)
            {
                insertMode = false;
            }
            else
            {
                insertMode = true;
            }

            return true;
        }


        /// <summary>
        ///     Toggle reverse mode key.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool ToggleReverseAction(params object[] args)
        {
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(ToggleReverseAction, args);
                return true;
            }

            if (telnet.IsAnsi)
            {
                return false;
            }

            reverseMode = !reverseMode;
            return true;
        }


        /// <summary>
        ///     Move the cursor to the first blank after the last nonblank in the field, or if the field is full, to the last
        ///     character in the field.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool FieldEndAction(params object[] args)
        {
            int address;
            int faIndex;
            var fa = telnet.Controller.FakeFA;
            byte c;
            var lastNonBlank = -1;


            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(FieldEndAction, args);
                return true;
            }
            if (telnet.IsAnsi)
            {
                return false;
            }

            if (!telnet.Controller.Formatted)
            {
                return false;
            }

            address = telnet.Controller.CursorAddress;
            faIndex = telnet.Controller.GetFieldAttribute(address);

            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }
            //
            if (faIndex == telnet.Controller.ScreenBuffer[address] || FieldAttribute.IsProtected(fa))
            {
                return false;
            }

            address = faIndex;
            while (true)
            {
                telnet.Controller.IncrementAddress(ref address);
                c = telnet.Controller.ScreenBuffer[address];
                if (FieldAttribute.IsFA(c))
                {
                    break;
                }
                if (c != CharacterGenerator.Null && c != CharacterGenerator.Space)
                {
                    lastNonBlank = address;
                }
            }

            if (lastNonBlank == -1)
            {
                address = faIndex; // - this.telnet.tnctlr.screen_buf;
                telnet.Controller.IncrementAddress(ref address);
            }
            else
            {
                address = lastNonBlank;
                telnet.Controller.IncrementAddress(ref address);
                if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
                {
                    address = lastNonBlank;
                }
            }
            telnet.Controller.SetCursorAddress(address);
            return true;
        }


        /// <summary>
        ///     MoveCursor action.  Depending on arguments, this is either a move to the mouse cursor position, or to an absolute
        ///     location.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool MoveCursorAction(params object[] args)
        {
            int address;
            int row, col;


            if (keyboardLock != 0)
            {
                if (args.Length == 2)
                {
                    EnqueueTypeAheadAction(MoveCursorAction, args);
                }
                return true;
            }

            if (args.Length == 2)
            {
                //Probably a macro call
                row = (int) args[0];
                col = (int) args[1];

                if (!telnet.Is3270)
                {
                    row--;
                    col--;
                }

                if (row < 0)
                {
                    row = 0;
                }

                if (col < 0)
                {
                    col = 0;
                }

                address = (row*telnet.Controller.ColumnCount + col)%
                          (telnet.Controller.RowCount*telnet.Controller.ColumnCount);
                telnet.Controller.SetCursorAddress(address);
            }
            else
            {
                //Couldn't say
                telnet.Events.ShowError("MoveCursor_action requires 0 or 2 arguments");
            }

            return true;
        }


        /// <summary>
        ///     Key action.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool SendKeyAction(params object[] args)
        {
            int i;
            int k;
            KeyType keytype;


            for (i = 0; i < args.Length; i++)
            {
                var s = args[i] as string;

                k = StringToKeySymbol(s, out keytype);
                if (k == KeyboardConstants.NoSymbol)
                {
                    telnet.Events.ShowError("SendKey action: Nonexistent or invalid KeySym: " + s);
                    continue;
                }
                if ((k & ~0xff) != 0)
                {
                    telnet.Events.ShowError("SendKey action: Invalid KeySym: " + s);
                    continue;
                }
                HandleAsciiCharacter((byte) (k & 0xff), keytype, EIAction.Key);
            }
            return true;
        }


        /// <summary>
        ///     Translate a keysym name to a keysym, including APL and extended characters.
        /// </summary>
        /// <param name="s"></param>
        /// <param name="keytypep"></param>
        /// <returns></returns>
        private int StringToKeySymbol(string s, out KeyType keytypep)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///     String action.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool SendStringAction(params object[] args)
        {
            int i;
            var s = "";
            for (i = 0; i < args.Length; i++)
            {
                s += (string) args[i];
            }

            // Set a pending string.
            PsSet(s, false);
            var ok = !telnet.Events.IsError();

            if (!ok && telnet.Config.ThrowExceptionOnLockedScreen)
            {
                throw new ApplicationException(telnet.Events.GetErrorAsText());
            }

            return ok;
        }


        public bool HexStringAction(params object[] args)
        {
            int i;
            var s = "";
            string t;

            for (i = 0; i < args.Length; i++)
            {
                t = (string) args[i];
                if (t.Length > 2 && (t.Substring(0, 2) == "0x" || t.Substring(0, 2) == "0X"))
                {
                    t = t.Substring(2);
                }
                s += t;
            }
            if (s.Length == 0)
            {
                return false;
            }

            // Set a pending string.
            PsSet(s, true);
            return true;
        }


        /// <summary>
        ///     Dual-mode action for the "asciicircum" ("^") key:
        ///     If in ANSI mode, pass through untranslated.
        ///     If in 3270 mode, translate to "notsign".
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool CircumNotAction(params object[] args)
        {
            if (telnet.Is3270 && composing == Composing.None)
            {
                HandleAsciiCharacter(0xac, KeyType.Standard, EIAction.Key);
            }
            else
            {
                HandleAsciiCharacter((byte) '^', KeyType.Standard, EIAction.Key);
            }
            return true;
        }

        /// <summary>
        ///     PA key action for String actions
        /// </summary>
        /// <param name="n"></param>
        private void DoPA(int n)
        {
            if (n < 1 || n > PA_SZ)
            {
                telnet.Events.ShowError("Unknown PA key %d", n);
                return;
            }
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(PAAction, n.ToString());
                return;
            }
            HandleAttentionIdentifierKey(KeyboardConstants.PaTranslation[n - 1]);
        }

        /// <summary>
        ///     PF key action for String actions
        /// </summary>
        /// <param name="n"></param>
        private void DoFunctionKey(int n)
        {
            if (n < 1 || n > PF_SZ)
            {
                telnet.Events.ShowError("Unknown PF key %d", n);
                return;
            }
            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(PFAction, n.ToString());
                return;
            }
            HandleAttentionIdentifierKey(KeyboardConstants.PfTranslation[n - 1]);
        }


        /// <summary>
        ///     Set or clear the keyboard scroll lock.
        /// </summary>
        /// <param name="lockflag"></param>
        private void ToggleScrollLock(bool lockflag)
        {
            if (telnet.Is3270)
            {
                if (lockflag)
                {
                    KeyboardLockSet(KeyboardConstants.Scrolled, "ToggleScrollLock");
                }
                else
                {
                    KeyboardLockClear(KeyboardConstants.Scrolled, "ToggleScrollLock");
                }
            }
        }


        /// <summary>
        ///     Move the cursor back within the legal paste area.
        /// </summary>
        /// <param name="lMargin"></param>
        /// <returns>Returns a bool indicating success.</returns>
        private bool RemarginCursor(int lMargin)
        {
            var ever = false;
            var address = 0;
            var b0 = 0;
            int faIndex;
            var fa = telnet.Controller.FakeFA;


            address = telnet.Controller.CursorAddress;
            while (telnet.Controller.AddressToColumn(address) < lMargin)
            {
                address = telnet.Controller.RowColumnToByteAddress(telnet.Controller.AddresstoRow(address), lMargin);
                if (!ever)
                {
                    b0 = address;
                    ever = true;
                }
                faIndex = telnet.Controller.GetFieldAttribute(address);
                if (faIndex != -1)
                    fa = telnet.Controller.ScreenBuffer[faIndex];

                if (faIndex == address || FieldAttribute.IsProtected(fa))
                {
                    address = telnet.Controller.GetNextUnprotectedField(address);
                    if (address <= b0)
                        return false;
                }
            }

            telnet.Controller.SetCursorAddress(address);
            return true;
        }


        /// <summary>
        ///     Pretend that a sequence of keys was entered at the keyboard.
        ///     "Pasting" means that the sequence came from the X clipboard.  Returns are ignored; newlines mean
        ///     "move to beginning of next line"; tabs and formfeeds become spaces.  Backslashes are not special,
        ///     but ASCII ESC characters are used to signify 3270 Graphic Escapes.
        ///     "Not pasting" means that the sequence is a login string specified in the hosts file, or a parameter
        ///     to the String action.  Returns are "move to beginning of next line"; newlines mean "Enter AID" and
        ///     the termination of processing the string.  Backslashes are processed as in C.
        /// </summary>
        /// <param name="args"></param>
        /// <returns>Returns the number of unprocessed characters.</returns>
        public bool EmulateInputAction(params object[] args)
        {
            var sb = new StringBuilder();
            int i;
            for (i = 0; i < args.Length; i++)
            {
                sb.Append(args[i]);
            }
            EmulateInput(sb.ToString(), false);
            return true;
        }


        private int EmulateInput(string s, bool pasting)
        {
            char c;

            var state = EIState.Base;
            var literal = 0;
            var nc = 0;
            var ia = pasting ? EIAction.Paste : EIAction.String;
            var originalAddress = telnet.Controller.CursorAddress;
            var originalColumn = telnet.Controller.AddressToColumn(telnet.Controller.CursorAddress);
            var length = s.Length;


            //In the switch statements below, "break" generally means "consume this character," while "continue" means "rescan this character."
            while (s.Length > 0)
            {
                //It isn't possible to unlock the keyboard from a string, so if the keyboard is locked, it's fatal
                if (keyboardLock != 0)
                {
                    telnet.Trace.trace_event("  keyboard locked, string dropped. kybdlock=" + keyboardLock + "\n");
                    if (telnet.Config.ThrowExceptionOnLockedScreen)
                    {
                        throw new ApplicationException(
                            "Keyboard locked typing data onto screen - data was lost.  Turn of configuration option 'ThrowExceptionOnLockedScreen' to ignore this exception.");
                    }
                    return 0;
                }

                if (pasting && telnet.Is3270)
                {
                    // Check for cursor wrap to top of screen
                    if (telnet.Controller.CursorAddress < originalAddress)
                    {
                        // Wrapped
                        return length - 1;
                    }

                    // Jump cursor over left margin.
                    if (telnet.Appres.Toggled(Appres.MarginedPaste) &&
                        telnet.Controller.AddressToColumn(telnet.Controller.CursorAddress) < originalColumn)
                    {
                        if (!RemarginCursor(originalColumn))
                        {
                            return length - 1;
                        }
                    }
                }

                c = s[0];

                switch (state)
                {
                    case EIState.Base:
                        switch (c)
                        {
                            case '\b':
                            {
                                action.action_internal(LeftAction, ia);
                                continue;
                            }
                            case '\f':
                            {
                                if (pasting)
                                {
                                    HandleAsciiCharacter((byte) ' ', KeyType.Standard, ia);
                                }
                                else
                                {
                                    action.action_internal(ClearAction, ia);
                                    if (telnet.Is3270)
                                    {
                                        return length - 1;
                                    }
                                }
                                break; // mfw - added BUGBUG
                            }
                            case '\n':
                            {
                                if (pasting)
                                {
                                    action.action_internal(MoveCursorToNewLine, ia);
                                }
                                else
                                {
                                    action.action_internal(EnterAction, ia);
                                    if (telnet.Is3270)
                                        return length - 1;
                                }
                                break;
                            }
                            case '\r':
                            {
                                // Ignored
                                break;
                            }
                            case '\t':
                            {
                                action.action_internal(TabForwardAction, ia);
                                break;
                            }
                            case '\\':
                            {
                                // Backslashes are NOT special when pasting
                                if (!pasting)
                                {
                                    state = EIState.Backslash;
                                }
                                else
                                {
                                    HandleAsciiCharacter((byte) c, KeyType.Standard, ia);
                                }
                                break;
                            }
                            case (char) 0x1b: /* ESC is special only when pasting */
                            {
                                if (pasting)
                                {
                                    state = EIState.XGE;
                                }
                                break;
                            }
                            case '[':
                            {
                                // APL left bracket

                                //MFW 
                                /* if (pasting && appres.apl_mode)
									   key_ACharacter((byte) XK_Yacute,KT_GE, ia);
							   else*/
                                HandleAsciiCharacter((byte) c, KeyType.Standard, ia);
                                break;
                            }
                            case ']':
                            {
                                // APL right bracket

                                //MFW 
                                /* if (pasting && appres.apl_mode)
									   key_ACharacter((byte) XK_diaeresis, KT_GE, ia);
							   else*/
                                HandleAsciiCharacter((byte) c, KeyType.Standard, ia);
                                break;
                            }
                            default:
                            {
                                HandleAsciiCharacter((byte) c, KeyType.Standard, ia);
                                break;
                            }
                        }
                        break;
                    case EIState.Backslash:
                    {
                        //Last character was a backslash */
                        switch (c)
                        {
                            case 'a':
                            {
                                telnet.Events.ShowError("String_action: Bell not supported");
                                state = EIState.Base;
                                break;
                            }
                            case 'b':
                            {
                                action.action_internal(LeftAction, ia);
                                state = EIState.Base;
                                break;
                            }
                            case 'f':
                            {
                                action.action_internal(ClearAction, ia);
                                state = EIState.Base;
                                if (telnet.Is3270)
                                {
                                    return length - 1;
                                }
                                break;
                            }
                            case 'n':
                            {
                                action.action_internal(EnterAction, ia);
                                state = EIState.Base;
                                if (telnet.Is3270)
                                    return length - 1;
                                break;
                            }
                            case 'p':
                            {
                                state = EIState.BackP;
                                break;
                            }
                            case 'r':
                            {
                                action.action_internal(MoveCursorToNewLine, ia);
                                state = EIState.Base;
                                break;
                            }
                            case 't':
                            {
                                action.action_internal(TabForwardAction, ia);
                                state = EIState.Base;
                                break;
                            }
                            case 'T':
                            {
                                action.action_internal(BackTab_action, ia);
                                state = EIState.Base;
                            }
                                break;
                            case 'v':
                            {
                                telnet.Events.ShowError("String_action: Vertical tab not supported");
                                state = EIState.Base;
                                break;
                            }
                            case 'x':
                            {
                                state = EIState.BackX;
                                break;
                            }
                            case '\\':
                            {
                                HandleAsciiCharacter((byte) c, KeyType.Standard, ia);
                                state = EIState.Base;
                                break;
                            }
                            case '0':
                            case '1':
                            case '2':
                            case '3':
                            case '4':
                            case '5':
                            case '6':
                            case '7':
                            {
                                state = EIState.Octal;
                                literal = 0;
                                nc = 0;
                                continue;
                            }
                            default:
                            {
                                state = EIState.Base;
                                continue;
                            }
                        }
                        break;
                    }
                    case EIState.BackP:
                    {
                        // Last two characters were "\p"
                        switch (c)
                        {
                            case 'a':
                            {
                                literal = 0;
                                nc = 0;
                                state = EIState.BackPA;
                                break;
                            }
                            case 'f':
                            {
                                literal = 0;
                                nc = 0;
                                state = EIState.BackPF;
                                break;
                            }
                            default:
                            {
                                telnet.Events.ShowError("StringAction: Unknown character after \\p");
                                state = EIState.Base;
                                break;
                            }
                        }
                        break;
                    }
                    case EIState.BackPF:
                    {
                        // Last three characters were "\pf"
                        if (nc < 2 && IsDigit(c))
                        {
                            literal = literal*10 + (c - '0');
                            nc++;
                        }
                        else if (nc == 0)
                        {
                            telnet.Events.ShowError("StringAction: Unknown character after \\pf");
                            state = EIState.Base;
                        }
                        else
                        {
                            DoFunctionKey(literal);
                            if (telnet.Is3270)
                            {
                                return length - 1;
                            }
                            state = EIState.Base;
                            continue;
                        }
                        break;
                    }
                    case EIState.BackPA:
                    {
                        // Last three characters were "\pa"
                        if (nc < 1 && IsDigit(c))
                        {
                            literal = literal*10 + (c - '0');
                            nc++;
                        }
                        else if (nc == 0)
                        {
                            telnet.Events.ShowError("String_action: Unknown character after \\pa");
                            state = EIState.Base;
                        }
                        else
                        {
                            DoPA(literal);
                            if (telnet.Is3270)
                            {
                                return length - 1;
                            }
                            state = EIState.Base;
                            continue;
                        }
                        break;
                    }
                    case EIState.BackX:
                    {
                        // Last two characters were "\x"
                        if (IsXDigit(c))
                        {
                            state = EIState.Hex;
                            literal = 0;
                            nc = 0;
                            continue;
                        }
                        telnet.Events.ShowError("String_action: Missing hex digits after \\x");
                        state = EIState.Base;
                        continue;
                    }
                    case EIState.Octal:
                    {
                        // Have seen \ and one or more octal digits
                        if (nc < 3 && IsDigit(c) && c < '8')
                        {
                            literal = literal*8 + FromHex(c);
                            nc++;
                            break;
                        }
                        HandleAsciiCharacter((byte) literal, KeyType.Standard, ia);
                        state = EIState.Base;
                        continue;
                    }
                    case EIState.Hex:
                    {
                        // Have seen \ and one or more hex digits
                        if (nc < 2 && IsXDigit(c))
                        {
                            literal = literal*16 + FromHex(c);
                            nc++;
                            break;
                        }
                        HandleAsciiCharacter((byte) literal, KeyType.Standard, ia);
                        state = EIState.Base;
                        continue;
                    }
                    case EIState.XGE:
                    {
                        //Have seen ESC
                        switch (c)
                        {
                            case ';':
                            {
                                // FM
                                HandleOrdinaryCharacter(CharacterGenerator.fm, false, true);
                                break;
                            }
                            case '*':
                            {
                                // DUP
                                HandleOrdinaryCharacter(CharacterGenerator.dup, false, true);
                                break;
                            }
                            default:
                            {
                                HandleAsciiCharacter((byte) c, KeyType.GE, ia);
                                break;
                            }
                        }
                        state = EIState.Base;
                        break;
                    }
                }
                s = s.Substring(1);
                //s++;
                length--;
            }

            switch (state)
            {
                case EIState.Octal:
                case EIState.Hex:
                {
                    HandleAsciiCharacter((byte) literal, KeyType.Standard, ia);
                    state = EIState.Base;
                    break;
                }
                case EIState.BackPF:
                    if (nc > 0)
                    {
                        DoFunctionKey(literal);
                        state = EIState.Base;
                    }
                    break;
                case EIState.BackPA:
                    if (nc > 0)
                    {
                        DoPA(literal);
                        state = EIState.Base;
                    }
                    break;
                default:
                    break;
            }

            if (state != EIState.Base)
            {
                telnet.Events.ShowError("String_action: Missing data after \\");
            }

            return length;
        }


        /// <summary>
        ///     Pretend that a sequence of hexadecimal characters was entered at the keyboard.  The input is a sequence
        ///     of hexadecimal bytes, 2 characters per byte.  If connected in ANSI mode, these are treated as ASCII
        ///     characters; if in 3270 mode, they are considered EBCDIC.
        ///     Graphic Escapes are handled as \E.
        /// </summary>
        /// <param name="s"></param>
        private void HexInput(string s)
        {
            bool escaped;
            byte[] xBuffer = null;
            var bufferIndex = 0;
            var byteCount = 0;
            var index = 0;
            escaped = false;

            // Validate the string.
            if (s.Length%2 != 0)
            {
                telnet.Events.ShowError("HexStringAction: Odd number of characters in specification");
                return;
            }

            while (index < s.Length)
            {
                if (IsXDigit(s[index]) && IsXDigit(s[index + 1]))
                {
                    escaped = false;
                    byteCount++;
                }
                else if (s.Substring(index, 2).ToLower() == "\\e")
                {
                    if (escaped)
                    {
                        telnet.Events.ShowError("HexString_action: Double \\E");
                        return;
                    }
                    if (!telnet.Is3270)
                    {
                        telnet.Events.ShowError("HexString_action: \\E in ANSI mode");
                        return;
                    }
                    escaped = true;
                }
                else
                {
                    telnet.Events.ShowError("HexString_action: Illegal character in specification");
                    return;
                }
                index += 2;
            }
            if (escaped)
            {
                telnet.Events.ShowError("HexString_action: Nothing follows \\E");
                return;
            }

            // Allocate a temporary buffer.
            if (!telnet.Is3270 && byteCount != 0)
            {
                xBuffer = new byte[byteCount];
                bufferIndex = 0;
            }

            // Fill it
            index = 0;
            escaped = false;
            while (index < s.Length)
            {
                if (IsXDigit(s[index]) && IsXDigit(s[index + 1]))
                {
                    byte c;

                    c = (byte) (FromHex(s[index])*16 + FromHex(s[index + 1]));
                    if (telnet.Is3270)
                    {
                        HandleOrdinaryCharacter(Tables.Ebc2Cg[c], escaped, true);
                    }
                    else
                    {
                        xBuffer[bufferIndex++] = c;
                    }
                    escaped = false;
                }
                else if (s.Substring(index, 2).ToLower() == "\\e")
                {
                    escaped = true;
                }
                index += 2;
            }
            if (!telnet.Is3270 && byteCount != 0)
            {
                telnet.SendHexAnsiOut(xBuffer, byteCount);
            }
        }


        /// <summary>
        ///     FieldExit for the 5250-like emulation.
        ///     Erases from the current cursor position to the end of the field, and moves the cursor to the beginning of the next
        ///     field.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public bool FieldExitAction(params object[] args)
        {
            int address;
            int faIndex;
            var fa = telnet.Controller.FakeFA;


            if (telnet.IsAnsi)
            {
                telnet.SendChar('\n');
                return true;
            }

            if (keyboardLock != 0)
            {
                EnqueueTypeAheadAction(FieldExitAction, args);
                return true;
            }

            address = telnet.Controller.CursorAddress;
            faIndex = telnet.Controller.GetFieldAttribute(address);

            if (faIndex != -1)
            {
                fa = telnet.Controller.ScreenBuffer[faIndex];
            }

            if (FieldAttribute.IsProtected(fa) || FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
            {
                HandleOperatorError(address, KeyboardConstants.ErrorProtected);
                return false;
            }

            if (telnet.Controller.Formatted)
            {
                //Erase to next field attribute
                do
                {
                    telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                    telnet.Controller.IncrementAddress(ref address);
                } while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]));

                telnet.Controller.SetMDT(telnet.Controller.ScreenBuffer, faIndex);
                telnet.Controller.SetCursorAddress(
                    telnet.Controller.GetNextUnprotectedField(telnet.Controller.CursorAddress));
            }
            else
            {
                // Erase to end of screen
                do
                {
                    telnet.Controller.AddCharacter(address, CharacterGenerator.Null, 0);
                    telnet.Controller.IncrementAddress(ref address);
                } while (address != 0);
            }
            return true;
        }


        public bool FieldsAction(params object[] args)
        {
            var fa = telnet.Controller.FakeFA;

            var fieldpos = 0;
            var index = 0;
            int end;

            do
            {
                var newfield = telnet.Controller.GetNextUnprotectedField(fieldpos);

                if (newfield <= fieldpos)
                {
                    break;
                }

                end = newfield;
                while (!FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[end]))
                {
                    telnet.Controller.IncrementAddress(ref end);
                    if (end == 0)
                    {
                        end = telnet.Controller.ColumnCount*telnet.Controller.RowCount - 1;
                        break;
                    }
                }

                telnet.Action.action_output("data: field[" + index + "] at " + newfield + " to " + end + " (x=" +
                                            telnet.Controller.AddressToColumn(newfield) + ", y=" +
                                            telnet.Controller.AddresstoRow(newfield) + ", len=" + (end - newfield + 1) +
                                            ")\n");

                index++;
                fieldpos = newfield;
            } while (true);

            return true;
        }


        public bool FieldGetAction(params object[] args)
        {
            var fieldnumber = (int) args[0];
            var fieldpos = 0;
            var index = 0;

            if (!telnet.Controller.Formatted)
            {
                telnet.Events.ShowError("FieldGet: Screen is not formatted");
                return false;
            }

            do
            {
                var newfield = telnet.Controller.GetNextUnprotectedField(fieldpos);
                if (newfield <= fieldpos)
                {
                    break;
                }

                if (fieldnumber == index)
                {
                    var fa = telnet.Controller.FakeFA;
                    int faIndex;
                    int start;
                    int address;
                    var length = 0;

                    faIndex = telnet.Controller.GetFieldAttribute(newfield);
                    if (faIndex != -1)
                    {
                        fa = telnet.Controller.ScreenBuffer[faIndex];
                    }

                    start = faIndex;
                    telnet.Controller.IncrementAddress(ref start);
                    address = start;

                    do
                    {
                        if (FieldAttribute.IsFA(telnet.Controller.ScreenBuffer[address]))
                        {
                            break;
                        }

                        length++;
                        telnet.Controller.IncrementAddress(ref address);
                    } while (address != start);

                    telnet.Controller.DumpRange(start, length, true, telnet.Controller.ScreenBuffer,
                        telnet.Controller.RowCount, telnet.Controller.ColumnCount);

                    return true;
                }

                index++;
                fieldpos = newfield;
            } while (true);

            telnet.Events.ShowError("FieldGet: Field %d not found", fieldnumber);
            return true;
        }


        public bool FieldSetAction(params object[] args)
        {
            var fieldnumber = (int) args[0];
            var fielddata = (string) args[1];
            var fieldpos = 0;
            var index = 0;

            var fa = telnet.Controller.FakeFA;

            if (!telnet.Controller.Formatted)
            {
                telnet.Events.ShowError("FieldSet: Screen is not formatted");
                return false;
            }

            do
            {
                var newfield = telnet.Controller.GetNextUnprotectedField(fieldpos);
                if (newfield <= fieldpos)
                {
                    break;
                }

                if (fieldnumber == index)
                {
                    telnet.Controller.CursorAddress = newfield;
                    DeleteFieldAction(null, null, null, 0);
                    PsSet(fielddata, false);

                    return true;
                }

                index++;
                fieldpos = newfield;
            } while (true);

            telnet.Events.ShowError("FieldGet: Field %d not found", fieldnumber);
            return true;
        }

        #region Nested Classes

        internal class AKeySym
        {
            public byte keysym;
            public KeyType keytype;
        }

        internal class TAItem
        {
            public object[] args;
            public ActionDelegate fn;

            public TAItem(ActionDelegate fn, object[] args)
            {
                this.fn = fn;
                this.args = args;
            }
        }

        #endregion Nested Classes
    }
}