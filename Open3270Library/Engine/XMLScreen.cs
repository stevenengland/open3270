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
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using StEn.Open3270.Interfaces;

namespace StEn.Open3270.Engine
{
    /// <summary>
    ///     Do not use this class, use IXMLScreen instead...!
    /// </summary>
    [Serializable]
    public class XMLScreen : IXMLScreen, IDisposable
    {
        // CFC,Jr. 2008/07/11 initialize _CX, _CY to default values
        [XmlIgnore] private Guid _ScreenGuid;

        private string _stringValueCache;
        //
        [XmlElement("Field")] public XMLScreenField[] Field;

        [XmlIgnore] public string FileName;

        public bool Formatted;

        [XmlIgnore] public string Hash;

        private bool isDisposed;
        [XmlIgnore] public string MatchListIdentified;

        private char[] mScreenBuffer;
        private string[] mScreenRows;

        [XmlElement("Unformatted")] public XMLUnformattedScreen Unformatted;

        public Guid UniqueID;
        public string UserIdentified;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        //
        [XmlIgnore]
        public Guid ScreenGuid
        {
            get { return _ScreenGuid; }
        }

        public XMLScreenField[] Fields
        {
            get { return Field; }
        }


        public int CX { get; private set; } = 80;
        public int CY { get; private set; } = 25;

        public string Name
        {
            get { return MatchListIdentified; }
        }

        // helper functions
        public string GetText(int x, int y, int length)
        {
            return GetText(x + y*CX, length);
        }


        public string GetText(int offset, int length)
        {
            int i;
            var result = "";
            var maxlen = mScreenBuffer.Length;
            for (i = 0; i < length; i++)
            {
                if (i + offset < maxlen)
                    result += mScreenBuffer[i + offset];
            }
            return result;
        }

        public char GetCharAt(int offset)
        {
            return mScreenBuffer[offset];
        }

        public string GetRow(int row)
        {
            return mScreenRows[row];
        }

        public string Dump()
        {
            var audit = new StringAudit();
            Dump(audit);
            return audit.ToString();
        }

        public void Dump(IAudit stream)
        {
            int i;
            //stream.WriteLine("-----");
            //string tens = "   ", singles= "   "; // the quoted strings must be 3 spaces each, it gets lost in translation by codeplex...
            //for (i = 0; i < _CX; i += 10)
            //{
            //	tens += String.Format("{0,-10}", i / 10);
            //	singles += "0123456789";
            //}
            //stream.WriteLine(tens.Substring(0,3+_CX));
            //stream.WriteLine(singles.Substring(0, 3 + _CX));
            for (i = 0; i < CY; i++)
            {
                var line = GetText(0, i, CX);
                //string lr = ""+i+"       ";
                stream.WriteLine(line);
            }
            //stream.WriteLine("-----");
        }

        public string[] GetUnformatedStrings()
        {
            if (Unformatted != null && Unformatted.Text != null)
                return Unformatted.Text;
            return null;
        }


        public string GetXMLText()
        {
            return GetXMLText(true);
        }

        public string GetXMLText(bool useCache)
        {
            if (useCache == false || _stringValueCache == null)
            {
                //
                var serializer = new XmlSerializer(typeof(XMLScreen));
                //
                StringWriter fs = null;

                try
                {
                    var builder = new StringBuilder();
                    fs = new StringWriter(builder);
                    serializer.Serialize(fs, this);

                    fs.Close();

                    _stringValueCache = builder.ToString();
                }
                finally
                {
                    if (fs != null)
                        fs.Close();
                }
            }
            return _stringValueCache;
        }

        ~XMLScreen()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
                return;
            isDisposed = true;

            if (disposing)
            {
                Field = null;
                Unformatted = null;
                MatchListIdentified = null;
                mScreenBuffer = null;
                mScreenRows = null;
                Hash = null;
            }
        }

        public static XMLScreen load(Stream sr)
        {
            var serializer = new XmlSerializer(typeof(XMLScreen));
            //
            XMLScreen rules = null;

            var temp = serializer.Deserialize(sr);
            rules = (XMLScreen) temp;

            if (rules != null)
            {
                rules.FileName = null;

                rules.Render();
            }
            return rules;
        }

        public static XMLScreen load(string filename)
        {
            var serializer = new XmlSerializer(typeof(XMLScreen));
            //
            FileStream fs = null;
            XMLScreen rules = null;

            try
            {
                fs = new FileStream(filename, FileMode.Open);
                //XmlTextReader reader = new XmlTextReader(fs);

                rules = (XMLScreen) serializer.Deserialize(fs);
                rules.FileName = filename;
            }
            finally
            {
                if (fs != null)
                    fs.Close();
            }
            rules.Render();
            return rules;
        }

