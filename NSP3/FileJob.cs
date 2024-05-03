using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System;
using System.Net;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Net.Sockets;

#nullable enable

namespace SwiftUtils
{
    public enum FileJobType { TRANSMIT, RECEIVE };

    public enum FileJobStatus : int 
    { 
        DISABLED = 0, 
        QUEUED = 1, 
        RUNNING = 2, 
        SUCCESS = 3, 
        FAILED = 4 
    };

    public class FileJobPool 
    {
        private Collection<FileJob> _Jobs = new Collection<FileJob>();
        private Queue<string> _Queue = new Queue<string>();

        public uint MaximumJobs { set; get; } = 16;
        public bool AutoReceive { set; get; } = true;

        public string BaseFolderPath { set; get; } = "Received";

        public FileJob[] Jobs
        {
            get
            {
                FileJob[] output = new FileJob[_Jobs.Count];
                for (int i=0; i<_Jobs.Count; i++)
                {
                    output[i] = _Jobs[i];
                }
                return output;
            }
        }

        public bool AddJob(FileJob job, out byte[]? transmitPacket, byte[]? passHash = null)
        {
            // Add the job to the list; however, we need to queue the Job on the event of too high.
            transmitPacket = null;
            _Jobs.Add(job);

            job.Process(string.Empty, out transmitPacket, passHash);

            //if (_Jobs.Count >= MaximumJobs)
            //    transmitPacket = job.RequestQueueBegin(passHash);
            return true;
        }

        public FileJob? FindJobByID(string id)
        {
            foreach (FileJob job in _Jobs)
            {
                if (id == job.ID)
                    return job;
            }
            return null;
        }

        public int Process(List<string> decodedPackets, out List<byte[]> transmitPackets, byte[]? passHash = null)
        {
            transmitPackets = new List<byte[]>();

            int ret = 0;
            // Attempt to auto-start the FileJob.
            foreach (string packet in decodedPackets)
            {
                if (AutoReceive)
                {
                    FileJob? job = FileJob.TryParse(packet, BaseFolderPath);
                    if (job != null)
                    {
                        if (AddJob(job, out byte[]? tP, passHash))
                        {
                            ret++;
                            if (tP != null)
                                transmitPackets.Add(tP);
                        }
                    }
                }
                foreach (FileJob job in _Jobs)
                {
                    if (job.IsCompleted && _Queue.Count > 0)
                    {
                        // Move the next element from the queue into the list.
                        string nextID = _Queue.Dequeue();
                        FileJob? foundJob = FindJobByID(nextID);
                        if (foundJob != null)
                        {
                            byte[]? tP = foundJob.RequestQueueEnd(passHash);
                            if (tP != null)
                                transmitPackets.Add(tP);
                        }
                    }
                    if (job.Process(packet, out byte[]? tP2, passHash))
                    {
                        if (tP2 != null)
                            transmitPackets.Add(tP2);
                    }
                }
            }
            return ret;
        }
    }
    
    public class FileJob
    {
        private long _ExpectedLength = 0;
        private long _CurrentLength = 0;
        private long _LastCurrentLength = 0;
        private long _CurrentParts = -1;
        private FileStream? _Stream = null;
        private DateTime _NextUpdate = DateTime.Now;
        private DateTime _Expire = DateTime.Now;
        private FileJobStatus _JobStatus = FileJobStatus.DISABLED;
        
        public string ID               { private set; get; } = string.Empty;
        public string FilePath         { private set; get; } = string.Empty;
        public long   BytesPerSecond   { private set; get; } = 0;

        public FileJobType JobType     { private set; get; } = FileJobType.TRANSMIT;
        public DateTime?   StartTime   { private set; get; } = null;
        public DateTime?   EndTime     { private set; get; } = null;

        public byte[]? RequestQueueBegin(byte[]? passHash=null)
        {
            if (JobStatus != FileJobStatus.RUNNING)
                return null;
            JobStatus = FileJobStatus.QUEUED;
            return SocketUtil.GeneratePacket($"SWQS^^{ID}", passHash);
        }

