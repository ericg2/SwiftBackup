using AwesomeSockets.Domain.Sockets;
using AwesomeSockets.Sockets;
using System.Net;
using System.Net.Sockets;
using AwesomeSockets.Buffers;
using Buffer = AwesomeSockets.Buffers.Buffer;
using System.Reflection.Metadata.Ecma335;

namespace SwiftUtils
{
    public class SwiftServer
    {
        public class ServerClientEventArgs : EventArgs
        {
            public SwiftServer Server { private set; get; }
            public IServerClient Client { private set; get; }
            public byte[]? Message { private set; get; }

            public ServerClientEventArgs(SwiftServer server, IServerClient client, byte[]? message = null)
            {
                Server = server;
                Client = client;
                Message = message;
            }
        }

        private class ServerClient : IServerClient
        {
            public string ID { set; get; }

            public DateTime ConnectTime { set; get; }

            public DateTime KeepAlive { set; get; }

            public ISocket Socket { get; }

            public SwiftServer AssociatedServer { get; }

            public bool IsHandshake { set; get; }

            public IPAddress? Address
            {
                get
                {
                    if (!IsConnected)
                        return null;

                    EndPoint? ep = Socket.GetRemoteEndPoint();
                    if (ep == null)
                        return null;

                    return (ep as IPEndPoint)!.Address;
                }
            }

            public bool IsConnected
            {
                get
                {
                    return Socket.GetSocket().Connected;
                }
            }

            public bool SendMessage(byte[] message)
            {
                // Attempt to generate a packet message.
                Buffer? buf = SocketUtil.GeneratePacket(message, AssociatedServer._PasswordHash);
                if (buf == null)
                    return false;
                try
                {
                    if (Socket.SendMessage(buf) <= 0)
                        throw new Exception();
                    return true;
                }
                catch (Exception)
                {
                    AssociatedServer.Kick(ID);
                    return false;
                }
            }

            public bool SendMessage(string message)
            {
                return SendMessage(SocketUtil.ENCODING.GetBytes(message));
            }

            public bool Kick()
            {
                // Attempt to kick the client off the server.
                return AssociatedServer.Kick(ID);
            }

            public ServerClient(SwiftServer server, ISocket socket)
            {
                AssociatedServer = server;
                ConnectTime = DateTime.Now;
                Socket = socket;
                KeepAlive = DateTime.Now.AddMinutes(1);
                IsHandshake = false;

                // Attempt to generate an ID whcich is UNIQUE.
                for (int i = 0; i < 100; i++)
                {
                    string id = SocketUtil.GenerateRandomString(4);
                    bool bad = false;
                    foreach (ServerClient client in AssociatedServer._Clients)
                    {
                        if (client.ID == id)
                        {
                            bad = true;
                            break;
                        }
                    }
                    if (!bad)
                    {
                        ID = id;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(ID))
                    throw new SystemException();
            }
        }

        private SimpleLogger _Logger;
        private List<ServerClient> _Clients;

        private Queue<string> _RemoveQueue;
        private byte[]? _PasswordHash;
        private ISocket? _Listener;
        private Thread? _ListenThread;
        private TimeSpan? _KeepAlive;

        private object _ClientLock = new object();

        public event EventHandler<ServerClientEventArgs>? ClientConnected;
        public event EventHandler<ServerClientEventArgs>? ClientDisconnected;
        public event EventHandler<ServerClientEventArgs>? MessageReceived;
   
        public uint MaxClients { set; get; }
        public ushort Port { set; get; }
        public bool IsRunning { private set; get; }
        public DateTime? StartTime { private set; get; }

        public TimeSpan? KeepAlive { set; get; }

        public int Clients
        {
            get
            {
                return _Clients.Count;
            }
        }

        public string Password
        {
            set
            {
                _PasswordHash = string.IsNullOrEmpty(value) 
                    ? null 
                    : SocketUtil.ComputeSHA256(SocketUtil.ENCODING.GetBytes(value));
            }
        }

        public TimeSpan Uptime
        {
            get
            {
                if (StartTime == null)
                    return TimeSpan.FromSeconds(0); // never started
                return (TimeSpan)(DateTime.Now - StartTime);
            }
        }

        public SwiftServer(ushort port=8080, bool logEnabled=false)
        {
            _Logger = new SimpleLogger();
            _Clients = new List<ServerClient>();
            _RemoveQueue = new Queue<string>();
            _ListenThread = null;
            Port = port;
            if (logEnabled)
                _Logger.Start();
        }    