        public void Render()
        {
            //   TO DO: REWRITE THIS CLASS! and maybe the whole process of
            //          getting data from the lower classes, why convert buffers
            //          to XML just to convert them again in this Render method?
            //  ALSO: this conversion process does not take into account that
            //        the XML data that this class is converted from might
            //        contain more than _CX characters in a line, since the
            //        previous conversion to XML converts '<' to "&lt;" and
            //        the like, which will also cause shifts in character positions.
            //
            // Reset cache
            //
            _stringValueCache = null;
            //
            if (CX == 0 || CY == 0)
            {
                // TODO: Need to fix this
                CX = 132;
                CY = 43;
            }

            // CFCJr 2008/07/11
            if (CX < 80)
                CX = 80;
            if (CY < 25)
                CY = 25;


            // CFCJr 2008/07/11
            if (CX < 80)
                CX = 80;
            if (CY < 25)
                CY = 25;

            UserIdentified = null;
            MatchListIdentified = null;
            //
            // Render text image of screen
            //
            //
            mScreenBuffer = new char[CX*CY];
            mScreenRows = new string[CY];

            // CFCJr 2008/07/11
            // The following might be much faster:
            //
            //   string str = "".PadRight(_CX*_CY, ' ');
            //   mScreenBuffer = str.ToCharArray();
            //     ........do operations on mScreenBuffer to fill it......
            //   str = string.FromCharArray(mScreenBuffer);
            //   for (int r = 0; r < _CY; r++)
            //        mScreenRows[i] = str.SubString(r*_CY,_CX);
            //
            //  ie, fill mScreenBuffer with the data from Unformatted and Field, then
            //   create str (for the hash) and mScreenRows[]
            //   with the result.
            int i;
            for (i = 0; i < mScreenBuffer.Length; i++) // CFCJr. 2008.07/11 replase _CX*CY with mScreenBuffer.Length
            {
                mScreenBuffer[i] = ' ';
            }
            //

            int chindex;

            if (Field == null || Field.Length == 0 &&
                (Unformatted == null || Unformatted.Text == null))
            {
                if (Unformatted == null || Unformatted.Text == null)
                    Console.WriteLine("XMLScreen:Render: **BUGBUG** XMLScreen.Unformatted screen is blank");
                else
                    Console.WriteLine("XMLScreen:Render: **BUGBUG** XMLScreen.Field is blank");

                Console.Out.Flush();

                // CFCJr. Move logic for what is in mScreenRows to seperate if logic
                //        this will give unformatted results even if Field==null or 0 length
                //        and vise-a-versa.
                /*
				for (i=0; i<mScreenRows.Length; i++)
				{
					mScreenRows[i] = new String(' ',_CX); 
				}
                */
            }

            var blankRow = string.Empty;
            blankRow = blankRow.PadRight(CX, ' ');

            if (Unformatted == null || Unformatted.Text == null)
            {
                // CFCJr. 2008/07/11 initilize a blank row of _CX (80?) spaces

                for (i = 0; i < mScreenRows.Length; i++)
                {
                    //mScreenRows[i] = "                                                                                              ".Substring(0, _CX);
                    // CFCJr. 2008/07/11 replace above method of 80 spaces with following
                    mScreenRows[i] = blankRow;
                }
            }
            else
            {
                for (i = 0; i < Unformatted.Text.Length; i++)
                {
                    var text = Unformatted.Text[i];

                    // CFCJr, make sure text is not null

                    if (string.IsNullOrEmpty(text))
                        text = string.Empty;

                    // CFCJr, replace "&lt;" with '<'
                    text = text.Replace("&lt;", "<");

                    // CFCJr, Remove this loop to pad text
                    // and use text.PadRight later.
                    // This will help in not processing more
                    // characters than necessary into mScreenBuffer
                    // below

                    //while (text.Length < _CX)
                    //	text+=" ";

                    //
                    int p;
                    //for (p=0; p<_CX; p++)
                    for (p = 0; p < text.Length; p++) // CFC,Jr.
                    {
                        if (text[p] < 32 || text[p] > 126)
                            text = text.Replace(text[p], ' ');
                    }
                    //
                    //for (chindex=0; chindex<Unformatted.Text[i].Length; chindex++)
                    // CFCJr, 2008/07/11 use text.length instead of Unformatted.Text[i].Length
                    // since we only pad text with 80 chars but if Unformatted.Text[i]
                    // contains XML codes (ie, "&lt;") then it could be longer than
                    // 80 chars (hence, longer than text). 
                    // Also, I replace "&lt;" above with "<".

                    for (chindex = 0; chindex < text.Length; chindex++)
                    {
                        // CFCJr, calculate mScreenBuffer index only once
                        var bufNdx = chindex + i*CX;

                        if (bufNdx < mScreenBuffer.Length)
                        {
                            mScreenBuffer[bufNdx] = text[chindex];
                        }
                    }
                    // CFCJr, make sure we don't overflow the index of mScreenRows
                    //        since i is based on the dimensions of Unformatted.Text
                    //        instead of mScreenRows.Length
                    if (i < mScreenRows.Length)
                    {
                        text = text.PadRight(CX, ' '); // CFCJr. 2008/07/11 use PadRight instead of loop above
                        mScreenRows[i] = text;
                    }
                }
            }

            // CFCJr, lets make sure we have _CY rows in mScreenRows here
            // since we use Unformated.Text.Length for loop above which
            // could possibly be less than _CY.

            for (i = 0; i < mScreenRows.Length; i++)
                if (string.IsNullOrEmpty(mScreenRows[i]))
                    mScreenRows[i] = blankRow;

            //==============
            // Now process the Field (s)

            if (Field != null && Field.Length > 0)
            {
                //
                // Now superimpose the formatted fields on the unformatted base
                //
                for (i = 0; i < Field.Length; i++)
                {
                    var field = Field[i];
                    if (field.Text != null)
                    {
                        for (chindex = 0; chindex < field.Text.Length; chindex++)
                        {
                            var ch = field.Text[chindex];
                            if (ch < 32 || ch > 126)
                                ch = ' ';
                            // CFCJr, 2008/07/11 make sure we don't get out of bounds 
                            //        of the array m_ScreenBuffer.
                            var bufNdx = chindex + field.Location.left + field.Location.top*CX;
                            if (bufNdx >= 0 && bufNdx < mScreenBuffer.Length)
                                mScreenBuffer[bufNdx] = ch;
                        }
                    }
                }

                // CFCJr, 2008/07/11
                // SOMETHING needs to be done in this method to speed things up.
                // Above, in the processing of the Unformatted.Text, Render()
                // goes to the trouble of loading up mScreenBuffer and mScreenRows.
                // now here, we replace mScreenRows with the contents of mScreenBuffer.
                // Maybe, we should only load mScreenBuffer and then at the end
                // of Render(), load mScreenRows from it (or vise-a-vera).
                // WE COULD ALSO use
                //   mScreenRows[i] = string.FromCharArraySubset(mScreenBuffer, i*_CX, _CX);
                //  inside this loop.

                for (i = 0; i < CY; i++)
                {
                    var temp = string.Empty; // CFCJr, 2008/07/11 replace ""

                    for (var j = 0; j < CX; j++)
                    {
                        temp += mScreenBuffer[i*CX + j];
                    }
                    mScreenRows[i] = temp;
                }
            }

            // now calculate our screen's hash
            //
            // CFCJr, dang, now we're going to copy the data again,
            //   this time into a long string.....(see comments at top of Render())
            //   I bet there's a easy way to redo this class so that we use just
            //   one buffer (string or char[]) instead of all these buffers.
            // WE COULD also use
            //   string hashStr = string.FromCharArray(mScreenBuffer);
            // instead of converting mScreenRows to StringBuilder 
            // and then converting it to a string.

            var hash = (HashAlgorithm) CryptoConfig.CreateFromName("MD5");
            var builder = new StringBuilder();
            for (i = 0; i < mScreenRows.Length; i++)
            {
                builder.Append(mScreenRows[i]);
            }
            var myHash = hash.ComputeHash(new UnicodeEncoding().GetBytes(builder.ToString()));
            Hash = BitConverter.ToString(myHash);
            _ScreenGuid = Guid.NewGuid();
        }

        public static XMLScreen LoadFromString(string text)
        {
            var serializer = new XmlSerializer(typeof(XMLScreen));
            //
            StringReader fs = null;
            XMLScreen rules = null;

            try
            {
                fs = new StringReader(text);
                //XmlTextReader reader = new XmlTextReader(fs);

                rules = (XMLScreen) serializer.Deserialize(fs); //reader);
                rules.FileName = null;
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception " + e.Message + " reading document. saved as c:\\dump.xml");
                var sw = File.CreateText("c:\\dump.xml");
                sw.WriteLine(text);
                sw.Close();
                throw;
            }
            finally
            {
                if (fs != null)
                    fs.Close();
            }
            rules.Render();
            rules._stringValueCache = text;
            return rules;
        }

        public void save(string filename)
        {
            var serializer = new XmlSerializer(typeof(XMLScreen));
            //
            // now expand back to xml
            var fsw = new StreamWriter(filename, false, Encoding.Unicode);
            serializer.Serialize(fsw, this);
            fsw.Close();
        }
    }

    //
    // 
}