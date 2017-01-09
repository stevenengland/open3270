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
using System.Linq;
using System.Reflection;
using System.Threading;
using StEn.Open3270.CommFramework;
using StEn.Open3270.Exceptions;
using StEn.Open3270.Interfaces;
using StEn.Open3270.TN3270E;

namespace StEn.Open3270.Engine
{
    /// <summary>
    ///     Summary description for TNEmulator.
    /// </summary>
    public class TNEmulator : IDisposable
    {
        #region Private Variables and Objects

        private static bool firstTime = true;

        private readonly MySemaphore semaphore = new MySemaphore(0, 9999);

        private IXMLScreen currentScreenXML; // don't access me directly, use helper
        private string mScreenName;
        private TN3270API currentConnection;

        #endregion

        #region Event Handlers

        /// <summary>
        ///     Event fired when the host disconnects. Note - this must be set before you connect to the host.
        /// </summary>
        public event OnDisconnectDelegate Disconnected;

        public event EventHandler CursorLocationChanged;
        private OnDisconnectDelegate apiOnDisconnectDelegate;

        #endregion

        #region Constructors / Destructors

        public TNEmulator()
        {
            currentScreenXML = null;
            currentConnection = null;
            Config = new ConnectionConfig();
        }

        ~TNEmulator()
        {
            Dispose(false);
        }

        #endregion

        #region Properties

