using System;

namespace StEn.Open3270.CommFramework
{
    internal class MessageHeader
    {
        public const int ConstantForMagicNumber = 0x0FCDEEDC;
        public const int MessageHeaderSize = 12;
        public int uMagicNumber;
        public int uMessageSize;
        public int uVersion;

        public MessageHeader()
        {
            uMagicNumber = ConstantForMagicNumber;
            uVersion = 1;
            uMessageSize = 0;
        }

        public MessageHeader(byte[] data)
        {
            if (data.Length != MessageHeaderSize)
                throw new ApplicationException(
                    "INTERNAL ERROR - MessageHeader constructor passed buffer of invalid length");
            var offset = 0;
            offset = ByteHandler.FromBytes(data, offset, out uMagicNumber);
            offset = ByteHandler.FromBytes(data, offset, out uVersion);
            offset = ByteHandler.FromBytes(data, offset, out uMessageSize);

            if (offset != 12)
                throw new ApplicationException("FATAL INTERNAL ERROR - MessageHeader is not 12 bytes long");
            if (uMagicNumber != ConstantForMagicNumber)
                throw new ApplicationException("FATAL COMMUNICATIONS ERROR - MessageHeader Magic number is invalid");
        }


        public byte[] ToByte()
        {
            var result = new byte[MessageHeaderSize];
            var offset = 0;

            offset = ByteHandler.ToBytes(result, offset, uMagicNumber);
            offset = ByteHandler.ToBytes(result, offset, uVersion);
            offset = ByteHandler.ToBytes(result, offset, uMessageSize);
            return result;
        }
    }
}