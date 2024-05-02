using AwesomeSockets.Domain.SocketModifiers;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace AwesomeSockets.Domain.Sockets
{
    public interface ISocket
    {
        void SetGlobalConfiguration(Dictionary<SocketOptionName, object> opts);
        Socket GetSocket();

        ISocket Accept();
        void Connect(EndPoint remoteEndPoint);

        int SendMessage(byte[] buffer);
        int SendMessage(string ip, int port, byte[] buffer);

        Tuple<byte[], EndPoint?> ReceiveMessage();
        Tuple<byte[], EndPoint> ReceiveMessage(string ip, int port);

        EndPoint? GetRemoteEndPoint();
        ProtocolType GetProtocolType();

        int GetBytesAvailable();

        void Close(int timeout = 0);

        ISocket WithModifier<T>() where T : ISocketModifier, new();
    }
}
