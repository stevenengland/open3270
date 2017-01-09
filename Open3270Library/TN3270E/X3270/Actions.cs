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
using StEn.Open3270.Exceptions;

namespace StEn.Open3270.TN3270E.X3270
{
    internal class Actions
    {
        private readonly int actionCount;
        private readonly Hashtable actionLookup = new Hashtable();

        private readonly XtActionRec[] actions;
        private ArrayList datacapture;
        private ArrayList datastringcapture;

        //public iaction ia_cause;

        public string[] ia_name =
        {
            "String", "Paste", "Screen redraw", "Keypad", "Default", "Key",
            "Macro", "Script", "Peek", "Typeahead", "File transfer", "Command",
            "Keymap"
        };

        private readonly Telnet telnet;

        internal Actions(Telnet tn)
        {
            telnet = tn;
            actions = new[]
            {
                new XtActionRec("printtext", false, telnet.Print.PrintTextAction),
                new XtActionRec("flip", false, telnet.Keyboard.FlipAction),
                new XtActionRec("ascii", false, telnet.Controller.AsciiAction),
                new XtActionRec("dumpxml", false, telnet.Controller.DumpXMLAction),
                new XtActionRec("asciifield", false, telnet.Controller.AsciiFieldAction),
                new XtActionRec("attn", true, telnet.Keyboard.AttnAction),
                new XtActionRec("backspace", false, telnet.Keyboard.BackSpaceAction),
                new XtActionRec("backtab", false, telnet.Keyboard.BackTab_action),
                new XtActionRec("circumnot", false, telnet.Keyboard.CircumNotAction),
                new XtActionRec("clear", true, telnet.Keyboard.ClearAction),
                new XtActionRec("cursorselect", false, telnet.Keyboard.CursorSelectAction),
                new XtActionRec("delete", false, telnet.Keyboard.DeleteAction),
                new XtActionRec("deletefield", false, telnet.Keyboard.DeleteFieldAction),
                new XtActionRec("deleteword", false, telnet.Keyboard.DeleteWordAction),
                new XtActionRec("down", false, telnet.Keyboard.MoveCursorDown),
                new XtActionRec("dup", false, telnet.Keyboard.DupAction),
                new XtActionRec("emulateinput", true, telnet.Keyboard.EmulateInputAction),
                new XtActionRec("enter", true, telnet.Keyboard.EnterAction),
                new XtActionRec("erase", false, telnet.Keyboard.EraseAction),
                new XtActionRec("eraseeof", false, telnet.Keyboard.EraseEndOfFieldAction),
                new XtActionRec("eraseinput", false, telnet.Keyboard.EraseInputAction),
                new XtActionRec("fieldend", false, telnet.Keyboard.FieldEndAction),
                new XtActionRec("fields", false, telnet.Keyboard.FieldsAction),
                new XtActionRec("fieldget", false, telnet.Keyboard.FieldGetAction),
                new XtActionRec("fieldset", false, telnet.Keyboard.FieldSetAction),
                new XtActionRec("fieldmark", false, telnet.Keyboard.FieldMarkAction),
                new XtActionRec("fieldexit", false, telnet.Keyboard.FieldExitAction),
                new XtActionRec("hexString", false, telnet.Keyboard.HexStringAction),
                new XtActionRec("home", false, telnet.Keyboard.HomeAction),
                new XtActionRec("insert", false, telnet.Keyboard.InsertAction),
                new XtActionRec("interrupt", true, telnet.Keyboard.InterruptAction),
                new XtActionRec("key", false, telnet.Keyboard.SendKeyAction),
                new XtActionRec("left", false, telnet.Keyboard.LeftAction),
                new XtActionRec("left2", false, telnet.Keyboard.MoveCursorLeft2Positions),
                new XtActionRec("monocase", false, telnet.Keyboard.MonoCaseAction),
                new XtActionRec("movecursor", false, telnet.Keyboard.MoveCursorAction),
                new XtActionRec("Newline", false, telnet.Keyboard.MoveCursorToNewLine),
                new XtActionRec("NextWord", false, telnet.Keyboard.MoveCursorToNextUnprotectedWord),
                new XtActionRec("PA", true, telnet.Keyboard.PAAction),
                new XtActionRec("PF", true, telnet.Keyboard.PFAction),
                new XtActionRec("PreviousWord", false, telnet.Keyboard.PreviousWordAction),
                new XtActionRec("Reset", true, telnet.Keyboard.ResetAction),
                new XtActionRec("Right", false, telnet.Keyboard.MoveRight),
                new XtActionRec("Right2", false, telnet.Keyboard.MoveCursorRight2Positions),
                new XtActionRec("String", true, telnet.Keyboard.SendStringAction),
                new XtActionRec("SysReq", true, telnet.Keyboard.SystemRequestAction),
                new XtActionRec("Tab", false, telnet.Keyboard.TabForwardAction),
                new XtActionRec("ToggleInsert", false, telnet.Keyboard.ToggleInsertAction),
                new XtActionRec("ToggleReverse", false, telnet.Keyboard.ToggleReverseAction),
                new XtActionRec("Up", false, telnet.Keyboard.MoveCursorUp)
            };

            actionCount = actions.Length;
        }

