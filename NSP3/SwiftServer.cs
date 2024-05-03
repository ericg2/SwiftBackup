using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.IO;
using System;

using static SwiftUtils.SwiftCommand;

#nullable enable

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

            public Socket Socket { get; }

            public SwiftServer AssociatedServer { get; }

            public IPAddress Address
            {
                get
                {
                    // FIXME: may cause problems!
                    EndPoint? ep = Socket.RemoteEndPoint;
                    return (ep as IPEndPoint)!.Address;
                }
            }

            public bool IsConnected
            {
                get
                {
                    return Socket.Connected;
                }
            }

            public FileJobPool _Jobs = new FileJobPool();

            public bool SendMessage(byte[] message)
            {
                // Attempt to generate a packet message.
                byte[]? buf = SocketUtil.GeneratePacket(message, AssociatedServer._PasswordHash);
                if (buf == null)
                    return false;
                try
                {
                    if (Socket.Send(buf) <= 0)
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

            public bool Kick(string reason = "")
            {
                // Attempt to kick the client off the server.
                return AssociatedServer.Kick(ID, reason);
            }

            public bool AddJob(FileJob job)
            {
                if (_Jobs.AddJob(job, out byte[]? tP, AssociatedServer._PasswordHash))
                {
                    if (tP != null)
                        Socket.Send(tP);
                    return true;
                }
                return false;
            }

            public string GetString()
            {
                if (!IsConnected || Address == null)
                    return string.Empty;
                return $"{ID}§{Address.ToString()}§{ConnectTime.ToString()}";
            }

            public bool SendCommand(SwiftCommand.CommandType type, string message = "")
            {
                return SendMessage(GenerateComamnd(type, message));
            }

            public ServerClient(SwiftServer server, Socket socket)
            {
                AssociatedServer = server;
                ConnectTime = DateTime.Now;
                Socket = socket;
                KeepAlive = DateTime.Now.AddMinutes(1);

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
        private Socket? _Listener;
        private Thread? _ListenThread;
        private TimeSpan? _KeepAlive;
        private FileJobPool _FileJobs = new FileJobPool();

        private object _ClientLock = new object();

        public event EventHandler<ServerClientEventArgs>? ClientConnected;
        public event EventHandler<ServerClientEventArgs>? ClientDisconnected;
        public event EventHandler<ServerClientEventArgs>? MessageReceived;

        public uint      MaxClients { set; get; }
        public ushort    Port       { set; get; }
        
        public bool      IsRunning { private set; get; }
        public DateTime? StartTime { private set; get; }

        public IServerClient[] Clients
        {
            get
            {
                return _Clients.ToArray();
            }
        }

        public uint TotalConnected
        {
            get
            {
                return (uint)_Clients.Count;
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

        public SwiftServer SetPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                _PasswordHash = null;
                return this;
            }
            _PasswordHash = SocketUtil.ComputeSHA256(password);
            return this;
        }

        public SwiftServer(ushort port=8080, string password="", bool logEnabled=false)
        {
            _Logger = new SimpleLogger();
            _Clients = new List<ServerClient>();
            _RemoveQueue = new Queue<string>();
            _ListenThread = null;
            Port = port;
            if (logEnabled)
                _Logger.Start();
            SetPassword(password);
        }    

        public bool Kick(string id, string reason = "")
        {
            ServerClient? client = FindClientByIDInternal(id);
            if (client == null)
                return false;

            client.SendCommand(CommandType.KICK, reason);
            client.Socket.Close();
            
            ClientDisconnected?.Invoke(this, new ServerClientEventArgs(this, client));
            _Logger.Info("Disconnected client " + id + " due to " + (string.IsNullOrEmpty(reason) ? "N/A" : reason));
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
            tasks[0] = Task.Run(_Listener.Accept);
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
                        {
                            Kick(id, "Keep-alive");
                            continue;
                        }
                    }
                    if (!tasks[i].IsCompleted)
                        continue;
                    if (id == "Listener" && tasks[i] is Task<Socket>)
                    {
                        Socket socket = ((Task<Socket>)tasks[i]).Result;
                        
                        // Restart the task to begin running.
                        tasks[i] = Task.Run(_Listener.Accept);
                        socket.NoDelay = true;
                        socket.ReceiveBufferSize = 16384;
                        socket.SendBufferSize = 16384;
                        ServerClient client = new ServerClient(this, socket);
                        
                        if (_Clients.Count > MaxClients)
                        {
                            client.Kick("Too many clients!");
                            continue;
                        }

                        lock (_ClientLock)
                        {
                            _Clients.Add(client);
                            addQueue.Enqueue(client.ID);
                        }
                        ClientConnected?.Invoke(this, new ServerClientEventArgs(this, client));
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
                            break;

                        client.KeepAlive = DateTime.Now.AddSeconds(60);

                        // Check if a command has been sent.
                        string packetStr = SocketUtil.ENCODING.GetString(res);
                        SwiftCommand? command = SwiftCommand.FromString(packetStr);
                        if (command == null)
                        {

                            // Process any receive jobs.
                            if (client._Jobs.Process(new List<string>() { packetStr}, out List<byte[]> tPs, _PasswordHash) > 0)
                            {
                                foreach (byte[] tP in tPs)
                                {
                                    client.Socket.Send(tP);
                                }
                            }

                            MessageReceived?.Invoke(this, new ServerClientEventArgs(this, client, res));
                            continue;
                        }

                        switch (command.Type)
                        {
                            case CommandType.KEEP_ALIVE:
                            {
                                // Tell the client their request has been processed.
                                client.SendCommand(CommandType.SUCCESS);
                                break;
                            }
                            case CommandType.DISCONNECT: 
                            {
                                client.Kick("Requested");
                                break;
                            }
                            default:
                            {
                                client.SendCommand(CommandType.FAILURE, "Invalid command.");
                                break;
                            }
                        }
                    }
                }
            }
        }

        public bool Stop()
        {
            if (!IsRunning)
            {
                _Logger.Error("The 'stop' method has been called while not running!");
                return false;
            }
            IsRunning = false;
            StartTime = new DateTime();
            foreach (IServerClient cli in new List<IServerClient>(_Clients))
            {
                Kick(cli.ID);
            }
            Thread.Sleep(2000);
            _Listener?.Close();
            _Logger.Info("Shut down server. All clients disconnected.");
            return true;
        }

        public bool Start()
        {
            if (IsRunning)
            {
                _Logger.Error("The 'start' method has been called while the server was running!");
                return false;
            }

            IsRunning = true;

            _Listener = new Socket(SocketType.Stream, ProtocolType.IP);
            _Listener.Bind(new IPEndPoint(IPAddress.Any, Port));
            _Listener.Listen(100);

            _ListenThread = new Thread(new ThreadStart(HandleClients));
            _ListenThread.Start();
            StartTime = DateTime.Now;

            _Logger.Info("Started server at port " + Port);

            return true;
        }
    }
}