using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System;
using System.Net;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Net.Sockets;

#nullable enable

namespace SwiftUtils
{
    public class SwiftCommand
    {
        public enum CommandType : int
        {
            NONE = -1,
            CONNECTED = 0,
            KEEP_ALIVE = 1,
            KICK = 200,
            DISCONNECT = 201,
            SUCCESS = 400,
            FAILURE = 401
        }

        public CommandType Type { set; get; } = CommandType.NONE;
        public string Message { set; get; } = "";

        public override string ToString()
        {
            return $"SWIFTCMD-{(int)Type}|{Message}";
        }

        public static SwiftCommand? FromString(string str)
        {
            if (string.IsNullOrEmpty(str))
                return null;

            int idx = str.IndexOf("SWIFTCMD-");
            if (idx < 0)
                return null;
            try
            {
                string[] spl = str.Substring(idx + 9).Split("|");
                if (spl.Length != 2)
                    return null;
                return new SwiftCommand((CommandType)Convert.ToInt32(spl[0]), spl[1].Trim());
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string GenerateComamnd(CommandType type, string message)
        {
            return new SwiftCommand(type, message).ToString();
        }

        private SwiftCommand(CommandType type, string message)
        {
            Type = type;
            Message = message;
        }
    }

    public class ReceiveJob
    {
        private long _ExpectedLength = 0;
        private long _RecvLength = 0;
        private long _RecvParts = 0;
        private long _LastRecvLength = 0;
        private FileStream? _Stream = null;
        private DateTime _NextUpdate = DateTime.Now;

        public string ID { private set; get; } = "";
        public string FilePath { private set; get; } = "";
        public bool IsCompleted { private set; get; } = false;
        public bool IsSuccess { private set; get; } = false;
        public double BytesPerSecond { private set; get; } = 0;
        public DateTime? StartTime { private set; get; } = null;
        public DateTime? EndTime { private set; get; } = null;

        public TimeSpan ElapsedTime
        {
            get
            {
                if (StartTime == null)
                    return TimeSpan.FromSeconds(0);
                if (EndTime == null)
                    return DateTime.Now - StartTime ?? TimeSpan.FromSeconds(0);
                return EndTime - StartTime ?? TimeSpan.FromSeconds(0);
            }
        }

        public double Percentage
        {
            get
            {
                if (_ExpectedLength == 0)
                    return 0;
                return Math.Min(((double)_RecvLength / (double)_ExpectedLength) * 100, 100);
            }
        }

        public event EventHandler<EventArgs>? OnJobCompleted;
        public event EventHandler<EventArgs>? OnJobUpdate;

        public ReceiveJob(string filePath = "")
        {
            FilePath = filePath;
        }

        public bool Process(byte[] packet)
        {
            return Process(SocketUtil.ENCODING.GetString(packet));
        }

        public int Process(IEnumerable<string> packets)
        {
            int ret = 0;
            foreach (string packet in packets)
            {
                if (Process(packet))
                    ret++;
            }
            return ret;
        }

        public bool Process(string packet, Socket? socket = null, byte[]? passHash = null)
        {
            if (IsCompleted)
                return false;
            string[] spl = packet.Split("^^");
            if (spl.Length <= 1)
                return false;
            try
            {
                switch (spl[0].Trim())
                {
                    case "SWHD":
                        {
                            if (!string.IsNullOrEmpty(ID) || spl.Length != 4)
                                return false;
                            string fP = string.IsNullOrEmpty(FilePath) ? spl[1].Trim() : FilePath;
                            string id = spl[2].Trim();
                            long len = Convert.ToInt64(spl[3]);

                            // If 'fP' has parent directories, create them.
                            if (fP.EndsWith('/') || fP.EndsWith('\\'))
                            {
                                // We cannot write to a directory, generate a random file name.
                                fP += Guid.NewGuid().ToString();
                            }
                            string[] dirSpl = fP.Split('/');
                            if (dirSpl.Length <= 1)
                                dirSpl = fP.Split('\\');
                            for (int i = 0; i < dirSpl.Length - 1; i++)
                            {
                                if (!string.IsNullOrEmpty(dirSpl[i]))
                                    Directory.CreateDirectory(dirSpl[i]);
                            }

                            try
                            {
                                _Stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Write);
                            }
                            catch (Exception ex)
                            {
                                IsCompleted = true;
                                IsSuccess = false;
                                OnJobCompleted?.Invoke(this, EventArgs.Empty);
                                OnJobUpdate?.Invoke(this, EventArgs.Empty);
                                throw ex;
                            }
                           
                            _ExpectedLength = len;
                            ID = id;
                            FilePath = fP;
                            StartTime = DateTime.Now;
                            break;
                        }
                    case "SWFD":
                        {
                            if (string.IsNullOrEmpty(ID) || spl.Length != 3 || spl[1] != ID || _Stream == null)
                                return false;

                            // Convert the string into bytes to write the data.
                            //byte[] res = SocketUtil.ENCODING.GetBytes(spl[2].Trim());
                            byte[] res = Convert.FromBase64String(spl[2].Trim());
                            _Stream.Write(res, 0, res.Length);
                            _RecvLength += res.Length;
                            _RecvParts++;
                            if (DateTime.Now >= _NextUpdate)
                            {
                                BytesPerSecond = _RecvLength - _LastRecvLength;
                                _LastRecvLength = _RecvLength;

                                OnJobUpdate?.Invoke(this, EventArgs.Empty);
                                _NextUpdate = DateTime.Now.AddSeconds(5);
                            }

                            if (socket != null)
                            {
                                // Send the ACK response to continue more.
                                byte[]? ackBytes = SocketUtil.GeneratePacket($"SWAK^^{ID}", passHash);
                                if (ackBytes != null)
                                    socket.Send(ackBytes);
                            }
                            break;
                        }
                    case "SWDD":
                        {
                            if (string.IsNullOrEmpty(ID) || spl.Length != 3 || spl[1] != ID || _Stream == null)
                                return false;

                            long expectedParts = Convert.ToInt64(spl[2]);

                            // Validate all the data to ensure they match.
                            _Stream.Close();
                            IsCompleted = true;
                            IsSuccess = (expectedParts == _RecvParts);
                            OnJobCompleted?.Invoke(this, EventArgs.Empty);
                            OnJobUpdate?.Invoke(this, EventArgs.Empty);
                            break;
                        }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static ReceiveJob WithSocketBlocking(Socket socket, byte[]? passHash = null, string filePath = "")
        {
            ReceiveJob job = new ReceiveJob(filePath);
            while (!job.IsCompleted)
            {
                job.Process(SocketUtil.ReceivePacketString(socket, passHash));
            }
            return job;
        }
    }

    public class SocketUtil
    {
        public readonly static byte[] PACKET_PREFIX = [4, 3, 5, 2];
        public readonly static Encoding ENCODING = new UTF8Encoding();

        private class ParsedClient : IClient
        {
            public string ID { private set; get; }
            public IPAddress Address { private set; get; }
            public DateTime ConnectTime { private set; get; }
            public bool IsConnected { private set; get; }

            public ParsedClient(string id, IPAddress address, DateTime connectTime, bool isConnected)
            {
                ID = id;
                Address = address;
                ConnectTime = connectTime;
                IsConnected = isConnected;
            }

            public static IClient? FromString(string str)
            {
                if (string.IsNullOrEmpty(str))
                    return null;
                string[] spl = str.Split("§");
                if (spl.Length != 3)
                    return null;

                try
                {
                    return new ParsedClient(spl[0], IPAddress.Parse(spl[1]), DateTime.Parse(spl[2]), true);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Tests if two ByteArrays are the same length, and contain equal values.
        /// </summary>
        /// <param name="a1">Byte Array #1</param>
        /// <param name="a2">Byte Array #2</param>
        /// <returns>The equality of the input arrays.</returns>
        public static bool ByteArraysEqual(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }

        /// <summary>
        /// Computes the SHA256 hash of a byte array.
        /// </summary>
        /// <param name="data">The byte array to use</param>
        /// <returns>A byte array containing tha SHA256 of the input.</returns>
        public static byte[] ComputeSHA256(byte[] data)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(data);
            }
        }

        /// <summary>
        /// Computes the SHA256 hash of a byte array.
        /// </summary>
        /// <param name="str">The string to use; processed with UTF-8 encoding</param>
        /// <returns>A byte array containing tha SHA256 of the input.</returns>
        public static byte[] ComputeSHA256(string str)
        {
            return ComputeSHA256(ENCODING.GetBytes(str));
        }

        /// <summary>
        /// Splits a ByteArray by a delimiter, similar to <see cref="string.Split(char[]?)"/>
        /// </summary>
        /// <param name="source">The input ByteArray to process</param>
        /// <param name="separator">The delimiter ByteArray to split by.</param>
        /// <returns>A two-dimensional ByteArray, split by the Delimiter.</returns>
        public static byte[][] SeperateBytes(byte[] source, byte[] separator)
        {
            var parts = new List<byte[]>();
            var index = 0;
            byte[] part;
            for (int i = 0; i < source.Length - separator.Length + 1; ++i)
            {
                bool equal = true;
                for (int j = 0; j < separator.Length; ++j)
                {
                    if (source[i + j] != separator[j])
                    {
                        equal = false;
                        break;
                    }
                }
                if (equal)
                {
                    part = new byte[i - index];
                    Array.Copy(source, index, part, 0, part.Length);
                    parts.Add(part);
                    index = i + separator.Length;
                    i += separator.Length - 1;
                }
            }
            part = new byte[source.Length - index];
            Array.Copy(source, index, part, 0, part.Length);
            parts.Add(part);
            return parts.ToArray();
        }

        /// <summary>
        /// Generate a random alphanumeric string with a specified length. Used for ID generation.
        /// </summary>
        /// <param name="length">The length to generate</param>
        /// <returns>The randomly generated string</returns>
        public static string GenerateRandomString(int length)
        {
            string charset = "QWERTYUIOPASDFGHJKLZXCVBNMqwertyuiopasdfghjklzxcvbnm1234567890";
            Random rand = new Random();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                sb.Append(charset[rand.Next(charset.Length)]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Decrypts ciphertext with a password-hash ByteArray.
        /// </summary>
        /// <param name="cipher">The cipher to decrypt</param>
        /// <param name="passHash">The password-hash to use for decryption</param>
        /// <returns>The decrypted text.</returns>
        public static byte[]? Decrypt(byte[] cipher, byte[] passHash)
        {
            if (cipher.Length < 16)
                return null; // impossible for the IV to be present.
            try
            {
                using Aes aes = Aes.Create();
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Key = ComputeSHA256(passHash); // padding is required for the key.

                List<byte> cipherList = cipher.ToList();

                aes.IV = cipherList.Take(16).ToArray();
                cipherList.RemoveRange(0, 16);

                byte[] data = cipherList.ToArray();

                using ICryptoTransform transform = aes.CreateDecryptor();
                return transform.TransformFinalBlock(data, 0, data.Length);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Encrypts plaintext with a password-hash ByteArray.
        /// </summary>
        /// <param name="plaintext">The plaintext to encrypt</param>
        /// <param name="passHash">The password-hash to use for encryption</param>
        /// <returns>The encrypted ByteArray.</returns>
        public static byte[]? Encrypt(byte[] plaintext, byte[] passHash)
        {
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.BlockSize = 128;
                    aes.Key = ComputeSHA256(passHash); // padding is required for the key.
                    aes.GenerateIV();

                    using ICryptoTransform encryptor = aes.CreateEncryptor();

                    List<byte> encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length).ToList();

                    encrypted.InsertRange(0, aes.IV);

                    aes.Dispose();
                    encryptor.Dispose();

                    return encrypted.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static byte[] TrimEnd(byte[] array)
        {
            int lastIndex = Array.FindLastIndex(array, b => b != 0);
            Array.Resize(ref array, lastIndex + 1);
            return array;
        }

        public static string MakeRelative(string filePath)
        {
            var fileUri = new Uri(filePath);
            var referenceUri = new Uri(Directory.GetCurrentDirectory());
            return Uri.UnescapeDataString(referenceUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        public static int TrySendFile(string filePath, Socket socket, byte[]? passHash = null, bool recursive = false)
        {
            if (Directory.Exists(filePath))
            {
                // If the path is a directory AND recursive, loop through each file and call this method.
                if (!recursive)
                    return 0;

                int output = 0;
                foreach (string file in Directory.GetFiles(filePath))
                {
                    output += TrySendFile(file, socket, passHash, false);
                }
                foreach (string dir in Directory.GetDirectories(filePath))
                {
                    output += TrySendFile(dir, socket, passHash, true);
                }
                return output;
            }

            // Generate a header stream containing the information. A checksum is NOT required
            // since it will be checked on EACH stream.
            string id = GenerateRandomString(4);
            byte[]? headerBuffer = GeneratePacket(
                $"SWHD^^{MakeRelative(Path.GetFullPath(filePath))}^^{id}^^{new FileInfo(filePath).Length}", passHash);

            if (headerBuffer == null || socket.Send(headerBuffer) == 0)
                return 0;

            long parts = 0;
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[65535];
                int bytesRead;

                while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (bytesRead != 65535)
                        Array.Resize(ref buffer, bytesRead);
                    // Generate a packet which provides the data for the file.
                    //byte[] startByte = ENCODING.GetBytes($"SWFD^^{id}^^");
                    //byte[]? dataBuffer = GeneratePacket([.. startByte, .. buffer], passHash);
                    byte[]? dataBuffer = GeneratePacket($"SWFD^^{id}^^{Convert.ToBase64String(buffer)}");
                    if (dataBuffer == null)
                        return 0;
                    Task.Run(() => socket.Send(dataBuffer));

                    // Wait for an ACK signal to continue further.
                    DateTime expire = DateTime.Now.AddSeconds(3);
                    while (DateTime.Now < expire)
                    {
                        if (ReceivePacketString(socket, passHash).Contains($"SWAK^^{id}"))
                            break;
                    }
                    parts++;
                }
            }

            // Finally, send a concluding packet to indicate the receive has finished.
            byte[]? endBuffer = GeneratePacket($"SWDD^^{id}^^{parts}", passHash);
            if (endBuffer == null || socket.Send(endBuffer) == 0)
                return 0;

            return 1;
        }


        public static List<string> ReceivePacketString(Socket socket, byte[]? passHash = null)
        {
            List<string> output = new List<string>();
            foreach (byte[] res in ReceivePacketBytes(socket, passHash))
            {
                output.Add(ENCODING.GetString(res));
            }
            return output;
        }

        public static List<byte[]> ReceivePacketBytes(Socket socket, byte[]? passHash = null)
        {
            try
            {
                byte[]? res = null;
                // Attempt to read the bytes of the socket.
                var buffer = new List<byte>();
                bool wasReading = false;

                while (true)
                {
                    if (socket.Available > 0)
                    {
                        wasReading = true;
                        var currByte = new byte[1];
                        var byteCounter = socket.Receive(currByte, 0, currByte.Length, SocketFlags.None);

                        if (byteCounter == 1)
                        {
                            buffer.Add(currByte[0]);
                        }
                    }
                    else if (wasReading)
                        break;
                }
                //Console.WriteLine("Read " + buffer.Count);
                return DecodePacket(buffer.ToArray(), passHash);
            }
            catch (Exception)
            {
                return new List<byte[]>();
            }
        }

        /// <summary>
        /// Checks if an <see cref="object"/> is equal to its default value.
        /// </summary>
        /// <typeparam name="T">Any type to compare.</typeparam>
        /// <param name="value">The <see cref="object"/> to compare</param>
        /// <returns>True if the <see cref="object"/> is equilvant to its default counterpart; otherwise, false</returns>
        public static bool IsDefault<T>(object value)
        {
            return object.Equals(value, default(T));
        }

        /// <summary>
        /// Attempts to decode a packet, with optional password-hash and compression.
        /// </summary>
        /// <param name="packetBytes">The packet ByteArray to decode.</param>
        /// <param name="passHash">The password hash ByteArray.</param>
        /// <param name="useCompression">If de-compression should be used.</param>
        /// <returns>A decoded packet, or null if an error occurred.</returns>
        public static List<byte[]> DecodePacket(byte[] packetBytes, byte[]? passHash = null)
        {
            List<byte[]> output = new List<byte[]>();
            if (packetBytes == null || packetBytes.Length == 0)
                return output;
            try
            {
                // Split the byte array by the checksum suffix to match it.
                //byte[][] spl = SeperateBytes(packetBytes, CHECKSUM_PREFIX);
                //if (spl.Length != 2)
                //    return null; // corrupt packet

                //// The first section is the checksum, 2nd part is packet.
                //packetBytes = spl[0];

                //byte[] deliveredChecksum = ComputeSHA256(packetBytes);
                //if (!ByteArraysEqual(deliveredChecksum, spl[1]))
                //    return null; // corrupt packet

                // Check if MULTIPLE packets were contained in this message.
                List<byte[]> spl = SeperateBytes(packetBytes, PACKET_PREFIX).ToList();
                if (spl.Count < 1)
                    return output;
                if (spl[0].Length == 0)
                    spl.RemoveAt(0);

                for (int i=1; i<spl.Count; i++)
                {
                    output.AddRange(DecodePacket(spl[i]));
                }

                packetBytes = spl[0];

                if (passHash != null && passHash.Length > 0)
                {
                    byte[]? dec = Decrypt(packetBytes, passHash);
                    if (dec == null)
                        return output;
                    packetBytes = dec;
                }

                using var compressedStream = new MemoryStream(packetBytes);
                using var zipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                using var resultStream = new MemoryStream();

                zipStream.CopyTo(resultStream);
                packetBytes = resultStream.ToArray();

                output.Add(packetBytes);
                return output;
            }
            catch (Exception)
            {
                return output;
            }
        }

        public static byte[]? GeneratePacket(string objStr, byte[]? passHash = null)
        {
            //Console.WriteLine("Generated packet: " + objStr);
            return GeneratePacket(ENCODING.GetBytes(objStr), passHash);
        }

        /// <summary>
        /// Generates a packet from an object, with optional password-hash compression.
        /// </summary>
        /// <param name="objBytes">The list of bytes to encode.</param>
        /// <param name="passHash">The password hash ByteArray.</param>
        /// <returns>An encoded packet, or null if an error occurred.</returns>
        public static byte[]? GeneratePacket(byte[] objBytes, byte[]? passHash = null)
        {
            try
            {
                // For data integrity, checksum the original bytes and append to the end of the list.
                using (var compressedStream = new MemoryStream())
                using (var zipStream = new GZipStream(compressedStream, CompressionLevel.Fastest))
                {
                    zipStream.Write(objBytes, 0, objBytes.Length);
                    zipStream.Close();
                    objBytes = compressedStream.ToArray();
                }

                if (passHash != null && passHash.Length > 0)
                {
                    byte[]? enc = Encrypt(objBytes, passHash);
                    if (enc == null)
                        return null;
                    objBytes = enc;
                }

                //return [.. objBytes, .. CHECKSUM_PREFIX, .. ComputeSHA256(objBytes)];
                return [ .. PACKET_PREFIX, .. objBytes] ;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
