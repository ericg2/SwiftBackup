using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.IO;
using System.Linq;
using System;

using static SwiftUtils.SwiftCommand;
using System.Text;

#nullable enable

namespace SwiftUtils
{
    public class SwiftClient
    {
        public class ClientEventArgs : EventArgs
        {
            public SwiftClient Client { private set; get; }
            public byte[]? Message { private set; get; }

            public ClientEventArgs(SwiftClient client, byte[]? message)
            {
                Client = client;
                Message = message;
            }
        }

        public IPAddress Address { private set; get; }
        public bool IsRunning { private set; get; }
        public ushort Port { private set; get; }

        public event EventHandler<ClientEventArgs>? OnMessageReceived;
        public event EventHandler<ClientEventArgs>? OnConnected;
        public event EventHandler<ClientEventArgs>? OnDisconnected;

        private Thread? _ListenThread;
        private Socket? _Socket;
        private byte[]? _PasswordHash;
        private DateTime _NextKeepAlive;
        private List<ReceiveJob> _ReceiveJobs;

        private void HandleServer()
        {
            if (_Socket == null)
                return;

            List<byte[]> res = new List<byte[]>();
            while (IsRunning)
            {
                res = SocketUtil.ReceivePacketBytes(_Socket!, _PasswordHash);
                if (res.Count > 0)
                {
                    foreach (byte[] packet in res)
                    {
                        // Check if there is a command or not.
                        string packetStr = SocketUtil.ENCODING.GetString(packet);
                        SwiftCommand? command = SwiftCommand.FromString(packetStr);
                        if (command == null)
                        {
                            OnMessageReceived?.Invoke(this, new ClientEventArgs(this, packet));
                            _ReceiveJobs.RemoveAll(o => o.IsCompleted);

                            foreach (ReceiveJob job in _ReceiveJobs)
                            {
                                job.Process(packetStr, _Socket, _PasswordHash);
                            }
                        }
                        else
                        {
                            if (command.Type == CommandType.KICK || command.Type == CommandType.DISCONNECT)
                            {
                                Stop();
                            }
                        }
                    }
                }
            }
        }

        public ReceiveJob AddReceiveJob(string filePath="")
        {
            ReceiveJob job = new ReceiveJob(filePath);
            _ReceiveJobs.Add(job);
            return job;
        }

        private bool SendCommand(CommandType type, string message = "")
        {
            return SendMessage(SwiftCommand.GenerateComamnd(type, message));
        }

        public bool SendMessage(string message)
        {
            if (!IsRunning || _Socket == null)
                return false;
            byte[]? packet = SocketUtil.GeneratePacket(message, _PasswordHash);
            if (packet == null)
                return false;
            return _Socket.Send(packet) > 0;
        }

        public bool SendMessage(byte[] message)
        {
            if (!IsRunning || _Socket == null)
                return false;
            byte[]? packet = SocketUtil.GeneratePacket(message, _PasswordHash);
            if (packet == null)
                return false;
            return _Socket.Send(packet) > 0;
        }

        public int SendFile(string filePath, bool recursive=false)
        {
            if (_Socket == null)
                return 0;
            return SocketUtil.TrySendFile(filePath, _Socket, _PasswordHash, recursive);
        }

        public SwiftClient SetPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                _PasswordHash = null;
                return this;
            }
            _PasswordHash = SocketUtil.ComputeSHA256(password);
            return this;
        }

        public SwiftClient(string ipAddress, ushort port, string password="")
        {
            // Make sure the IP address can be parsed correctly.
            Address = IPAddress.Parse(ipAddress);
            _ReceiveJobs = new List<ReceiveJob>();
            Port = port;
            SetPassword(password);
        }

        public bool Start()
        {
            if (IsRunning)
                return false;
            for (int i=0; i<3; i++)
            {
                try
                {
                    _Socket = new Socket(SocketType.Stream, ProtocolType.IP);
                    _Socket.Connect(new IPEndPoint(Address, Port));
                    _Socket.NoDelay = true;
                    _Socket.ReceiveBufferSize = 16384;
                    _Socket.SendBufferSize = 16384;
                    break;
                }
                catch (Exception)
                { }
            }
            if (_Socket == null)
                return false; // the connection failed after 3 attempts.

            IsRunning = true;
            _NextKeepAlive = DateTime.Now;
            _ListenThread = new Thread(new ThreadStart(HandleServer));
            _ListenThread.Start();
            
            OnConnected?.Invoke(this, new ClientEventArgs(this, null));
            return true;
        }

        public bool Stop()
        {
            if (!IsRunning || _Socket == null)
                return false;
            IsRunning = false;
            SendCommand(CommandType.DISCONNECT); // cleanly send a disconnect message to the server.
            _Socket.Close();
            _ListenThread = null;
            OnDisconnected?.Invoke(this, new ClientEventArgs(this, null));
            return true;
        }
    }
}