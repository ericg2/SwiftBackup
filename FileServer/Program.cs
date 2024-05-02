using NetCoreServer;
using SwiftUtils;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace FileServer
{
    internal class Program
    {
        /*
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
        */

        public static void Main(string[] args)
        {

            string password = "Testing123";
            SwiftServer server = new SwiftServer(8080);
            SwiftClient client = new SwiftClient("127.0.0.1", 8080);

            server.Start();
            client.Start();

            ReceiveJob job = client.AddReceiveJob("RESULT.avi");
            job.OnJobCompleted += (sender, e) => { Console.WriteLine("Receive Completed!"); };
            job.OnJobUpdate += (sender, e) => { Console.WriteLine("Job is " + ((ReceiveJob)sender!).Percentage + " percent complete"); };

            server.ClientConnected += (sender, e) =>
            {
                // Send a file to the client.
                Thread.Sleep(2000);
                e.Client.SendFile("TEST.avi");
            };
            
        }
    }
}
