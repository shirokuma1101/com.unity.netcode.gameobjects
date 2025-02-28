using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Unity.Netcode
{
    /// <summary>
    /// The main component of the library
    /// </summary>
    [AddComponentMenu("Netcode/Network Manager", -100)]
    public class NetworkManager : MonoBehaviour, INetworkUpdateSystem
    {
#pragma warning disable IDE1006 // disable naming rule violation check

        // RuntimeAccessModifiersILPP will make this `public`
        internal delegate void RpcReceiveHandler(NetworkBehaviour behaviour, FastBufferReader reader, __RpcParams parameters);

        // RuntimeAccessModifiersILPP will make this `public`
        internal static readonly Dictionary<uint, RpcReceiveHandler> __rpc_func_table = new Dictionary<uint, RpcReceiveHandler>();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // RuntimeAccessModifiersILPP will make this `public`
        internal static readonly Dictionary<uint, string> __rpc_name_table = new Dictionary<uint, string>();
#endif

#pragma warning restore IDE1006 // restore naming rule violation check

        public void NetworkUpdate(NetworkUpdateStage updateStage)
        {
            switch (updateStage)
            {
                case NetworkUpdateStage.EarlyUpdate:
                    {
                        ConnectionManager.ProcessPendingApprovals();
                        ConnectionManager.PollAndHandleNetworkEvents();

                        MessageManager.ProcessIncomingMessageQueue();
                        MessageManager.CleanupDisconnectedClients();
                    }
                    break;
                case NetworkUpdateStage.PreUpdate:
                    {
                        NetworkTimeSystem.UpdateTime();
                    }
                    break;
                case NetworkUpdateStage.PostLateUpdate:
                    {
                        // This should be invoked just prior to the MessageManager processes its outbound queue.
                        SceneManager.CheckForAndSendNetworkObjectSceneChanged();

                        // Process outbound messages
                        MessageManager.ProcessSendQueues();

                        // Metrics update needs to be driven by NetworkConnectionManager's update to assure metrics are dispatched after the send queue is processed.
                        MetricsManager.UpdateMetrics();

                        // TODO: Determine a better way to handle this
                        NetworkObject.VerifyParentingStatus();

                        // This is "ok" to invoke when not processing messages since it is just cleaning up messages that never got handled within their timeout period.
                        DeferredMessageManager.CleanupStaleTriggers();

                        if (m_ShuttingDown)
                        {
                            ShutdownInternal();
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// The client id used to represent the server
        /// </summary>
        public const ulong ServerClientId = 0;

        /// <summary>
        /// Returns ServerClientId if IsServer or LocalClientId if not
        /// </summary>
        public ulong LocalClientId
        {
            get => ConnectionManager.LocalClient.ClientId;
            internal set => ConnectionManager.LocalClient.ClientId = value;
        }

        /// <summary>
        /// Gets a dictionary of connected clients and their clientId keys. This is only accessible on the server.
        /// </summary>
        public IReadOnlyDictionary<ulong, NetworkClient> ConnectedClients => IsServer ? ConnectionManager.ConnectedClients : throw new NotServerException($"{nameof(ConnectionManager.ConnectedClients)} should only be accessed on server.");

        /// <summary>
        /// Gets a list of connected clients. This is only accessible on the server.
        /// </summary>
        public IReadOnlyList<NetworkClient> ConnectedClientsList => IsServer ? ConnectionManager.ConnectedClientsList : throw new NotServerException($"{nameof(ConnectionManager.ConnectedClientsList)} should only be accessed on server.");

        /// <summary>
        /// Gets a list of just the IDs of all connected clients. This is only accessible on the server.
        /// </summary>
        public IReadOnlyList<ulong> ConnectedClientsIds => IsServer ? ConnectionManager.ConnectedClientIds : throw new NotServerException($"{nameof(ConnectionManager.ConnectedClientIds)} should only be accessed on server.");

        /// <summary>
        /// Gets the local <see cref="NetworkClient"/> for this client.
        /// </summary>
        public NetworkClient LocalClient => ConnectionManager.LocalClient;

        /// <summary>
        /// Gets a dictionary of the clients that have been accepted by the transport but are still pending by the Netcode. This is only populated on the server.
        /// </summary>
        // See NetworkConnectionManager.AddPendingClient and NetworkConnectionManager.RemovePendingClient to see how this is now populated
        public readonly Dictionary<ulong, PendingClient> PendingClients = new Dictionary<ulong, PendingClient>();

        /// <summary>
        /// Gets Whether or not a server is running
        /// </summary>
        public bool IsServer => ConnectionManager.LocalClient.IsServer;

        /// <summary>
        /// Gets Whether or not a client is running
        /// </summary>
        public bool IsClient => ConnectionManager.LocalClient.IsClient;

        /// <summary>
        /// Gets if we are running as host
        /// </summary>
        public bool IsHost => ConnectionManager.LocalClient.IsHost;

        /// <summary>
        /// When disconnected from the server, the server may send a reason. If a reason was sent, this property will
        /// tell client code what the reason was. It should be queried after the OnClientDisconnectCallback is called
        /// </summary>
        public string DisconnectReason => ConnectionManager.DisconnectReason;

        /// <summary>
        /// Is true when a server or host is listening for connections.
        /// Is true when a client is connecting or connected to a network session.
        /// Is false when not listening, connecting, or connected.
        /// </summary>
        public bool IsListening
        {
            get => ConnectionManager.IsListening;
            internal set => ConnectionManager.IsListening = value;
        }

        /// <summary>
        /// When true, the client is connected, approved, and synchronized with
        /// the server.
        /// <see cref="NetworkClient.IsConnected"/> <br />
        /// <see cref="NetworkClient.IsApproved"/> <br />
        /// </summary>
        public bool IsConnectedClient
        {
            get => ConnectionManager.LocalClient.IsConnected;
            internal set => ConnectionManager.LocalClient.IsConnected = value;
        }

        /// <summary>
        /// Is true when the client has been approved.
        /// </summary>
        /// <remarks>
        /// This only reflects the client's approved status and does not mean the client
        /// has finished the connection and synchronization process. The server-host will
        /// always be approved upon being starting the <see cref="NetworkManager"/>
        /// <see cref="NetworkClient.IsConnectedClient"/>
        /// </remarks>
        public bool IsApproved
        {
            get => ConnectionManager.LocalClient.IsApproved;
            internal set => ConnectionManager.LocalClient.IsApproved = value;
        }

        /// <summary>
        /// The callback to invoke if the <see cref="NetworkTransport"/> fails.
        /// This callback will be null on shutdown.
        /// </summary>
        /// <remarks>
        /// A failure of the transport is always followed by the <see cref="NetworkManager"/> shutting down. Recovering
        /// from a transport failure would normally entail reconfiguring the transport (e.g. re-authenticating, or
        /// recreating a new service allocation depending on the transport) and restarting the client/server/host.
        /// </remarks>
        public event Action OnTransportFailure
        {
            add => ConnectionManager.OnTransportFailure += value;
            remove => ConnectionManager.OnTransportFailure -= value;
        }

        /// <summary>
        /// The callback to invoke during connection approval. Allows client code to decide whether or not to allow incoming client connection
        /// This callback will be null on shutdown.
        /// </summary>
        public Action<ConnectionApprovalRequest, ConnectionApprovalResponse> ConnectionApprovalCallback
        {
            get => ConnectionManager.ConnectionApprovalCallback;
            set
            {
                if (value != null && value.GetInvocationList().Length > 1)
                {
                    throw new InvalidOperationException($"Only one {nameof(ConnectionApprovalCallback)} can be registered at a time.");
                }

                ConnectionManager.ConnectionApprovalCallback = value;
            }
        }

        /// <summary>
        /// The callback to invoke once a client connects. This callback is only ran on the server and on the local client that connects.
        /// This callback will be null on shutdown.
        /// </summary>
        public event Action<ulong> OnClientConnectedCallback
        {
            add => ConnectionManager.OnClientConnectedCallback += value;
            remove => ConnectionManager.OnClientConnectedCallback -= value;
        }

        /// <summary>
        /// The callback to invoke when a client disconnects. This callback is only ran on the server and on the local client that disconnects.
        /// This callback will be null on shutdown.
        /// </summary>
        public event Action<ulong> OnClientDisconnectCallback
        {
            add => ConnectionManager.OnClientDisconnectCallback += value;
            remove => ConnectionManager.OnClientDisconnectCallback -= value;
        }

        // Edited
        /// <summary>
        /// This callback is invoke when a new object is spawned on the server.
        /// This callback will be null on shutdown.
        /// </summary>
        public event Action<NetworkObject> OnObjectSpawnedCallback
        {
            add => SpawnManager.OnObjectSpawnedCallback += value;
            remove => SpawnManager.OnObjectSpawnedCallback -= value;
        }

        // Edited
        /// <summary>
        /// This callback is invoke when a new object is despawned on the server.
        /// This callback will be null on shutdown.
        /// </summary>
        public event Action<NetworkObject> OnObjectDespawnedCallback
        {
            add => SpawnManager.OnObjectDespawnedCallback += value;
            remove => SpawnManager.OnObjectDespawnedCallback -= value;
        }

        /// <summary>
        /// The current host name we are connected to, used to validate certificate
        /// </summary>
        public string ConnectedHostname => string.Empty;

        /// <summary>
        /// Connection Approval Response
        /// </summary>
        public class ConnectionApprovalResponse
        {
            /// <summary>
            /// Whether or not the client was approved
            /// </summary>
            public bool Approved;

            /// <summary>
            /// If true, a player object will be created. Otherwise the client will have no object.
            /// </summary>
            public bool CreatePlayerObject;

            /// <summary>
            /// The prefabHash to use for the client. If createPlayerObject is false, this is ignored. If playerPrefabHash is null, the default player prefab is used.
            /// </summary>
            public uint? PlayerPrefabHash;

            /// <summary>
            /// The position to spawn the client at. If null, the prefab position is used.
            /// </summary>
            public Vector3? Position;

            /// <summary>
            /// The rotation to spawn the client with. If null, the prefab position is used.
            /// </summary>
            public Quaternion? Rotation;

            /// <summary>
            /// If the Approval decision cannot be made immediately, the client code can set Pending to true, keep a reference to the ConnectionApprovalResponse object and write to it later. Client code must exercise care to setting all the members to the value it wants before marking Pending to false, to indicate completion. If the field is set as Pending = true, we'll monitor the object until it gets set to not pending anymore and use the parameters then.
            /// </summary>
            public bool Pending;

            /// <summary>
            /// Optional reason. If Approved is false, this reason will be sent to the client so they know why they
            /// were not approved.
            /// </summary>
            public string Reason;
        }

        /// <summary>
        /// Connection Approval Request
        /// </summary>
        public struct ConnectionApprovalRequest
        {
            /// <summary>
            /// The connection data payload
            /// </summary>
            public byte[] Payload;

            /// <summary>
            /// The Network Id of the client we are about to handle
            /// </summary>
            public ulong ClientNetworkId;
        }

        /// <summary>
        /// Can be used to determine if the <see cref="NetworkManager"/> is currently shutting itself down
        /// </summary>
        public bool ShutdownInProgress => m_ShuttingDown;

        private bool m_ShuttingDown;

        /// <summary>
        /// The current netcode project configuration
        /// </summary>
        [HideInInspector]
        public NetworkConfig NetworkConfig;

        /// <summary>
        /// The local <see cref="NetworkTime"/>
        /// </summary>
        public NetworkTime LocalTime => NetworkTickSystem?.LocalTime ?? default;

        /// <summary>
        /// The <see cref="NetworkTime"/> on the server
        /// </summary>
        public NetworkTime ServerTime => NetworkTickSystem?.ServerTime ?? default;

        /// <summary>
        /// Gets or sets if the application should be set to run in background
        /// </summary>
        [HideInInspector]
        public bool RunInBackground = true;

        /// <summary>
        /// The log level to use
        /// </summary>
        [HideInInspector]
        public LogLevel LogLevel = LogLevel.Normal;

        /// <summary>
        /// The singleton instance of the NetworkManager
        /// </summary>
        public static NetworkManager Singleton { get; private set; }

        internal static event Action OnSingletonReady;

        /// <summary>
        /// This callback is invoked when the local server is started and listening for incoming connections.
        /// This callback will be null on shutdown.
        /// </summary>
        public event Action OnServerStarted = null;

        /// <summary>
        /// The callback to invoke once the local client is ready.
        /// This callback will be null on shutdown.
        /// </summary>
        public event Action OnClientStarted = null;

        /// <summary>
        /// This callback is invoked once the local server is stopped.
        /// This callback will be null on shutdown.
        /// </summary>
        /// <param name="arg1">The first parameter of this event will be set to <see cref="true"/> when stopping a host instance and <see cref="false"/> when stopping a server instance.</param>
        public event Action<bool> OnServerStopped = null;

        /// <summary>
        /// The callback to invoke once the local client stops.
        /// This callback will be null on shutdown.
        /// </summary>
        /// <remarks>The parameter states whether the client was running in host mode</remarks>
        /// <param name="arg1">The first parameter of this event will be set to <see cref="true"/> when stopping the host client and <see cref="false"/> when stopping a standard client instance.</param>
        public event Action<bool> OnClientStopped = null;

        // Edited
        /// <summary>
        /// This callback is invoked when NetworkManager is initialized.
        /// This callback is never null internally.
        /// </summary>
        public event Action OnInitialized = null;

        /// <summary>
        /// This callback is invoked when NetworkManager is shutdown.
        /// This callback is never null internally.
        /// </summary>
        public event Action OnShutdown = null;

        /// <summary>
        /// The <see cref="NetworkPrefabHandler"/> instance created after starting the <see cref="NetworkManager"/>
        /// </summary>
        public NetworkPrefabHandler PrefabHandler
        {
            get
            {
                if (m_PrefabHandler == null)
                {
                    m_PrefabHandler = new NetworkPrefabHandler();
                    m_PrefabHandler.Initialize(this);
                }

                return m_PrefabHandler;
            }
        }

        private NetworkPrefabHandler m_PrefabHandler;

        /// <summary>
        /// Gets the SpawnManager for this NetworkManager
        /// </summary>
        public NetworkSpawnManager SpawnManager { get; private set; }

        internal IDeferredNetworkMessageManager DeferredMessageManager { get; private set; }

        /// <summary>
        /// Gets the CustomMessagingManager for this NetworkManager
        /// </summary>
        public CustomMessagingManager CustomMessagingManager { get; private set; }

        /// <summary>
        /// The <see cref="NetworkSceneManager"/> instance created after starting the <see cref="NetworkManager"/>
        /// </summary>
        public NetworkSceneManager SceneManager { get; private set; }

        internal NetworkBehaviourUpdater BehaviourUpdater { get; set; }

        /// <summary>
        /// Accessor property for the <see cref="NetworkTimeSystem"/> of the NetworkManager.
        /// Prefer the use of the LocalTime and ServerTime properties
        /// </summary>
        public NetworkTimeSystem NetworkTimeSystem { get; private set; }

        /// <summary>
        /// Accessor property for the <see cref="NetworkTickSystem"/> of the NetworkManager.
        /// </summary>
        public NetworkTickSystem NetworkTickSystem { get; private set; }

        /// <summary>
        /// Used for time mocking in tests
        /// </summary>
        internal IRealTimeProvider RealTimeProvider { get; private set; }

        internal INetworkMetrics NetworkMetrics => MetricsManager.NetworkMetrics;
        internal NetworkMetricsManager MetricsManager = new NetworkMetricsManager();
        internal NetworkConnectionManager ConnectionManager = new NetworkConnectionManager();
        internal NetworkMessageManager MessageManager = null;

#if UNITY_EDITOR
        internal static INetworkManagerHelper NetworkManagerHelper;

        /// <summary>
        /// Interface for NetworkManagerHelper
        /// </summary>
        internal interface INetworkManagerHelper
        {
            bool NotifyUserOfNestedNetworkManager(NetworkManager networkManager, bool ignoreNetworkManagerCache = false, bool editorTest = false);
            void CheckAndNotifyUserNetworkObjectRemoved(NetworkManager networkManager, bool editorTest = false);
        }

        internal delegate void ResetNetworkManagerDelegate(NetworkManager manager);

        internal static ResetNetworkManagerDelegate OnNetworkManagerReset;

        private void Reset()
        {
            OnNetworkManagerReset?.Invoke(this);
        }

        internal void OnValidate()
        {
            if (NetworkConfig == null)
            {
                return; // May occur when the component is added
            }

            if (GetComponentInChildren<NetworkObject>() != null)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                {
                    NetworkLog.LogWarning($"{nameof(NetworkManager)} cannot be a {nameof(NetworkObject)}.");
                }
            }

            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            // If the scene is not dirty or the asset database is currently updating then we can skip updating the NetworkPrefab information
            if (!activeScene.isDirty || EditorApplication.isUpdating)
            {
                return;
            }

            // During OnValidate we will always clear out NetworkPrefabOverrideLinks and rebuild it
            NetworkConfig.Prefabs.NetworkPrefabOverrideLinks.Clear();

            var prefabs = NetworkConfig.Prefabs.Prefabs;
            // Check network prefabs and assign to dictionary for quick look up
            for (int i = 0; i < prefabs.Count; i++)
            {
                var networkPrefab = prefabs[i];
                var networkPrefabGo = networkPrefab?.Prefab;
                if (networkPrefabGo == null)
                {
                    continue;
                }

                var networkObject = networkPrefabGo.GetComponent<NetworkObject>();
                if (networkObject == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                    {
                        NetworkLog.LogError($"Cannot register {NetworkPrefabHandler.PrefabDebugHelper(networkPrefab)}, it does not have a {nameof(NetworkObject)} component at its root");
                    }

                    continue;
                }

                {
                    var childNetworkObjects = new List<NetworkObject>();
                    networkPrefabGo.GetComponentsInChildren(true, childNetworkObjects);
                    if (childNetworkObjects.Count > 1) // total count = 1 root NetworkObject + n child NetworkObjects
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                        {
                            NetworkLog.LogWarning($"{NetworkPrefabHandler.PrefabDebugHelper(networkPrefab)} has child {nameof(NetworkObject)}(s) but they will not be spawned across the network (unsupported {nameof(NetworkPrefab)} setup)");
                        }
                    }
                }
            }
        }
#endif

        /// <summary>
        /// Determines if the NetworkManager's GameObject is parented under another GameObject and
        /// notifies the user that this is not allowed for the NetworkManager.
        /// </summary>
        internal bool NetworkManagerCheckForParent(bool ignoreNetworkManagerCache = false)
        {
#if UNITY_EDITOR
            var isParented = NetworkManagerHelper.NotifyUserOfNestedNetworkManager(this, ignoreNetworkManagerCache);
#else
            var isParented = transform.root != transform;
            if (isParented)
            {
                throw new Exception(GenerateNestedNetworkManagerMessage(transform));
            }
#endif
            return isParented;
        }

        internal static string GenerateNestedNetworkManagerMessage(Transform transform)
        {
            return $"{transform.name} is nested under {transform.root.name}. NetworkManager cannot be nested.\n";
        }

        /// <summary>
        /// Handle runtime detection for parenting the NetworkManager's GameObject under another GameObject
        /// </summary>
        private void OnTransformParentChanged()
        {
            NetworkManagerCheckForParent();
        }

        /// <summary>
        /// Set this NetworkManager instance as the static NetworkManager singleton
        /// </summary>
        public void SetSingleton()
        {
            Singleton = this;

            OnSingletonReady?.Invoke();
        }

        private void Awake()
        {
            NetworkConfig?.InitializePrefabs();

            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void OnEnable()
        {
            if (RunInBackground)
            {
                Application.runInBackground = true;
            }

            if (Singleton == null)
            {
                SetSingleton();
            }

            if (!NetworkManagerCheckForParent())
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        /// <summary>
        /// <see cref="NetworkPrefabHandler.GetNetworkPrefabOverride(GameObject)"/>
        /// </summary>
        /// <param name="gameObject">the <see cref="GameObject"/> to be checked for a <see cref="NetworkManager"/> defined NetworkPrefab override</param>
        /// <returns>a <see cref="GameObject"/> that is either the override or if no overrides exist it returns the same as the one passed in as a parameter</returns>
        public GameObject GetNetworkPrefabOverride(GameObject gameObject) => PrefabHandler.GetNetworkPrefabOverride(gameObject);

        /// <summary>
        /// <see cref="NetworkPrefabHandler.AddNetworkPrefab(GameObject)"/>
        /// </summary>
        /// <param name="prefab"></param>
        /// <exception cref="Exception"></exception>
        public void AddNetworkPrefab(GameObject prefab) => PrefabHandler.AddNetworkPrefab(prefab);

        /// <summary>
        /// <see cref="NetworkPrefabHandler.RemoveNetworkPrefab(GameObject)"/>
        /// </summary>
        /// <param name="prefab"></param>
        public void RemoveNetworkPrefab(GameObject prefab) => PrefabHandler.RemoveNetworkPrefab(prefab);

        /// <summary>
        /// Sets the maximum size of a single non-fragmented message (or message batch) passed through the transport.
        /// This should represent the transport's default MTU size, minus any transport-level overhead.
        /// This value will be used for any remote endpoints that haven't had per-endpoint MTUs set.
        /// This value is also used as the size of the temporary buffer used when serializing
        /// a single message (to avoid serializing multiple times when sending to multiple endpoints),
        /// and thus should be large enough to ensure it can hold each message type.
        /// This value defaults to 1296.
        /// </summary>
        /// <param name="size"></param>
        public int MaximumTransmissionUnitSize
        {
            set => MessageManager.NonFragmentedMessageMaxSize = value & ~7; // Round down to nearest word aligned size
            get => MessageManager.NonFragmentedMessageMaxSize;
        }

        /// <summary>
        /// Set the maximum transmission unit for a specific peer.
        /// This determines the maximum size of a message batch that can be sent to that client.
        /// If not set for any given client, <see cref="MaximumTransmissionUnitSize"/> will be used instead.
        /// </summary>
        /// <param name="clientId"></param>
        /// <param name="size"></param>
        public void SetPeerMTU(ulong clientId, int size)
        {
            MessageManager.PeerMTUSizes[clientId] = size;
        }

        /// <summary>
        /// Queries the current MTU size for a client.
        /// If no MTU has been set for that client, will return <see cref="MaximumTransmissionUnitSize"/>
        /// </summary>
        /// <param name="clientId"></param>
        /// <returns></returns>
        public int GetPeerMTU(ulong clientId)
        {
            if (MessageManager.PeerMTUSizes.TryGetValue(clientId, out var ret))
            {
                return ret;
            }

            return MessageManager.NonFragmentedMessageMaxSize;
        }

        /// <summary>
        /// Sets the maximum size of a message (or message batch) passed through the transport with the ReliableFragmented delivery.
        /// Warning: setting this value too low may result in the SDK becoming non-functional with projects that have a large number of NetworkBehaviours or NetworkVariables, as the SDK relies on the transport's ability to fragment some messages when they grow beyond the MTU size.
        /// </summary>
        /// <param name="size"></param>
        public int MaximumFragmentedMessageSize
        {
            set => MessageManager.FragmentedMessageMaxSize = value;
            get => MessageManager.FragmentedMessageMaxSize;
        }

        internal void Initialize(bool server)
        {
            // Don't allow the user to start a network session if the NetworkManager is
            // still parented under another GameObject
            if (NetworkManagerCheckForParent(true))
            {
                return;
            }

            if (NetworkConfig.NetworkTransport == null)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                {
                    NetworkLog.LogError("No transport has been selected!");
                }

                return;
            }

            // Logging initializes first for any logging during systems initialization
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo(nameof(Initialize));
            }

            this.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
            this.RegisterNetworkUpdate(NetworkUpdateStage.PreUpdate);
            this.RegisterNetworkUpdate(NetworkUpdateStage.PostLateUpdate);

            // ComponentFactory needs to set its defaults next
            ComponentFactory.SetDefaults();

            // UnityTransport dependencies are then initialized
            RealTimeProvider = ComponentFactory.Create<IRealTimeProvider>(this);
            MetricsManager.Initialize(this);

            {
                MessageManager = new NetworkMessageManager(new DefaultMessageSender(this), this);

                MessageManager.Hook(new NetworkManagerHooks(this));

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                MessageManager.Hook(new ProfilingHooks());
#endif

#if MULTIPLAYER_TOOLS
                MessageManager.Hook(new MetricHooks(this));
#endif

                // Assures there is a server message queue available
                MessageManager.ClientConnected(ServerClientId);
            }

            // Now the connection manager can initialize (which initializes transport)
            ConnectionManager.Initialize(this);

            // The remaining systems can then be initialized
            NetworkTimeSystem = server ? NetworkTimeSystem.ServerTimeSystem() : new NetworkTimeSystem(1.0 / NetworkConfig.TickRate);
            NetworkTickSystem = NetworkTimeSystem.Initialize(this);

            // Create spawn manager instance
            SpawnManager = new NetworkSpawnManager(this);

            DeferredMessageManager = ComponentFactory.Create<IDeferredNetworkMessageManager>(this);

            CustomMessagingManager = new CustomMessagingManager(this);

            SceneManager = new NetworkSceneManager(this);

            BehaviourUpdater = new NetworkBehaviourUpdater();
            BehaviourUpdater.Initialize(this);

            NetworkConfig.InitializePrefabs();
            PrefabHandler.RegisterPlayerPrefab();

            // Edited
            // Invoke initialization callback
            OnInitialized?.Invoke();
        }

        private enum StartType
        {
            Server,
            Host,
            Client
        }

        /// <summary>
        /// Determines if NetworkManager can start based on the current
        /// NetworkManager instance state(s)
        /// </summary>
        private bool CanStart(StartType type)
        {
            if (IsListening)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                {
                    NetworkLog.LogWarning("Cannot start " + type + " while an instance is already running");
                }

                return false;
            }

            // Only if it is starting as a server or host do we need to check this
            // Clients don't invoke the ConnectionApprovalCallback
            if (NetworkConfig.ConnectionApproval && type != StartType.Client)
            {
                if (ConnectionApprovalCallback == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                    {
                        NetworkLog.LogWarning("No ConnectionApproval callback defined. Connection approval will timeout");
                    }
                }
            }

            if (ConnectionApprovalCallback != null)
            {
                if (!NetworkConfig.ConnectionApproval)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                    {
                        NetworkLog.LogWarning("A ConnectionApproval callback is defined but ConnectionApproval is disabled. In order to use ConnectionApproval it has to be explicitly enabled ");
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Starts a server
        /// </summary>
        /// <returns>(<see cref="true"/>/<see cref="false"/>) returns true if <see cref="NetworkManager"/> started in server mode successfully.</returns>
        public bool StartServer()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo(nameof(StartServer));
            }

            if (!CanStart(StartType.Server))
            {
                return false;
            }

            ConnectionManager.LocalClient.SetRole(true, false, this);
            ConnectionManager.LocalClient.ClientId = ServerClientId;

            Initialize(true);

            try
            {
                IsListening = NetworkConfig.NetworkTransport.StartServer();
                // If we failed to start then shutdown and notify user that the transport failed to start
                if (IsListening)
                {
                    SpawnManager.ServerSpawnSceneObjectsOnStartSweep();

                    OnServerStarted?.Invoke();
                    ConnectionManager.LocalClient.IsApproved = true;
                    return true;
                }

                ConnectionManager.TransportFailureEventHandler(true);
            }
            catch (Exception)
            {
                ConnectionManager.LocalClient.SetRole(false, false);
                IsListening = false;
                throw;
            }

            return IsListening;
        }

        /// <summary>
        /// Starts a client
        /// </summary>
        /// <returns>(<see cref="true"/>/<see cref="false"/>) returns true if <see cref="NetworkManager"/> started in client mode successfully.</returns>
        public bool StartClient()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo(nameof(StartClient));
            }

            if (!CanStart(StartType.Client))
            {
                return false;
            }

            ConnectionManager.LocalClient.SetRole(false, true, this);

            Initialize(false);

            try
            {
                IsListening = NetworkConfig.NetworkTransport.StartClient();
                // If we failed to start then shutdown and notify user that the transport failed to start
                if (!IsListening)
                {
                    ConnectionManager.TransportFailureEventHandler(true);
                }
                else
                {
                    OnClientStarted?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                ConnectionManager.LocalClient.SetRole(false, false);
                IsListening = false;
            }

            return IsListening;
        }

        /// <summary>
        /// Starts a Host
        /// </summary>
        /// <returns>(<see cref="true"/>/<see cref="false"/>) returns true if <see cref="NetworkManager"/> started in host mode successfully.</returns>
        public bool StartHost()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo(nameof(StartHost));
            }

            if (!CanStart(StartType.Host))
            {
                return false;
            }

            ConnectionManager.LocalClient.SetRole(true, true, this);
            Initialize(true);
            try
            {
                IsListening = NetworkConfig.NetworkTransport.StartServer();
                // If we failed to start then shutdown and notify user that the transport failed to start
                if (!IsListening)
                {
                    ConnectionManager.TransportFailureEventHandler(true);
                }
                else
                {
                    // Finalize host-client and server creation logic
                    HostServerInitialize();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                ConnectionManager.LocalClient.SetRole(false, false);
                IsListening = false;
            }

            return IsListening;
        }

        /// <summary>
        /// Handles the host client creation logic along with
        /// additional server creation logic
        /// </summary>
        private void HostServerInitialize()
        {
            LocalClientId = ServerClientId;
            NetworkMetrics.SetConnectionId(LocalClientId);

            if (NetworkConfig.ConnectionApproval && ConnectionApprovalCallback != null)
            {
                var response = new ConnectionApprovalResponse();
                ConnectionApprovalCallback(new ConnectionApprovalRequest { Payload = NetworkConfig.ConnectionData, ClientNetworkId = ServerClientId }, response);
                if (!response.Approved)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                    {
                        NetworkLog.LogWarning("You cannot decline the host connection. The connection was automatically approved.");
                    }
                }

                response.Approved = true;
                ConnectionManager.HandleConnectionApproval(ServerClientId, response);
            }
            else
            {
                var response = new ConnectionApprovalResponse
                {
                    Approved = true,
                    CreatePlayerObject = NetworkConfig.PlayerPrefab != null
                };
                ConnectionManager.HandleConnectionApproval(ServerClientId, response);
            }

            SpawnManager.ServerSpawnSceneObjectsOnStartSweep();

            OnServerStarted?.Invoke();
            OnClientStarted?.Invoke();

            // This assures that any in-scene placed NetworkObject is spawned and
            // any associated NetworkBehaviours' netcode related properties are
            // set prior to invoking OnClientConnected.
            ConnectionManager.InvokeOnClientConnectedCallback(LocalClientId);
        }

        /// <summary>
        /// Disconnects the remote client.
        /// </summary>
        /// <param name="clientId">The ClientId to disconnect</param>
        public void DisconnectClient(ulong clientId) => ConnectionManager.DisconnectClient(clientId);

        /// <summary>
        /// Disconnects the remote client.
        /// </summary>
        /// <param name="clientId">The ClientId to disconnect</param>
        /// <param name="reason">Disconnection reason. If set, client will receive a DisconnectReasonMessage and have the
        /// reason available in the NetworkManager.DisconnectReason property</param>
        public void DisconnectClient(ulong clientId, string reason = null) => ConnectionManager.DisconnectClient(clientId, reason);

        /// <summary>
        /// Globally shuts down the library.
        /// Disconnects clients if connected and stops server if running.
        /// </summary>
        /// <param name="discardMessageQueue">
        /// If false, any messages that are currently in the incoming queue will be handled,
        /// and any messages in the outgoing queue will be sent, before the shutdown is processed.
        /// If true, NetworkManager will shut down immediately, and any unprocessed or unsent messages
        /// will be discarded.
        /// </param>
        public void Shutdown(bool discardMessageQueue = false)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo(nameof(Shutdown));
            }

            // If we're not running, don't start shutting down, it would only cause an immediate
            // shutdown the next time the manager is started.
            if (IsServer || IsClient)
            {
                m_ShuttingDown = true;
                if (MessageManager != null)
                {
                    MessageManager.StopProcessing = discardMessageQueue;
                }
            }

            if (NetworkConfig != null && NetworkConfig.NetworkTransport != null)
            {
                NetworkConfig.NetworkTransport.OnTransportEvent -= ConnectionManager.HandleNetworkEvent;
            }
        }

        // Ensures that the NetworkManager is cleaned up before OnDestroy is run on NetworkObjects and NetworkBehaviours when unloading a scene with a NetworkManager
        private void OnSceneUnloaded(Scene scene)
        {
            if (gameObject != null && scene == gameObject.scene)
            {
                OnDestroy();
            }
        }

        internal void ShutdownInternal()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
            {
                NetworkLog.LogInfo(nameof(ShutdownInternal));
            }

            this.UnregisterAllNetworkUpdates();

            // Everything is shutdown in the order of their dependencies
            DeferredMessageManager?.CleanupAllTriggers();
            CustomMessagingManager = null;

            BehaviourUpdater?.Shutdown();
            BehaviourUpdater = null;

            // Shutdown connection manager last which shuts down transport
            ConnectionManager.Shutdown();

            if (MessageManager != null)
            {
                MessageManager.Dispose();
                MessageManager = null;
            }

            // We need to clean up NetworkObjects before we reset the IsServer
            // and IsClient properties. This provides consistency of these two
            // property values for NetworkObjects that are still spawned when
            // the shutdown cycle begins.
            SpawnManager?.DespawnAndDestroyNetworkObjects();
            SpawnManager?.ServerResetShudownStateForSceneObjects();
            SpawnManager = null;

            // Let the NetworkSceneManager clean up its two SceneEvenData instances
            SceneManager?.Dispose();
            SceneManager = null;
            IsListening = false;
            m_ShuttingDown = false;

            if (ConnectionManager.LocalClient.IsClient)
            {
                // If we were a client, we want to know if we were a host
                // client or not. (why we pass in "IsServer")
                OnClientStopped?.Invoke(ConnectionManager.LocalClient.IsServer);
            }

            if (ConnectionManager.LocalClient.IsServer)
            {
                // If we were a server, we want to know if we were a host
                // or not. (why we pass in "IsClient")
                OnServerStopped?.Invoke(ConnectionManager.LocalClient.IsClient);
            }

            // Edited
            // Clear all callbacks
            OnServerStarted = null;
            OnClientStarted = null;
            OnServerStopped = null;
            OnClientStopped = null;

            // In the event shutdown is invoked within OnClientStopped or OnServerStopped, set it to false again
            m_ShuttingDown = false;

            // Reset the client's roles
            ConnectionManager.LocalClient.SetRole(false, false);

            // This cleans up the internal prefabs list
            NetworkConfig?.Prefabs?.Shutdown();

            // Reset the configuration hash for next session in the event
            // that the prefab list changes
            NetworkConfig?.ClearConfigHash();

            // Time & tick systems should be the last system shutdown so other systems
            // can unsubscribe from tick updates and such.
            NetworkTimeSystem?.Shutdown();
            NetworkTickSystem = null;

            // Edited
            // Invoke shutdown callback
            OnShutdown?.Invoke();
        }

        // Ensures that the NetworkManager is cleaned up before OnDestroy is run on NetworkObjects and NetworkBehaviours when quitting the application.
        private void OnApplicationQuit()
        {
            // Make sure ShutdownInProgress returns true during this time
            m_ShuttingDown = true;
        }

        // Note that this gets also called manually by OnSceneUnloaded and OnApplicationQuit
        private void OnDestroy()
        {
            ShutdownInternal();

            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnSceneUnloaded;

            if (Singleton == this)
            {
                Singleton = null;
            }
        }
    }
}
