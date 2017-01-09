using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using StEn.Open3270.CommFramework;
using StEn.Open3270.Engine;
using StEn.Open3270.Exceptions;
using StEn.Open3270.Interfaces;

namespace StEn.Open3270.TN3270E.X3270
{
    internal class Telnet : IDisposable
    {
        #region Eventhandlers and similar

        private void ReactToConnectionChange(bool success)
        {
            if (IsConnected || Appres.disconnect_clear)
            {
                Controller.Erase(true);
            }
        }

        #endregion Eventhandlers and similar

        public void Output(NetBuffer obptr)
        {
            var outputBuffer = new NetBuffer();

            //Set the TN3720E header.
            if (IsTn3270E || IsSscp)
            {
                var header = new TnHeader();

                //Check for sending a TN3270E response.
                if (responseRequired == TnHeader.HeaderReponseFlags.AlwaysResponse)
                {
                    SendAck();
                    responseRequired = TnHeader.HeaderReponseFlags.NoResponse;
                }

                //Set the outbound TN3270E header.
                header.DataType = IsTn3270E ? DataType3270.Data3270 : DataType3270.SscpLuData;
                header.RequestFlag = 0;

                // CFCJR:
                // Request a response if negotiated to do so

                //JNU: THIS is the code that broke everything and caused the Sense 00004002 failure
                //if ((e_funcs & E_OPT(TN3270E_FUNC_RESPONSES)) != 0)
                //	h.response_flag = TN3270E_HEADER.TN3270E_RSF_ALWAYS_RESPONSE;
                //else
                header.ResponseFlag = 0;

                header.SequenceNumber[0] = (byte) ((eTransmitSequence >> 8) & 0xff);
                header.SequenceNumber[1] = (byte) (eTransmitSequence & 0xff);

                Trace.trace_dsn("SENT TN3270E(%s %s %u)\n",
                    IsTn3270E ? "3270-DATA" : "SSCP-LU-DATA",
                    header.ResponseFlag == TnHeader.HeaderReponseFlags.ErrorResponse
                        ? "ERROR-RESPONSE"
                        : (header.ResponseFlag == TnHeader.HeaderReponseFlags.AlwaysResponse
                            ? "ALWAYS-RESPONSE"
                            : "NO-RESPONSE"),
                    eTransmitSequence);

                if (Config.IgnoreSequenceCount == false &&
                    (currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_RESPONSES)) != 0)
                {
                    eTransmitSequence = (eTransmitSequence + 1) & 0x7fff;
                }

                header.AddToNetBuffer(outputBuffer);
            }

            int i;
            var data = obptr.Data;
            /* Copy and expand IACs. */
            for (i = 0; i < data.Length; i++)
            {
                outputBuffer.Add(data[i]);
                if (data[i] == TelnetConstants.IAC)
                    outputBuffer.Add(TelnetConstants.IAC);
            }
            /* Append the IAC EOR and transmit. */
            outputBuffer.Add(TelnetConstants.IAC);
            outputBuffer.Add(TelnetConstants.EOR);
            SendRawOutput(outputBuffer);

            Trace.trace_dsn("SENT EOR\n");
            bytesSent++;
        }


        public void Break()
        {
            byte[] buf = {TelnetConstants.IAC, TelnetConstants.BREAK};

            //Should we first send TELNET synch?
            SendRawOutput(buf, buf.Length);
            Trace.trace_dsn("SENT BREAK\n");
        }


        public void Interrupt()
        {
            byte[] buf = {TelnetConstants.IAC, TelnetConstants.IP};

            //Should we first send TELNET synch?
            SendRawOutput(buf, buf.Length);
            Trace.trace_dsn("SENT IP\n");
        }


        /// <summary>
        ///     Send user data out in ANSI mode, without cooked-mode processing.
        /// </summary>
        /// <param name="buf"></param>
        /// <param name="len"></param>
        private void SendCookedOut(byte[] buf, int len)
        {
            if (Appres.Toggled(Appres.DSTrace))
            {
                int i;

                Trace.trace_dsn(">");
                for (i = 0; i < len; i++)
                {
                    Trace.trace_dsn(" %s", Util.ControlSee(buf[i]));
                }
                Trace.trace_dsn("\n");
            }
            SendRawOutput(buf, len);
        }


        /// <summary>
        ///     Send a Telnet window size sub-option negotation.
        /// </summary>
        private void SendNaws()
        {
            var buffer = new NetBuffer();

            buffer.Add(TelnetConstants.IAC);
            buffer.Add(TelnetConstants.SB);
            buffer.Add(TelnetConstants.TELOPT_NAWS);
            buffer.Add16(Controller.MaxColumns);
            buffer.Add16(Controller.MaxRows);
            buffer.Add(TelnetConstants.IAC);
            buffer.Add(TelnetConstants.SE);
            SendRawOutput(buffer);
            Trace.trace_dsn("SENT %s NAWS %d %d %s\n", GetCommand(TelnetConstants.SB), Controller.MaxColumns,
                Controller.MaxRows, GetCommand(TelnetConstants.SE));
        }


        /// <summary>
        ///     Negotiation of TN3270E options.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns>Returns 0 if okay, -1 if we have to give up altogether.</returns>
        private int Tn3270e_Negotiate(NetBuffer buffer)
        {
            int bufferLength;
            int capabilitiesRequested;
            NetBuffer hostWants = null;

            //Find out how long the subnegotiation buffer is.
            for (bufferLength = 0;; bufferLength++)
            {
                if (buffer.Data[bufferLength] == TelnetConstants.SE)
                    break;
            }

            Trace.trace_dsn("TN3270E ");

            switch (buffer.Data[1])
            {
                case TnHeader.Ops.Send:
                {
                    if (buffer.Data[2] == TnHeader.Ops.DeviceType)
                    {
                        //Host wants us to send our device type.
                        Trace.trace_dsn("SEND DEVICE-TYPE SE\n");
                        Tn3270e_SendRequest();
                    }
                    else
                    {
                        Trace.trace_dsn("SEND ??%u SE\n", buffer.Data[2]);
                    }
                    break;
                }
                case TnHeader.Ops.DeviceType:
                {
                    //Device type negotiation
                    Trace.trace_dsn("DEVICE-TYPE ");

                    switch (buffer.Data[2])
                    {
                        case TnHeader.Ops.Is:
                        {
                            //Device type success.
                            int tnLength, snLength;

                            //Isolate the terminal type and session.
                            tnLength = 0;
                            while (buffer.Data[3 + tnLength] != TelnetConstants.SE &&
                                   buffer.Data[3 + tnLength] != TnHeader.Ops.Connect)
                            {
                                tnLength++;
                            }

                            snLength = 0;
                            if (buffer.Data[3 + tnLength] == TnHeader.Ops.Connect)
                            {
                                while (buffer.Data[3 + tnLength + 1 + snLength] != TelnetConstants.SE)
                                {
                                    snLength++;
                                }
                            }

                            Trace.trace_dsn("IS " + buffer.AsString(3, tnLength) + " CONNECT " +
                                            buffer.AsString(3 + tnLength + 1, snLength) + " SE\n");


                            //Remember the LU
                            if (tnLength != 0)
                            {
                                if (tnLength > TelnetConstants.LU_MAX)
                                {
                                    tnLength = TelnetConstants.LU_MAX;
                                }

                                reportedType = buffer.AsString(3, tnLength);
                                connectedType = reportedType;
                            }
                            if (snLength != 0)
                            {
                                if (snLength > TelnetConstants.LU_MAX)
                                {
                                    snLength = TelnetConstants.LU_MAX;
                                }

                                reportedLu = buffer.AsString(3 + tnLength + 1, snLength);
                                connectedLu = reportedLu;
                            }

                            // Tell them what we can do.
                            Tn3270e_Subneg_Send(TnHeader.Ops.Request, currentOptionMask);
                            break;
                        }
                        case TnHeader.Ops.Reject:
                        {
                            //Device type failure.
                            Trace.trace_dsn("REJECT REASON %s SE\n", TelnetConstants.GetReason(buffer.Data[4]));

                            if (buffer.Data[4] == TnHeader.NegotiationReasonCodes.InvDeviceType ||
                                buffer.Data[4] == TnHeader.NegotiationReasonCodes.UnsupportedReq)
                            {
                                Backoff_TN3270e("Host rejected device type or request type");
                                break;
                            }

                            currentLUIndex++;

                            if (currentLUIndex < Lus.Count)
                            {
                                //Try the next LU.
                                Tn3270e_SendRequest();
                            }
                            else if (Lus != null)
                            {
                                //No more LUs to try.  Give up.
                                Backoff_TN3270e("Host rejected resource(s)");
                            }
                            else
                            {
                                Backoff_TN3270e("Device type rejected");
                            }

                            break;
                        }
                        default:
                        {
                            Trace.trace_dsn("??%u SE\n", buffer.Data[2]);
                            break;
                        }
                    }
                    break;
                }
                case TnHeader.Ops.Functions:
                {
                    //Functions negotiation.
                    Trace.trace_dsn("FUNCTIONS ");

                    switch (buffer.Data[2])
                    {
                        case TnHeader.Ops.Request:
                        {
                            //Host is telling us what functions it wants
                            hostWants = buffer.CopyFrom(3, bufferLength - 3);
                            Trace.trace_dsn("REQUEST %s SE\n", GetFunctionCodesAsText(hostWants));

                            capabilitiesRequested = tn3270e_fdecode(hostWants);
                            if ((capabilitiesRequested == currentOptionMask) ||
                                (currentOptionMask & ~capabilitiesRequested) != 0)
                            {
                                //They want what we want, or less.  Done.
                                currentOptionMask = capabilitiesRequested;
                                Tn3270e_Subneg_Send(TnHeader.Ops.Is, currentOptionMask);
                                tn3270e_negotiated = true;
                                Trace.trace_dsn("TN3270E option negotiation complete.\n");
                                CheckIn3270();
                            }
                            else
                            {
                                // They want us to do something we can't.
                                //Request the common subset.
                                currentOptionMask &= capabilitiesRequested;
                                Tn3270e_Subneg_Send(TnHeader.Ops.Request, currentOptionMask);
                            }
                            break;
                        }
                        case TnHeader.Ops.Is:
                        {
                            //They accept our last request, or a subset thereof.
                            hostWants = buffer.CopyFrom(3, bufferLength - 3);
                            Trace.trace_dsn("IS %s SE\n", GetFunctionCodesAsText(hostWants));
                            capabilitiesRequested = tn3270e_fdecode(hostWants);
                            if (capabilitiesRequested != currentOptionMask)
                            {
                                if ((currentOptionMask & ~capabilitiesRequested) != 0)
                                {
                                    //They've removed something.  This is technically illegal, but we can live with it.
                                    currentOptionMask = capabilitiesRequested;
                                }
                                else
                                {
                                    //They've added something.  Abandon TN3270E.  They're brain dead.
                                    Backoff_TN3270e("Host illegally added function(s)");
                                    break;
                                }
                            }

                            tn3270e_negotiated = true;
                            Trace.trace_dsn("TN3270E option negotiation complete.\n");
                            CheckIn3270();
                            break;
                        }
                        default:
                        {
                            Trace.trace_dsn("??%u SE\n", buffer.Data[2]);
                            break;
                        }
                    }
                    break;
                }
                default:
                    Trace.trace_dsn("??%u SE\n", buffer.Data[1]);
                    break;
            }

            //Good enough for now.
            return 0;
        }


