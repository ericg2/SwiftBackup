using AwesomeSockets.Domain.Sockets;
using AwesomeSockets.Sockets;
using AwesomeSockets.Buffers;
using SwiftUtils;
using System.Net.Sockets;
using Buffer = AwesomeSockets.Buffers.Buffer;

namespace FileServer
{
    internal class Program
    {
        public static void TestSocket()
        {
            SwiftServer server = new SwiftServer();
            server.Start();
            server.ClientConnected += Server_ClientConnected;
            server.MessageReceived += Server_MessageReceived;
            server.ClientDisconnected += Server_ClientDisconnected;
            Thread.Sleep(2000);

            List<ISocket> clients = new List<ISocket>();
            for (int i=0; i<20; i++)
            {
                ISocket client = AweSock.TcpConnect("127.0.0.1", 8080);
                clients.Add(client);
                Thread.Sleep(200);
            }

            foreach (ISocket client in clients)
            {
                Buffer? buf = SocketUtil.GeneratePacket("hello world!");
                if (buf != null)
                    client.SendMessage(buf);
                Thread.Sleep(500);
            }
        }

        private static void Server_ClientDisconnected(object? sender, SwiftServer.ServerClientEventArgs e)
        {
            Console.WriteLine("Client was disconnected! (count is " + e.Server.Clients + ")");
        }

        private static void Server_MessageReceived(object? sender, SwiftServer.ServerClientEventArgs e)
        {
            Console.WriteLine(SocketUtil.ENCODING.GetString(e.Message!));
           // Console.WriteLine($"Total RAM: {GC.GetTotalMemory(false) / 1000} MB");
        }

        private static void Server_ClientConnected(object? sender, SwiftServer.ServerClientEventArgs e)
        {
            Console.WriteLine("Client was connected! (count is " + e.Server.Clients + ")");
        }

        static void Main(string[] args)
        {
            TestSocket();
        }
    }
}
