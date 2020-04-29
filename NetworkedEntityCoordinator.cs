using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using me.jordanwood.proto.v1.statemanager;
using Newtonsoft.Json;
using Debug = UnityEngine.Debug;
using NetworkedType = me.jordanwood.proto.v1.statemanager.Type;

public class NetworkedEntityCoordinator {
    internal readonly WorkerType workerType;
    internal readonly ConcurrentDictionary<string, NetworkedEntity> _entities;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<MemberInfo, object>> _localTrackedState;
    private readonly HashSet<string> _existing;
    internal readonly ConcurrentBag<(string, string)> AuthoritativeItems;

    public Boolean isStopping = false;
    public readonly ConcurrentQueue<EntityState> MarkedForInstantiation;
    public readonly ConcurrentQueue<(NetworkedEntity, Member, MemberInfo)> MarkedForUpdate;
    public readonly ConcurrentQueue<(string, WorkerType)> ClientConnectEventQueue;
    private readonly ConcurrentQueue<(string, EntityState)> _createEntityQueue;

    private readonly ConcurrentQueue<(string, MemberInfo, string)> _markedForAuthorityNotification;

    private readonly State.StateClient _stateClient;
    private readonly string _token;
    private readonly int _msPerPacket;

    private NetworkedType NetworkedType {
        get {
            switch (workerType) {
                case WorkerType.WORKER:
                    return NetworkedType.Worker;
                case WorkerType.CLIENT:
                    return NetworkedType.Client;
                case WorkerType.TELEMETRY:
                    return NetworkedType.Telemetry;
                default:
                    return NetworkedType.Client;
            }
        }
    }

    public NetworkedEntityCoordinator(WorkerType type, State.StateClient stateClient, int tickrate, string token) {
        _entities = new ConcurrentDictionary<string, NetworkedEntity>();
        _localTrackedState = new ConcurrentDictionary<string, ConcurrentDictionary<MemberInfo, object>>();
        _existing = new HashSet<string>();
        AuthoritativeItems = new ConcurrentBag<(string, string)>();
        _markedForAuthorityNotification = new ConcurrentQueue<(string, MemberInfo, string)>();
        _createEntityQueue = new ConcurrentQueue<(string, EntityState)>();

        ClientConnectEventQueue = new ConcurrentQueue<(string, WorkerType)>();
        MarkedForInstantiation = new ConcurrentQueue<EntityState>();
        MarkedForUpdate = new ConcurrentQueue<(NetworkedEntity, Member, MemberInfo)>();

        workerType = type;
        _stateClient = stateClient;
        _token = token;
        _msPerPacket = 1000 / tickrate;
    }

    private AsyncServerStreamingCall<EntityState> _stateStreamCall;
    private AsyncServerStreamingCall<ConnectionRequest> _subscriptionCall;

    public void DataStream() {
        FullLoadHandler();

        var stateStreamThread = new Thread(StateStreamHandler) {IsBackground = true, Name = "State Stream Thread"};
        stateStreamThread.Start();

        var updateObjectThread = new Thread(UpdateObjectHandler) {IsBackground = true, Name = "Update Object Thread"};
        updateObjectThread.Start();

        var createObjectThread = new Thread(CreateObjectHandler) {IsBackground = true, Name = "Create Object Thread"};
        createObjectThread.Start();

        var subscriptionObjectThread = new Thread(SubscriptionStreamHandler) {IsBackground = true, Name = "Subscription Stream Thread"};
        subscriptionObjectThread.Start();
    }

    public void Cleanup() {
        _stateStreamCall.Dispose();
        _subscriptionCall.Dispose();
    }

    private void FullLoadHandler() {
        var fullLoadCall = _stateClient.FullLoad(new ConnectionRequest {Token = _token, Type = NetworkedType});

        foreach (var item in fullLoadCall.State) {
            NetworkedEntity entity = null;
            if (_entities.ContainsKey(item.Id))
                entity = _entities[item.Id];

            InstantiateOrUpdateEntity(item, entity);
        }
    }

    private async void StateStreamHandler() {
        var req = new ConnectionRequest {
            Token = _token,
            Type = (NetworkedType) (((int) workerType) - 1)
        };

        _stateStreamCall = _stateClient.StateStream(req);

        while (!isStopping) {
            while (await _stateStreamCall.ResponseStream.MoveNext(CancellationToken.None)) {
                var item = _stateStreamCall.ResponseStream.Current;

                NetworkedEntity entity = null;
                if (_entities.ContainsKey(item.Id))
                    entity = _entities[item.Id];

                InstantiateOrUpdateEntity(item, entity);
            }
        }
    }

