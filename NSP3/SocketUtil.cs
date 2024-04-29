using AwesomeSockets.Domain.Sockets;
using AwesomeSockets.Sockets;
using System.IO.Compression;
using AwesomeSockets.Buffers;
using System.Security.Cryptography;
using System.Text;
using Buffer = AwesomeSockets.Buffers.Buffer;

namespace SwiftUtils
{
    public class SocketUtil
    {
        public readonly static byte[] CHECKSUM_PREFIX = [4, 3, 5, 2];
        public readonly static Encoding ENCODING = new UTF8Encoding();

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

        public static byte[]? ReceivePacketBytes(ISocket socket, byte[]? passHash = null)
        {
            try
            {
                Buffer buffer = Buffer.New();
                AweSock.ReceiveMessage(socket, buffer);
                return DecodePacket(buffer, passHash);
            }
            catch (Exception)
            {
                return null;
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
        public static byte[]? DecodePacket(Buffer buffer, byte[]? passHash = null)
        {
            // Pull all bytes from the buffer.

            byte[] packetBytes = Convert.FromBase64String(Buffer.Get<string>(buffer));
          
            if (packetBytes == null || packetBytes.Length == 0)
                return null;
            try
            {
                // Split the byte array by the checksum suffix to match it.
                byte[][] spl = SeperateBytes(packetBytes, CHECKSUM_PREFIX);
                if (spl.Length != 2)
                    return null; // corrupt packet

                // The first section is the checksum, 2nd part is packet.
                packetBytes = spl[0];

                byte[] deliveredChecksum = ComputeSHA256(packetBytes);
                if (!ByteArraysEqual(deliveredChecksum, spl[1]))
                    return null; // corrupt packet

                if (passHash != null && passHash.Length > 0)
                {
                    byte[]? dec = Decrypt(packetBytes, passHash);
                    if (dec == null)
                        return default;
                    packetBytes = dec;
                }

                using var compressedStream = new MemoryStream(packetBytes);
                using var zipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                using var resultStream = new MemoryStream();

                zipStream.CopyTo(resultStream);
                packetBytes = resultStream.ToArray();

                return packetBytes;
            }
            catch (Exception)
            {
                return default;
            }
        }

        public static Buffer? GeneratePacket(string objStr, byte[]? passHash = null)
        {
            return GeneratePacket(ENCODING.GetBytes(objStr), passHash);
        }

        /// <summary>
        /// Generates a packet from an object, with optional password-hash compression.
        /// </summary>
        /// <param name="objBytes">The list of bytes to encode.</param>
        /// <param name="passHash">The password hash ByteArray.</param>
        /// <returns>An encoded packet, or null if an error occurred.</returns>
        public static Buffer? GeneratePacket(byte[] objBytes, byte[]? passHash = null)
        {
            try
            {
                // For data integrity, checksum the original bytes and append to the end of the list.
                using (var compressedStream = new MemoryStream())
                using (var zipStream = new GZipStream(compressedStream, CompressionMode.Compress))
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

                Buffer output = Buffer.New();
                byte[] arr = [.. objBytes, .. CHECKSUM_PREFIX, .. ComputeSHA256(objBytes)];
                Buffer.Add(output, Convert.ToBase64String(arr));
                Buffer.FinalizeBuffer(output);
                return output;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
