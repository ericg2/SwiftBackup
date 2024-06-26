﻿using AwesomeSockets.Domain.SocketModifiers;
using AwesomeSockets.Sockets;
using System;
using System.Net;
using System.Net.Sockets;

namespace AwesomeSockets.Domain.Sockets
{
    public class AwesomeSocket : ISocket
    {
        private readonly Socket _internalSocket;

        private AwesomeSocket(Socket socket)
        {
            _internalSocket = socket;
        }

        internal static AwesomeSocket New(Socket socket)
        {
            return new AwesomeSocket(socket);
        }

        public Socket GetSocket()
        {
            return _internalSocket;
        }

        public ISocket Accept()
        {
            return New(_internalSocket.Accept());
        }

        public void Connect(EndPoint remoteEndPoint)
        {
            _internalSocket.Connect(remoteEndPoint);
        }

        public int SendMessage(byte[] buffer)
        {
            return _internalSocket.Send(buffer);
        }

        public int SendMessage(string ip, int port, byte[] buffer)
        {
            var ipAddress = IPAddress.Parse(ip);
            var remoteEndpoint = new IPEndPoint(ipAddress, port);
            return _internalSocket.SendTo(buffer, remoteEndpoint);
        }

        private static byte[] TrimEnd(byte[] array)
        {
            int lastIndex = Array.FindLastIndex(array, b => b != 0);
            Array.Resize(ref array, lastIndex + 1);
            return array;
        }

        public Tuple<byte[], EndPoint?> ReceiveMessage()
        {
            byte[] buffer = new byte[4192];
            _internalSocket.Receive(buffer);

            return Tuple.Create(TrimEnd(buffer), _internalSocket.RemoteEndPoint);
        }

        public Tuple<byte[], EndPoint?> ReceiveMessage(string ip, int port)
        {
            byte[] buffer = new byte[4192];

            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            _internalSocket.ReceiveFrom(buffer, ref remoteEndPoint);
           
            return Tuple.Create(TrimEnd(buffer), remoteEndPoint);
        }

        public EndPoint? GetRemoteEndPoint()
        {
            return _internalSocket.RemoteEndPoint;
        }

        public ProtocolType GetProtocolType()
        {
            return _internalSocket.ProtocolType;
        }

        public int GetBytesAvailable()
        {
            return _internalSocket.Available;
        }

        public void Close(int timeout = 0)
        {
            if (timeout == 0)
                _internalSocket.Close();
            else 
                _internalSocket.Close(timeout);
        }

        public ISocket WithModifier<T>() where T : ISocketModifier, new()
        {
            return new WithModifierWrapper<T>().ApplyModifier(this);
        }

        public void SetGlobalConfiguration(Dictionary<SocketOptionName, object> opts)
        {
            foreach (var opt in opts)
            {
                _internalSocket.SetSocketOption(SocketOptionLevel.Socket, opt.Key, opt.Value);
            }
        }
    }
}