    private async void UpdateObjectHandler() {
        try {
            while (!isStopping) {
                List<AsyncUnaryCall<GenericResponse>> responseList = new List<AsyncUnaryCall<GenericResponse>>();

                if (!_localTrackedState.IsEmpty) {
                    foreach (var item in _localTrackedState) {
                        var entityId = item.Key;
                        var members = item.Value;
                        _entities.TryGetValue(entityId, out var entity);

                        var data = new EntityState {
                            Id = entityId,
                            PrefabName = entity.prefabName
                        };

                        foreach (var item1 in members) {
                            // TODO properties
                            var fieldInfo = item1.Key as FieldInfo;

                            data.Members.Add(new Member {
                                AuthoritativeMember = _token,
                                Data = JsonConvert.SerializeObject(item1.Value),
                                MemberName = fieldInfo.Name,
                                MemberType = $"{fieldInfo.FieldType.FullName}"
                            });
                        }

                        var res = _stateClient.UpdateObject(new EntityEditRequest
                            {EntityId = entityId, State = data, Token = _token});
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(_msPerPacket));
            }
        }
        catch (Exception e) {
            Debug.LogError("UpdateObject: " + e.Message);
        }
    }

    private async void CreateObjectHandler() {
        while (!isStopping) {
            List<AsyncUnaryCall<GenericResponse>> responseList = new List<AsyncUnaryCall<GenericResponse>>();

            while (!_createEntityQueue.IsEmpty) {
                _createEntityQueue.TryDequeue(out var item);
                (string id, EntityState state) = item;

                responseList.Add(_stateClient.CreateObjectAsync(new EntityEditRequest {
                    EntityId = id,
                    State = state,
                    Token = _token
                }));
            }

            foreach (var item in responseList
                .Where(item => !item.ResponseAsync.Result.Successful))
                Debug.LogWarning("Failed to create object " + item.ResponseAsync.Result.Error);

            await Task.Delay(TimeSpan.FromMilliseconds(_msPerPacket));
        }
    }

    private async void SubscriptionStreamHandler() {
        while (!isStopping) {
            _subscriptionCall = _stateClient.SubscriptionStream(new ConnectionRequest {
                Token = _token,
                Type = NetworkedType
            });

            while (await _subscriptionCall.ResponseStream.MoveNext(CancellationToken.None)) {
                var item = _subscriptionCall.ResponseStream.Current;
                Debug.Log("New client connecting " + item.Type + "|" + (int) item.Type);
                ClientConnectEventQueue.Enqueue(item: (item.Token, (WorkerType) (int) item.Type));
            }
        }
    }

    private void InstantiateOrUpdateEntity(EntityState item, NetworkedEntity entity = null) {
        if (entity != null) {
            foreach (var member in entity.GetType().GetMembers(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                BindingFlags.DeclaredOnly)) {
                var value = item.Members.FirstOrDefault(x =>
                    x.MemberName.Equals(member.Name, StringComparison.OrdinalIgnoreCase));
                if (value == null) continue;

                MarkedForUpdate.Enqueue((entity, value, member));
            }
        }
        else {
            foreach (var member in item.Members) {
                Debug.Log(NetworkedType + " - " + member.AuthoritativeMember + "|" + _token);
                if (member.AuthoritativeMember == _token)
                    AuthoritativeItems.Add((item.Id, member.MemberName));
            }

            _existing.Add(item.Id);
            MarkedForInstantiation.Enqueue(item);
        }
    }

    public bool HaveAuthority(string id, MemberInfo info) {
        return AuthoritativeItems.Contains((id, info.Name));
    }

    public void RegisterNetworkedEntity(NetworkedEntity entity, string id = "") {
        bool initLocal = false;
        if (id == "") {
            id = Guid.NewGuid().ToString();

            initLocal = true;
            // TODO mark for creation
        }

        if (!_existing.Contains(id)) _existing.Add(id);

        entity.entityId = id;
        Debug.Log("Assigned id " + id);
        _entities.TryAdd(id, entity);

        // TODO remove one sync and combine

        if (initLocal) _createEntityQueue.Enqueue((entity.entityId, entity.ToEntityState()));
        SyncTrackedMembers(entity);

        Debug.Log("Successfully registered. Total entities: " + _entities.Count);
    }

    public void SyncTrackedMembers(NetworkedEntity entity) {
        foreach (var member in entity.getSyncedMemberInfos()) {
            object value;
            switch (member.MemberType) {
                case MemberTypes.Field:
                    value = (member as FieldInfo).GetValue(entity);
                    break;
                case MemberTypes.Property:
                    value = (member as PropertyInfo).GetValue(entity);
                    break;
                default:
                    //Debug.LogWarning(
                    //    $"{entity.gameObject.name}:{nameof(entity)}->{member.Name} is not valid member type");
                    continue;
            }

            if (!_localTrackedState.ContainsKey(entity.entityId)) {
                var dic = new ConcurrentDictionary<MemberInfo, object>();
                dic.TryAdd(member, value);

                _localTrackedState.TryAdd(entity.entityId, dic);
            }
            else {
                _localTrackedState.TryGetValue(entity.entityId, out var val);
                if (val != null) val[member] = value;
            }
        }
    }
}