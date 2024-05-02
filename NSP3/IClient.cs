using System;
using System.Net;

#nullable enable

namespace SwiftUtils
{
    public interface IClient
    {
        public string ID { get; }
        public IPAddress Address { get; }
        public DateTime ConnectTime { get; }
        public bool IsConnected { get; }
    }
}