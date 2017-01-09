using System;
using System.Collections.Generic;
using System.Text;
using StEn.Open3270.Engine;
using StEn.Open3270.Exceptions;
using StEn.Open3270.Interfaces;
using StEn.Open3270.TN3270E.X3270;

namespace StEn.Open3270.TN3270E
{
    public class TN3270API : IDisposable
    {
        #region Private Methods

        private void tn_CursorLocationChanged(object sender, EventArgs e)
        {
            OnCursorLocationChanged(e);
        }

        #endregion

        #region Events and Delegates

        public event RunScriptDelegate RunScriptRequested;
        public event OnDisconnectDelegate Disconnected;
        public event EventHandler CursorLocationChanged;

        #endregion Events

        #region Fields

        private Telnet tn;

        private bool debug;
        private bool isDisposed;

        private string sourceIP = string.Empty;

        #endregion Fields

        #region Properties

        /// <summary>
        ///     Gets or sets whether or not we are using SSL.
        /// </summary>
        public bool UseSSL { get; set; } = false;

        /// <summary>
        ///     Returns whether or not the session is connected.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                if (tn != null && tn.IsSocketConnected)
                    return true;
                return false;
            }
        }

        /// <summary>
        ///     Sets the value of debug.
        /// </summary>
        public bool Debug
        {
            set { debug = value; }
        }

        /// <summary>
        ///     Returns the state of the keyboard.
        /// </summary>
        public int KeyboardLock
        {
            get { return tn.Keyboard.keyboardLock; }
        }

        /// <summary>
        ///     Returns the cursor's current X position.
        /// </summary>
        public int CursorX
        {
            get
            {
                lock (tn)
                {
                    return tn.Controller.CursorX;
                }
            }
        }

        /// <summary>
        ///     Returns the cursor's current Y positon.
        /// </summary>
        public int CursorY
        {
            get
            {
                lock (tn)
                {
                    return tn.Controller.CursorY;
                }
            }
        }

        /// <summary>
        ///     Returns the text of the last exception thrown.
        /// </summary>
        public string LastException
        {
            get { return tn.Events.GetErrorAsText(); }
        }

        internal TN3270API()
        {
            tn = null;
        }

        internal string DisconnectReason
        {
            get
            {
                if (tn != null) return tn.DisconnectReason;
                return null;
            }
        }

        internal bool ShowParseError
        {
            set
            {
                if (tn != null) tn.ShowParseError = value;
            }
        }

        #endregion Properties

        #region Ctors, dtors, and clean-up

        ~TN3270API()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed)
            {
                isDisposed = true;
                if (disposing)
                {
                    Disconnect();
                    Disconnected = null;
                    RunScriptRequested = null;
                    if (tn != null)
                    {
                        tn.telnetDataEventOccurred -= tn_DataEventReceived;
                        tn.Dispose();
                    }
                }
            }
        }

        #endregion Ctors, dtors, and clean-up

        #region Public Methods

        /// <summary>
        ///     Connects to host using a local IP
        ///     If a source IP is given then use it for the local IP
        /// </summary>
        /// <param name="audit">IAudit interface to post debug/tracing to</param>
        /// <param name="localIP">ip to use for local end point</param>
        /// <param name="host">host ip/name</param>
        /// <param name="port">port to use</param>
        /// <param name="config">configuration parameters</param>
        /// <returns></returns>
        public bool Connect(IAudit audit, string localIP, string host, int port, ConnectionConfig config)
        {
            sourceIP = localIP;
            return Connect(audit, host, port, string.Empty, config);
        }

        /// <summary>
        ///     Connects a Telnet object to the host using the parameters provided
        /// </summary>
        /// <param name="audit">IAudit interface to post debug/tracing to</param>
        /// <param name="host">host ip/name</param>
        /// <param name="port">port to use</param>
        /// <param name="lu">lu to use or empty string for host negotiated</param>
        /// <param name="config">configuration parameters</param>
        /// <returns></returns>
        public bool Connect(IAudit audit, string host, int port, string lu, ConnectionConfig config)
        {
            if (tn != null)
            {
                tn.CursorLocationChanged -= tn_CursorLocationChanged;
            }

            tn = new Telnet(this, audit, config);

            tn.Trace.optionTraceAnsi = debug;
            tn.Trace.optionTraceDS = debug;
            tn.Trace.optionTraceDSN = debug;
            tn.Trace.optionTraceEvent = debug;
            tn.Trace.optionTraceNetworkData = debug;

            tn.telnetDataEventOccurred += tn_DataEventReceived;
            tn.CursorLocationChanged += tn_CursorLocationChanged;

            if (lu == null || lu.Length == 0)
            {
                tn.Lus = null;
            }
            else
            {
                tn.Lus = new List<string>();
                tn.Lus.Add(lu);
            }

            if (!string.IsNullOrEmpty(sourceIP))
            {
                tn.Connect(this, host, port, sourceIP);
            }
            else
            {
                tn.Connect(this, host, port);
            }

            if (!tn.WaitForConnect())
            {
                tn.Disconnect();
                var text = tn.DisconnectReason;
                tn = null;
                throw new TNHostException("connect to " + host + " on port " + port + " failed", text, null);
            }
            tn.Trace.WriteLine("--connected");

            return true;
        }

        /// <summary>
        ///     Disconnects the connected telnet object from the host
        /// </summary>
        public void Disconnect()
        {
            if (tn != null)
            {
                tn.Disconnect();
                tn.CursorLocationChanged -= tn_CursorLocationChanged;
                tn = null;
            }
        }

        /// <summary>
        ///     Waits for the connection to complete.
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public bool WaitForConnect(int timeout)
        {
            var success = tn.WaitFor(SmsState.ConnectWait, timeout);
            if (success)
            {
                if (!tn.IsConnected)
                {
                    success = false;
                }
            }
            return success;
        }

        /// <summary>
        ///     Gets the entire contents of the screen.
        /// </summary>
        /// <param name="crlf"></param>
        /// <returns></returns>
        public string GetAllStringData(bool crlf = false)
        {
            lock (tn)
            {
                var builder = new StringBuilder();
                var index = 0;
                string temp;
                while ((temp = tn.Action.GetStringData(index)) != null)
                {
                    builder.Append(temp);
                    if (crlf)
                    {
                        builder.Append("\n");
                    }
                    index++;
                }
                return builder.ToString();
            }
        }

        /// <summary>
        ///     Sends an operator key to the mainframe.
        /// </summary>
        /// <param name="op"></param>
        /// <returns></returns>
        public bool SendKeyOp(KeyboardOp op)
        {
            var success = false;
            lock (tn)
            {
                // These can go to a locked screen		
                if (op == KeyboardOp.Reset)
                {
                    success = true;
                }
                else
                {
                    if ((tn.Keyboard.keyboardLock & KeyboardConstants.OiaMinus) != 0 ||
                        tn.Keyboard.keyboardLock != 0)
                    {
                        success = false;
                    }
                    else
                    {
                        // These need unlocked screen
                        switch (op)
                        {
                            case KeyboardOp.AID:
                            {
                                var v = (byte) typeof(AID).GetField(op.ToString()).GetValue(null);
                                tn.Keyboard.HandleAttentionIdentifierKey(v);
                                success = true;
                                break;
                            }
                            case KeyboardOp.Home:
                            {
                                if (tn.IsAnsi)
                                {
                                    Console.WriteLine("IN_ANSI Home key not supported");
                                    //ansi_send_home();
                                    return false;
                                }

                                if (!tn.Controller.Formatted)
                                {
                                    tn.Controller.SetCursorAddress(0);
                                    return true;
                                }
                                tn.Controller.SetCursorAddress(
                                    tn.Controller.GetNextUnprotectedField(tn.Controller.RowCount*
                                                                          tn.Controller.ColumnCount - 1));
                                success = true;
                                break;
                            }
                            case KeyboardOp.ATTN:
                            {
                                tn.Interrupt();
                                success = true;
                                break;
                            }
                            default:
                            {
                                throw new ApplicationException("Sorry, key '" + op + "'not known");
                            }
                        }
                    }
                }
            }
            return success;
        }

        /// <summary>
        ///     Gets the text at the specified cursor position.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public string GetText(int x, int y, int length)
        {
            MoveCursor(CursorOp.Exact, x, y);
            lock (tn)
            {
                tn.Controller.MoveCursor(CursorOp.Exact, x, y);
                return tn.Action.GetStringData(length);
            }
        }

        /// <summary>
        ///     Sets the text to the specified value at the specified position.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="paste"></param>
        public void SetText(string text, int x, int y, bool paste = true)
        {
            MoveCursor(CursorOp.Exact, x, y);
            lock (tn)
            {
                SetText(text, paste);
            }
        }

        /// <summary>
        ///     Sets the text value at the current cursor position.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="paste"></param>
        /// <returns></returns>
        public bool SetText(string text, bool paste = true)
        {
            lock (tn)
            {
                var success = true;
                int i;
                if (text != null)
                {
                    for (i = 0; i < text.Length; i++)
                    {
                        success = tn.Keyboard.HandleOrdinaryCharacter(text[i], false, paste);
                        if (!success)
                        {
                            break;
                        }
                    }
                }
                return success;
            }
        }

        /// <summary>
        ///     Gets the field attributes of the field at the specified coordinates.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public FieldAttributes GetFieldAttribute(int x, int y)
        {
            byte b = 0;
            lock (tn)
            {
                b = (byte) tn.Controller.GetFieldAttribute(tn.Controller.CursorAddress);
            }

            var fa = new FieldAttributes();
            fa.IsHigh = FieldAttribute.IsHigh(b);
            fa.IsIntense = FieldAttribute.IsIntense(b);
            fa.IsModified = FieldAttribute.IsModified(b);
            fa.IsNormal = FieldAttribute.IsNormal(b);
            fa.IsNumeric = FieldAttribute.IsNumeric(b);
            fa.IsProtected = FieldAttribute.IsProtected(b);
            fa.IsSelectable = FieldAttribute.IsSelectable(b);
            fa.IsSkip = FieldAttribute.IsSkip(b);
            fa.IsZero = FieldAttribute.IsZero(b);
            return fa;
        }

        /// <summary>
        ///     Moves the cursor to the specified position.
        /// </summary>
        /// <param name="op"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public bool MoveCursor(CursorOp op, int x, int y)
        {
            lock (tn)
            {
                return tn.Controller.MoveCursor(op, x, y);
            }
        }

        /// <summary>
        ///     Returns the text of the last error thrown.
        /// </summary>
        /// <returns></returns>
        public bool ExecuteAction(bool submit, string name, params object[] args)
        {
            lock (tn)
            {
                return tn.Action.Execute(submit, name, args);
            }
        }

        public bool KeyboardCommandCausesSubmit(string name)
        {
            return tn.Action.KeyboardCommandCausesSubmit(name);
        }

        public bool Wait(int timeout)
        {
            return tn.WaitFor(SmsState.KBWait, timeout);
        }

        public void RunScript(string where)
        {
            if (RunScriptRequested != null)
            {
                RunScriptRequested(where);
            }
        }

        #endregion Public Methods

        #region Depricated Methods

        [Obsolete(
            "This method has been deprecated.  Please use SendKeyOp(KeyboardOp op) instead. This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public bool SendKeyOp(KeyboardOp op, string key)
        {
            return SendKeyOp(op);
        }

        [Obsolete(
            "This method has been deprecated.  Please use SetText instead. This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public bool SendText(string text, bool paste)
        {
            return SetText(text, paste);
        }

        [Obsolete(
            "This method has been deprecated.  Please use GetText instead. This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public string GetStringData(int index)
        {
            lock (tn)
            {
                return tn.Action.GetStringData(index);
            }
        }

        [Obsolete(
            "This method has been deprecated.  Please use LastException instead. This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public string GetLastError()
        {
            return LastException;
        }

        #endregion

        #region Eventhandlers and such

        private void tn_DataEventReceived(object parentData, TNEvent eventType, string text)
        {
            //Console.WriteLine("event = "+eventType+" text='"+text+"'");
            if (eventType == TNEvent.Disconnect)
            {
                if (Disconnected != null)
                {
                    Disconnected(null, "Client disconnected session");
                }
            }
            if (eventType == TNEvent.DisconnectUnexpected)
            {
                if (Disconnected != null)
                {
                    Disconnected(null, "Host disconnected session");
                }
            }
        }


        protected virtual void OnCursorLocationChanged(EventArgs args)
        {
            if (CursorLocationChanged != null)
            {
                CursorLocationChanged(this, args);
            }
        }

        #endregion Eventhandlers and such
    }
}