        /// <summary>
        ///     Send a TN3270E terminal type request.
        /// </summary>
        public void Tn3270e_SendRequest()
        {
            var buffer = new NetBuffer();
            string try_lu = null;

            if (Lus != null)
            {
                try_lu = Lus[currentLUIndex];
            }

            buffer.Add(TelnetConstants.IAC);
            buffer.Add(TelnetConstants.SB);
            buffer.Add(TelnetConstants.TELOPT_TN3270E);
            buffer.Add(TnHeader.Ops.DeviceType);
            buffer.Add(TnHeader.Ops.Request);
            var temp = TermType;

            // Replace 3279 with 3278 as per the RFC
            temp = temp.Replace("3279", "3278");
            buffer.Add(temp);
            if (try_lu != null)
            {
                buffer.Add(TnHeader.Ops.Connect);
                buffer.Add(try_lu);
            }
            buffer.Add(TelnetConstants.IAC);
            buffer.Add(TelnetConstants.SE);
            SendRawOutput(buffer);

            Trace.trace_dsn("SENT %s %s DEVICE-TYPE REQUEST %s %s%s%s\n",
                GetCommand(TelnetConstants.SB),
                GetOption(TelnetConstants.TELOPT_TN3270E),
                TermType,
                try_lu != null ? " CONNECT " : "",
                try_lu != null ? try_lu : "",
                GetCommand(TelnetConstants.SE));
        }


        private string GetFunctionName(int i)
        {
            var functionName = string.Empty;

            if (i >= 0 && i < TelnetConstants.FunctionNames.Length)
            {
                functionName = TelnetConstants.FunctionNames[i];
            }
            else
            {
                functionName = "?[function_name=" + i + "]?";
            }

            return functionName;
        }


        /// <summary>
        ///     Expand a string of TN3270E function codes into text.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public string GetFunctionCodesAsText(NetBuffer buffer)
        {
            int i;
            var temp = "";
            var bufferData = buffer.Data;

            if (bufferData.Length == 0)
            {
                return "(null)";
            }
            for (i = 0; i < bufferData.Length; i++)
            {
                if (temp != null)
                {
                    temp += " ";
                }
                temp += GetFunctionName(bufferData[i]);
            }
            return temp;
        }


        /// <summary>
        ///     Expand the current TN3270E function codes into text.
        /// </summary>
        /// <returns></returns>
        private string GetCurrentOptionsAsText()
        {
            int i;
            var temp = "";

            if (currentOptionMask == 0 || !IsE)
                return null;
            for (i = 0; i < 32; i++)
            {
                if ((currentOptionMask & Shift(i)) != 0)
                {
                    if (temp != null)
                    {
                        temp += " ";
                    }
                    temp += GetFunctionName(i);
                }
            }
            return temp;
        }


        /// <summary>
        ///     Transmit a TN3270E FUNCTIONS REQUEST or FUNCTIONS IS message.
        /// </summary>
        /// <param name="op"></param>
        /// <param name="funcs"></param>
        private void Tn3270e_Subneg_Send(byte op, int funcs)
        {
            var protoBuffer = new byte[7 + 32];
            int length;
            int i;

            //Construct the buffers.
            protoBuffer[0] = TelnetConstants.FunctionsReq[0];
            protoBuffer[1] = TelnetConstants.FunctionsReq[1];
            protoBuffer[2] = TelnetConstants.FunctionsReq[2];
            protoBuffer[3] = TelnetConstants.FunctionsReq[3];
            protoBuffer[4] = op;
            length = 5;

            for (i = 0; i < 32; i++)
            {
                if ((funcs & Shift(i)) != 0)
                {
                    protoBuffer[length++] = (byte) i;
                }
            }

            //Complete and send out the protocol message.
            protoBuffer[length++] = TelnetConstants.IAC;
            protoBuffer[length++] = TelnetConstants.SE;
            SendRawOutput(protoBuffer, length);

            //Complete and send out the trace text.
            Trace.trace_dsn("SENT %s %s FUNCTIONS %s %s %s\n",
                GetCommand(TelnetConstants.SB), GetOption(TelnetConstants.TELOPT_TN3270E),
                op == TnHeader.Ops.Request ? "REQUEST" : "IS",
                GetFunctionCodesAsText(new NetBuffer(protoBuffer, 5, length - 7)),
                GetCommand(TelnetConstants.SE));
        }


        //Translate a string of TN3270E functions into a bit-map.
        private int tn3270e_fdecode(NetBuffer netbuf)
        {
            var r = 0;
            int i;
            var buf = netbuf.Data;

            //Note that this code silently ignores options >= 32.
            for (i = 0; i < buf.Length; i++)
            {
                if (buf[i] < 32)
                {
                    r |= Shift(buf[i]);
                }
            }
            return r;
        }


        /// <summary>
        ///     Back off of TN3270E.
        /// </summary>
        /// <param name="why"></param>
        private void Backoff_TN3270e(string why)
        {
            Trace.trace_dsn("Aborting TN3270E: %s\n", why);

            //Tell the host 'no'
            wontDoOption[2] = TelnetConstants.TELOPT_TN3270E;
            SendRawOutput(wontDoOption, wontDoOption.Length);
            Trace.trace_dsn("SENT %s %s\n", GetCommand(TelnetConstants.WONT), GetOption(TelnetConstants.TELOPT_TN3270E));

            //Restore the LU list; we may need to run it again in TN3270 mode.
            currentLUIndex = 0;

            //Reset our internal state.
            clientOptions[TelnetConstants.TELOPT_TN3270E] = 0;
            CheckIn3270();
        }


        protected virtual void OnPrimaryConnectionChanged(bool success)
        {
            ReactToConnectionChange(success);

            if (PrimaryConnectionChanged != null)
            {
                PrimaryConnectionChanged(this, new PrimaryConnectionChangedArgs(success));
            }
        }


        protected virtual void OnConnectionPending()
        {
            if (ConnectionPending != null)
            {
                ConnectionPending(this, EventArgs.Empty);
            }
        }


        protected virtual void OnConnected3270(bool is3270)
        {
            ReactToConnectionChange(is3270);

            if (Connected3270 != null)
            {
                Connected3270(this, new Connected3270EventArgs(is3270));
            }
        }


        protected virtual void OnConnectedLineMode()
        {
            if (ConnectedLineMode != null)
            {
                ConnectedLineMode(this, EventArgs.Empty);
            }
        }


        //public void Status_Changed(StCallback id, bool v)
        //{
        //	int i;
        //	//var b = StCallback.Mode3270;

        //	for (i = 0; i < this.statusChangeList.Count; i++)
        //	{
        //		StatusChangeItem item = (StatusChangeItem)statusChangeList[i];
        //		if (item.id == id)
        //		{
        //			item.proc(v);
        //		}
        //	}
        //}


        private void Host_Connected()
        {
            connectionState = ConnectionState.ConnectedInitial;
            OnPrimaryConnectionChanged(true);
        }


