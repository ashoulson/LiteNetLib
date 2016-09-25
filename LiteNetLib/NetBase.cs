#if DEBUG
#define STATS_ENABLED
#endif
#if WINRT && !UNITY_EDITOR
using Windows.System.Threading;
#endif

using System;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    public abstract class NetBase
    {
        internal delegate void OnMessageReceived(byte[] data, int length, int errorCode, NetEndPoint remoteEndPoint);

        private struct FlowMode
        {
            public int PacketsPerSecond;
            public int StartRtt;
        }

        protected enum NetEventType
        {
            Connect,
            Disconnect,
            Authenticate,
            Receive,
            ReceiveUnconnected,
            Reject,
            Error,
            ConnectionLatencyUpdated,
            DiscoveryRequest,
            DiscoveryResponse
        }

        protected sealed class NetEvent
        {
            public NetPeer Peer;
            public NetDataReader DataReader;
            public NetEventType Type;
            public NetEndPoint RemoteEndPoint;
            public int AdditionalData;
            public string KeyData;
            public DisconnectReason DisconnectReason;
        }

#if DEBUG
        private struct IncomingData
        {
            public byte[] Data;
            public NetEndPoint EndPoint;
            public DateTime TimeWhenGet;
        }
        private readonly LinkedList<IncomingData> _pingSimulationList = new LinkedList<IncomingData>(); 
        private readonly Random _randomGenerator = new Random();
#endif

        private readonly NetSocket _socket;
        private readonly List<FlowMode> _flowModes;

#if WINRT && !UNITY_EDITOR
        private readonly ManualResetEvent _updateWaiter = new ManualResetEvent(false);
#else
        private Thread _logicThread;
#endif

        private bool _running;
        private readonly Queue<NetEvent> _netEventsQueue;
        private readonly Stack<NetEvent> _netEventsPool;
        private readonly INetEventListener _netEventListener;

        //config section
        public bool UnconnectedMessagesEnabled = false;
        public bool NatPunchEnabled = false;
        public int UpdateTime = 100;
        public int ReliableResendTime = 500;
        public int PingInterval = NetConstants.DefaultPingInterval;
        public long DisconnectTimeout = 5000;
        public bool SimulatePacketLoss = false;
        public bool SimulateLatency = false;
        public int SimulationPacketLossChance = 10;
        public int SimulationMinLatency = 30;
        public int SimulationMaxLatency = 100;
        public bool UnsyncedEvents = false;
        public bool DiscoveryEnabled = false;

        //stats
        public ulong PacketsSent { get; private set; }
        public ulong PacketsReceived { get; private set; }
        public ulong BytesSent { get; private set; }
        public ulong BytesReceived { get; private set; }

        //modules
        public readonly NatPunchModule NatPunchModule;

        public void AddFlowMode(int startRtt, int packetsPerSecond)
        {
            var fm = new FlowMode {PacketsPerSecond = packetsPerSecond, StartRtt = startRtt};

            if (_flowModes.Count > 0 && startRtt < _flowModes[0].StartRtt)
            {
                _flowModes.Insert(0, fm);
            }
            else
            {
                _flowModes.Add(fm);
            }
        }

        internal int GetPacketsPerSecond(int flowMode)
        {
            if (flowMode < 0 || _flowModes.Count == 0)
                return NetConstants.PacketsPerSecondMax;
            return _flowModes[flowMode].PacketsPerSecond;
        }

        internal int GetMaxFlowMode()
        {
            return _flowModes.Count - 1;
        }

        internal int GetStartRtt(int flowMode)
        {
            if (flowMode < 0 || _flowModes.Count == 0)
                return 0;
            return _flowModes[flowMode].StartRtt;
        }

        protected NetBase(INetEventListener listener)
        {
            _socket = new NetSocket(ReceiveLogic);
            _netEventListener = listener;
            _flowModes = new List<FlowMode>();
            _netEventsQueue = new Queue<NetEvent>();
            _netEventsPool = new Stack<NetEvent>();
            NatPunchModule = new NatPunchModule(this, _socket);
        }

        protected void SocketClearPeers()
        {
#if WINRT && !UNITY_EDITOR
            _socket.ClearPeers();
#endif
        }

        protected void SocketRemovePeer(NetEndPoint ep)
        {
#if WINRT && !UNITY_EDITOR
            _socket.RemovePeer(ep);
#endif
        }

        protected NetPeer CreatePeer(NetEndPoint remoteEndPoint)
        {
            var peer = new NetPeer(this, remoteEndPoint);
            peer.PingInterval = PingInterval;
            return peer;
        }

        internal void ConnectionLatencyUpdated(NetPeer fromPeer, int latency)
        {
            var evt = CreateEvent(NetEventType.ConnectionLatencyUpdated);
            evt.Peer = fromPeer;
            evt.AdditionalData = latency;
            EnqueueEvent(evt);
        }

        /// <summary>
        /// Start logic thread and listening on available port
        /// </summary>
        public bool Start()
        {
            return Start(0);
        }

        /// <summary>
        /// Start logic thread and listening on selected port
        /// </summary>
        /// <param name="port">port to listen</param>
        public virtual bool Start(int port)
        {
            if (_running)
            {
                return false;
            }

            _netEventsQueue.Clear();
            if (!_socket.Bind(port))
                return false;

            _running = true;
#if WINRT && !UNITY_EDITOR
            ThreadPool.RunAsync(
                a => UpdateLogic(), 
                WorkItemPriority.Normal, 
                WorkItemOptions.TimeSliced).AsTask();
#else
            _logicThread = new Thread(UpdateLogic);
            _logicThread.Name = "LogicThread(" + port + ")";
            _logicThread.IsBackground = true;
            _logicThread.Start();
#endif
            return true;
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="message">Raw data</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(byte[] message, NetEndPoint remoteEndPoint)
        {
            return SendUnconnectedMessage(message, 0, message.Length, remoteEndPoint);
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="writer">Data serializer</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(NetDataWriter writer, NetEndPoint remoteEndPoint)
        {
            return SendUnconnectedMessage(writer.Data, 0, writer.Length, remoteEndPoint);
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="message">Raw data</param>
        /// <param name="start">data start</param>
        /// <param name="length">data length</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(byte[] message, int start, int length, NetEndPoint remoteEndPoint)
        {
            if (!_running)
                return false;
            var packet = NetPacket.CreateRawPacket(PacketProperty.UnconnectedMessage, message, start, length);
            return SendRaw(packet, remoteEndPoint);
        }

        public bool SendDiscoveryRequest(NetDataWriter writer, int port)
        {
            return SendDiscoveryRequest(writer.Data, 0, writer.Length, port);
        }

        public bool SendDiscoveryRequest(byte[] data, int port)
        {
            return SendDiscoveryRequest(data, 0, data.Length, port);
        }

        public bool SendDiscoveryRequest(byte[] data, int start, int length, int port)
        {
            if (!_running)
                return false;
            var packet = NetPacket.CreateRawPacket(PacketProperty.DiscoveryRequest, data, start, length);
            return _socket.SendBroadcast(packet, 0, packet.Length, port);
        }

        public bool SendDiscoveryResponse(NetDataWriter writer, NetEndPoint remoteEndPoint)
        {
            return SendDiscoveryResponse(writer.Data, 0, writer.Length, remoteEndPoint);
        }

        public bool SendDiscoveryResponse(byte[] data, NetEndPoint remoteEndPoint)
        {
            return SendDiscoveryResponse(data, 0, data.Length, remoteEndPoint);
        }

        public bool SendDiscoveryResponse(byte[] data, int start, int length, NetEndPoint remoteEndPoint)
        {
            if (!_running)
                return false;
            var packet = NetPacket.CreateRawPacket(PacketProperty.DiscoveryResponse, data, start, length);
            return SendRaw(packet, remoteEndPoint);
        }

        internal bool SendRaw(byte[] message, NetEndPoint remoteEndPoint)
        {
            return SendRaw(message, 0, message.Length, remoteEndPoint);
        }

        internal bool SendRaw(byte[] message, int start, int length, NetEndPoint remoteEndPoint)
        {
            if (!_running)
                return false;

            int errorCode = 0;
            bool result = _socket.SendTo(message, start, length, remoteEndPoint, ref errorCode) > 0;

            //10040 message to long... need to check
            //10065 no route to host
            if (errorCode != 0 && errorCode != 10040 && errorCode != 10065)
            {
                ProcessSendError(remoteEndPoint, errorCode);
                return false;
            }
            if (errorCode == 10040)
            {
                NetUtils.DebugWrite(ConsoleColor.Red, "[SRD] 10040, datalen: {0}", length);
                return false;
            }
#if STATS_ENABLED
            PacketsSent++;
            BytesSent += (uint)length;
#endif

            return result;
        }

        internal void RejectPeer(NetPacket incomingPacket, NetEndPoint remoteEndPoint, ConnectRejectReason rejectReason)
        {
            var rejectPacket = NetPacket.CreateRawPacket(PacketProperty.ConnectReject, 9);
            Buffer.BlockCopy(incomingPacket.RawData, 1, rejectPacket, 1, 8);
            rejectPacket[9] = (byte)rejectReason;

            //Note that this packet will leak because we have no peer pool to put it into
            SendRaw(rejectPacket, remoteEndPoint);

            var netEvent = CreateEvent(NetEventType.Reject);
            netEvent.RemoteEndPoint = remoteEndPoint;
            netEvent.AdditionalData = (int)rejectReason;
            EnqueueEvent(netEvent);

            return;
        }

        /// <summary>
        /// Stop updating thread and listening
        /// </summary>
        public virtual void Stop()
        {
            if (_running)
            {
                _running = false;
#if !WINRT || UNITY_EDITOR
                if(Thread.CurrentThread != _logicThread)
                    _logicThread.Join();
                _logicThread = null;
#endif
                _socket.Close();
            }
        }

        /// <summary>
        /// Returns true if socket listening and update thread is running
        /// </summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>
        /// Returns local EndPoint (host and port)
        /// </summary>
        public NetEndPoint LocalEndPoint
        {
            get { return _socket.LocalEndPoint; }
        }

        protected NetEvent CreateEvent(NetEventType type)
        {
            NetEvent evt = null;

            lock (_netEventsPool)
            {
                if (_netEventsPool.Count > 0)
                {
                    evt = _netEventsPool.Pop();
                }
            }
            if(evt == null)
            {
                evt = new NetEvent {DataReader = new NetDataReader()};
            }
            evt.Type = type;
            return evt;
        }

        protected void EnqueueEvent(NetEvent evt)
        {
            if (UnsyncedEvents)
            {
                ProcessEvent(evt);
            }
            else
            {
                lock (_netEventsQueue)
                {
                    _netEventsQueue.Enqueue(evt);
                }
            }
        }

        private void ProcessEvent(NetEvent evt)
        {
            switch (evt.Type)
            {
                case NetEventType.Connect:
                    _netEventListener.OnPeerConnected(evt.Peer);
                    break;
                case NetEventType.Disconnect:
                    _netEventListener.OnPeerDisconnected(evt.Peer, evt.DisconnectReason, evt.AdditionalData);
                    break;
                case NetEventType.Authenticate:
                    _netEventListener.OnPeerAuthenticating(evt.Peer, evt.KeyData);
                    break;
                case NetEventType.Receive:
                    _netEventListener.OnNetworkReceive(evt.Peer, evt.DataReader);
                    break;
                case NetEventType.ReceiveUnconnected:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.Default);
                    break;
                case NetEventType.Reject:
                    _netEventListener.OnNetworkReject(evt.RemoteEndPoint, (ConnectRejectReason)evt.AdditionalData);
                    break;
                case NetEventType.DiscoveryRequest:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.DiscoveryRequest);
                    break;
                case NetEventType.DiscoveryResponse:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.DiscoveryResponse);
                    break;
                case NetEventType.Error:
                    _netEventListener.OnNetworkError(evt.RemoteEndPoint, evt.AdditionalData);
                    break;
                case NetEventType.ConnectionLatencyUpdated:
                    _netEventListener.OnNetworkLatencyUpdate(evt.Peer, evt.AdditionalData);
                    break;
            }

            //Recycle
            evt.DataReader.Clear();
            evt.Peer = null;
            evt.AdditionalData = 0;
            evt.RemoteEndPoint = null;
            evt.KeyData = null;

            lock (_netEventsPool)
            {
                _netEventsPool.Push(evt);
            }
        }

        public void PollEvents()
        {
            if (UnsyncedEvents)
                return;

            while (_netEventsQueue.Count > 0)
            {
                NetEvent evt;
                lock (_netEventsQueue)
                {
                    evt = _netEventsQueue.Dequeue();
                }
                ProcessEvent(evt);
            }
        }

        //Update function
        private void UpdateLogic()
        {
            while (_running)
            {
#if DEBUG
                if (SimulateLatency)
                {
                    var node = _pingSimulationList.First;
                    var time = DateTime.UtcNow;
                    while (node != null)
                    {
                        var incomingData = node.Value;
                        if (incomingData.TimeWhenGet <= time)
                        {
                            DataReceived(incomingData.Data, incomingData.Data.Length, incomingData.EndPoint);
                            var nodeToRemove = node;
                            node = node.Next;

                            lock (_pingSimulationList)
                                _pingSimulationList.Remove(nodeToRemove);
                        }
                        else
                        {
                            node = node.Next;
                        }
                    }
                }
#endif
                PostProcessEvent(UpdateTime);
#if WINRT && !UNITY_EDITOR
                _updateWaiter.WaitOne(UpdateTime);
#else
                Thread.Sleep(UpdateTime);
#endif
            }
        }

        private void ReceiveLogic(byte[] data, int length, int errorCode, NetEndPoint remoteEndPoint)
        {
            //Receive some info
            if (errorCode == 0)
            {
#if DEBUG
                bool receivePacket = true;

                if (SimulatePacketLoss && _randomGenerator.Next(100/SimulationPacketLossChance) == 0)
                {
                    receivePacket = false;
                }
                else if (SimulateLatency)
                {
                    int latency = _randomGenerator.Next(SimulationMinLatency, SimulationMaxLatency);
                    if (latency > 5)
                    {
                        byte[] holdedData = new byte[length];
                        Buffer.BlockCopy(data, 0, holdedData, 0, length);

                        lock(_pingSimulationList)
                            _pingSimulationList.AddFirst(new IncomingData
                            {
                                Data = holdedData, EndPoint = remoteEndPoint, TimeWhenGet = DateTime.UtcNow.AddMilliseconds(latency)
                            });
                        receivePacket = false;
                    }
                }

                if (receivePacket) //DataReceived
#endif
                    //ProcessEvents
                    DataReceived(data, length, remoteEndPoint);
            }
            else
            {
                ProcessReceiveError(errorCode);
                Stop();
            }
        }

        private void DataReceived(byte[] reusableBuffer, int count, NetEndPoint remoteEndPoint)
        {
#if STATS_ENABLED
            PacketsReceived++;
            BytesReceived += (uint) count;
#endif

            //Try get packet property
            PacketProperty property;
            if (!NetPacket.GetPacketProperty(reusableBuffer, out property))
                return;

            //Check unconnected
            switch (property)
            {
                case PacketProperty.DiscoveryRequest:
                if(DiscoveryEnabled)
                {
                    var netEvent = CreateEvent(NetEventType.DiscoveryRequest);
                    netEvent.RemoteEndPoint = remoteEndPoint;
                    netEvent.DataReader.SetSource(NetPacket.GetUnconnectedData(reusableBuffer, count));
                    EnqueueEvent(netEvent);
                }
                return;
                case PacketProperty.DiscoveryResponse:
                {
                    var netEvent = CreateEvent(NetEventType.DiscoveryResponse);
                    netEvent.RemoteEndPoint = remoteEndPoint;
                    netEvent.DataReader.SetSource(NetPacket.GetUnconnectedData(reusableBuffer, count));
                    EnqueueEvent(netEvent);
                }
                return;
                case PacketProperty.UnconnectedMessage:
                if (UnconnectedMessagesEnabled)
                {
                    var netEvent = CreateEvent(NetEventType.ReceiveUnconnected);
                    netEvent.RemoteEndPoint = remoteEndPoint;
                    netEvent.DataReader.SetSource(NetPacket.GetUnconnectedData(reusableBuffer, count));
                    EnqueueEvent(netEvent);
                }
                return;
                case PacketProperty.NatIntroduction:
                case PacketProperty.NatIntroductionRequest:
                case PacketProperty.NatPunchMessage:
                {
                    if (NatPunchEnabled)
                        NatPunchModule.ProcessMessage(remoteEndPoint, property, NetPacket.GetUnconnectedData(reusableBuffer, count));
                    return;
                }
            }

            //other
            ReceiveFromSocket(reusableBuffer, count, remoteEndPoint);
        }

        internal byte[] PrepareConnectPacket(ulong connectId, string connectKey, string authKey)
        {
            //Get connect key bytes
            byte[] keyData = Encoding.UTF8.GetBytes(connectKey);
            if (keyData.Length > NetConstants.MaxConnectKeyBytes)
                throw new Exception("Connect key too long: " + connectKey);

            //Get auth key bytes
            byte[] authData;
            if (authKey != null)
                authData = Encoding.UTF8.GetBytes(authKey);
            else
                authData = new byte[0];

            //Make initial packet
            int totalSize = 8 + 1 + keyData.Length + authData.Length;
            var connectPacket = NetPacket.CreateRawPacket(PacketProperty.ConnectRequest, totalSize);

            //Add id
            FastBitConverter.GetBytes(connectPacket, 1, connectId);

            //Add connect key
            connectPacket[9] = (byte)keyData.Length;
            Buffer.BlockCopy(keyData, 0, connectPacket, 10, keyData.Length);

            //Add auth key
            Buffer.BlockCopy(authData, 0, connectPacket, 10 + keyData.Length, authData.Length);

            return connectPacket;
        }

        internal string GetConnectKey(NetPacket packet)
        {
            byte connectKeyLength = packet.RawData[9];
            return Encoding.UTF8.GetString(packet.RawData, 10, connectKeyLength);
        }

        internal string GetAuthKey(NetPacket packet)
        {
            byte connectKeyLength = packet.RawData[9];
            int authKeyStart = 10 + connectKeyLength;
            int authKeyLength = packet.RawData.Length - authKeyStart;
            if (authKeyLength <= 0)
              return null;
            return Encoding.UTF8.GetString(packet.RawData, authKeyStart, authKeyLength);
        }

        protected virtual void ProcessReceiveError(int socketErrorCode)
        {
            var netEvent = CreateEvent(NetEventType.Error);
            netEvent.AdditionalData = socketErrorCode;
            EnqueueEvent(netEvent);
        }

        internal virtual void ProcessSendError(NetEndPoint endPoint, int socketErrorCode)
        {
            var netEvent = CreateEvent(NetEventType.Error);
            netEvent.RemoteEndPoint = endPoint;
            netEvent.AdditionalData = socketErrorCode;
            EnqueueEvent(netEvent);
        }

        protected abstract void ReceiveFromSocket(byte[] reusableBuffer, int count, NetEndPoint remoteEndPoint);
        protected abstract void PostProcessEvent(int deltaTime);
        internal abstract void ReceiveFromPeer(NetPacket packet, NetEndPoint endPoint);
    }
}