        /*
		 * Return a name for an action.
		 */

        private string action_name(ActionDelegate action)
        {
            int i;

            for (i = 0; i < actionCount; i++)
            {
                if (actions[i].proc == action)
                    return actions[i].name;
            }
            return "(unknown)";
        }


        /*
		 * Wrapper for calling an action internally.
		 */

        public bool action_internal(ActionDelegate action, params object[] args)
        {
            return action(args);
        }

        public void action_output(string data)
        {
            action_output(data, false);
        }

        private string encodeXML(string data)
        {
            //data = data.Replace("\"", "&quot;");
            //data = data.Replace(">", "&gt;");
            data = data.Replace("<", "&lt;");
            data = data.Replace("&", "&amp;");
            return data;
        }

        public void action_output(string data, bool encode)
        {
            if (datacapture == null)
                datacapture = new ArrayList();
            if (datastringcapture == null)
                datastringcapture = new ArrayList();

            datacapture.Add(Encoding.ASCII.GetBytes(data));
            //
            if (encode)
            {
                data = encodeXML(data);
            }
            //
            datastringcapture.Add(data);
        }

        public void action_output(byte[] data, int length)
        {
            action_output(data, length, false);
        }

        public void action_output(byte[] data, int length, bool encode)
        {
            if (datacapture == null)
                datacapture = new ArrayList();
            if (datastringcapture == null)
                datastringcapture = new ArrayList();

            //
            var temp = new byte[length];
            int i;
            for (i = 0; i < length; i++)
            {
                temp[i] = data[i];
            }
            datacapture.Add(temp);
            var strdata = Encoding.ASCII.GetString(temp);
            if (encode)
            {
                strdata = encodeXML(strdata);
            }

            datastringcapture.Add(strdata);
        }

        public string GetStringData(int index)
        {
            if (datastringcapture == null)
                return null;
            if (index >= 0 && index < datastringcapture.Count)
                return (string) datastringcapture[index];
            return null;
        }

        public byte[] GetByteData(int index)
        {
            if (datacapture == null)
                return null;
            if (index >= 0 && index < datacapture.Count)
                return (byte[]) datacapture[index];
            return null;
        }

        public bool KeyboardCommandCausesSubmit(string name)
        {
            var rec = actionLookup[name.ToLower()] as XtActionRec;
            if (rec != null)
            {
                return rec.CausesSubmit;
            }

            for (var i = 0; i < actions.Length; i++)
            {
                if (actions[i].name.ToLower() == name.ToLower())
                {
                    actionLookup[name.ToLower()] = actions[i];
                    return actions[i].CausesSubmit;
                }
            }

            throw new ApplicationException("Sorry, action '" + name + "' is not known");
        }

        public bool Execute(bool submit, string name, params object[] args)
        {
            telnet.Events.Clear();
            // Check that we're connected
            if (!telnet.IsConnected)
            {
                throw new TNHostException("TN3270 Host is not connected", telnet.DisconnectReason, null);
            }

            datacapture = null;
            datastringcapture = null;
            var rec = actionLookup[name.ToLower()] as XtActionRec;
            if (rec != null)
            {
                return rec.proc(args);
            }
            int i;
            for (i = 0; i < actions.Length; i++)
            {
                if (actions[i].name.ToLower() == name.ToLower())
                {
                    actionLookup[name.ToLower()] = actions[i];
                    return actions[i].proc(args);
                }
            }
            throw new ApplicationException("Sorry, action '" + name + "' is not known");
        }

        #region Nested classes

        internal class XtActionRec
        {
            public bool CausesSubmit;
            public string name;
            public ActionDelegate proc;

            public XtActionRec(string name, bool CausesSubmit, ActionDelegate fn)
            {
                this.CausesSubmit = CausesSubmit;
                proc = fn;
                this.name = name.ToLower();
            }
        }

        #endregion Nested classes
    }
}