        private void Net_Connected()
        {
            Trace.trace_dsn("NETCONNECTED Connected to %s, port %u.\n", address, port);

            //Set up telnet options 
            int i;
            for (i = 0; i < clientOptions.Length; i++)
            {
                clientOptions[i] = 0;
            }
            for (i = 0; i < hostOptions.Length; i++)
            {
                hostOptions[i] = 0;
            }
            if (Config.IgnoreSequenceCount)
            {
                currentOptionMask = Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE) |
                                    Shift(TelnetConstants.TN3270E_FUNC_SYSREQ);
            }
            else
            {
                currentOptionMask = Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE) |
                                    Shift(TelnetConstants.TN3270E_FUNC_RESPONSES) |
                                    Shift(TelnetConstants.TN3270E_FUNC_SYSREQ);
            }
            eTransmitSequence = 0;
            responseRequired = TnHeader.HeaderReponseFlags.NoResponse;
            telnetState = TelnetState.Data;

            //Clear statistics and flags
            bytesReceived = 0;
            bytesSent = 0;
            syncing = false;
            tn3270e_negotiated = false;
            tn3270eSubmode = TN3270ESubmode.None;
            tn3270eBound = false;

            CheckLineMode(true);
        }


        private void Host_Disconnect(bool failed)
        {
            if (IsConnected || IsPending)
            {
                Disconnect();

                //Remember a disconnect from ANSI mode, to keep screen tracing in sync.
                Trace.Stop(IsAnsi);
                connectionState = ConnectionState.NotConnected;

                //Propagate the news to everyone else.
                OnPrimaryConnectionChanged(false);
            }
        }


        public void RestartReceive()
        {
            // Define a new Callback to read the data 
            AsyncCallback recieveData = OnRecievedData;
            // Begin reading data asyncronously
            socketStream.BeginRead(byteBuffer, 0, byteBuffer.Length, recieveData, socketStream);
            Trace.trace_dsn("\nRestartReceive : SocketStream.BeginRead called to read asyncronously\n");
        }


        public bool WaitForConnect()
        {
            while (!IsAnsi && !Is3270)
            {
                Thread.Sleep(100);
                if (!IsResolving)
                {
                    DisconnectReason = "Timeout waiting for connection";
                    return false;
                }
            }
            return true;
        }


        public void test_enter()
        {
            Console.WriteLine("state = " + connectionState);

            if ((Keyboard.keyboardLock & KeyboardConstants.OiaMinus) != 0)
            {
                Console.WriteLine("--KL_OIA_MINUS");
            }
            else if (Keyboard.keyboardLock != 0)
            {
                Console.WriteLine("queue key - " + Keyboard.keyboardLock);
                throw new ApplicationException(
                    "Sorry, queue key is not implemented, please contact mikewarriner@gmail.com for assistance");
            }
            else
            {
                Console.WriteLine("do key");
                Keyboard.HandleAttentionIdentifierKey(AID.Enter);
            }
        }


        public bool WaitFor(SmsState what, int timeout)
        {
            lock (this)
            {
                WaitState = what;
                WaitEvent1.Reset();
                // Are we already there?
                Controller.Continue();
            }
            if (WaitEvent1.WaitOne(timeout, false))
            {
                return true;
            }
            lock (this)
            {
                WaitState = SmsState.Idle;
            }
            return false;
        }


        private bool cryptocallback(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        public event EventHandler CursorLocationChanged;

        protected virtual void OnCursorLocationChanged(EventArgs args)
        {
            if (CursorLocationChanged != null)
            {
                CursorLocationChanged(this, args);
            }
        }

        private void controller_CursorLocationChanged(object sender, EventArgs e)
        {
            OnCursorLocationChanged(e);
        }

        #region Fields

        public event TelnetDataDelegate telnetDataEventOccurred;
        public event EventHandler<Connected3270EventArgs> Connected3270;
        public event EventHandler ConnectedLineMode;
        public event EventHandler ConnectionPending;
        public event EventHandler<PrimaryConnectionChangedArgs> PrimaryConnectionChanged;

        private TN3270State tnState = TN3270State.InNeither;
        private ConnectionState connectionState = ConnectionState.NotConnected;
        private TN3270ESubmode tn3270eSubmode = TN3270ESubmode.None;
        private TelnetState telnetState = TelnetState.Data;

        #region Services

        #endregion Services

        private readonly bool nonTn3270eHost = false;
        private readonly bool isValid;
        private bool tn3270eBound;
        private bool linemode;
        private bool syncing;
        private bool tn3270e_negotiated;
        private bool logFileProcessorThread_Quit;
        private bool closeRequested;
        private bool isDisposed;

        // ANSI stuff
        private byte vintr;
        private byte vquit;
        private readonly byte verase;
        private readonly byte vkill;
        private byte veof;
        private readonly byte vwerase;
        private byte vrprnt;
        private byte vlnext;


        private int port;
        private int bytesReceived;
        private int bytesSent;
        private int currentLUIndex;
        private int eTransmitSequence;
        private int ansiData;
        private int currentOptionMask;
        private int responseRequired = TnHeader.HeaderReponseFlags.NoResponse;
        private int inputBufferIndex;
        private int startedReceivingCount;

        private readonly int[] clientOptions = new int[256];
        private readonly int[] hostOptions;


        private string connectedType;
        private string reportedType;
        private string connectedLu;
        private string reportedLu;
        private string sourceIP = string.Empty;
        private string address;


        //Buffers
        private NetBuffer sbBuffer;
        private readonly byte[] byteBuffer = new byte[32767];
        private byte[] inputBuffer;

        //Telnet predefined messages
        private readonly byte[] doOption = {TelnetConstants.IAC, TelnetConstants.DO, 0};
        private readonly byte[] dontOption = {TelnetConstants.IAC, TelnetConstants.DONT, 0};
        private readonly byte[] willDoOption = {TelnetConstants.IAC, TelnetConstants.WILL, 0};
        private readonly byte[] wontDoOption = {TelnetConstants.IAC, TelnetConstants.WONT, 0};


        //Sockets
        private IPEndPoint remoteEndpoint;
        private IPEndPoint localEndpoint;
        private AsyncCallback callbackProc;
        private Socket socketBase;
        private Stream socketStream;


        //Threading and synchronization fields
        private readonly object receivingPadlock = new object();
        private MySemaphore logFileSemaphore;
        private Thread logFileProcessorThread;
        private Thread mainThread;
        private Queue logClientData;

        private object parentData;

        #endregion Fields

        #region Simple Properties

        public TNTrace Trace { get; set; }

        public Controller Controller { get; }

        public Print Print { get; set; }

        public Actions Action { get; set; }

        public Idle Idle { get; set; }

        public Ansi Ansi { get; set; }

        public Events Events { get; set; }

        public Keyboard Keyboard { get; set; }

        public Appres Appres { get; }

        public ManualResetEvent WaitEvent1 { get; set; } = new ManualResetEvent(false);

        public bool ParseLogFileOnly { get; set; }

        public TN3270API TelnetApi { get; set; }

        public ConnectionConfig Config { get; }

        public SmsState WaitState { get; set; }

        internal bool ShowParseError { get; set; }

        public List<string> Lus { get; set; } = null;

        public string TermType { get; set; }

        public string DisconnectReason { get; private set; }

        public int StartedReceivingCount
        {
            get
            {
                lock (receivingPadlock)
                {
                    return startedReceivingCount;
                }
            }
        }

        #endregion Simple Properties

        #region Macro-like Properties

        public bool IsKeyboardInWait
        {
            get
            {
                return 0 !=
                       (Keyboard.keyboardLock &
                        (KeyboardConstants.OiaLocked | KeyboardConstants.OiaTWait | KeyboardConstants.DeferredUnlock));
            }
        }

        //Macro that defines when it's safe to continue a Wait()ing sms.
        public bool CanProceed
        {
            get
            {
                return IsSscp ||
                       (Is3270 && Controller.Formatted && Controller.CursorAddress != 0 && !IsKeyboardInWait) ||
                       (IsAnsi && 0 == (Keyboard.keyboardLock & KeyboardConstants.AwaitingFirst));
            }
        }

        public bool IsSocketConnected
        {
            get
            {
                if (Config.LogFile != null)
                {
                    return true;
                }

                if (socketBase != null && socketBase.Connected)
                {
                    return true;
                }
                if (DisconnectReason == null)
                    DisconnectReason = "Server disconnected socket";
                return false;
            }
        }


        public bool IsResolving
        {
            get { return (int) connectionState >= (int) ConnectionState.Resolving; }
        }

        public bool IsPending
        {
            get { return connectionState == ConnectionState.Resolving || connectionState == ConnectionState.Pending; }
        }

        public bool IsConnected
        {
            get { return (int) connectionState >= (int) ConnectionState.ConnectedInitial; }
        }

        public bool IsAnsi
        {
            get
            {
                return connectionState == ConnectionState.ConnectedANSI ||
                       connectionState == ConnectionState.ConnectedNVT;
            }
        }

        public bool Is3270
        {
            get
            {
                return connectionState == ConnectionState.Connected3270 ||
                       connectionState == ConnectionState.Connected3270E ||
                       connectionState == ConnectionState.ConnectedSSCP;
            }
        }

        public bool IsSscp
        {
            get { return connectionState == ConnectionState.ConnectedSSCP; }
        }

        public bool IsTn3270E
        {
            get { return connectionState == ConnectionState.Connected3270E; }
        }

        public bool IsE
        {
            get { return connectionState >= ConnectionState.ConnectedInitial3270E; }
        }

        #endregion Macro-like Properties

        #region Ctors, Dtors, clean-up

        public Telnet(TN3270API api, IAudit audit, ConnectionConfig config)
        {
            Config = config;
            TelnetApi = api;
            if (config.IgnoreSequenceCount)
            {
                currentOptionMask = Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE) |
                                    Shift(TelnetConstants.TN3270E_FUNC_SYSREQ);
            }
            else
            {
                currentOptionMask = Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE) |
                                    Shift(TelnetConstants.TN3270E_FUNC_RESPONSES) |
                                    Shift(TelnetConstants.TN3270E_FUNC_SYSREQ);
            }


            DisconnectReason = null;

            Trace = new TNTrace(this, audit);
            Appres = new Appres();
            Events = new Events(this);
            Ansi = new Ansi(this);
            Print = new Print(this);
            Controller = new Controller(this, Appres);
            Keyboard = new Keyboard(this);
            Action = new Actions(this);
            Keyboard.Actions = Action;
            Idle = new Idle(this);

            Controller.CursorLocationChanged += controller_CursorLocationChanged;

            if (!isValid)
            {
                vintr = ParseControlCharacter(Appres.intr);
                vquit = ParseControlCharacter(Appres.quit);
                verase = ParseControlCharacter(Appres.erase);
                vkill = ParseControlCharacter(Appres.kill);
                veof = ParseControlCharacter(Appres.eof);
                vwerase = ParseControlCharacter(Appres.werase);
                vrprnt = ParseControlCharacter(Appres.rprnt);
                vlnext = ParseControlCharacter(Appres.lnext);
                isValid = true;
            }

            int i;
            hostOptions = new int[256];
            for (i = 0; i < 256; i++)
            {
                hostOptions[i] = 0;
            }
        }


        ~Telnet()
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
            if (isDisposed)
            {
                return;
            }
            isDisposed = true;

            if (disposing)
            {
                Disconnect();
                if (Controller != null)
                {
                    Controller.CursorLocationChanged -= controller_CursorLocationChanged;
                    Controller.Dispose();
                }
                if (Idle != null)
                {
                    Idle.Dispose();
                }

                if (Keyboard != null)
                {
                    Keyboard.Dispose();
                }

                if (Ansi != null)
                {
                    Ansi.Dispose();
                }
            }
        }

        #endregion Ctors

        #region Public Methods

        /// <summary>
        ///     Connects to host using a sourceIP for VPN's that require
        ///     source IP to determine LU
        /// </summary>
        /// <param name="parameterObjectToSendCallbacks">object to send to callbacks/events</param>
        /// <param name="hostAddress">host ip address or name</param>
        /// <param name="hostPort">host port</param>
        /// <param name="sourceIP">IP to use as local IP</param>
        public void Connect(object parameterObjectToSendCallbacks, string hostAddress, int hostPort, string sourceIP)
        {
            this.sourceIP = sourceIP;
            Connect(parameterObjectToSendCallbacks, hostAddress, hostPort);
        }


        /// <summary>
        ///     Connects to host at address/port
        /// </summary>
        /// <param name="parameterObjectToSendCallbacks">object to send to callbacks/events</param>
        /// <param name="hostAddress">host ip address or name</param>
        /// <param name="hostPort">host port</param>
        public void Connect(object parameterObjectToSendCallbacks, string hostAddress, int hostPort)
        {
            parentData = parameterObjectToSendCallbacks;
            address = hostAddress;
            port = hostPort;
            DisconnectReason = null;
            closeRequested = false;

            //Junk
            if (Config.TermType == null)
            {
                TermType = "IBM-3278-2";
            }
            else
            {
                TermType = Config.TermType;
            }

            Controller.Initialize(-1);
            Controller.Reinitialize(-1);
            Keyboard.Initialize();
            Ansi.ansi_init();


            //Set up colour screen
            Appres.mono = false;
            Appres.m3279 = true;
            //Set up trace options
            Appres.debug_tracing = true;


            //Handle initial toggle settings.
            if (!Appres.debug_tracing)
            {
                Appres.SetToggle(Appres.DSTrace, false);
                Appres.SetToggle(Appres.EventTrace, false);
            }

            Appres.SetToggle(Appres.DSTrace, true);

            if (Config.LogFile != null)
            {
                ParseLogFileOnly = true;
                // Simulate a connect
                logFileSemaphore = new MySemaphore(0, 9999);
                logClientData = new Queue();
                logFileProcessorThread_Quit = false;
                mainThread = Thread.CurrentThread;
                logFileProcessorThread = new Thread(LogFileProcessorThreadHandler);

                logFileProcessorThread.Start();
            }
            else
            {
                // Actually connect

                //TODO: Replace IP address analysis with a regex
                var ipaddress = false;
                var text = false;
                var count = 0;

                for (var i = 0; i < address.Length; i++)
                {
                    if (address[i] == '.')
                        count++;
                    else
                    {
                        if (address[i] < '0' || address[i] > '9')
                            text = true;
                    }
                }
                if (count == 3 && text == false)
                {
                    ipaddress = true;
                }

                if (!ipaddress)
                {
                    try
                    {
                        var hostEntry = Dns.GetHostEntry(address);
                        var aliases = hostEntry.Aliases;
                        var addr = hostEntry.AddressList;
                        remoteEndpoint = new IPEndPoint(addr[0], port);
                    }
                    catch (SocketException se)
                    {
                        throw new TNHostException("Unable to resolve host '" + address + "'", se.Message, null);
                    }
                }
                else
                {
                    try
                    {
                        remoteEndpoint = new IPEndPoint(IPAddress.Parse(address), port);
                    }
                    catch (FormatException se)
                    {
                        throw new TNHostException("Invalid Host TCP/IP address '" + address + "'", se.Message, null);
                    }
                }


                // If a source IP is given then use it for the local IP
                if (!string.IsNullOrEmpty(sourceIP))
                {
                    try
                    {
                        localEndpoint = new IPEndPoint(IPAddress.Parse(sourceIP), port);
                    }
                    catch (FormatException se)
                    {
                        throw new TNHostException("Invalid Source TCP/IP address '" + address + "'", se.Message, null);
                    }
                }
                else
                {
                    localEndpoint = new IPEndPoint(IPAddress.Any, 0);
                }

                DisconnectReason = null;

                try
                {
                    // Create New Socket 
                    socketBase = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    // Create New EndPoint
                    // Assign Callback function to read from Asyncronous Socket
                    callbackProc = ConnectCallback;
                    // Begin Asyncronous Connection
                    connectionState = ConnectionState.Resolving;
                    socketBase.Bind(localEndpoint);
                    socketBase.BeginConnect(remoteEndpoint, callbackProc, socketBase);
                }
                catch (SocketException se)
                {
                    throw new TNHostException(
                        "An error occured connecting to the host '" + address + "' on port " + port, se.Message, null);
                }
                catch (Exception eeeee)
                {
                    Console.WriteLine("e=" + eeeee);
                    throw;
                }
            }
        }


        public void Disconnect()
        {
            if (!ParseLogFileOnly)
            {
                lock (this)
                {
                    if (socketStream != null)
                    {
                        //Console.WriteLine("Disconnect TN3270 socket");
                        closeRequested = true;
                        socketStream.Close();
                        socketStream = null;

                        if (string.IsNullOrEmpty(DisconnectReason))
                            DisconnectReason = "telnet.disconnect socket-stream requested";
                    }
                    //
                    if (socketBase != null)
                    {
                        closeRequested = true;

                        try
                        {
                            socketBase.Close();

                            if (string.IsNullOrEmpty(DisconnectReason))
                                DisconnectReason = "telnet.disconnect socket-base requested";
                        }
                        catch (ObjectDisposedException)
                        {
                            // Ignore this
                        }
                        socketBase = null;
                    }
                }
            }
            if (logFileProcessorThread != null)
            {
                logFileProcessorThread_Quit = true;
                Console.WriteLine("closing log processor thread");
                logFileProcessorThread.Join();
                logFileProcessorThread = null;
            }
        }


        public bool ParseByte(byte b)
        {
            lock (this)
            {
                if (tnState == TN3270State.InNeither)
                {
                    Keyboard.KeyboardLockClear(KeyboardConstants.AwaitingFirst, "telnet_fsm");
                    //status_reset();
                    tnState = TN3270State.ANSI;
                }
                if (TelnetProcessFiniteStateMachine(ref sbBuffer, b) != 0)
                {
                    Host_Disconnect(true);
                    Disconnect();
                    DisconnectReason = "Telnet state machine error during ParseByte";
                    return false;
                }
            }
            return true;
        }

        public int TelnetProcessFiniteStateMachine(ref NetBuffer sbptr, byte currentByte)
        {
            int i;
            var sl = 0;
            if (sbptr == null)
            {
                sbptr = new NetBuffer();
            }

            //Console.WriteLine(""+telnet_state+"-0x"+(int)c);

            switch (telnetState)
            {
                case TelnetState.Data:
                {
                    //Normal data processing
                    if (currentByte == TelnetConstants.IAC)
                    {
                        //Got a telnet command
                        telnetState = TelnetState.IAC;
                        if (ansiData != 0)
                        {
                            Trace.trace_dsn("\n");
                            ansiData = 0;
                        }
                        break;
                    }
                    if (connectionState == ConnectionState.ConnectedInitial)
                    {
                        //Now can assume ANSI mode 
                        SetHostState(ConnectionState.ConnectedANSI);
                        Keyboard.KeyboardLockClear(KeyboardConstants.AwaitingFirst, "telnet_fsm");
                        Controller.ProcessPendingInput();
                    }
                    if (IsAnsi && !IsE)
                    {
                        if (ansiData == 0)
                        {
                            Trace.trace_dsn("<.. ");
                            ansiData = 4;
                        }
                        var see_chr = Util.ControlSee(currentByte);
                        ansiData += sl = see_chr.Length;
                        if (ansiData >= TNTrace.TRACELINE)
                        {
                            Trace.trace_dsn(" ...\n... ");
                            ansiData = 4 + sl;
                        }
                        Trace.trace_dsn(see_chr);
                        if (!syncing)
                        {
                            if (linemode && Appres.onlcr && currentByte == '\n')
                            {
                                Ansi.ansi_process((byte) '\r');
                            }
                            Ansi.ansi_process(currentByte);
                        }
                    }
                    else
                    {
                        Store3270Input(currentByte);
                    }
                    break;
                }
                case TelnetState.IAC:
                {
                    //Process a telnet command
                    if (currentByte != TelnetConstants.EOR && currentByte != TelnetConstants.IAC)
                    {
                        Trace.trace_dsn("RCVD " + GetCommand(currentByte) + " ");
                    }

                    switch (currentByte)
                    {
                        case TelnetConstants.IAC:
                        {
                            //Ecaped IAC, insert it
                            if (IsAnsi && !IsE)
                            {
                                if (ansiData == 0)
                                {
                                    Trace.trace_dsn("<.. ");
                                    ansiData = 4;
                                }
                                var see_chr = Util.ControlSee(currentByte);
                                ansiData += sl = see_chr.Length;
                                if (ansiData >= TNTrace.TRACELINE)
                                {
                                    Trace.trace_dsn(" ...\n ...");
                                    ansiData = 4 + sl;
                                }
                                Trace.trace_dsn(see_chr);
                                Ansi.ansi_process(currentByte);
                                //Console.WriteLine("--BUGBUG--sms_store");
                                //sms_store(c);
                            }
                            else
                            {
                                Store3270Input(currentByte);
                            }

                            telnetState = TelnetState.Data;
                            break;
                        }
                        case TelnetConstants.EOR:
                        {
                            //EOR, process accumulated input
                            Trace.trace_dsn("RCVD EOR\n");
                            if (Is3270 || (IsE && tn3270e_negotiated))
                            {
                                //Can't see this being used. --> ns_rrcvd++;
                                if (ProcessEOR())
                                {
                                    return -1;
                                }
                            }
                            else
                            {
                                Events.Warning("EOR received when not in 3270 mode, ignored.");
                            }

                            inputBufferIndex = 0;
                            telnetState = TelnetState.Data;
                            break;
                        }
                        case TelnetConstants.WILL:
                        {
                            telnetState = TelnetState.Will;
                            break;
                        }
                        case TelnetConstants.WONT:
                        {
                            telnetState = TelnetState.Wont;
                            break;
                        }
                        case TelnetConstants.DO:
                        {
                            telnetState = TelnetState.Do;
                            break;
                        }
                        case TelnetConstants.DONT:
                        {
                            telnetState = TelnetState.Dont;
                            break;
                        }
                        case TelnetConstants.SB:
                        {
                            telnetState = TelnetState.SB;
                            sbBuffer = new NetBuffer();
                            //if (sbbuf == null)
                            //	sbbuf = (int)Malloc(1024); //bug
                            //sbptr = sbbuf;
                            break;
                        }
                        case TelnetConstants.DM:
                        {
                            Trace.trace_dsn("\n");
                            if (syncing)
                            {
                                syncing = false;
                                //x_except_on(sock);
                            }
                            telnetState = TelnetState.Data;
                            break;
                        }
                        case TelnetConstants.GA:
                        case TelnetConstants.NOP:
                        {
                            Trace.trace_dsn("\n");
                            telnetState = TelnetState.Data;
                            break;
                        }
                        default:
                        {
                            Trace.trace_dsn("???\n");
                            telnetState = TelnetState.Data;
                            break;
                        }
                    }
                    break;
                }
                case TelnetState.Will:
                {
                    //Telnet WILL DO OPTION command
                    Trace.trace_dsn("" + GetOption(currentByte) + "\n");
                    if (currentByte == TelnetConstants.TELOPT_SGA ||
                        currentByte == TelnetConstants.TELOPT_BINARY ||
                        currentByte == TelnetConstants.TELOPT_EOR ||
                        currentByte == TelnetConstants.TELOPT_TTYPE ||
                        currentByte == TelnetConstants.TELOPT_ECHO ||
                        currentByte == TelnetConstants.TELOPT_TN3270E)
                    {
                        if (currentByte != TelnetConstants.TELOPT_TN3270E || !nonTn3270eHost)
                        {
                            if (hostOptions[currentByte] == 0)
                            {
                                hostOptions[currentByte] = 1;
                                doOption[2] = currentByte;
                                SendRawOutput(doOption);
                                Trace.trace_dsn("SENT DO " + GetOption(currentByte) + "\n");

                                //For UTS, volunteer to do EOR when they do.
                                if (currentByte == TelnetConstants.TELOPT_EOR && clientOptions[currentByte] == 0)
                                {
                                    clientOptions[currentByte] = 1;
                                    willDoOption[2] = currentByte;
                                    SendRawOutput(willDoOption);
                                    Trace.trace_dsn("SENT WILL " + GetOption(currentByte) + "\n");
                                }

                                CheckIn3270();
                                CheckLineMode(false);
                            }
                        }
                    }
                    else
                    {
                        dontOption[2] = currentByte;
                        SendRawOutput(dontOption);
                        Trace.trace_dsn("SENT DONT " + GetOption(currentByte) + "\n");
                    }
                    telnetState = TelnetState.Data;
                    break;
                }
                case TelnetState.Wont:
                {
                    //Telnet WONT DO OPTION command
                    Trace.trace_dsn("" + GetOption(currentByte) + "\n");
                    if (hostOptions[currentByte] != 0)
                    {
                        hostOptions[currentByte] = 0;
                        dontOption[2] = currentByte;
                        SendRawOutput(dontOption);
                        Trace.trace_dsn("SENT DONT " + GetOption(currentByte) + "\n");
                        CheckIn3270();
                        CheckLineMode(false);
                    }
                    telnetState = TelnetState.Data;
                    break;
                }
                case TelnetState.Do:
                {
                    //Telnet PLEASE DO OPTION command
                    Trace.trace_dsn("" + GetOption(currentByte) + "\n");
                    if (currentByte == TelnetConstants.TELOPT_BINARY ||
                        currentByte == TelnetConstants.TELOPT_BINARY ||
                        currentByte == TelnetConstants.TELOPT_EOR ||
                        currentByte == TelnetConstants.TELOPT_TTYPE ||
                        currentByte == TelnetConstants.TELOPT_SGA ||
                        currentByte == TelnetConstants.TELOPT_NAWS ||
                        currentByte == TelnetConstants.TELOPT_TM ||
                        (currentByte == TelnetConstants.TELOPT_TN3270E && !Config.RefuseTN3270E))
                    {
                        var fallthrough = true;
                        if (currentByte != TelnetConstants.TELOPT_TN3270E || !nonTn3270eHost)
                        {
                            if (clientOptions[currentByte] == 0)
                            {
                                if (currentByte != TelnetConstants.TELOPT_TM)
                                {
                                    clientOptions[currentByte] = 1;
                                }
                                willDoOption[2] = currentByte;

                                SendRawOutput(willDoOption);
                                Trace.trace_dsn("SENT WILL " + GetOption(currentByte) + "\n");
                                CheckIn3270();
                                CheckLineMode(false);
                            }
                            if (currentByte == TelnetConstants.TELOPT_NAWS)
                            {
                                SendNaws();
                            }
                            fallthrough = false;
                        }
                        if (fallthrough)
                        {
                            wontDoOption[2] = currentByte;
                            SendRawOutput(wontDoOption);
                            Trace.trace_dsn("SENT WONT " + GetOption(currentByte) + "\n");
                        }
                    }
                    else
                    {
                        wontDoOption[2] = currentByte;
                        SendRawOutput(wontDoOption);
                        Trace.trace_dsn("SENT WONT " + GetOption(currentByte) + "\n");
                    }

                    telnetState = TelnetState.Data;
                    break;
                }
                case TelnetState.Dont:
                {
                    //Telnet PLEASE DON'T DO OPTION command
                    Trace.trace_dsn("" + GetOption(currentByte) + "\n");
                    if (clientOptions[currentByte] != 0)
                    {
                        clientOptions[currentByte] = 0;
                        wontDoOption[2] = currentByte;
                        SendRawOutput(wontDoOption);
                        Trace.trace_dsn("SENT WONT " + GetOption(currentByte) + "\n");
                        CheckIn3270();
                        CheckLineMode(false);
                    }
                    telnetState = TelnetState.Data;
                    break;
                }
                case TelnetState.SB:
                {
                    //Telnet sub-option string command
                    if (currentByte == TelnetConstants.IAC)
                    {
                        telnetState = TelnetState.SbIac;
                    }
                    else
                    {
                        sbBuffer.Add(currentByte);
                    }
                    break;
                }
                case TelnetState.SbIac:
                {
                    //Telnet sub-option string command
                    sbBuffer.Add(currentByte);
                    if (currentByte == TelnetConstants.SE)
                    {
                        telnetState = TelnetState.Data;
                        if (sbptr.Data[0] == TelnetConstants.TELOPT_TTYPE &&
                            sbptr.Data[1] == TelnetConstants.TELQUAL_SEND)
                        {
                            int tt_len;
                            Trace.trace_dsn("" + GetOption(sbptr.Data[0]) + " " +
                                            TelnetConstants.TelQuals[sbptr.Data[1]] + "\n");
                            if (Lus != null && currentLUIndex >= Lus.Count)
                            {
                                //Console.WriteLine("BUGBUG-resending LUs, rather than forcing error");

                                //this.currentLUIndex=0;
                                /* None of the LUs worked. */
                                Events.ShowError("Cannot connect to specified LU");
                                return -1;
                            }

                            tt_len = TermType.Length;
                            if (Lus != null)
                            {
                                tt_len += Lus[currentLUIndex].Length + 1;
                                //tt_len += strlen(try_lu) + 1;
                                connectedLu = Lus[currentLUIndex];
                            }
                            else
                            {
                                connectedLu = null;
                                //status_lu(connected_lu);
                            }

                            var tt_out = new NetBuffer();
                            tt_out.Add(TelnetConstants.IAC);
                            tt_out.Add(TelnetConstants.SB);
                            tt_out.Add(TelnetConstants.TELOPT_TTYPE);
                            tt_out.Add(TelnetConstants.TELQUAL_IS);
                            tt_out.Add(TermType);

                            if (Lus != null)
                            {
                                tt_out.Add((byte) '@');

                                var b_try_lu = Encoding.ASCII.GetBytes(Lus[currentLUIndex]);
                                for (i = 0; i < b_try_lu.Length; i++)
                                {
                                    tt_out.Add(b_try_lu[i]);
                                }
                                tt_out.Add(TelnetConstants.IAC);
                                tt_out.Add(TelnetConstants.SE);
                                Console.WriteLine("Attempt LU='" + Lus[currentLUIndex] + "'");
                            }
                            else
                            {
                                tt_out.Add(TelnetConstants.IAC);
                                tt_out.Add(TelnetConstants.SE);
                            }
                            SendRawOutput(tt_out);

                            Trace.trace_dsn("SENT SB " + GetOption(TelnetConstants.TELOPT_TTYPE) + " " +
                                            tt_out.Data.Length + " " + TermType + " " + GetCommand(TelnetConstants.SE));

                            /* Advance to the next LU name. */
                            currentLUIndex++;
                        }
                        else if (clientOptions[TelnetConstants.TELOPT_TN3270E] != 0 &&
                                 sbBuffer.Data[0] == TelnetConstants.TELOPT_TN3270E)
                        {
                            if (Tn3270e_Negotiate(sbptr) != 0)
                            {
                                return -1;
                            }
                        }
                    }
                    else
                    {
                        telnetState = TelnetState.SB;
                    }
                    break;
                }
            }
            return 0;
        }


        public void SendString(string s)
        {
            int i;
            for (i = 0; i < s.Length; i++)
                SendChar(s[i]);
        }


        public void SendChar(char c)
        {
            SendByte((byte) c);
        }


        public void SendByte(byte c)
        {
            var buf = new byte[2];
            if (c == '\r' && !linemode)
            {
                /* CR must be quoted */
                buf[0] = (byte) '\r';
                buf[1] = 0;

                Cook(buf, 2);
            }
            else
            {
                buf[0] = c;
                Cook(buf, 1);
            }
        }


        public void Abort()
        {
            byte[] buf = {TelnetConstants.IAC, TelnetConstants.AO};

            if ((currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_SYSREQ)) != 0)
            {
                /* I'm not sure yet what to do here.  Should the host respond
				 * to the AO by sending us SSCP-LU data (and putting us into
				 * SSCP-LU mode), or should we put ourselves in it?
				 * Time, and testers, will tell.
				 */
                switch (tn3270eSubmode)
                {
                    case TN3270ESubmode.None:
                    case TN3270ESubmode.NVT:
                        break;
                    case TN3270ESubmode.SSCP:
                        SendRawOutput(buf, buf.Length);
                        Trace.trace_dsn("SENT AO\n");
                        if (tn3270eBound ||
                            0 == (currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE)))
                        {
                            tn3270eSubmode = TN3270ESubmode.Mode3270;
                            CheckIn3270();
                        }
                        break;
                    case TN3270ESubmode.Mode3270:
                        SendRawOutput(buf, buf.Length);
                        Trace.trace_dsn("SENT AO\n");
                        tn3270eSubmode = TN3270ESubmode.SSCP;
                        CheckIn3270();
                        break;
                }
            }
        }

        /// <summary>
        ///     Sends erase character in ANSI mode
        /// </summary>
        public void SendErase()
        {
            var data = new byte[1];
            data[0] = verase;
            Cook(data, 1);
        }


        /// <summary>
        ///     Sends the KILL character in ANSI mode
        /// </summary>
        public void SendKill()
        {
            var data = new byte[1];
            data[0] = vkill;
            Cook(data, 1);
        }

        /// <summary>
        ///     Sends WERASE character
        /// </summary>
        public void SendWErase()
        {
            var data = new byte[1];
            data[0] = vwerase;
            Cook(data, 1);
        }


        /// <summary>
        ///     Send uncontrolled user data to the host in ANSI mode, performing IAC and CR quoting as necessary.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="length"></param>
        public void SendHexAnsiOut(byte[] buffer, int length)
        {
            byte[] tempBuffer;
            int index;

            if (length > 0)
            {
                //Trace the data.
                if (Appres.Toggled(Appres.DSTrace))
                {
                    int i;

                    Trace.trace_dsn(">");
                    for (i = 0; i < length; i++)
                    {
                        Trace.trace_dsn(" " + Util.ControlSee(buffer[i]));
                    }
                    Trace.trace_dsn("\n");
                }


                //Expand it.
                tempBuffer = new byte[2*length];
                index = 0;
                var bindex = 0;
                while (length > 0)
                {
                    var c = buffer[bindex++];

                    tempBuffer[index++] = c;
                    length--;
                    if (c == TelnetConstants.IAC)
                    {
                        tempBuffer[index++] = TelnetConstants.IAC;
                    }
                    else if (c == (byte) '\r' && (length == 0 || buffer[bindex] != (byte) '\n'))
                    {
                        tempBuffer[index++] = 0;
                    }
                }

                //Send it to the host.
                SendRawOutput(tempBuffer, index);
            }
        }

        #endregion Public Methods

        #region Private Methods

        private void LogFileProcessorThreadHandler()
        {
            try
            {
                // Simulate notification of parent
                OnTelnetData(parentData, TNEvent.Connect, null);

                // Notify TN
                connectionState = ConnectionState.ConnectedInitial;
                OnPrimaryConnectionChanged(true);

                Net_Connected();
                SetHostState(ConnectionState.ConnectedANSI);

                // Now simulate TCP/IP data coming in from the file
                while (!logFileProcessorThread_Quit)
                {
                    var text = Config.LogFile.ReadLine();
                    if (text == null)
                    {
                        // Simulate disconnect
                        Trace.trace_dsn("RCVD disconnect\n");
                        //host_disconnect(false);
                        // If no data was recieved then the connection is probably dead

                        Console.WriteLine("Disconnected from log file");
                        // (We are this thread!)
                        OnTelnetData(parentData, TNEvent.Disconnect, null);
                        // Close thread.
                    }
                    else if (text.Length >= 11)
                    {
                        var time = Convert.ToInt32(text.Substring(0, 6));
                        Trace.WriteLine("\n" + text.Substring(7));
                        if (text.Substring(9, 2) == "H ")
                        {
                            text = text.Substring(18);
                            if (IsPending)
                            {
                                Host_Connected();
                                Net_Connected();
                            }

                            lock (this)
                            {
                                while (text.Length > 1)
                                {
                                    var v = Convert.ToByte(text.Substring(0, 2), 16);
                                    if (tnState == TN3270State.InNeither)
                                    {
                                        Keyboard.KeyboardLockClear(KeyboardConstants.AwaitingFirst, "telnet_fsm");
                                        //status_reset();
                                        tnState = TN3270State.ANSI;
                                    }
                                    if (TelnetProcessFiniteStateMachine(ref sbBuffer, v) != 0)
                                    {
                                        Host_Disconnect(true);
                                        Disconnect();
                                        DisconnectReason =
                                            "open3270.LogfileProcessorThreadHandler telnet_fsm error : disconnected";
                                        return;
                                    }

                                    text = text.Substring(2).Trim();
                                }
                            }
                        }
                        else if (text.Substring(9, 2) == "C ")
                        {
                            Trace.WriteLine("--client data - should wait for netout before moving to next row. CC=" +
                                            logFileSemaphore.Count);
                            var length = 0;
                            text = text.Substring(18);
                            while (text.Length > 1)
                            {
                                var v = Convert.ToByte(text.Substring(0, 2), 16);
                                length++;
                                byte netoutbyte = 0;
                                try
                                {
                                    netoutbyte = (byte) logClientData.Dequeue();
                                }
                                catch (InvalidOperationException)
                                {
                                    Console.WriteLine("Queue empty - increment empty queue flag");
                                }
                                if (v != netoutbyte)
                                {
                                    Console.WriteLine("**BUGBUG** " +
                                                      string.Format(
                                                          "oops - byte is not the same as client buffer. Read {0:x2}'{2}' netout {1:x2}'{3}'",
                                                          v, netoutbyte, Convert.ToChar(Tables.Ebc2Ascii[v]),
                                                          Convert.ToChar(Tables.Ebc2Ascii[netoutbyte])));
                                }
                                while (!logFileProcessorThread_Quit)
                                {
                                    if (logFileSemaphore.Acquire(1000))
                                    {
                                        break;
                                    }

                                    if (!mainThread.IsAlive ||
                                        (mainThread.ThreadState & ThreadState.Stopped) != 0 ||
                                        (mainThread.ThreadState & ThreadState.StopRequested) != 0
                                        )
                                    {
                                        logFileProcessorThread_Quit = true;
                                        break;
                                    }
                                }
                                if (logFileProcessorThread_Quit)
                                {
                                    break;
                                }

                                text = text.Substring(2).Trim();
                            }
                            Trace.WriteLine("--client data - acquired " + length + " bytes ok. CC=" +
                                            logFileSemaphore.Count);
                        }
                    }
                }
                // Done
            }
            catch (Exception e)
            {
                Console.WriteLine("Telnet logfile parser exception " + e);
                throw;
            }
            finally
            {
                Console.WriteLine("LogFileProcessor Thread stopped");
            }
        }


        private void ConnectCallback(IAsyncResult ar)
        {
            try
            {
                // Get The connection socket from the callback
                var socket1 = (Socket) ar.AsyncState;
                if (socket1.Connected)
                {
                    //Notify parent
                    OnTelnetData(parentData, TNEvent.Connect, null);

                    //Notify TN
                    connectionState = ConnectionState.ConnectedInitial;
                    OnPrimaryConnectionChanged(true);

                    Net_Connected();
                    SetHostState(ConnectionState.ConnectedANSI);

                    //Define a new callback to read the data 
                    AsyncCallback recieveData = OnRecievedData;

                    if (Config.UseSSL)
                    {
                        var mNetworkStream = new NetworkStream(socketBase, false);
                        var ssl = new SslStream(mNetworkStream, false, cryptocallback);
                        ssl.AuthenticateAsClient(address);
                        Trace.WriteLine("SSL Connection made. Encryption is '" + ssl.IsEncrypted + "'");

                        socketStream = ssl;
                    }
                    else
                    {
                        socketStream = new NetworkStream(socketBase, false);
                    }

                    // Begin reading data asyncronously
                    socketStream.BeginRead(byteBuffer, 0, byteBuffer.Length, recieveData, socketStream);
                    Trace.trace_dsn("\nConnectCallback : SocketStream.BeginRead called to read asyncronously\n");
                }
                else
                {
                    DisconnectReason = "Unable to connect to host - timeout on connect";
                    OnTelnetData(parentData, TNEvent.Error, "Connect callback returned 'not connected'");
                    // spurious, but to meet spec
                    connectionState = ConnectionState.NotConnected;

                    OnPrimaryConnectionChanged(false);
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine("Setup Receive callback failed " + ex);
                Trace.trace_event("%s", "Exception occured connecting to host. Disconnecting\n\n" + ex);
                Disconnect();
                DisconnectReason = "exception during telnet connect callback";
            }
        }


        /// <summary>
        ///     This section is for screen syncronization with the user of this library
        ///     StartedReceivingCount gets incremented each time OnReceiveData is invoked by the socket.
        ///     (CFC,Jr, 2008/06/26)
        /// </summary>
        private void NofityStartedReceiving()
        {
            lock (receivingPadlock)
            {
                startedReceivingCount++;
            }
            Trace.trace_dsn("NotifyStartedReceiving : startedReceivingCount = " + StartedReceivingCount +
                            Environment.NewLine);
        }


        /// <summary>
        ///     Called from the socket when data is available
        /// </summary>
        /// <param name="ar"></param>
        private void OnRecievedData(IAsyncResult ar)
        {
            // (CFC, added for screen syncronization)
            try
            {
                NofityStartedReceiving();
            }
            catch
            {
            }

            // Get The connection socket from the callback
            var streamSocket = (Stream) ar.AsyncState;
            var disconnectme = false;

            // Is socket closing
            DisconnectReason = null;

            if (socketBase == null || socketBase.Connected == false)
            {
                disconnectme = true;
                if (string.IsNullOrEmpty(DisconnectReason))
                {
                    DisconnectReason = "Host dropped connection or not connected in telnet.OnReceivedData";
                }
            }
            else
            {
                try
                {
                    // Get The data , if any
                    var nBytesRec = 0;
                    nBytesRec = streamSocket.EndRead(ar);

                    ansiData = 0;
                    if (nBytesRec > 0)
                    {
                        if (IsPending)
                        {
                            Host_Connected();
                            Net_Connected();
                        }

                        Trace.trace_netdata('<', byteBuffer, nBytesRec);
                        bytesReceived += nBytesRec;

                        int i;

                        if (ShowParseError)
                        {
                            Trace.trace_dsn("ShowParseError called - throw exception");
                            throw new ApplicationException("ShowParseError exception test requested");
                        }

                        var data = new byte[nBytesRec];

                        lock (this)
                        {
                            //CFCJR sync up sequence number
                            if (nBytesRec >= 5 &&
                                (currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_RESPONSES)) != 0) // CFCJR
                            {
                                eTransmitSequence = (byteBuffer[3] << 8) | byteBuffer[4];
                                eTransmitSequence = (eTransmitSequence + 1) & 0x7FFF;

                                Trace.trace_dsn("\nxmit sequence set to " + eTransmitSequence + "\n");
                            }

                            for (i = 0; i < nBytesRec; i++)
                            {
                                if (tnState == TN3270State.InNeither)
                                {
                                    Keyboard.KeyboardLockClear(KeyboardConstants.AwaitingFirst, "telnet_fsm");
                                    //status_reset();
                                    tnState = TN3270State.ANSI;
                                }
                                if (TelnetProcessFiniteStateMachine(ref sbBuffer, byteBuffer[i]) != 0)
                                {
                                    Host_Disconnect(true);
                                    Disconnect();
                                    DisconnectReason = "telnet_fsm error in OnReceiveData"; //CFC,Jr. 7/8/2008
                                    return;
                                }
                            }
                        }

                        // Define a new Callback to read the data 
                        AsyncCallback recieveData = OnRecievedData;
                        // Begin reading data asyncronously
                        socketStream.BeginRead(byteBuffer, 0, byteBuffer.Length, recieveData, socketStream);
                        Trace.trace_dsn("\nOnReceiveData : SocketStream.BeginRead called to read asyncronously\n");
                    }
                    else
                    {
                        disconnectme = true;
                        DisconnectReason = "No data received in telnet.OnReceivedData, disconnecting";
                    }
                }
                catch (ObjectDisposedException)
                {
                    disconnectme = true;
                    DisconnectReason = "Client dropped connection : Using Disposed Object Exception";
                }
                catch (Exception e)
                {
                    Trace.trace_event("%s", "Exception occured processing Telnet buffer. Disconnecting\n\n" + e);
                    disconnectme = true;
                    DisconnectReason = "Exception in data stream (" + e.Message + "). Connection dropped.";
                }
            }

            if (disconnectme)
            {
                var closeWasRequested = closeRequested;
                Trace.trace_dsn("RCVD disconnect\n");
                Host_Disconnect(false);
                // If no data was recieved then the connection is probably dead

                Disconnect();

                if (closeWasRequested)
                {
                    OnTelnetData(parentData, TNEvent.Disconnect, null);
                }
                else
                {
                    OnTelnetData(parentData, TNEvent.DisconnectUnexpected, null);
                }

                closeRequested = false;
            }
        }


        protected void OnTelnetData(object parentData, TNEvent eventType, string text)
        {
            if (telnetDataEventOccurred != null)
            {
                telnetDataEventOccurred(parentData, eventType, text);
            }
        }


        private string DumpToString(byte[] data, int length)
        {
            var output = " ";
            for (var i = 0; i < length; i++)
            {
                output += string.Format("{0:x2}", data[i]) + " ";
            }
            return output;
        }


        private string ToHex(int n)
        {
            return string.Format("{0:x2}", n);
        }


        private int Shift(int n)
        {
            return 1 << n;
        }


        private void SendRawOutput(NetBuffer smk)
        {
            var bytes = smk.Data;
            SendRawOutput(bytes);
        }


        private void SendRawOutput(byte[] smkBuffer)
        {
            SendRawOutput(smkBuffer, smkBuffer.Length);
        }


        private void SendRawOutput(byte[] smkBuffer, int length)
        {
            if (ParseLogFileOnly)
            {
                Trace.WriteLine("\nnet_rawout2 [" + length + "]" + DumpToString(smkBuffer, length) + "\n");

                // If we're reading the log file, allow the next bit of data to flow
                if (logFileSemaphore != null)
                {
                    //trace.WriteLine("net_rawout2 - CC="+mLogFileSemaphore.Count);
                    for (var i = 0; i < length; i++)
                    {
                        logClientData.Enqueue(smkBuffer[i]);
                    }
                    logFileSemaphore.Release(length);
                }
            }
            else
            {
                Trace.trace_netdata('>', smkBuffer, length);
                socketStream.Write(smkBuffer, 0, length);
            }
        }


        private void Store3270Input(byte c)
        {
            if (inputBufferIndex >= inputBuffer.Length)
            {
                var temp = new byte[inputBuffer.Length + 256];
                inputBuffer.CopyTo(temp, 0);
                inputBuffer = temp;
            }
            inputBuffer[inputBufferIndex++] = c;
        }


        private void SetHostState(ConnectionState new_cstate)
        {
            var now3270 = new_cstate == ConnectionState.Connected3270 ||
                          new_cstate == ConnectionState.ConnectedSSCP ||
                          new_cstate == ConnectionState.Connected3270E;

            connectionState = new_cstate;
            Controller.Is3270 = now3270;

            OnConnected3270(now3270);
        }

        private void CheckIn3270()
        {
            var newConnectionState = ConnectionState.NotConnected;


            if (clientOptions[TelnetConstants.TELOPT_TN3270E] != 0)
            {
                if (!tn3270e_negotiated)
                    newConnectionState = ConnectionState.ConnectedInitial3270E;
                else
                {
                    switch (tn3270eSubmode)
                    {
                        case TN3270ESubmode.None:
                            newConnectionState = ConnectionState.ConnectedInitial3270E;
                            break;
                        case TN3270ESubmode.NVT:
                            newConnectionState = ConnectionState.ConnectedNVT;
                            break;
                        case TN3270ESubmode.Mode3270:
                            newConnectionState = ConnectionState.Connected3270E;
                            break;
                        case TN3270ESubmode.SSCP:
                            newConnectionState = ConnectionState.ConnectedSSCP;
                            break;
                    }
                }
            }
            else if (clientOptions[TelnetConstants.TELOPT_BINARY] != 0 &&
                     clientOptions[TelnetConstants.TELOPT_EOR] != 0 &&
                     clientOptions[TelnetConstants.TELOPT_TTYPE] != 0 &&
                     hostOptions[TelnetConstants.TELOPT_BINARY] != 0 &&
                     hostOptions[TelnetConstants.TELOPT_EOR] != 0)
            {
                newConnectionState = ConnectionState.Connected3270;
            }
            else if (connectionState == ConnectionState.ConnectedInitial)
            {
                //Nothing has happened, yet.
                return;
            }
            else
            {
                newConnectionState = ConnectionState.ConnectedANSI;
            }

            if (newConnectionState != connectionState)
            {
                var wasInE = connectionState >= ConnectionState.ConnectedInitial3270E;

                Trace.trace_dsn("Now operating in " + newConnectionState + " mode\n");
                SetHostState(newConnectionState);


                //If we've now switched between non-TN3270E mode and TN3270E mode, reset the LU list so we can try again in the new mode.
                if (Lus != null && wasInE != IsE)
                {
                    currentLUIndex = 0;
                }

                //Allocate the initial 3270 input buffer.
                if (newConnectionState >= ConnectionState.ConnectedInitial && inputBuffer == null)
                {
                    inputBuffer = new byte[256];
                    inputBufferIndex = 0;
                }

                //Reinitialize line mode.
                if ((newConnectionState == ConnectionState.ConnectedANSI && linemode) ||
                    newConnectionState == ConnectionState.ConnectedNVT)
                {
                    Console.WriteLine("cooked_init-bad");
                    //cooked_init();
                }

                //If we fell out of TN3270E, remove the state.
                if (clientOptions[TelnetConstants.TELOPT_TN3270E] == 0)
                {
                    tn3270e_negotiated = false;
                    tn3270eSubmode = TN3270ESubmode.None;
                    tn3270eBound = false;
                }
                // Notify script
                Controller.Continue();
            }
        }


        private bool ProcessEOR()
        {
            int i;
            var result = false;

            if (!syncing && inputBufferIndex != 0)
            {
                if (connectionState >= ConnectionState.ConnectedInitial3270E)
                {
                    var h = new TnHeader(inputBuffer);
                    PDS rv;

                    Trace.trace_dsn("RCVD TN3270E(datatype: " + h.DataType + ", request: " + h.RequestFlag +
                                    ", response: " + h.ResponseFlag + ", seq: " +
                                    (h.SequenceNumber[0] << 8 | h.SequenceNumber[1]) + ")\n");

                    switch (h.DataType)
                    {
                        case DataType3270.Data3270:
                        {
                            if ((currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE)) == 0 ||
                                tn3270eBound)
                            {
                                tn3270eSubmode = TN3270ESubmode.Mode3270;
                                CheckIn3270();
                                responseRequired = h.ResponseFlag;
                                rv = Controller.ProcessDS(inputBuffer, TnHeader.EhSize,
                                    inputBufferIndex - TnHeader.EhSize);
                                //Console.WriteLine("*** RV = "+rv);
                                //Console.WriteLine("*** response_required = "+response_required);						
                                if (rv < 0 && responseRequired != TnHeader.HeaderReponseFlags.NoResponse)
                                {
                                    SendNak();
                                }
                                else if (rv == PDS.OkayNoOutput &&
                                         responseRequired == TnHeader.HeaderReponseFlags.AlwaysResponse)
                                {
                                    SendAck();
                                }
                                responseRequired = TnHeader.HeaderReponseFlags.NoResponse;
                            }
                            result = false;
                            break;
                        }
                        case DataType3270.BindImage:
                        {
                            if ((currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE)) != 0)
                            {
                                tn3270eBound = true;
                                CheckIn3270();
                            }

                            result = false;
                            break;
                        }
                        case DataType3270.Unbind:
                        {
                            if ((currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE)) != 0)
                            {
                                tn3270eBound = false;
                                if (tn3270eSubmode == TN3270ESubmode.Mode3270)
                                {
                                    tn3270eSubmode = TN3270ESubmode.None;
                                }
                                CheckIn3270();
                            }
                            result = false;
                            break;
                        }
                        case DataType3270.NvtData:
                        {
                            //In tn3270e NVT mode
                            tn3270eSubmode = TN3270ESubmode.NVT;
                            CheckIn3270();
                            for (i = 0; i < inputBufferIndex; i++)
                            {
                                Ansi.ansi_process(inputBuffer[i]);
                            }
                            result = false;
                            break;
                        }
                        case DataType3270.SscpLuData:
                        {
                            if ((currentOptionMask & Shift(TelnetConstants.TN3270E_FUNC_BIND_IMAGE)) != 0)
                            {
                                tn3270eSubmode = TN3270ESubmode.SSCP;
                                CheckIn3270();
                                Controller.WriteSspcLuData(inputBuffer, TnHeader.EhSize,
                                    inputBufferIndex - TnHeader.EhSize);
                            }

                            result = false;
                            break;
                        }
                        default:
                        {
                            //Should do something more extraordinary here.
                            result = false;
                            break;
                        }
                    }
                }
                else
                {
                    Controller.ProcessDS(inputBuffer, 0, inputBufferIndex);
                }
            }
            return result;
        }


        /// <summary>
        ///     Send acknowledgment
        /// </summary>
        private void SendAck()
        {
            Ack(true);
        }


        /// <summary>
        ///     Send a TN3270E negative response to the server
        /// </summary>
        /// <param name="rv"></param>
        private void SendNak()
        {
            Ack(false);
        }


        /// <summary>
        ///     Sends an ACK or NAK
        /// </summary>
        /// <param name="positive">True to send ACK (positive acknowledgment), otherwise it NAK will be sent</param>
        private void Ack(bool positive)
        {
            var responseBuffer = new byte[9];
            var header = new TnHeader();
            var header_in = new TnHeader(inputBuffer);
            var responseLength = TnHeader.EhSize;

            header.DataType = DataType3270.Response;
            header.RequestFlag = 0;
            header.ResponseFlag = positive
                ? TnHeader.HeaderReponseFlags.PositiveResponse
                : TnHeader.HeaderReponseFlags.NegativeResponse;

            header.SequenceNumber[0] = header_in.SequenceNumber[0];
            header.SequenceNumber[1] = header_in.SequenceNumber[1];
            header.OnToByte(responseBuffer);

            if (header.SequenceNumber[1] == TelnetConstants.IAC)
            {
                responseBuffer[responseLength++] = TelnetConstants.IAC;
            }

            responseBuffer[responseLength++] = positive
                ? TnHeader.HeaderReponseData.PosDeviceEnd
                : TnHeader.HeaderReponseData.NegCommandReject;
            responseBuffer[responseLength++] = TelnetConstants.IAC;
            responseBuffer[responseLength++] = TelnetConstants.EOR;

            Trace.trace_dsn("SENT TN3270E(RESPONSE " + (positive ? "POSITIVE" : "NEGATIVE") + "-RESPONSE: " +
                            (header_in.SequenceNumber[0] << 8 | header_in.SequenceNumber[1]) + ")\n");

            SendRawOutput(responseBuffer, responseLength);
        }

        /// <summary>
        ///     Set the global variable 'linemode', which indicates whether we are in character-by-character mode or line mode.
        /// </summary>
        /// <param name="init"></param>
        private void CheckLineMode(bool init)
        {
            var wasline = linemode;

            /*
				* The next line is a deliberate kluge to effectively ignore the SGA
				* option.  If the host will echo for us, we assume
				* character-at-a-time; otherwise we assume fully cooked by us.
				*
				* This allows certain IBM hosts which volunteer SGA but refuse
				* ECHO to operate more-or-less normally, at the expense of
				* implementing the (hopefully useless) "character-at-a-time, local
				* echo" mode.
				*
				* We still implement "switch to line mode" and "switch to character
				* mode" properly by asking for both SGA and ECHO to be off or on, but
				* we basically ignore the reply for SGA.
				*/
            linemode = hostOptions[TelnetConstants.TELOPT_ECHO] == 0;

            if (init || linemode != wasline)
            {
                OnConnectedLineMode();
                if (!init)
                {
                    Trace.trace_dsn("Operating in %s mode.\n",
                        linemode ? "line" : "character-at-a-time");
                }
                if (IsAnsi && linemode)
                    CookedInitialized();
            }
        }


        private string GetCommand(int index)
        {
            return TelnetConstants.TelnetCommands[index - TelnetConstants.TELCMD_FIRST];
        }


        private string GetOption(int index)
        {
            string option;

            if (index <= TelnetConstants.TELOPT_TN3270E)
            {
                option = TelnetConstants.TelnetOptions[index];
            }
            else
            {
                option = index == TelnetConstants.TELOPT_TN3270E ? "TN3270E" : index.ToString();
            }
            return option;
        }


        private byte ParseControlCharacter(string s)
        {
            if (s == null || s.Length == 0)
                return 0;

            if (s.Length > 1)
            {
                if (s[0] != '^')
                    return 0;
                if (s[1] == '?')
                    return 0x7f;
                return (byte) (s[1] - '@');
            }
            return (byte) s[0];
        }


        /// <summary>
        ///     Send output in ANSI mode, including cooked-mode processing if appropriate.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="length"></param>
        private void Cook(byte[] buffer, int length)
        {
            if (!IsAnsi || (Keyboard.keyboardLock & KeyboardConstants.AwaitingFirst) != 0)
            {
                return;
            }
            if (linemode)
            {
                Trace.WriteLine("**BUGBUG** net_cookedout not implemented for line mode");
            }
            else
            {
                SendCookedOut(buffer, length);
            }
        }


        private void AnsiProcessString(string data)
        {
            int i;
            for (i = 0; i < data.Length; i++)
            {
                Ansi.ansi_process((byte) data[i]);
            }
        }


        private void CookedInitialized()
        {
            Console.WriteLine("--bugbug--cooked-init())");
        }

        #endregion Private Methods
    }
}