        public bool Kick(string id)
        {
            ServerClient? client = FindClientByIDInternal(id);
            if (client == null)
                return false;
            client.SendMessage("SERVKICK");
            client.Socket.Close();
            ClientDisconnected?.Invoke(this, new ServerClientEventArgs(this, client));
            lock (_ClientLock)
            {
                _RemoveQueue.Enqueue(id);
                return _Clients.Remove(client);
            }
        }

        private ServerClient? FindClientByIDInternal(string id)
        {
            lock (_ClientLock)
            {
                foreach (ServerClient client in _Clients)
                {
                    if (client.ID == id)
                        return client;
                }
            }
            return null;
        }

        public IServerClient? FindClientByID(string id)
        {
            return FindClientByIDInternal(id);
        }

        private static T[] RemoveAt<T>(T[] source, int index)
        {
            T[] dest = new T[source.Length - 1];
            if (index > 0)
                Array.Copy(source, 0, dest, 0, index);

            if (index < source.Length - 1)
                Array.Copy(source, index + 1, dest, index, source.Length - index - 1);

            return dest;
        }

        private void HandleClients()
        {
            string[] ids = new string[1];
            Task[] tasks = new Task[1];

            if (_Listener == null)
                return;

            ids[0] = "Listener";
            tasks[0] = Task.Run(() => AweSock.TcpAccept(_Listener));
            Queue<string> addQueue = new Queue<string>();

            while (IsRunning)
            {
                if (_RemoveQueue.Count > 0)
                {
                    while (_RemoveQueue.Count > 0)
                    {
                        string id = _RemoveQueue.Dequeue();
                        for (int i = 0; i < ids.Length; i++)
                        {
                            if (ids[i] == id)
                            {
                                ids = RemoveAt(ids, i);
                                tasks = RemoveAt(tasks, i);
                                break;
                            }
                        }
                    }
                }

                // If any values are queued, add the task to the list.
                if (addQueue.Count > 0)
                {
                    int idx = tasks.Length;
                    Array.Resize(ref ids, ids.Length + addQueue.Count);
                    Array.Resize(ref tasks, tasks.Length + addQueue.Count);

                    while (addQueue.Count > 0)
                    {
                        string id = addQueue.Dequeue();
                        ids[idx] = id;
                        ServerClient? client = FindClientByIDInternal(id);
                        if (client == null)
                            continue;
                        tasks[idx] = Task.Run(() => SocketUtil.ReceivePacketBytes(client.Socket));
                    }
                }

                // Wait for any task to finish.
                for (int i=0; i<tasks.Length; i++)
                {
                    string id = ids[i];
                    if (id != "Listener")
                    {
                        // Check if the keep-alive is violated.
                        if (DateTime.Now >= FindClientByIDInternal(id)?.KeepAlive)
                            Kick(id);
                    }
                    if (!tasks[i].IsCompleted)
                        continue;
                    if (id == "Listener" && tasks[i] is Task<ISocket>)
                    {
                        ISocket socket = ((Task<ISocket>)tasks[i]).Result;
                        ServerClient client = new ServerClient(this, socket);
                        _Clients.Add(client);
                        addQueue.Enqueue(client.ID);
                        ClientConnected?.Invoke(this, new ServerClientEventArgs(this, client));

                        // Restart the task to begin running.
                        tasks[i] = Task.Run(() => AweSock.TcpAccept(_Listener));
                        continue;
                    }

                    if (tasks[i] is Task<byte[]?>)
                    {
                        byte[]? res = ((Task<byte[]?>)tasks[i]).Result;
                        ServerClient? client = FindClientByIDInternal(id);
                        if (client == null)
                            break;

                        tasks[i] = Task.Run(() => SocketUtil.ReceivePacketBytes(client.Socket));
                        if (res == null)
                        {
                            break;
                        }

                        if (SocketUtil.ENCODING.GetString(res) == "SERVKEEPALIVE")
                        {
                            client.KeepAlive = DateTime.Now.AddSeconds(10);
                            continue;
                        }
                        client.KeepAlive = DateTime.Now.AddSeconds(10);
                        MessageReceived?.Invoke(this, new ServerClientEventArgs(this, client, res));
                    }
                }
            }
        }

        public bool Start()
        {
            if (IsRunning)
            {
                _Logger.Error("The 'start' method has been called while the server was running!");
                return false;
            }

            IsRunning = true;
            _Listener = AweSock.TcpListen(Port);
            _ListenThread = new Thread(new ThreadStart(HandleClients));
            _ListenThread.Start();
            StartTime = DateTime.Now;

            return true;
        }
    }
}