        /// <summary>
        ///     Returns whether or not this session is connected to the mainframe.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                if (currentConnection == null)
                    return false;
                return currentConnection.IsConnected;
            }
        }

        /// <summary>
        ///     Gets or sets the ojbect state.
        /// </summary>
        public object ObjectState { get; set; }

        /// <summary>
        ///     Returns whether or not the disposed action has been performed on this object.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        ///     Returns the reason why the session has been disconnected.
        /// </summary>
        public string DisconnectReason
        {
            get
            {
                lock (this)
                {
                    if (currentConnection != null)
                        return currentConnection.DisconnectReason;
                }
                return string.Empty;
            }
        }

        /// <summary>
        ///     Returns zero if the keyboard is currently locked (inhibited)
        ///     non-zero otherwise
        /// </summary>
        public int KeyboardLocked
        {
            get
            {
                if (currentConnection == null)
                    throw new TNHostException("TNEmulator is not connected",
                        "There is no currently open TN3270 connection", null);
                return currentConnection.KeyboardLock;
            }
        }

        /// <summary>
        ///     Returns the zero based X coordinate of the cursor
        /// </summary>
        public int CursorX
        {
            get
            {
                if (currentConnection == null)
                    throw new TNHostException("TNEmulator is not connected",
                        "There is no currently open TN3270 connection", null);
                return currentConnection.CursorX;
            }
        }

        /// <summary>
        ///     Returns the zero based Y coordinate of the cursor
        /// </summary>
        public int CursorY
        {
            get
            {
                if (currentConnection == null)
                    throw new TNHostException("TNEmulator is not connected",
                        "There is no currently open TN3270 connection", null);
                return currentConnection.CursorY;
            }
        }

        /// <summary>
        ///     Returns the IP address of the mainframe.
        /// </summary>
        public string LocalIP { get; private set; } = string.Empty;

        /// <summary>
        ///     Returns the internal configuration object for this connection
        /// </summary>
        public ConnectionConfig Config { get; private set; }

        /// <summary>
        ///     Debug flag - setting this to true turns on much more debugging output on the
        ///     Audit output
        /// </summary>
        public bool Debug { get; set; } = false;

        /// <summary>
        ///     Set this flag to true to enable SSL connections. False otherwise
        /// </summary>
        public bool UseSSL { get; set; } = false;

        /// <summary>
        ///     Returns the current screen XML
        /// </summary>
        public IXMLScreen CurrentScreenXML
        {
            get
            {
                if (currentScreenXML == null)
                {
                    if (Audit != null && Debug)
                    {
                        Audit.WriteLine("CurrentScreenXML reloading by calling GetScreenAsXML()");
                        currentScreenXML = GetScreenAsXML();
                        currentScreenXML.Dump(Audit);
                    }
                    else
                    {
                        //
                        currentScreenXML = GetScreenAsXML();
                    }
                }
                //
                return currentScreenXML;
            }
        }

        /// <summary>
        ///     Auditing interface
        /// </summary>
        public IAudit Audit { get; set; } = null;

        #endregion

        #region Public Methods

        /// <summary>
        ///     Disposes of this emulator object.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Sends the specified key stroke to the emulator.
        /// </summary>
        /// <param name="waitForScreenToUpdate"></param>
        /// <param name="key"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public bool SendKey(bool waitForScreenToUpdate, TnKey key, int timeout)
        {
            var triggerSubmit = false;
            var success = false;
            string command;

            //This is only used as a parameter for other methods when we're using function keys.
            //e.g. F1 yields a command of "PF" and a functionInteger of 1.
            var functionInteger = -1;


            if (Audit != null && Debug)
            {
                Audit.WriteLine("SendKeyFromText(" + waitForScreenToUpdate + ", \"" + key + "\", " + timeout + ")");
            }

            if (currentConnection == null)
            {
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            }


            //Get the command name and accompanying int parameter, if applicable
            if (Constants.FunctionKeys.Contains(key))
            {
                command = "PF";
                functionInteger = Constants.FunctionKeyIntLUT[key];
            }
            else if (Constants.AKeys.Contains(key))
            {
                command = "PA";
                functionInteger = Constants.FunctionKeyIntLUT[key];
            }
            else
            {
                command = key.ToString();
            }

            //Should this action be followed by a submit?
            triggerSubmit = Config.SubmitAllKeyboardCommands || currentConnection.KeyboardCommandCausesSubmit(command);

            if (triggerSubmit)
            {
                lock (this)
                {
                    DisposeOfCurrentScreenXML();
                    currentScreenXML = null;

                    if (Audit != null && Debug)
                    {
                        Audit.WriteLine("mre.Reset. Count was " + semaphore.Count);
                    }

                    // Clear to initial count (0)
                    semaphore.Reset();
                }
            }

            success = currentConnection.ExecuteAction(triggerSubmit, command, functionInteger);


            if (Audit != null && Debug)
            {
                Audit.WriteLine("SendKeyFromText - submit = " + triggerSubmit + " ok=" + success);
            }

            if (triggerSubmit && success)
            {
                // Wait for a valid screen to appear
                if (waitForScreenToUpdate)
                {
                    success = Refresh(true, timeout);
                }
                else
                {
                    success = true;
                }
            }

            return success;
        }

        /// <summary>
        ///     Waits until the keyboard state becomes unlocked.
        /// </summary>
        /// <param name="timeoutms"></param>
        public void WaitTillKeyboardUnlocked(int timeoutms)
        {
            var dttm = DateTime.Now.AddMilliseconds(timeoutms);

            while (KeyboardLocked != 0 && DateTime.Now < dttm)
            {
                Thread.Sleep(10); // Wait 1/100th of a second
            }
        }

        /// <summary>
        ///     Refresh the current screen.  If timeout > 0, it will wait for
        ///     this number of milliseconds.
        ///     If waitForValidScreen is true, it will wait for a valid screen, otherwise it
        ///     will return immediately that any screen data is visible
        /// </summary>
        /// <param name="waitForValidScreen"></param>
        /// <param name="timeoutMS">The time to wait in ms</param>
        /// <returns></returns>
        public bool Refresh(bool waitForValidScreen, int timeoutMS)
        {
            var start = DateTime.Now.Ticks/(10*1000);
            var end = start + timeoutMS;

            if (currentConnection == null)
            {
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            }

            if (Audit != null && Debug)
            {
                Audit.WriteLine("Refresh::Refresh(" + waitForValidScreen + ", " + timeoutMS + "). FastScreenMode=" +
                                Config.FastScreenMode);
            }

            do
            {
                if (waitForValidScreen)
                {
                    var run = false;
                    var timeout = 0;
                    do
                    {
                        timeout = (int) (end - DateTime.Now.Ticks/10000);
                        if (timeout > 0)
                        {
                            if (Audit != null && Debug)
                            {
                                Audit.WriteLine("Refresh::Acquire(" + timeout +
                                                " milliseconds). unsafe Count is currently " + semaphore.Count);
                            }

                            run = semaphore.Acquire(Math.Min(timeout, 1000));

                            if (!IsConnected)
                            {
                                throw new TNHostException("The TN3270 connection was lost",
                                    currentConnection.DisconnectReason, null);
                            }

                            if (run)
                            {
                                if (Audit != null && Debug)
                                {
                                    Audit.WriteLine("Refresh::return true at line 279");
                                }
                                return true;
                            }
                        }
                    } while (!run && timeout > 0);
                    if (Audit != null && Debug)
                        Audit.WriteLine("Refresh::Timeout or acquire failed. run= " + run + " timeout=" + timeout);
                }

                if (Config.FastScreenMode || KeyboardLocked == 0)
                {
                    // Store screen in screen database and identify it
                    DisposeOfCurrentScreenXML();

                    // Force a refresh
                    currentScreenXML = null;
                    if (Audit != null && Debug)
                    {
                        Audit.WriteLine(
                            "Refresh::Timeout, but since keyboard is not locked or fastmode=true, return true anyway");
                    }

                    return true;
                }
                Thread.Sleep(10);
            } while (DateTime.Now.Ticks/10000 < end);

            if (Audit != null)
            {
                Audit.WriteLine("Refresh::Timed out (2) waiting for a valid screen. Timeout was " + timeoutMS);
            }

            if (Config.FastScreenMode == false && Config.ThrowExceptionOnLockedScreen && KeyboardLocked != 0)
            {
                throw new ApplicationException(
                    "Timeout waiting for new screen with keyboard inhibit false - screen present with keyboard inhibit. Turn off the configuration option 'ThrowExceptionOnLockedScreen' to turn off this exception. Timeout was " +
                    timeoutMS + " and keyboard inhibit is " + KeyboardLocked);
            }

            if (Config.IdentificationEngineOn)
            {
                throw new TNIdentificationException(mScreenName, GetScreenAsXML());
            }
            return false;
        }

        /// <summary>
        ///     Dump fields to the current audit output
        /// </summary>
        public void ShowFields()
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);

            if (Audit != null)
            {
                Audit.WriteLine("-------------------dump screen data -----------------");
                currentConnection.ExecuteAction(false, "Fields");
                Audit.WriteLine("" + currentConnection.GetAllStringData(false));
                CurrentScreenXML.Dump(Audit);
                Audit.WriteLine("-------------------dump screen end -----------------");
            }
            else
                throw new ApplicationException("ShowFields requires an active 'Audit' connection on the emulator");
        }

        /// <summary>
        ///     Retrieves text at the specified location on the screen
        /// </summary>
        /// <param name="x">Column</param>
        /// <param name="y">Row</param>
        /// <param name="length">Length of the text to be returned</param>
        /// <returns></returns>
        public string GetText(int x, int y, int length)
        {
            return CurrentScreenXML.GetText(x, y, length);
        }

        /// <summary>
        ///     Sends a string starting at the indicated screen position
        /// </summary>
        /// <param name="text">The text to send</param>
        /// <param name="x">Column</param>
        /// <param name="y">Row</param>
        /// <returns>True for success</returns>
        public bool SetText(string text, int x, int y)
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);

            SetCursor(x, y);

            return SetText(text);
        }

        /// <summary>
        ///     Sents the specified string to the emulator at it's current position.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public bool SetText(string text)
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);

            lock (this)
            {
                DisposeOfCurrentScreenXML();
                currentScreenXML = null;
            }
            return currentConnection.ExecuteAction(false, "String", text);
        }

        /// <summary>
        ///     Returns after new screen data has stopped flowing from the host for screenCheckInterval time.
        /// </summary>
        /// <param name="screenCheckInterval">
        ///     The amount of time between screen data comparisons in milliseconds.
        ///     It's probably impractical for this to be much less than 100 ms.
        /// </param>
        /// <param name="finalTimeout">The absolute longest time we should wait before the method should time out</param>
        /// <returns>True if data ceased, and false if the operation timed out. </returns>
        public bool WaitForHostSettle(int screenCheckInterval, int finalTimeout)
        {
            var success = true;
            //Accumulator for total poll time.  This is less accurate than using an interrupt or DateTime deltas, but it's light weight.
            var elapsed = 0;

            //This is low tech and slow, but simple to implement right now.
            while (!Refresh(true, screenCheckInterval))
            {
                if (elapsed > finalTimeout)
                {
                    success = false;
                    break;
                }
                elapsed += screenCheckInterval;
            }

            return success;
        }

        /// <summary>
        ///     Returns the last asynchronous error that occured internally
        /// </summary>
        /// <returns></returns>
        public string GetLastError()
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            return currentConnection.LastException;
        }

        /// <summary>
        ///     Set field value.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="text"></param>
        public void SetField(int index, string text)
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            if (index == -1001)
            {
                switch (text)
                {
                    case "showparseerror":
                        currentConnection.ShowParseError = true;
                        return;
                    default:
                        return;
                }
            }
            currentConnection.ExecuteAction(false, "FieldSet", index, text);
            DisposeOfCurrentScreenXML();
            currentScreenXML = null;
        }

        public void SetField(FieldInfo field, string text)
        {
            //this.currentConnection.ExecuteAction()
            throw new NotImplementedException();
        }

        /// <summary>
        ///     Set the cursor position on the screen
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        public void SetCursor(int x, int y)
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            //currentConnection.ExecuteAction("MoveCursor", x,y);
            currentConnection.MoveCursor(CursorOp.Exact, x, y);
        }

        /// <summary>
        ///     Connects to the mainframe.
        /// </summary>
        public void Connect()
        {
            Connect(Config.HostName,
                Config.HostPort,
                Config.HostLU);
        }

        /// <summary>
        ///     Connects to host using a local IP endpoint
        ///     <remarks>
        ///         Added by CFCJR on Feb/29/2008
        ///         if a source IP is given then use it for the local IP
        ///     </remarks>
        /// </summary>
        /// <param name="localIP"></param>
        /// <param name="host"></param>
        /// <param name="port"></param>
        public void Connect(string localIP, string host, int port)
        {
            LocalIP = localIP;
            Connect(host, port, string.Empty);
        }

        /// <summary>
        ///     Connect to TN3270 server using the connection details specified.
        /// </summary>
        /// <remarks>
        ///     You should set the Audit property to an instance of an object that implements
        ///     the IAudit interface if you want to see any debugging information from this function
        ///     call.
        /// </remarks>
        /// <param name="host">Host name or IP address. Mandatory</param>
        /// <param name="port">TCP/IP port to connect to (default TN3270 port is 23)</param>
        /// <param name="lu">TN3270E LU to connect to. Specify null for no LU.</param>
        public void Connect(string host, int port, string lu)
        {
            if (currentConnection != null)
            {
                currentConnection.Disconnect();
                currentConnection.CursorLocationChanged -= currentConnection_CursorLocationChanged;
            }

            try
            {
                semaphore.Reset();

                currentConnection = null;
                currentConnection = new TN3270API();
                currentConnection.Debug = Debug;
                currentConnection.RunScriptRequested += currentConnection_RunScriptEvent;
                currentConnection.CursorLocationChanged += currentConnection_CursorLocationChanged;
                currentConnection.Disconnected += apiOnDisconnectDelegate;

                apiOnDisconnectDelegate = currentConnection_OnDisconnect;

                //
                // Debug out our current state
                //
                if (Audit != null)
                {
                    Audit.WriteLine("Open3270 emulator version " +
                                    Assembly.GetAssembly(typeof(TNEmulator)).GetName().Version);
                    Audit.WriteLine("(c) 2004-2006 Mike Warriner (mikewarriner@gmail.com). All rights reserved");
                    Audit.WriteLine("");
                    Audit.WriteLine("This is free software; you can redistribute it and/or modify it");
                    Audit.WriteLine("under the terms of the GNU Lesser General Public License as");
                    Audit.WriteLine("published by the Free Software Foundation; either version 2.1 of");
                    Audit.WriteLine("the License, or (at your option) any later version.");
                    Audit.WriteLine("");
                    Audit.WriteLine("This software is distributed in the hope that it will be useful,");
                    Audit.WriteLine("but WITHOUT ANY WARRANTY; without even the implied warranty of");
                    Audit.WriteLine("MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU");
                    Audit.WriteLine("Lesser General Public License for more details.");
                    Audit.WriteLine("");
                    Audit.WriteLine("You should have received a copy of the GNU Lesser General Public");
                    Audit.WriteLine("License along with this software; if not, write to the Free");
                    Audit.WriteLine("Software Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA");
                    Audit.WriteLine("02110-1301 USA, or see the FSF site: http://www.fsf.org.");
                    Audit.WriteLine("");

                    if (firstTime)
                    {
                        firstTime = false;
                    }
                    if (Debug)
                    {
                        Config.Dump(Audit);
                        Audit.WriteLine("Connect to host \"" + host + "\"");
                        Audit.WriteLine("           port \"" + port + "\"");
                        Audit.WriteLine("           LU   \"" + lu + "\"");
                        Audit.WriteLine("     Local IP   \"" + LocalIP + "\"");
                    }
                }

                currentConnection.UseSSL = UseSSL;


                /// Modified CFCJR Feb/29/2008 to support local IP endpoint
                if (!string.IsNullOrEmpty(LocalIP))
                {
                    currentConnection.Connect(Audit, LocalIP, host, port, Config);
                }
                else
                {
                    currentConnection.Connect(Audit, host, port, lu, Config);
                }

                currentConnection.WaitForConnect(-1);
                DisposeOfCurrentScreenXML();
                currentScreenXML = null;
                // Force refresh 
                // GetScreenAsXML();
            }
            catch (Exception)
            {
                currentConnection = null;
                throw;
            }

            // These don't close the connection
            try
            {
                mScreenName = "Start";
                Refresh(true, 10000);
                if (Audit != null && Debug) Audit.WriteLine("Debug::Connected");
                //mScreenProcessor.Update_Screen(currentScreenXML, true);
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        ///     Closes the current connection to the mainframe.
        /// </summary>
        public void Close()
        {
            if (currentConnection != null)
            {
                currentConnection.Disconnect();
                currentConnection = null;
            }
        }

        /// <summary>
        ///     Waits for the specified text to appear at the specified location.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="text"></param>
        /// <param name="timeoutMS"></param>
        /// <returns></returns>
        public bool WaitForText(int x, int y, string text, int timeoutMS)
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            var start = DateTime.Now.Ticks;
            //bool ok = false;
            if (Config.AlwaysRefreshWhenWaiting)
            {
                lock (this)
                {
                    DisposeOfCurrentScreenXML();
                    currentScreenXML = null;
                }
            }
            do
            {
                if (CurrentScreenXML != null)
                {
                    var screenText = CurrentScreenXML.GetText(x, y, text.Length);
                    if (screenText == text)
                    {
                        if (Audit != null)
                            Audit.WriteLine("WaitForText('" + text + "') Found!");
                        return true;
                    }
                }
                //
                if (timeoutMS == 0)
                {
                    if (Audit != null)
                        Audit.WriteLine("WaitForText('" + text + "') Not found");
                    return false;
                }
                //
                Thread.Sleep(100);
                if (Config.AlwaysRefreshWhenWaiting)
                {
                    lock (this)
                    {
                        DisposeOfCurrentScreenXML();
                        currentScreenXML = null;
                    }
                }
                Refresh(true, 1000);
            } while ((DateTime.Now.Ticks - start)/10000 < timeoutMS);
            //
            if (Audit != null)
                Audit.WriteLine("WaitForText('" + text + "') Timed out");
            return false;
        }

        /// <summary>
        ///     Dump current screen to the current audit output
        /// </summary>
        public void Dump()
        {
            lock (this)
            {
                if (Audit != null)
                    CurrentScreenXML.Dump(Audit);
            }
        }

        /// <summary>
        ///     Refreshes the connection to the mainframe.
        /// </summary>
        public void Refresh()
        {
            lock (this)
            {
                DisposeOfCurrentScreenXML();
                currentScreenXML = null;
            }
        }

        #endregion

        #region Protected / Internal Methods

        protected void DisposeOfCurrentScreenXML()
        {
            if (currentScreenXML != null)
            {
                var disposeXML = currentScreenXML as IDisposable;
                if (disposeXML != null)
                    disposeXML.Dispose();
                currentScreenXML = null;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (this)
            {
                if (IsDisposed)
                    return;
                IsDisposed = true;

                if (Audit != null)
                    Audit.WriteLine("TNEmulator.Dispose(" + IsDisposed + ")");

                if (disposing)
                {
                    //----------------------------
                    // release managed resources

                    if (currentConnection != null)
                    {
                        if (Audit != null)
                            Audit.WriteLine("TNEmulator.Dispose() Disposing of currentConnection");
                        try
                        {
                            currentConnection.Disconnect();
                            currentConnection.CursorLocationChanged -= currentConnection_CursorLocationChanged;

                            if (apiOnDisconnectDelegate != null)
                                currentConnection.Disconnected -= apiOnDisconnectDelegate;

                            currentConnection.Dispose();
                        }
                        catch
                        {
                            if (Audit != null)
                                Audit.WriteLine("TNEmulator.Dispose() Exception during currentConnection.Dispose");
                        }
                        currentConnection = null;
                    }

                    Disconnected = null;

                    if (Audit != null)
                        Audit.WriteLine("TNEmulator.Dispose() Disposing of currentScreenXML");

                    DisposeOfCurrentScreenXML();

                    if (ObjectState != null)
                    {
                        ObjectState = null;
                    }
                    if (Config != null)
                    {
                        Config = null;
                    }
                    if (mScreenName != null)
                    {
                        mScreenName = null;
                    }
                }

                //------------------------------
                // release unmanaged resources
            }
        }

        protected virtual void OnCursorLocationChanged(EventArgs args)
        {
            if (CursorLocationChanged != null)
            {
                CursorLocationChanged(this, args);
            }
        }

        /// <summary>
        ///     Get the current screen as an XMLScreen data structure
        /// </summary>
        /// <returns></returns>
        internal IXMLScreen GetScreenAsXML()
        {
            DisposeOfCurrentScreenXML();

            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            if (currentConnection.ExecuteAction(false, "DumpXML"))
            {
                //
                return XMLScreen.LoadFromString(currentConnection.GetAllStringData(false));
            }
            return null;
        }

        #endregion

        #region Private Methods

        private void currentConnection_CursorLocationChanged(object sender, EventArgs e)
        {
            OnCursorLocationChanged(e);
        }

        private void currentConnection_RunScriptEvent(string where)
        {
            lock (this)
            {
                DisposeOfCurrentScreenXML();

                if (Audit != null && Debug) Audit.WriteLine("mre.Release(1) from location " + where);
                semaphore.Release(1);
            }
        }

        /// <summary>
        ///     Wait for some text to appear at the specified location
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="text"></param>
        /// <param name="timeoutMS"></param>
        /// <returns></returns>
        private void currentConnection_OnDisconnect(TNEmulator where, string Reason)
        {
            if (Disconnected != null)
                Disconnected(this, Reason);
        }

        #endregion

        #region Deprecated Methods

        [Obsolete(
            "This method has been deprecated.  Please use SendKey instead. This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public bool SendKeyFromText(bool waitForScreenToUpdate, string text)
        {
            return SendKeyFromText(waitForScreenToUpdate, text, Config.DefaultTimeout);
        }

        [Obsolete(
            "This method has been deprecated.  Please use SendKey instead.  This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public bool SendKeyFromText(bool waitForScreenToUpdate, string text, int timeout)
        {
            var submit = false;
            var success = false;

            if (Audit != null && Debug)
            {
                Audit.WriteLine("SendKeyFromText(" + waitForScreenToUpdate + ", \"" + text + "\", " + timeout + ")");
            }

            if (currentConnection == null)
            {
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            }

            if (text.Length < 2)
            {
                // No keys are less than 2 characters.
                return false;
            }


            if (Config.SubmitAllKeyboardCommands)
            {
                submit = true;
            }
            else
            {
                if (text.Substring(0, 2) == "PF")
                {
                    submit = currentConnection.KeyboardCommandCausesSubmit("PF");
                }
                else if (text.Substring(0, 2) == "PA")
                {
                    submit = currentConnection.KeyboardCommandCausesSubmit("PA");
                }
                else
                {
                    submit = currentConnection.KeyboardCommandCausesSubmit(text);
                }
            }


            if (submit)
            {
                lock (this)
                {
                    DisposeOfCurrentScreenXML();
                    currentScreenXML = null;

                    if (Audit != null && Debug) Audit.WriteLine("mre.Reset. Count was " + semaphore.Count);
                    {
                        // Clear to initial count (0)
                        semaphore.Reset();
                    }
                }
            }


            if (text.Substring(0, 2) == "PF")
            {
                success = currentConnection.ExecuteAction(submit, "PF", Convert.ToInt32(text.Substring(2, 2)));
            }
            else if (text.Substring(0, 2) == "PA")
            {
                success = currentConnection.ExecuteAction(submit, "PA", Convert.ToInt32(text.Substring(2, 2)));
            }
            else
            {
                success = currentConnection.ExecuteAction(submit, text);
            }

            if (Audit != null && Debug)
            {
                Audit.WriteLine("SendKeyFromText - submit = " + submit + " ok=" + success);
            }

            if (submit && success)
            {
                // Wait for a valid screen to appear
                if (waitForScreenToUpdate)
                {
                    return Refresh(true, timeout);
                }
                return true;
            }
            return success;
        }

        [Obsolete(
            "This method has been deprecated.  Please use SetText instead.  This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public bool SendText(string text)
        {
            if (currentConnection == null)
                throw new TNHostException("TNEmulator is not connected", "There is no currently open TN3270 connection",
                    null);
            lock (this)
            {
                DisposeOfCurrentScreenXML();
                currentScreenXML = null;
            }
            return currentConnection.ExecuteAction(false, "String", text);
        }

        [Obsolete(
            "This method has been deprecated.  Please use SetText instead.  This method is only included for backwards compatibiity and might not exist in future releases."
            )]
        public bool PutText(string text, int x, int y)
        {
            return SetText(text, x, y);
        }

        #endregion
    }
}