using NUnit.Framework;
using SwiftUtils;
using System.Net.Sockets;
using System.Text;

namespace SwiftUtils.Tests
{
    [TestFixture()]
    public class SocketUtilTests
    {
        [Test()]
        public void ByteArraysEqual_Test()
        {
            Assert.IsFalse(SocketUtil.ByteArraysEqual([1, 2, 3, 4], [5, 6, 7, 8]));
            Assert.IsTrue(SocketUtil.ByteArraysEqual([1, 2, 3], [1, 2, 3]));
        }

        [Test()]
        public void ComputeSHA256_Test()
        {
            byte[] expectedHash = [127, 131, 177, 101, 127, 241, 252, 83, 185, 45, 193, 129, 72, 161, 
                214, 93, 252, 45, 75, 31, 163, 214, 119, 40, 74, 221, 210, 0, 18, 109, 144, 105];

            byte[] actualHash = SocketUtil.ComputeSHA256(Encoding.UTF8.GetBytes("Hello World!"));
            Assert.That(actualHash, Is.EquivalentTo(expectedHash));
        }

        [Test()]
        public void SeperateBytes_Test()
        {
            byte[][] output = SocketUtil.SeperateBytes([1, 2, 3, 9, 9, 4, 5, 6], [9, 9]);
            Assert.That(output.Length, Is.EqualTo(2));

            Assert.That(output[0], Is.EquivalentTo(new byte[] { 1, 2, 3 }));
            Assert.That(output[1], Is.EquivalentTo(new byte[] { 4, 5, 6 }));
        }

       
    }
}
