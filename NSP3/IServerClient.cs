using System;
using System.Net;

#nullable enable

namespace SwiftUtils
{
    public interface IServerClient : IClient
    {
        public SwiftServer AssociatedServer { get; }

        public bool Kick(string reason="");
        public bool SendMessage(byte[] message);
        public bool SendMessage(string message);
        public bool AddJob(FileJob job);

        public string GetString();
    }
}