        public byte[]? RequestQueueEnd(byte[]? passHash = null)
        {
            if (JobStatus != FileJobStatus.QUEUED)
                return null;
            JobStatus = FileJobStatus.RUNNING;
            return SocketUtil.GeneratePacket($"SWQE^^{ID}", passHash);
        }

        public bool IsCompleted
        {
            get
            {
                return JobStatus == FileJobStatus.SUCCESS || JobStatus == FileJobStatus.FAILED;
            }
        }

        public FileJobStatus JobStatus 
        {
            private set
            {
                if (value != _JobStatus)
                    OnJobUpdate?.Invoke(this, EventArgs.Empty);
                if (value == FileJobStatus.SUCCESS || value == FileJobStatus.FAILED)
                    OnJobCompleted?.Invoke(this, EventArgs.Empty);
                _JobStatus = value;
            }
            get { return _JobStatus; }
        }
        
        public TimeSpan ElapsedTime
        {   
            get
            {
                if (StartTime == null)
                    return TimeSpan.FromSeconds(0);
                if (EndTime == null)
                    return DateTime.Now - StartTime ?? TimeSpan.FromSeconds(0);
                return EndTime - StartTime ?? TimeSpan.FromSeconds(0);
            }
        }

        public double Percentage
        {
            get
            {
                if (_ExpectedLength <= 0)
                    return 0; // prevent divide-by-zero exception
                return Math.Min(_CurrentLength / _ExpectedLength * 100, 100);
            }
        }

        private bool _AckRequired = false;

        public event EventHandler<EventArgs>? OnJobCompleted;
        public event EventHandler<EventArgs>? OnJobUpdate;

        public static FileJob? TryParse(string decodedPacket, string baseFolderPath="")
        {
            string id, filePath;
            long expectedLength;
            FileStream? stream;
            if (!TryParseReceiveHeader(decodedPacket, out id, out filePath, out expectedLength, out stream))
                return null;
            /*
            if (!string.IsNullOrEmpty(baseFolderPath))
            {
                if (!baseFolderPath.EndsWith(Path.PathSeparator))
                    baseFolderPath += Path.PathSeparator;
                filePath = baseFolderPath + filePath;
            }
            */
            return new FileJob(id, filePath, expectedLength, stream!);
        }

        private static bool TryParseReceiveHeader(string decodedPacket,
                                          out string id, out string filePath, out long expectedLength, out FileStream? stream)
        {
            id = string.Empty;
            filePath = string.Empty;
            expectedLength = 0;
            stream = null;

            if (string.IsNullOrEmpty(decodedPacket))
                return false;
            string[] spl = decodedPacket.Split("^^");
            if (spl.Length != 4 || spl[0].Trim() != "SWHD")
                return false;
            filePath = string.IsNullOrEmpty(filePath) ? spl[1].Trim() : filePath;
            id = spl[2].Trim();
            long len = Convert.ToInt64(spl[3]);

            // If 'fP' has parent directories, create them.
            if (filePath.EndsWith('/') || filePath.EndsWith('\\'))
            {
                // We cannot write to a directory, generate a random file name.
                filePath += Guid.NewGuid().ToString();
            }
            string[] dirSpl = filePath.Split('/');
            if (dirSpl.Length <= 1)
                dirSpl = filePath.Split('\\');
            for (int i = 0; i < dirSpl.Length - 1; i++)
            {
                if (!string.IsNullOrEmpty(dirSpl[i]))
                    Directory.CreateDirectory(dirSpl[i]);
            }

            try
            {
                stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Write);
            }
            catch (Exception)
            {
                return false;
            }

            expectedLength = len;
            return true;
        }

        private FileJob(FileJobType type, string filePath)
        {
            JobType = type;
            FilePath = filePath;
        }

        private FileJob(string id, string filePath, long expectedLength, FileStream stream)
        {
            // This will be an automated receive job.
            JobType = FileJobType.RECEIVE;
            JobStatus = FileJobStatus.RUNNING;
            FilePath = filePath;
            ID = id;
            _ExpectedLength = expectedLength;
            _Stream = stream;
        }

