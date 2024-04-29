using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SwiftUtils
{
    public interface IServerClient
    {
        public string ID { get; }
        public IPAddress? Address { get; }
        public DateTime ConnectTime { get; }

        public SwiftServer AssociatedServer { get; }

        public bool IsConnected { get; }

        bool Kick();
        bool SendMessage(byte[] message);
        bool SendMessage(string message);
    }
}
