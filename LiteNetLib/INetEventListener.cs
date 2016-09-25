using LiteNetLib.Utils;

namespace LiteNetLib
{
    public enum UnconnectedMessageType
    {
        Default,
        DiscoveryRequest,
        DiscoveryResponse
    }

    public enum DisconnectReason
    {
        StopCalled,
        SocketReceiveError,
        ConnectionFailed,
        ConnectionRejected,
        Timeout,
        SocketSendError,
        RemoteConnectionClose,
        DisconnectPeerCalled,
    }

    public enum ConnectRejectReason : byte
    {
        Unknown,             //0
        BadConnectKey,       //1
        ServerFull,          //2
    }

    public interface INetEventListener
    {
        void OnPeerConnected(NetPeer peer);
        void OnPeerDisconnected(NetPeer peer, DisconnectReason disconnectReason, int additionalData);
        void OnPeerAuthenticating(NetPeer peer, string authKey);
        void OnNetworkError(NetEndPoint endPoint, int socketErrorCode);
        void OnNetworkReceive(NetPeer peer, NetDataReader reader);
        void OnNetworkReceiveUnconnected(NetEndPoint remoteEndPoint, NetDataReader reader, UnconnectedMessageType messageType);
        void OnNetworkReject(NetEndPoint remoteEndPoint, ConnectRejectReason reason);
        void OnNetworkLatencyUpdate(NetPeer peer, int latency);
    }

    public class EventBasedNetListener : INetEventListener
    {
        public delegate void OnPeerConnected(NetPeer peer);
        public delegate void OnPeerDisconnected(NetPeer peer, DisconnectReason disconnectReason, int additionalData);
        public delegate void OnPeerAuthenticating(NetPeer peer, string authKey);
        public delegate void OnNetworkError(NetEndPoint endPoint, int socketErrorCode);
        public delegate void OnNetworkReceive(NetPeer peer, NetDataReader reader);
        public delegate void OnNetworkReceiveUnconnected(NetEndPoint remoteEndPoint, NetDataReader reader, UnconnectedMessageType messageType);
        public delegate void OnNetworkReject(NetEndPoint remoteEndPoint, ConnectRejectReason reason);
        public delegate void OnNetworkLatencyUpdate(NetPeer peer, int latency);

        public event OnPeerConnected PeerConnectedEvent;
        public event OnPeerDisconnected PeerDisconnectedEvent;
        public event OnPeerAuthenticating PeerAuthenticatingEvent;
        public event OnNetworkError NetworkErrorEvent;
        public event OnNetworkReceive NetworkReceiveEvent;
        public event OnNetworkReceiveUnconnected NetworkReceiveUnconnectedEvent;
        public event OnNetworkReject NetworkRejectEvent;
        public event OnNetworkLatencyUpdate NetworkLatencyUpdateEvent; 
         
        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (PeerConnectedEvent != null)
                PeerConnectedEvent(peer);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectReason disconnectReason, int additionalData)
        {
            if (PeerDisconnectedEvent != null)
                PeerDisconnectedEvent(peer, disconnectReason, additionalData);
        }

        void INetEventListener.OnPeerAuthenticating(NetPeer peer, string authKey)
        {
            if (PeerAuthenticatingEvent != null)
                PeerAuthenticatingEvent(peer, authKey);
        }

        void INetEventListener.OnNetworkError(NetEndPoint endPoint, int socketErrorCode)
        {
            if (NetworkErrorEvent != null)
                NetworkErrorEvent(endPoint, socketErrorCode);
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetDataReader reader)
        {
            if (NetworkReceiveEvent != null)
                NetworkReceiveEvent(peer, reader);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(NetEndPoint remoteEndPoint, NetDataReader reader, UnconnectedMessageType messageType)
        {
            if (NetworkReceiveUnconnectedEvent != null)
                NetworkReceiveUnconnectedEvent(remoteEndPoint, reader, messageType);
        }

        void INetEventListener.OnNetworkReject(NetEndPoint remoteEndPoint, ConnectRejectReason reason)
        {
            if (NetworkRejectEvent != null)
                NetworkRejectEvent(remoteEndPoint, reason);
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            if (NetworkLatencyUpdateEvent != null)
                NetworkLatencyUpdateEvent(peer, latency);
        }
    }
}