        private bool RawProcessTransmit(string decodedPacket, out byte[]? transmitPacket, byte[]? passHash=null)
        {
            transmitPacket = null;
            // If the job has not started, attempt to create the path.
            if (JobType == FileJobType.RECEIVE)
                return false;

            if (JobStatus == FileJobStatus.DISABLED)
            {
                // Create the ID and begin sending the header.
                string id = SocketUtil.GenerateRandomString(4);
                _ExpectedLength = new FileInfo(FilePath).Length;
                transmitPacket = SocketUtil.GeneratePacket(
                $"SWHD^^{SocketUtil.MakeRelative(Path.GetFullPath(FilePath))}^^{id}^^{_ExpectedLength}", passHash);
               
                try
                {
                    if (transmitPacket == null)
                        throw new Exception();
                    _Stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch (Exception)
                {
                    _ExpectedLength = 0;
                    return false;
                }

                JobStatus = FileJobStatus.RUNNING;
                _Expire = DateTime.Now.AddSeconds(30);
            }
            else if (JobStatus == FileJobStatus.RUNNING)
            {                
                // Read through the file chunks and send the information. At the 
                // same time, if the client returns a "queue" request, pause and wait.
                if (decodedPacket == $"SWQS^^{ID}")
                {
                    JobStatus = FileJobStatus.QUEUED;
                    _Expire = DateTime.Now.AddMinutes(5); // allow up to 5 minutes for queueing.
                    return true;
                }
                else if (_AckRequired && decodedPacket == "$SWAK^^{ID}")
                {
                    _AckRequired = false;
                    _Expire = DateTime.Now.AddSeconds(30);
                }

                if (_AckRequired || _Stream == null)
                    return true; // waiting for ack.
                
                byte[] buffer = new byte[65535];
                int bytesRead = _Stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    // Finish the request with the end buffer.
                    transmitPacket = SocketUtil.GeneratePacket($"SWDD^^{ID}^^{_CurrentParts}", passHash);
                    if (transmitPacket == null)
                        return false;
                    JobStatus = FileJobStatus.SUCCESS;
                    _Expire = DateTime.Now.AddSeconds(30);
                    return true;
                } 
                else if (bytesRead != 65535)
                    Array.Resize(ref buffer, bytesRead); // resize the buffer

                _CurrentParts++;
                _CurrentLength += buffer.Count();
                transmitPacket = SocketUtil.GeneratePacket($"SWFD^^{ID}^^{Convert.ToBase64String(buffer)}");
                if (transmitPacket == null)
                    return false;
                _AckRequired = true;
                _Expire = DateTime.Now.AddSeconds(30);
                return true;
            }
            else if (JobStatus == FileJobStatus.QUEUED)
            {
                if (decodedPacket == $"SWQE^^{ID}")
                {
                    _Expire = DateTime.Now.AddSeconds(30);
                    JobStatus = FileJobStatus.RUNNING;
                    return true;
                }
                if (DateTime.Now > _Expire)
                    return false;
                return true;
            }
            return true;
        }


