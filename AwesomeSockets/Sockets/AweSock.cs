using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AwesomeSockets.Domain;
using AwesomeSockets.Domain.Sockets;
using AwesomeSockets.Domain.SocketModifiers;

namespace AwesomeSockets.Sockets
{
    public delegate void MessageThreadCallback(byte[] buffer, EndPoint? remoteEndpoint=null);

    class WithModifierWrapper<T> where T : ISocketModifier, new()
    {
        public ISocket ApplyModifier(ISocket socket)
        {
            return new T().Apply(socket);
        }
    }

    public static class AweSock
    {
        public static ISocket TcpListen(int port, int backlog = 10)
        {
            var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ip = new IPAddress(new byte[] { 0, 0, 0, 0 });
            var localEndPoint = new IPEndPoint(ip, port);
            listenSocket.Bind(localEndPoint);
            listenSocket.Listen(backlog);
            return AwesomeSocket.New(listenSocket);
        }

        public static ISocket? TcpAccept(ISocket listenSocket, SocketCommunicationTypes type = SocketCommunicationTypes.Blocking, 
            Func<ISocket, Exception, Socket>? callback = null)
        {
            if (type == SocketCommunicationTypes.Blocking)
            {
                return AwesomeSocket.New(listenSocket.GetSocket().Accept());
            }

            if (callback == null)
            {
                throw new ArgumentNullException(string.Format("{0}=null; You must provide a valid callback when using the NonBlocking type", "callback"));
            }

            new Thread(() => TcpAcceptThread(listenSocket, callback)).Start();
            return null;
        }

        public static ISocket? TcpConnect(string ipAddress, int port, SocketCommunicationTypes type = SocketCommunicationTypes.Blocking, 
            Func<ISocket, Exception, Socket>? callback = null)
        {
            var connectSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ip = IPAddress.Parse(ipAddress);
            var remoteEndpoint = new IPEndPoint(ip, port);
            if (type == SocketCommunicationTypes.Blocking)
            {
                connectSocket.Connect(remoteEndpoint);
                return AwesomeSocket.New(connectSocket);
            }

            if (callback == null)
            {
                throw new ArgumentNullException(string.Format("{0}=null; You must provide a valid callback when using the NonBlocking type", "callback"));
            }

            new Thread(() => TcpConnectThread(AwesomeSocket.New(connectSocket), remoteEndpoint, callback)).Start();
            return null;
        }

        public static ISocket UdpConnect(int port)
        {
            var udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var localEndpoint = new IPEndPoint(IPAddress.Any, port);
            udpSocket.Bind(localEndpoint);
            return AwesomeSocket.New(udpSocket);
        }

        public static void SetSockOpt(ISocket socket, Dictionary<SocketOptionName, object> opts)
        {
            socket.SetGlobalConfiguration(opts);
        }

        public static int SendMessage(ISocket socket, byte[] buffer)
        {
            if (socket.GetSocket().ProtocolType == ProtocolType.Udp)
            {
                throw new InvalidOperationException("Cannot call this method with a UDP socket. Call SendMessage(Socket, string, int, Buffer) instead.");
            }

            return socket.SendMessage(buffer);
        }

        public static int SendMessage(ISocket socket, string ip, int port, byte[] buffer)
        {
            if (socket.GetSocket().ProtocolType == ProtocolType.Tcp)
            {
                throw new InvalidOperationException("Cannot call this method with a TCP socket. Call SendMessage(Socket, Buffer) instead.");
            }

            return socket.SendMessage(ip, port, buffer);
        }

        private static byte[] TrimEnd(byte[] array)
        {
            int lastIndex = Array.FindLastIndex(array, b => b != 0);
            Array.Resize(ref array, lastIndex + 1);
            return array;
        }