        private bool RawProcessReceive(string decodedPacket, out byte[]? transmitPacket, byte[]? passHash=null)
        {
            transmitPacket = null;
            if (JobType == FileJobType.TRANSMIT)
                return false;
            string[] spl = decodedPacket.Split("^^");
            if (spl.Length <= 1)
                return true;

            try
            {
                switch (spl[0].Trim())
                {
                    case "SWQE":
                        {
                            if (JobStatus != FileJobStatus.QUEUED)
                                return true;
                            if (spl.Length != 2)
                                return true;
                            if (spl[1] == ID)
                            {
                                // We are no longer queued.
                                JobStatus = FileJobStatus.RUNNING;
                                _Expire = DateTime.Now.AddSeconds(30);
                                return true;
                            }
                            break;
                        }
                    case "SWQS":
                        {
                            if (JobStatus == FileJobStatus.QUEUED)
                                return true;
                            if (spl.Length != 2)
                                return true;
                            if (spl[1] == ID)
                            {
                                // We are now queued.
                                JobStatus = FileJobStatus.QUEUED;
                                _Expire = DateTime.Now.AddMinutes(5);
                                return true;
                            }
                            break;
                        }
                    case "SWHD":
                        {
                            if (JobStatus != FileJobStatus.DISABLED)
                                return true;
                            if (!string.IsNullOrEmpty(ID) || spl.Length != 4)
                                return true;

                            string fP, id;
                            long len;
                            FileStream? stream;
                            if (!TryParseReceiveHeader(decodedPacket, out id, out fP, out len, out stream))
                                return true; // allow for continued searching.

                            _ExpectedLength = len;
                            ID = id;
                            FilePath = fP;
                            JobStatus = FileJobStatus.RUNNING;
                            _Expire = DateTime.Now.AddSeconds(30);
                            break;
                        }
                    case "SWFD":
                        {
                            if (JobStatus != FileJobStatus.RUNNING)
                                return true;
                            if (string.IsNullOrEmpty(ID) || spl.Length != 3 || spl[1] != ID || _Stream == null)
                                return true;

                            byte[] res = Convert.FromBase64String(spl[2].Trim());
                            _Stream.Write(res, 0, res.Length);
                            _CurrentLength += res.Length;
                            _CurrentParts++;


                            // Send the ACK response to continue more.
                            transmitPacket = SocketUtil.GeneratePacket($"SWAK^^{ID}", passHash);
                            _Expire = DateTime.Now.AddSeconds(30);
                            break;
                        }
                    case "SWDD":
                        {
                            if (JobStatus != FileJobStatus.RUNNING)
                                return true;
                            if (string.IsNullOrEmpty(ID) || spl.Length != 3 || spl[1] != ID || _Stream == null)
                                return true;

                            long expectedParts = Convert.ToInt64(spl[2]);

                            // Validate all the data to ensure they match.
                            _Stream.Close();
                            
                            JobStatus = (expectedParts == _CurrentParts) 
                                ? FileJobStatus.SUCCESS 
                                : FileJobStatus.FAILED;
                            
                            OnJobCompleted?.Invoke(this, EventArgs.Empty);
                            OnJobUpdate?.Invoke(this, EventArgs.Empty);
                            break;
                        }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public int Process(string[] decodedPackets, out List<byte[]> transmitPackets, byte[]? passHash = null)
        {
            int ret = 0;
            transmitPackets = new List<byte[]>();
            foreach (string packet in decodedPackets)
            {
                if (Process(packet, out byte[]? transmitPacket, passHash))
                {
                    ret++;
                    if (transmitPacket != null)
                        transmitPackets.Add(transmitPacket);
                }
            }
            return ret;
        }

        public bool Process(string decodedPacket, out byte[]? transmitPacket, byte[]? passHash=null)
        {
            transmitPacket = null;
            bool ret = true;
            if ((JobStatus == FileJobStatus.RUNNING || JobStatus == FileJobStatus.QUEUED) && DateTime.Now >= _Expire)
                ret = false;

            if (ret)
            {
                // Attempt to process the value based on receive/transmit.
                ret = JobType == FileJobType.TRANSMIT 
                    ? RawProcessTransmit(decodedPacket, out transmitPacket, passHash)
                    : RawProcessReceive(decodedPacket, out transmitPacket, passHash);
            }

            if (!ret || JobStatus == FileJobStatus.SUCCESS)
            {
                // If something failed, end the task.
                if (!ret)
                    JobStatus = FileJobStatus.FAILED;
                EndTime = DateTime.Now;
                _Stream?.Close();
                OnJobUpdate?.Invoke(this, EventArgs.Empty);
                OnJobCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (JobStatus == FileJobStatus.RUNNING && DateTime.Now >= _NextUpdate)
            {
                BytesPerSecond = _CurrentLength - _LastCurrentLength;
                _LastCurrentLength = _CurrentLength;

                OnJobUpdate?.Invoke(this, EventArgs.Empty);
                _NextUpdate = DateTime.Now.AddSeconds(5);
            }
            
            return ret;
        }

        public static FileJob CreateReceiveJob(string filePath="")
        {
            return new FileJob(FileJobType.RECEIVE, filePath);
        }

        public static FileJob CreateTransmitJob(string filePath)
        {
            return new FileJob(FileJobType.TRANSMIT, filePath);
        }
    }
}