        public static Tuple<byte[], EndPoint?> ReceiveMessage(ISocket socket, SocketCommunicationTypes type = SocketCommunicationTypes.Blocking, 
            MessageThreadCallback? callback = null)
        {
            if (type == SocketCommunicationTypes.Blocking)
            {
                byte[] buffer = new byte[4192];
                switch (socket.GetSocket().ProtocolType)
                {
                    case ProtocolType.Tcp:
                        socket.GetSocket().Receive(buffer);
                        return Tuple.Create(TrimEnd(buffer), socket.GetSocket().RemoteEndPoint);
                    case ProtocolType.Udp:
                        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        socket.GetSocket().ReceiveFrom(buffer, ref remoteEndPoint);
                        #pragma warning disable CS8619 
                        return Tuple.Create(TrimEnd(buffer), remoteEndPoint);
                        #pragma warning restore CS8619
                    default:
                        throw new InvalidOperationException("Socket must be of type TCP or UDP.");
                }
            }

            if (callback == null)
            {
                throw new ArgumentNullException(string.Format("{0}=null; You must provide a valid callback when using the NonBlocking type", "callback"));
            }

            new Thread(() => MessageReceiveThread(socket, callback)).Start();
            #pragma warning disable CS8619 
            return Tuple.Create(new byte[0], new IPEndPoint(-1, -1) as EndPoint);  //Return negative 1 as 0 bytes received is valid and we want an invalid value 
            #pragma warning restore CS8619
        }

        public static IPAddress? GetRemoteIpAddress(ISocket socket)
        {
            if (socket.GetSocket().ProtocolType == ProtocolType.Udp)
            {
                throw new InvalidOperationException("Cannot get remote IP Address of a UDP socket directly. It is returned from the ReceiveMessage as the second item in the Tuple<>");
            }

            var socketEndPoint = (IPEndPoint?)socket.GetRemoteEndPoint();
            return socketEndPoint?.Address;
        }

        public static int? GetRemotePort(ISocket socket)
        {
            if (socket.GetSocket().ProtocolType == ProtocolType.Udp)
            {
                throw new InvalidOperationException("Cannot get remote IP Address of a UDP socket directly. It is returned from the ReceiveMessage as the second item in the Tuple<>");
            }

            var socketEndPoint = (IPEndPoint?)socket.GetRemoteEndPoint();
            return socketEndPoint?.Port;
        }

        public static int BytesAvailable(ISocket socket)
        {
            return socket.GetBytesAvailable();
        }

        public static void CloseSock(ISocket socket, int timeout = 0)
        {
            if (socket.GetSocket().ProtocolType == ProtocolType.Tcp)
            {
                socket.GetSocket().Shutdown(SocketShutdown.Both);
            }

            if (timeout == 0)
            {
                socket.Close();
            }

            else
            {
                socket.Close(timeout);
            }
        }

        private static void TcpAcceptThread(ISocket listenSocket, Func<ISocket?, Exception?, Socket> callback)
        {
            Socket? clientSocket = null;
            try
            {
                clientSocket = listenSocket.GetSocket().Accept();
            }
            catch (Exception ex)
            {
                callback(null, ex);
            }
            callback(AwesomeSocket.New(clientSocket!), null);
        }

        private static void TcpConnectThread(ISocket connectSocket, EndPoint remoteEndpoint, Func<ISocket?, Exception?, Socket> callback)
        {
            try
            {
                connectSocket.Connect(remoteEndpoint);
            }
            catch (Exception ex)
            {
                callback(null, ex);
            }
            callback(connectSocket, null);
        }

        private static void MessageReceiveThread(ISocket socket, MessageThreadCallback callback)
        {
            byte[] buffer = new byte[4192];
            switch (socket.GetProtocolType())
            {
                case ProtocolType.Tcp:
                    socket.GetSocket().Receive(buffer);
                    callback(TrimEnd(buffer));
                    break;
                case ProtocolType.Udp:
                    EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                    socket.GetSocket().ReceiveFrom(buffer, ref remoteEndPoint);
                    callback(TrimEnd(buffer), remoteEndPoint);
                    break;
                default:
                    callback(Array.Empty<byte>());
                    break;
            }
        }
    }
}
